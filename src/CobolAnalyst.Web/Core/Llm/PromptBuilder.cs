using CobolAnalyst.Web.Models;
using CobolAnalyst.Web.Core.Prompts;

namespace CobolAnalyst.Web.Core.Llm;

/// <summary>Builds extraction prompts for legacy source analysis (COBOL and Visual Basic).</summary>
public static class PromptBuilder
{
    private static readonly string[] VbExtensions = [".vb", ".bas", ".cls", ".frm", ".vbs"];

    // ── COBOL worked example ──────────────────────────────────────────────────
    private const string CobolExampleSource = """
        COMPUTE-OVERTIME.
            IF WS-HOURS-WORKED > 40
                COMPUTE WS-OT-HOURS = WS-HOURS-WORKED - 40
                COMPUTE WS-OT-PAY = WS-OT-HOURS * WS-HOURLY-RATE * 1.5
            ELSE
                MOVE ZERO TO WS-OT-PAY
            END-IF.
        """;

    private const string CobolExampleJson = """
        {
          "rules": [
            {
              "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
              "label": "Overtime Pay Threshold Calculation",
              "type": "Calculation",
              "description": "Hours worked beyond 40 per week are paid at 1.5x the standard hourly rate. Hours at or below 40 receive no overtime supplement. The overtime amount is computed separately and added to regular pay downstream.",
              "source_reference": "COMPUTE-OVERTIME, lines 1-7",
              "confidence": "High",
              "migration_notes": "Ensure decimal precision is preserved; C# decimal type is appropriate. Verify the 40-hour threshold is not configurable in the original system before hardcoding."
            }
          ]
        }
        """;

    // ── VB worked example ─────────────────────────────────────────────────────
    private const string VbExampleSource = """
        Private Function ApplyOvertime(hoursWorked As Decimal, hourlyRate As Decimal) As Decimal
            Dim otPay As Decimal = 0
            If hoursWorked > 40 Then
                Dim otHours As Decimal = hoursWorked - 40
                otPay = otHours * hourlyRate * 1.5
            End If
            Return otPay
        End Function
        """;

    private const string VbExampleJson = """
        {
          "rules": [
            {
              "id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
              "label": "Overtime Pay Threshold Calculation",
              "type": "Calculation",
              "description": "Hours worked beyond 40 per week attract overtime pay at 1.5 times the hourly rate for the excess hours only. Hours at or below 40 receive no overtime supplement. The result is returned as a Decimal value.",
              "source_reference": "ApplyOvertime, lines 1-7",
              "confidence": "High",
              "migration_notes": "Use C# decimal arithmetic. The 40-hour threshold should be externalised to configuration if other employment categories exist."
            }
          ]
        }
        """;

    private const string OutputSchema = """
        {
          "rules": [
            {
              "id": "<uuid>",
              "label": "<short name, 8 words or fewer>",
              "type": "<BusinessRule|Validation|Calculation|DataTransformation|ControlFlow>",
              "description": "<plain English, 3 sentences or fewer>",
              "source_reference": "<method / paragraph name and line range>",
              "confidence": "<High|Medium|Low>",
              "migration_notes": "<C# migration concerns>"
            }
          ]
        }
        """;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "could",
        "should", "may", "might", "shall", "can", "of", "in", "on", "at",
        "to", "for", "and", "or", "but", "if", "then", "that", "this",
        "with", "from", "by", "as", "not", "no", "it", "its"
    };

    /// <summary>
    /// Builds the full extraction prompt for a source chunk.
    /// Automatically adapts persona, worked example, and code fence to the file's language.
    /// </summary>
    /// <param name="chunk">The chunk to analyse.</param>
    /// <param name="knowledgeHints">Up to three previously confirmed rules to inject as context.</param>
    /// <param name="template">Optional active prompt template; overrides persona/suppression/instructions when set.</param>
    public static string BuildExtractionPrompt(CobolChunk chunk, IEnumerable<KnowledgeEntry> knowledgeHints, PromptTemplate? template = null)
    {
        bool isVb = IsVbFile(chunk.FileName);
        string language     = isVb ? "Visual Basic" : "COBOL";
        string fence        = isVb ? "vb" : "cobol";
        string unitLabel    = isVb ? "Method / Class" : "Paragraph / Division";
        string exampleSrc   = isVb ? VbExampleSource : CobolExampleSource;
        string exampleJson  = isVb ? VbExampleJson   : CobolExampleJson;

        string defaultPersona = $"You are a legacy {language} modernisation analyst. Your task is to extract structured business rules from the {language} source below.";
        string persona = template?.PersonaOverride is { Length: > 0 } p ? p : defaultPersona;

        string defaultSuppress = isVb
            ? "Do NOT extract or reference: inline comments, simple variable declarations with no business meaning, empty constructors, auto-generated designer code, or Debug.Print / Console.WriteLine statements used only for tracing."
            : "Do NOT extract or reference: inline comments, FILLER fields, section/division headers that contain no logic, STOP RUN statements, or DISPLAY statements used only for debugging.";
        string suppressList = template?.SuppressionOverride is { Length: > 0 } s ? s : defaultSuppress;

        var adaptiveInstruction = chunk.Complexity switch
        {
            ComplexityTier.Low => "Be concise. Extract only the primary rule; omit trivial assignments.",
            ComplexityTier.Medium => "Describe each conditional branch. Note any side-effects on variables or state.",
            ComplexityTier.High => "Trace every execution path and flag all edge cases. Identify implicit state dependencies and flag them in migration_notes.",
            _ => "Be concise."
        };

        var hintLines = knowledgeHints.Select(h => $"  - [{h.Type}] {h.Label}: {h.Description}").ToList();
        var hintsSection = hintLines.Count > 0
            ? "Previously confirmed rules for context (do not repeat these exactly):\n" + string.Join("\n", hintLines) + "\n\n"
            : string.Empty;

        var parts = new List<string>
        {
            persona,
            string.Empty,
            $"Complexity tier: {chunk.Complexity}. {adaptiveInstruction}",
            string.Empty,
            hintsSection,
            "## Worked Example",
            string.Empty,
            $"Input {language}:",
            $"```{fence}",
            exampleSrc,
            "```",
            string.Empty,
            "Expected JSON output:",
            "```json",
            exampleJson,
            "```",
            string.Empty,
            "## Suppression List",
            string.Empty,
            suppressList,
            string.Empty,
            "## Source to Analyse",
            string.Empty,
            $"File: {chunk.FileName}",
            $"{unitLabel}: {chunk.Label}",
            $"Lines: {chunk.StartLine}-{chunk.EndLine}",
            string.Empty,
            $"```{fence}",
            chunk.SourceText,
            "```",
            string.Empty,
            template?.AdditionalInstructions is { Length: > 0 } ai ? $"## Additional Instructions\n\n{ai}\n" : string.Empty,
            "## Output Instructions",
            string.Empty,
            "Respond ONLY with a valid JSON object matching this exact schema. Do not include markdown fences or prose outside the JSON.",
            string.Empty,
            OutputSchema
        };

        return string.Join("\n", parts);
    }

    /// <summary>Builds the prompt for a natural-language query over extracted rules.</summary>
    public static string BuildQueryPrompt(string query, IEnumerable<ExtractedRule> rules)
    {
        var rulesSummary = string.Join("\n", rules.Select((r, i) =>
            $"{i + 1}. [{r.Type}] {r.Label} ({r.Confidence}) — {r.Description}"));

        return string.Join("\n",
            "You are a legacy code modernisation analyst. The following rules have been extracted from source files.",
            string.Empty,
            "## Extracted Rules",
            string.Empty,
            rulesSummary,
            string.Empty,
            "## Analyst Query",
            string.Empty,
            query,
            string.Empty,
            "Answer the query based only on the rules listed above. Be specific: reference rule labels and types by name. If no rules match, say so clearly.");
    }

    /// <summary>
    /// Returns the content words from text for knowledge base matching
    /// (lowercase, no punctuation, stop words removed).
    /// </summary>
    public static HashSet<string> ContentWords(string text)
    {
        var words = text
            .ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', '.', ',', ':', ';', '(', ')', '-', '_'],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !StopWords.Contains(w));
        return [.. words];
    }

    private static bool IsVbFile(string fileName) =>
        VbExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());
}
