namespace CobolAnalyst.Web.Models;

/// <summary>Why an extracted rule was classified as a false positive.</summary>
public enum FpPattern
{
    TrivialAssignment,
    InfrastructureBoilerplate,
    LowConfidence,
    OverlyGeneric,
    Unclassified
}

/// <summary>Why a ground truth rule was missed (false negative).</summary>
public enum FnCause
{
    ShortDescription,
    ComplexLogic,
    TypeAbsent,
    NoEvidence
}

/// <summary>Precision / Recall / F1 for a single rule type category.</summary>
public sealed class CategoryMetrics
{
    public string Category { get; set; } = string.Empty;
    public int TruePositives { get; set; }
    public int FalsePositives { get; set; }
    public int FalseNegatives { get; set; }
    public double Precision => TruePositives + FalsePositives == 0 ? 0 : (double)TruePositives / (TruePositives + FalsePositives);
    public double Recall    => TruePositives + FalseNegatives == 0 ? 0 : (double)TruePositives / (TruePositives + FalseNegatives);
    public double F1        => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);
}

/// <summary>An extracted rule matched to a ground truth rule.</summary>
public sealed class MatchedRule
{
    public ExtractedRule Extracted { get; set; } = new();
    public GroundTruthRule GroundTruth { get; set; } = new();
    public double Similarity { get; set; }
}

/// <summary>An extracted rule that could not be matched to any ground truth entry.</summary>
public sealed class FalsePositiveResult
{
    public ExtractedRule Rule { get; set; } = new();
    public FpPattern Pattern { get; set; }
    public string PatternReason { get; set; } = string.Empty;
}

/// <summary>A ground truth rule that was not found among the extracted rules.</summary>
public sealed class FalseNegativeResult
{
    public GroundTruthRule Rule { get; set; } = new();
    public FnCause Cause { get; set; }
    public string CauseReason { get; set; } = string.Empty;
}

/// <summary>Full validation report comparing extracted rules against a ground truth set.</summary>
public sealed class ValidationReport
{
    public string SessionId { get; set; } = string.Empty;
    public string SessionName { get; set; } = string.Empty;
    public DateTime RunAt { get; set; } = DateTime.UtcNow;
    public double Threshold { get; set; }

    public List<MatchedRule> TruePositives { get; set; } = [];
    public List<FalsePositiveResult> FalsePositives { get; set; } = [];
    public List<FalseNegativeResult> FalseNegatives { get; set; } = [];

    public int TpCount => TruePositives.Count;
    public int FpCount => FalsePositives.Count;
    public int FnCount => FalseNegatives.Count;

    public double Precision => TpCount + FpCount == 0 ? 0 : (double)TpCount / (TpCount + FpCount);
    public double Recall    => TpCount + FnCount == 0 ? 0 : (double)TpCount / (TpCount + FnCount);
    public double F1        => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);

    public List<CategoryMetrics> ByCategory { get; set; } = [];

    public List<string> GuidanceItems { get; set; } = [];
}
