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
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var allRules = new ConcurrentBag<ExtractedRule>();
        var sem = new SemaphoreSlim(MaxConcurrency);
        var activeTemplate = _templates.GetActive();

        var tasks = chunks.Select(chunk => ProcessChunkAsync(
            chunk, sem, allRules, progress, sw, activeTemplate, cancellationToken)).ToList();

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
            var prompt = PromptBuilder.BuildExtractionPrompt(chunk, hints, activeTemplate);

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
                    Type = Enum.TryParse<RuleType>(
                        (el.TryGetString("type") ?? "BusinessRule").Replace(" ", ""),
                        out var t) ? t : RuleType.BusinessRule,
                    Confidence = Enum.TryParse<ConfidenceLevel>(
                        el.TryGetString("confidence") ?? "Medium",
                        out var c) ? c : ConfidenceLevel.Medium
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
            else if (ch == '}' || ch == ']')
            {
                if (stack.Count > 0 && stack.Peek() == ch)
                    stack.Pop();
            }
        }

        var sb = new StringBuilder(json);
        while (stack.Count > 0) sb.Append(stack.Pop());
        return sb.ToString();
    }

    private static List<ExtractedRule> Deduplicate(List<ExtractedRule> rules)
    {
        var kept = new List<ExtractedRule>();
        var labelAppearances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            bool isDuplicate = false;
            foreach (var existing in kept)
            {
                var similarity = JaccardSimilarity(
                    NormaliseText(rule.Description),
                    NormaliseText(existing.Description));

                if (similarity >= DeduplicationThreshold)
                {
                    isDuplicate = true;
                    // Track cross-cutting appearances on the surviving rule
                    var key = existing.Id;
                    labelAppearances[key] = labelAppearances.GetValueOrDefault(key) + 1;
                    if (labelAppearances[key] >= CrossCuttingThreshold - 1)
                        existing.IsCrossCutting = true;
                    break;
                }
            }
            if (!isDuplicate)
            {
                labelAppearances[rule.Id] = 1;
                kept.Add(rule);
            }
        }

        return kept;
    }

    private static double JaccardSimilarity(string a, string b)
    {
        var setA = new HashSet<string>(a.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var setB = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (setA.Count == 0 && setB.Count == 0) return 1.0;
        var intersection = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();
        return union == 0 ? 0 : (double)intersection / union;
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
}
