using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CobolAnalyst.Web.Core.Cache;
using CobolAnalyst.Web.Core.KnowledgeBase;
using CobolAnalyst.Web.Core.Llm;
using CobolAnalyst.Web.Core.Prompts;
using CobolAnalyst.Web.Models;
using Microsoft.Extensions.Logging;

namespace CobolAnalyst.Web.Core.Analysis;

/// <summary>
/// Orchestrates concurrent LLM analysis of COBOL chunks, repairs malformed JSON,
/// deduplicates rules, and caches results.
/// </summary>
public sealed class AnalysisOrchestrator : IAnalysisOrchestrator
{
    private const int MaxConcurrency = 4;
    private const double DeduplicationThreshold = 0.82;
    private const int CrossCuttingThreshold = 3;

    private readonly ILlmClient _llm;
    private readonly AnalysisCache _cache;
    private readonly KnowledgeBaseService _kb;
    private readonly PromptTemplateStore _templates;
    private readonly ILogger<AnalysisOrchestrator> _logger;

    /// <summary>Initialises the orchestrator with required services.</summary>
    public AnalysisOrchestrator(
        ILlmClient llm,
        AnalysisCache cache,
        KnowledgeBaseService kb,
        PromptTemplateStore templates,
        ILogger<AnalysisOrchestrator> logger)
    {
        _llm = llm;
        _cache = cache;
        _kb = kb;
        _templates = templates;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<List<ExtractedRule>> AnalyseAsync(
        List<CobolChunk> chunks,
        IProgress<AnalysisProgressEvent>? progress,
        CancellationToken cancellationToken = default,
        string? contextBlock = null,
        Dictionary<string, List<string>>? symbolTable = null)
    {
        var sw = Stopwatch.StartNew();
        var allRules = new ConcurrentBag<ExtractedRule>();
        var sem = new SemaphoreSlim(MaxConcurrency);
        var activeTemplate = _templates.GetActive();

        var tasks = chunks.Select(chunk => ProcessChunkAsync(
            chunk, sem, allRules, progress, sw, activeTemplate, contextBlock,
            symbolTable, cancellationToken)).ToList();

        await Task.WhenAll(tasks);

        var rules = Deduplicate(allRules.ToList());
        return rules;
    }

    /// <summary>Returns the active template info for recording on the session.</summary>
    public (string? Id, string? Name) GetActiveTemplateInfo()
    {
        var t = _templates.GetActive();
        return (t?.Id, t?.Name);
    }

    private async Task ProcessChunkAsync(
        CobolChunk chunk,
        SemaphoreSlim sem,
        ConcurrentBag<ExtractedRule> allRules,
        IProgress<AnalysisProgressEvent>? progress,
        Stopwatch sw,
        Models.PromptTemplate? activeTemplate,
        string? contextBlock,
        Dictionary<string, List<string>>? symbolTable,
        CancellationToken ct)
    {
        progress?.Report(new AnalysisProgressEvent
        {
            ChunkLabel = chunk.Label,
            Status = ChunkStatus.Queued,
            ElapsedMs = sw.ElapsedMilliseconds
        });

        await sem.WaitAsync(ct);
        try
        {
            progress?.Report(new AnalysisProgressEvent
            {
                ChunkLabel = chunk.Label,
                Status = ChunkStatus.Running,
                ElapsedMs = sw.ElapsedMilliseconds
            });

            var hints = _kb.GetTopHints(chunk.SourceText, 3);

            // Cross-file dependency context
            string? depsBlock = null;
            if (symbolTable is { Count: > 0 })
            {
                var deps = CrossFileDependencyDetector.DetectDependencies(
                    chunk.SourceText, symbolTable, chunk.FileName);
                depsBlock = CrossFileDependencyDetector.FormatForPrompt(deps);
            }

            var fullContext = string.IsNullOrEmpty(depsBlock)
                ? contextBlock
                : (contextBlock ?? string.Empty) + "\n" + depsBlock;

            var prompt = PromptBuilder.BuildExtractionPrompt(chunk, hints, activeTemplate, fullContext);

            var model = _llm.SelectedModel;
            var cached = _cache.TryGet(chunk.SourceText, prompt, model);
            List<ExtractedRule> rules;

            if (cached is not null)
            {
                rules = cached;
                _logger.LogDebug("Cache hit for chunk {Label}", chunk.Label);
            }
            else
            {
                var rawJson = await CollectStreamAsync(chunk, prompt, ct);
                rules = ParseAndRepairJson(rawJson, chunk);
                _cache.Store(chunk.SourceText, prompt, model, rules);
            }

            foreach (var r in rules)
            {
                r.SourceFile = chunk.FileName;
                r.SourceChunk = chunk.Label;
                allRules.Add(r);
            }

            progress?.Report(new AnalysisProgressEvent
            {
                ChunkLabel = chunk.Label,
                Status = ChunkStatus.Complete,
                RulesFound = rules.Count,
                ElapsedMs = sw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyse chunk {Label}", chunk.Label);
            progress?.Report(new AnalysisProgressEvent
            {
                ChunkLabel = chunk.Label,
                Status = ChunkStatus.Failed,
                ErrorMessage = ex.Message,
                ElapsedMs = sw.ElapsedMilliseconds
            });
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<string> CollectStreamAsync(CobolChunk chunk, string prompt, CancellationToken ct)
    {
        var sb = new StringBuilder();
        try
        {
            await foreach (var token in _llm.StreamCompletionAsync(prompt, ct))
                sb.Append(token);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Stream connection dropped for chunk {Label}", chunk.Label);
        }
        return sb.ToString();
    }

    private List<ExtractedRule> ParseAndRepairJson(string rawJson, CobolChunk chunk)
    {
        var trimmed = rawJson.Trim();

        // Strip <think>...</think> blocks (qwen3 models)
        trimmed = System.Text.RegularExpressions.Regex.Replace(
            trimmed, @"<think>[\s\S]*?</think>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        // Strip any markdown fences the model might have added
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0) trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```")) trimmed = trimmed[..^3];
            trimmed = trimmed.Trim();
        }

        trimmed = CloseBrackets(trimmed);

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (!doc.RootElement.TryGetProperty("rules", out var rulesEl))
                return [];

            var rules = new List<ExtractedRule>();
            foreach (var el in rulesEl.EnumerateArray())
            {
                var rule = new ExtractedRule
                {
                    Id = el.TryGetString("id") ?? Guid.NewGuid().ToString(),
                    Label = el.TryGetString("label") ?? "Unknown Rule",
                    Description = el.TryGetString("description") ?? string.Empty,
                    CobolReference = el.TryGetString("source_reference") ?? el.TryGetString("cobol_reference") ?? chunk.Label,
                    MigrationNotes = el.TryGetString("migration_notes") ?? string.Empty,
                    Type = MapRuleType(el.TryGetString("type") ?? "BusinessRule"),
                    Confidence = Enum.TryParse<ConfidenceLevel>(
                        el.TryGetString("confidence") ?? "Medium",
                        out var c) ? c : ConfidenceLevel.Medium,
                    Risk = el.TryGetString("risk") ?? "low",
                    CodeSnippet = el.TryGetString("code_snippet") ?? string.Empty,
                    CobolOrigin = el.TryGetBool("cobol_origin"),
                    Notes = el.TryGetString("notes") ?? string.Empty
                };
                if (rule.Label.Trim().Length > 0)
                    rules.Add(rule);
            }
            return rules;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON parse failed for chunk {Label} after repair attempt", chunk.Label);
            return [];
        }
    }

    private static string CloseBrackets(string json)
    {
        // Strip trailing commas before closing brackets
        json = StripTrailingCommas(json);

        var stack = new Stack<char>();
        bool inString = false;
        bool escaped = false;
        int lastCompleteOuterClose = -1;
        int depth = 0;

        for (int i = 0; i < json.Length; i++)
        {
            var ch = json[i];
            if (escaped) { escaped = false; continue; }
            if (ch == '\\' && inString) { escaped = true; continue; }
            if (ch == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (ch == '{') { stack.Push('}'); depth++; }
            else if (ch == '[') { stack.Push(']'); depth++; }
            else if (ch == '}' || ch == ']')
            {
                if (stack.Count > 0 && stack.Peek() == ch)
                {
                    stack.Pop();
                    depth--;
                    if (depth <= 1) lastCompleteOuterClose = i;
                }
            }
        }

        if (stack.Count == 0)
            return json;

        // Try simple bracket closing first
        var sb = new StringBuilder(json);
        while (stack.Count > 0) sb.Append(stack.Pop());
        var repaired = sb.ToString();

        // Validate the simple repair
        try
        {
            using var doc = JsonDocument.Parse(repaired);
            return repaired;
        }
        catch (JsonException)
        {
            // Simple closing failed — truncate to last complete object
            if (lastCompleteOuterClose > 0)
            {
                var truncated = json[..(lastCompleteOuterClose + 1)];
                // Close any remaining brackets
                return CloseBracketsSimple(truncated);
            }
            return repaired;
        }
    }

    private static string CloseBracketsSimple(string json)
    {
        var stack = new Stack<char>();
        bool inString = false;
        bool escaped = false;

        foreach (var ch in json)
        {
            if (escaped) { escaped = false; continue; }
            if (ch == '\\' && inString) { escaped = true; continue; }
            if (ch == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (ch == '{') stack.Push('}');
            else if (ch == '[') stack.Push(']');
            else if ((ch == '}' || ch == ']') && stack.Count > 0 && stack.Peek() == ch)
                stack.Pop();
        }

        var sb = new StringBuilder(json);
        while (stack.Count > 0) sb.Append(stack.Pop());
        return sb.ToString();
    }

    private static string StripTrailingCommas(string json)
    {
        var sb = new StringBuilder(json.Length);
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            var ch = json[i];
            if (escaped) { escaped = false; sb.Append(ch); continue; }
            if (ch == '\\' && inString) { escaped = true; sb.Append(ch); continue; }
            if (ch == '"') { inString = !inString; sb.Append(ch); continue; }
            if (inString) { sb.Append(ch); continue; }

            if (ch == ',')
            {
                // Look ahead for closing bracket (skip whitespace)
                int j = i + 1;
                while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                if (j < json.Length && (json[j] == '}' || json[j] == ']'))
                    continue; // skip this comma
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static List<ExtractedRule> Deduplicate(List<ExtractedRule> rules)
    {
        var labelAppearances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: broad dedup at 0.80 threshold using SequenceMatcher-style LCS ratio
        var pass1 = DeduplicatePass(rules, 0.80, sameTypeOnly: false, labelAppearances);

        // Pass 2: category-aware dedup at 0.72 threshold (same RuleType only)
        var pass2 = DeduplicatePass(pass1, 0.72, sameTypeOnly: true, labelAppearances);

        return pass2;
    }

    private static List<ExtractedRule> DeduplicatePass(
        List<ExtractedRule> rules,
        double threshold,
        bool sameTypeOnly,
        Dictionary<string, int> labelAppearances)
    {
        var kept = new List<ExtractedRule>();

        foreach (var rule in rules)
        {
            bool isDuplicate = false;
            foreach (var existing in kept)
            {
                if (sameTypeOnly && existing.Type != rule.Type)
                    continue;

                var similarity = SequenceMatcherRatio(
                    NormaliseText(rule.Description),
                    NormaliseText(existing.Description));

                if (similarity >= threshold)
                {
                    isDuplicate = true;
                    var key = existing.Id;
                    labelAppearances[key] = labelAppearances.GetValueOrDefault(key) + 1;
                    if (labelAppearances[key] >= CrossCuttingThreshold - 1)
                        existing.IsCrossCutting = true;
                    break;
                }
            }
            if (!isDuplicate)
            {
                if (!labelAppearances.ContainsKey(rule.Id))
                    labelAppearances[rule.Id] = 1;
                kept.Add(rule);
            }
        }

        return kept;
    }

    private static double SequenceMatcherRatio(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;

        int lcs = LongestCommonSubsequenceLength(a, b);
        return 2.0 * lcs / (a.Length + b.Length);
    }

    private static int LongestCommonSubsequenceLength(string a, string b)
    {
        int m = a.Length, n = b.Length;
        var prev = new int[n + 1];
        var curr = new int[n + 1];

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                curr[j] = a[i - 1] == b[j - 1]
                    ? prev[j - 1] + 1
                    : Math.Max(prev[j], curr[j - 1]);
            }
            (prev, curr) = (curr, prev);
            Array.Clear(curr, 0, n + 1);
        }

        return prev[n];
    }

    private static RuleType MapRuleType(string raw)
    {
        var key = raw.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");
        return key switch
        {
            "businessrule"       => RuleType.BusinessRule,
            "calculation"        => RuleType.Calculation,
            "validation"         => RuleType.Validation,
            "datatransform"      => RuleType.DataTransformation,
            "datatransformation" => RuleType.DataTransformation,
            "hardcodedvalue"     => RuleType.HardcodedValue,
            "workflow"           => RuleType.Workflow,
            "cobolartifact"      => RuleType.CobolArtifact,
            "constraint"         => RuleType.Constraint,
            "errorhandling"      => RuleType.ErrorHandling,
            "datamapping"        => RuleType.DataMapping,
            "controlflow"        => RuleType.ControlFlow,
            _ => Enum.TryParse<RuleType>(raw.Replace(" ", ""), ignoreCase: true, out var t)
                 ? t : RuleType.BusinessRule
        };
    }

    private static string NormaliseText(string text)
    {
        static bool IsStopWord(string w) => w is "the" or "a" or "an" or "is" or "are"
            or "was" or "were" or "it" or "its" or "in" or "on" or "at" or "to"
            or "for" or "and" or "or" or "but" or "of" or "with" or "from";

        return string.Join(" ", text
            .ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .Aggregate(new System.Text.StringBuilder(), (sb, c) => sb.Append(c))
            .ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !IsStopWord(w)));
    }
}

file static class JsonElementExtensions
{
    public static string? TryGetString(this JsonElement el, string propertyName)
    {
        return el.TryGetProperty(propertyName, out var prop) ? prop.GetString() : null;
    }

    public static bool TryGetBool(this JsonElement el, string propertyName)
    {
        return el.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.True;
    }
}
