using System.Text;
using System.Text.Json;
using CobolAnalyst.Web.Core.Llm;
using CobolAnalyst.Web.Models;
using DocumentFormat.OpenXml.Packaging;

namespace CobolAnalyst.Web.Core.Validation;

/// <summary>
/// Parses a ground truth file (.txt / .docx / .doc) into a list of <see cref="GroundTruthRule"/> objects.
///
/// Preferred format (.txt):
///   # Comment lines start with #
///   GT-001 | Calculation | Hours worked beyond 40 per week are paid at 1.5× rate.
///   GT-002 | Validation  | Employee ID must be numeric and exactly 6 digits.
///
/// Pipe-delimited rows with at least three segments (ID | Category | RuleText) are parsed
/// directly.  Any file that contains no such rows falls back to an LLM extraction pass.
/// </summary>
public sealed class GroundTruthParser
{
    private readonly ILlmClient _llm;
    private readonly ILogger<GroundTruthParser> _logger;

    public GroundTruthParser(ILlmClient llm, ILogger<GroundTruthParser> logger)
    {
        _llm    = llm;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses <paramref name="stream"/> (named <paramref name="fileName"/>) and returns
    /// all ground truth rules found.  Uses the LLM as a fallback for unstructured text.
    /// </summary>
    public async Task<List<GroundTruthRule>> ParseAsync(Stream stream, string fileName)
    {
        var ext  = Path.GetExtension(fileName).ToLowerInvariant();
        var text = ext switch
        {
            ".docx" => ExtractDocx(stream),
            ".doc"  => ExtractDoc(stream),
            _       => await new StreamReader(stream, Encoding.UTF8).ReadToEndAsync()
        };

        var structured = TryParseStructured(text);
        if (structured.Count > 0)
        {
            _logger.LogInformation(
                "GroundTruthParser: parsed {Count} structured rules from '{File}'",
                structured.Count, fileName);
            return structured;
        }

        _logger.LogInformation(
            "GroundTruthParser: no structured rows in '{File}' — falling back to LLM extraction",
            fileName);
        return await ParseWithLlmAsync(text);
    }

    // ── Structured parser: ID | Category | Rule text ──────────────────────────

    private static List<GroundTruthRule> TryParseStructured(string text)
    {
        var rules = new List<GroundTruthRule>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

            var parts = line.Split('|');
            if (parts.Length < 3) continue;

            var id       = parts[0].Trim();
            var category = parts[1].Trim();
            // Allow literal pipe characters inside the rule text by joining extra segments
            var ruleText = string.Join('|', parts[2..]).Trim();

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(ruleText)) continue;

            rules.Add(new GroundTruthRule
            {
                Id       = id,
                Category = category,
                RuleText = ruleText
            });
        }

        return rules;
    }

    // ── Document text extractors ──────────────────────────────────────────────

    private static string ExtractDocx(Stream stream)
    {
        using var doc  = WordprocessingDocument.Open(stream, false);
        var       body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return string.Empty;

        var sb = new StringBuilder();
        foreach (var para in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
        {
            var line = para.InnerText.Trim();
            if (!string.IsNullOrEmpty(line))
                sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private static string ExtractDoc(Stream stream)
    {
        // Legacy binary .doc format requires the HWPF library.
        // Attempt a plain-text scan for pipe-delimited rows; LLM fallback handles the rest.
        using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false);
        var raw = reader.ReadToEnd();
        // Keep only printable ASCII to strip binary noise
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (c == '\n' || c == '\r' || (c >= 0x20 && c < 0x7F))
                sb.Append(c);
        }
        return sb.ToString();
    }

    // ── LLM fallback ──────────────────────────────────────────────────────────

    private async Task<List<GroundTruthRule>> ParseWithLlmAsync(string text)
    {
        const string LlmPrompt =
            "You are a COBOL business-rule analyst.\n" +
            "The following text contains business rules in unstructured prose.\n" +
            "Extract every business rule you can identify.\n\n" +
            "Return ONLY a JSON array — no markdown, no explanations:\n" +
            "[\n" +
            "  {\"id\":\"GT-001\",\"category\":\"Calculation\",\"ruleText\":\"…\"},\n" +
            "  …\n" +
            "]\n\n" +
            "Valid categories: Calculation, Validation, WorkflowControl, DataMapping, Constraint, BusinessRule\n\n" +
            "TEXT:\n";

        var prompt = LlmPrompt + text;
        var sb     = new StringBuilder();

        await foreach (var token in _llm.StreamCompletionAsync(prompt))
            sb.Append(token);

        return ParseLlmJson(sb.ToString());
    }

    private List<GroundTruthRule> ParseLlmJson(string raw)
    {
        try
        {
            var start = raw.IndexOf('[');
            var end   = raw.LastIndexOf(']');
            if (start < 0 || end < start) return [];

            var json = raw[start..(end + 1)];

            var arr = JsonSerializer.Deserialize<List<LlmGtRule>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return arr?.Select(r => new GroundTruthRule
            {
                Id       = string.IsNullOrWhiteSpace(r.Id) ? Guid.NewGuid().ToString() : r.Id,
                Category = r.Category,
                RuleText = r.RuleText
            }).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GroundTruthParser: LLM JSON parse failed");
            return [];
        }
    }

    // ── Local DTO for LLM response ────────────────────────────────────────────

    private sealed class LlmGtRule
    {
        public string Id { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string RuleText { get; set; } = string.Empty;
    }
}
