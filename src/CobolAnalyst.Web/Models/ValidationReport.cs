namespace CobolAnalyst.Web.Models;

// ─── Result models ────────────────────────────────────────────────────────────

/// <summary>An extracted rule paired with the ground truth rule it matched.</summary>
public sealed class MatchedPair
{
    public ExtractedRule Extracted { get; set; } = new();
    public GroundTruthRule GroundTruth { get; set; } = new();
    public float Similarity { get; set; }
}

/// <summary>An extracted rule that could not be matched to any ground truth entry.</summary>
public sealed class FalsePositive
{
    public ExtractedRule Rule { get; set; } = new();
    public string Reason { get; set; } = string.Empty;
}

/// <summary>A ground truth rule that was not matched by any extracted rule.</summary>
public sealed class FalseNegative
{
    public GroundTruthRule Rule { get; set; } = new();
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Precision / Recall / F1 for a single rule category.</summary>
public sealed class CategoryResult
{
    public string Category { get; set; } = string.Empty;
    public int TruePositives { get; set; }
    public int FalsePositives { get; set; }
    public int FalseNegatives { get; set; }

    public float Precision => TruePositives + FalsePositives == 0
        ? 0f : (float)TruePositives / (TruePositives + FalsePositives);

    public float Recall => TruePositives + FalseNegatives == 0
        ? 0f : (float)TruePositives / (TruePositives + FalseNegatives);

    public float F1 => Precision + Recall == 0
        ? 0f : 2f * Precision * Recall / (Precision + Recall);
}

/// <summary>Full validation result for a single run.</summary>
public sealed class ValidationResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SessionId { get; set; } = string.Empty;
    public string SessionName { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public DateTime RunAt { get; set; } = DateTime.UtcNow;
    public float Threshold { get; set; }
    public int GroundTruthCount { get; set; }

    public List<MatchedPair> TruePositives { get; set; } = [];
    public List<FalsePositive> FalsePositives { get; set; } = [];
    public List<FalseNegative> FalseNegatives { get; set; } = [];
    public List<CategoryResult> ByCategory { get; set; } = [];
    public List<string> GuidanceItems { get; set; } = [];

    // ── Computed metrics ───────────────────────────────────────────────────────
    public int TpCount => TruePositives.Count;
    public int FpCount => FalsePositives.Count;
    public int FnCount => FalseNegatives.Count;

    public float Precision => TpCount + FpCount == 0
        ? 0f : (float)TpCount / (TpCount + FpCount);

    public float Recall => TpCount + FnCount == 0
        ? 0f : (float)TpCount / (TpCount + FnCount);

    public float F1 => Precision + Recall == 0
        ? 0f : 2f * Precision * Recall / (Precision + Recall);
}

// ─── Summary model (lightweight, for history list) ────────────────────────────

/// <summary>Lightweight summary of a past validation run for display in the History tab.</summary>
public sealed class ValidationSummary
{
    public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string SessionName { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public DateTime RunAt { get; set; }
    public float F1 { get; set; }
    public float Precision { get; set; }
    public float Recall { get; set; }
    public int TpCount { get; set; }
    public int FpCount { get; set; }
    public int FnCount { get; set; }
    public int GroundTruthCount { get; set; }
    public float Threshold { get; set; }
}
