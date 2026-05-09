using CobolAnalyst.Web.Models;
using CobolAnalyst.Web.Core.Llm;

namespace CobolAnalyst.Web.Core.Validation;

/// <summary>
/// Compares extracted rules against a ground truth set to produce a ValidationReport.
/// Uses Jaccard content-word similarity for matching.
/// </summary>
public sealed class ValidationService
{
    /// <summary>Runs validation and returns the full report.</summary>
    public ValidationReport Validate(
        AnalysisSession session,
        GroundTruthSet groundTruth,
        double threshold = 0.40)
    {
        var report = new ValidationReport
        {
            SessionId   = session.Id,
            SessionName = session.Name,
            Threshold   = threshold
        };

        var extracted = session.Rules.ToList();
        var gtRules   = groundTruth.Rules.ToList();

        var matchedGtIds        = new HashSet<string>();
        var matchedExtractedIds = new HashSet<string>();

        // Greedy best-match: for each extracted rule find best GT match above threshold
        foreach (var ext in extracted)
        {
            var extWords = PromptBuilder.ContentWords($"{ext.Label} {ext.Description}");
            double bestSim  = 0;
            GroundTruthRule? bestGt = null;

            foreach (var gt in gtRules)
            {
                if (matchedGtIds.Contains(gt.Id)) continue;
                var gtWords = PromptBuilder.ContentWords($"{gt.Label} {gt.Description}");
                double sim  = Jaccard(extWords, gtWords);
                if (sim > bestSim)
                {
                    bestSim = sim;
                    bestGt  = gt;
                }
            }

            if (bestGt is not null && bestSim >= threshold)
            {
                report.TruePositives.Add(new MatchedRule
                {
                    Extracted   = ext,
                    GroundTruth = bestGt,
                    Similarity  = bestSim
                });
                matchedGtIds.Add(bestGt.Id);
                matchedExtractedIds.Add(ext.Id);
            }
        }

        // False positives: extracted rules not matched to any GT rule
        foreach (var ext in extracted.Where(e => !matchedExtractedIds.Contains(e.Id)))
        {
            var (pattern, reason) = ClassifyFp(ext);
            report.FalsePositives.Add(new FalsePositiveResult
            {
                Rule          = ext,
                Pattern       = pattern,
                PatternReason = reason
            });
        }

        // False negatives: GT rules not matched by any extracted rule
        foreach (var gt in gtRules.Where(g => !matchedGtIds.Contains(g.Id)))
        {
            var (cause, reason) = DiagnoseFn(gt, extracted);
            report.FalseNegatives.Add(new FalseNegativeResult
            {
                Rule        = gt,
                Cause       = cause,
                CauseReason = reason
            });
        }

        // Per-category metrics
        var allTypes = extracted.Select(r => r.Type.ToString())
            .Concat(gtRules.Select(r => r.Type))
            .Distinct()
            .OrderBy(t => t);

        foreach (var cat in allTypes)
        {
            var tp = report.TruePositives.Count(m => m.Extracted.Type.ToString() == cat || m.GroundTruth.Type == cat);
            var fp = report.FalsePositives.Count(f => f.Rule.Type.ToString() == cat);
            var fn = report.FalseNegatives.Count(f => f.Rule.Type == cat);
            if (tp + fp + fn == 0) continue;
            report.ByCategory.Add(new CategoryMetrics
            {
                Category       = cat,
                TruePositives  = tp,
                FalsePositives = fp,
                FalseNegatives = fn
            });
        }

        report.GuidanceItems = BuildGuidance(report);
        return report;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 0;
        int intersection = a.Count(w => b.Contains(w));
        int union        = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static (FpPattern pattern, string reason) ClassifyFp(ExtractedRule rule)
    {
        var desc = rule.Description.ToLowerInvariant();

        if (rule.Confidence == ConfidenceLevel.Low)
            return (FpPattern.LowConfidence, "LLM-reported confidence is Low; rule may be speculative.");

        if (desc.Length < 40)
            return (FpPattern.TrivialAssignment, "Description is very short, suggesting a trivial assignment or constant.");

        if (desc.Contains("display") || desc.Contains("print") || desc.Contains("log") ||
            desc.Contains("trace")   || desc.Contains("filler"))
            return (FpPattern.InfrastructureBoilerplate, "Describes infrastructure output or structural padding with no business logic.");

        if (!desc.Contains("if") && !desc.Contains("when") && !desc.Contains("calculat") &&
            !desc.Contains("valid") && !desc.Contains("check") && !desc.Contains("comput"))
            return (FpPattern.OverlyGeneric, "Description lacks specific business vocabulary; rule may be too generic.");

        return (FpPattern.Unclassified, "No clear FP pattern detected; manual review recommended.");
    }

    private static (FnCause cause, string reason) DiagnoseFn(GroundTruthRule gt, List<ExtractedRule> extracted)
    {
        var gtWords = PromptBuilder.ContentWords($"{gt.Label} {gt.Description}");

        // Check if type exists at all in extracted
        bool typePresent = extracted.Any(e => e.Type.ToString().Equals(gt.Type, StringComparison.OrdinalIgnoreCase));
        if (!typePresent)
            return (FnCause.TypeAbsent, $"No extracted rule of type '{gt.Type}' was found; the prompt may not surface this category.");

        if (gt.Description.Split(' ').Length < 8)
            return (FnCause.ShortDescription, "Ground truth description is short; the LLM had little signal to match against.");

        // If GT description mentions complex constructs
        var desc = gt.Description.ToLowerInvariant();
        if (desc.Contains("nested") || desc.Contains("recursive") || desc.Contains("loop") ||
            desc.Contains("accumulate") || desc.Contains("iteration"))
            return (FnCause.ComplexLogic, "Ground truth rule involves complex iterative or nested logic the LLM may have collapsed.");

        // Find the best partial match to check if evidence exists
        double best = extracted.Max(e =>
        {
            var ew = PromptBuilder.ContentWords($"{e.Label} {e.Description}");
            return Jaccard(gtWords, ew);
        });

        if (best < 0.10)
            return (FnCause.NoEvidence, "No extracted rule has any meaningful word overlap; the source chunk may have been skipped or truncated.");

        return (FnCause.ComplexLogic, "Rule was partially detected but fell below the similarity threshold.");
    }

    private static List<string> BuildGuidance(ValidationReport r)
    {
        var items = new List<string>();

        if (r.Recall < 0.6)
            items.Add($"Recall is low ({r.Recall:P0}). Consider lowering the complexity threshold or adding more source files to the session.");

        if (r.Precision < 0.6)
            items.Add($"Precision is low ({r.Precision:P0}). Review the suppression list in Workshop — infrastructure boilerplate may be leaking through.");

        var lowConfFp = r.FalsePositives.Count(f => f.Pattern == FpPattern.LowConfidence);
        if (lowConfFp >= 2)
            items.Add($"{lowConfFp} FPs are Low-confidence extractions. Add a prompt instruction to omit rules where confidence is Low.");

        var typeAbsent = r.FalseNegatives.Where(f => f.Cause == FnCause.TypeAbsent)
                                         .Select(f => f.Rule.Type).Distinct().ToList();
        if (typeAbsent.Count > 0)
            items.Add($"Type(s) {string.Join(", ", typeAbsent)} never appear in extracted rules. Add a worked example for these types in Workshop.");

        var worstCat = r.ByCategory.OrderBy(c => c.F1).FirstOrDefault();
        if (worstCat is not null && worstCat.F1 < 0.5)
            items.Add($"Category '{worstCat.Category}' has the lowest F1 ({worstCat.F1:P0}). Target this category first when tuning prompts.");

        if (items.Count == 0)
            items.Add("Results look good. Continue iterating to push F1 above 0.85 for all categories.");

        return items;
    }
}
