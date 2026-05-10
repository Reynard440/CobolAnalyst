using CobolAnalyst.Web.Models;

namespace CobolAnalyst.Web.Core.Validation;

/// <summary>
/// Compares extracted rules against a ground truth set using LCS-based character similarity.
///
/// SIMILARITY(a, b) = 2.0 * lcsLength / (a.Length + b.Length)
/// where a and b are the normalised (lower-case, alphanumeric + spaces) concatenations of
/// the label/description (extracted) and ruleText (ground truth).
///
/// An additional +0.05 bonus is awarded when the extracted rule's type matches the ground
/// truth category (case-insensitive), capped at 1.0.
///
/// Default threshold: 0.40.
/// </summary>
public sealed class ValidationService
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs validation and returns the full <see cref="ValidationResult"/>.
    /// </summary>
    /// <param name="extractedRules">Rules produced by AnalysisOrchestrator.</param>
    /// <param name="groundTruth">Hand-verified reference rules.</param>
    /// <param name="threshold">Minimum LCS similarity to count as a true positive.</param>
    /// <param name="sessionName">Display name recorded in the result.</param>
    /// <param name="modelName">Ollama model tag recorded in the result.</param>
    /// <param name="sessionId">Session ID recorded in the result.</param>
    /// <param name="templateId">Active prompt template ID recorded in the result.</param>
    public ValidationResult RunValidation(
        List<ExtractedRule>    extractedRules,
        List<GroundTruthRule>  groundTruth,
        float                  threshold   = 0.40f,
        string                 sessionName = "",
        string                 modelName   = "",
        string                 sessionId   = "",
        string                 templateId  = "")
    {
        var result = new ValidationResult
        {
            SessionId        = sessionId,
            SessionName      = sessionName,
            ModelName        = modelName,
            TemplateId       = templateId,
            Threshold        = threshold,
            GroundTruthCount = groundTruth.Count
        };

        var matchedGtIds        = new HashSet<string>();
        var matchedExtractedIds = new HashSet<string>();

        // ── Greedy best-match ─────────────────────────────────────────────────
        // For each extracted rule find the best un-claimed GT rule above threshold.
        foreach (var ext in extractedRules)
        {
            float         bestSim = 0f;
            GroundTruthRule? bestGt = null;

            foreach (var gt in groundTruth)
            {
                if (matchedGtIds.Contains(gt.Id)) continue;
                float sim = ComputeSimilarity(ext, gt);
                if (sim > bestSim) { bestSim = sim; bestGt = gt; }
            }

            if (bestGt is not null && bestSim >= threshold)
            {
                result.TruePositives.Add(new MatchedPair
                {
                    Extracted   = ext,
                    GroundTruth = bestGt,
                    Similarity  = bestSim
                });
                matchedGtIds.Add(bestGt.Id);
                matchedExtractedIds.Add(ext.Id);
            }
        }

        // ── False positives ───────────────────────────────────────────────────
        foreach (var ext in extractedRules.Where(e => !matchedExtractedIds.Contains(e.Id)))
        {
            result.FalsePositives.Add(new FalsePositive
            {
                Rule   = ext,
                Reason = DiagnoseFP(ext, groundTruth, threshold)
            });
        }

        // ── False negatives ───────────────────────────────────────────────────
        foreach (var gt in groundTruth.Where(g => !matchedGtIds.Contains(g.Id)))
        {
            result.FalseNegatives.Add(new FalseNegative
            {
                Rule   = gt,
                Reason = DiagnoseFN(gt, extractedRules)
            });
        }

        // ── Per-category metrics ──────────────────────────────────────────────
        var categories = extractedRules.Select(r => r.Type.ToString())
            .Concat(groundTruth.Select(r => r.Category))
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c);

        foreach (var cat in categories)
        {
            int tp = result.TruePositives.Count(m =>
                m.Extracted.Type.ToString().Equals(cat, StringComparison.OrdinalIgnoreCase) ||
                m.GroundTruth.Category.Equals(cat, StringComparison.OrdinalIgnoreCase));
            int fp = result.FalsePositives.Count(f =>
                f.Rule.Type.ToString().Equals(cat, StringComparison.OrdinalIgnoreCase));
            int fn = result.FalseNegatives.Count(f =>
                f.Rule.Category.Equals(cat, StringComparison.OrdinalIgnoreCase));

            if (tp + fp + fn == 0) continue;

            result.ByCategory.Add(new CategoryResult
            {
                Category       = cat,
                TruePositives  = tp,
                FalsePositives = fp,
                FalseNegatives = fn
            });
        }

        result.GuidanceItems = BuildGuidance(result);
        return result;
    }

    // ── LCS similarity ────────────────────────────────────────────────────────

    /// <summary>
    /// SIMILARITY(a,b) = 2.0 * lcsLength / (a.Length + b.Length)
    /// Applied to normalised (lower-case, alphanumeric + single spaces) strings.
    /// +0.05 category bonus, capped at 1.0.
    /// </summary>
    private static float ComputeSimilarity(ExtractedRule ext, GroundTruthRule gt)
    {
        var a = Normalise($"{ext.Label} {ext.Description}");
        var b = Normalise(gt.RuleText);

        if (a.Length + b.Length == 0) return 0f;

        int lcs = LcsLength(a, b);
        float sim = 2.0f * lcs / (a.Length + b.Length);

        // Category bonus
        if (!string.IsNullOrEmpty(gt.Category) &&
            ext.Type.ToString().Equals(gt.Category, StringComparison.OrdinalIgnoreCase))
            sim += 0.05f;

        return Math.Min(1.0f, sim);
    }

    /// <summary>
    /// Normalises a string to lower-case letters and digits with collapsed single spaces.
    /// </summary>
    private static string Normalise(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
            else if (char.IsWhiteSpace(c) && sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
        }
        // Trim trailing space
        if (sb.Length > 0 && sb[^1] == ' ')
            sb.Length--;
        return sb.ToString();
    }

    /// <summary>
    /// Rolling-array LCS length in O(m·n) time, O(n) space.
    /// </summary>
    private static int LcsLength(string a, string b)
    {
        int m = a.Length, n = b.Length;
        if (m == 0 || n == 0) return 0;

        var prev = new int[n + 1];
        var curr = new int[n + 1];

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
                curr[j] = a[i - 1] == b[j - 1]
                    ? prev[j - 1] + 1
                    : Math.Max(prev[j], curr[j - 1]);

            (prev, curr) = (curr, prev);
            Array.Clear(curr, 0, n + 1);
        }

        return prev[n];
    }

    // ── Diagnostic helpers ────────────────────────────────────────────────────

    private static string DiagnoseFP(
        ExtractedRule          ext,
        List<GroundTruthRule>  groundTruth,
        float                  threshold)
    {
        var desc = ext.Description.ToLowerInvariant();

        if (ext.Confidence == ConfidenceLevel.Low)
            return "LLM-reported confidence is Low — rule may be speculative.";

        if (desc.Length < 40)
            return "Description is very short, suggesting a trivial statement with no GT match.";

        if (desc.Contains("display") || desc.Contains("print") || desc.Contains("filler") ||
            desc.Contains("trace")   || desc.Contains("log"))
            return "Describes infrastructure output or structural padding; no business GT rule exists.";

        // Find best partial match to give context
        float best = groundTruth.Max(gt => ComputeSimilarity(ext, gt));
        return best < 0.15f
            ? "No ground truth rule has any meaningful similarity; the extraction may be spurious."
            : $"Best GT similarity was {best:F2}, below the threshold of {threshold:F2}.";
    }

    private static string DiagnoseFN(
        GroundTruthRule      gt,
        List<ExtractedRule>  extractedRules)
    {
        bool typePresent = extractedRules.Any(e =>
            e.Type.ToString().Equals(gt.Category, StringComparison.OrdinalIgnoreCase));

        if (!typePresent)
            return $"No extracted rule of category '{gt.Category}' was found — the prompt may not surface this category.";

        if (gt.RuleText.Split(' ').Length < 8)
            return "Ground truth rule text is very short; the LLM had little signal to match against.";

        var text = gt.RuleText.ToLowerInvariant();
        if (text.Contains("nested") || text.Contains("recursive") ||
            text.Contains("loop")   || text.Contains("iteration"))
            return "Rule involves complex iterative or nested logic the LLM may have collapsed or missed.";

        float best = extractedRules.Max(e =>
        {
            var a = Normalise($"{e.Label} {e.Description}");
            var b = Normalise(gt.RuleText);
            if (a.Length + b.Length == 0) return 0f;
            return 2.0f * LcsLength(a, b) / (a.Length + b.Length);
        });

        return best < 0.10f
            ? "No extracted rule overlaps meaningfully; the source chunk may have been skipped or truncated."
            : $"Best extraction similarity was {best:F2} — partially detected but below threshold.";
    }

    // ── Guidance builder ──────────────────────────────────────────────────────

    private static List<string> BuildGuidance(ValidationResult r)
    {
        var items = new List<string>();

        if (r.Recall < 0.6f)
            items.Add($"Recall is low ({r.Recall:P0}). Consider lowering the complexity threshold or adding more source files to the session.");

        if (r.Precision < 0.6f)
            items.Add($"Precision is low ({r.Precision:P0}). Review the suppression list in Workshop — infrastructure boilerplate may be leaking through.");

        var lowConfFp = r.FalsePositives.Count(f =>
            f.Reason.Contains("Low", StringComparison.OrdinalIgnoreCase));
        if (lowConfFp >= 2)
            items.Add($"{lowConfFp} FPs are low-confidence extractions. Add a prompt instruction in Workshop to omit rules where confidence is Low.");

        var missingCats = r.FalseNegatives
            .Where(f => f.Reason.Contains("category", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Rule.Category)
            .Distinct()
            .ToList();
        if (missingCats.Count > 0)
            items.Add($"Category '{string.Join(", ", missingCats)}' never appears in extracted rules. Add a worked example for that category in Workshop.");

        var worstCat = r.ByCategory.OrderBy(c => c.F1).FirstOrDefault();
        if (worstCat is not null && worstCat.F1 < 0.5f)
            items.Add($"Category '{worstCat.Category}' has the lowest F1 ({worstCat.F1:P0}). Target it first when tuning prompts.");

        if (items.Count == 0)
            items.Add("Results look good. Continue iterating to push F1 above 0.85 for all categories.");

        return items;
    }
}
