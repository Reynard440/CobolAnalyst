namespace CobolAnalyst.Web.Models;

/// <summary>Category of a rule extracted from COBOL source.</summary>
public enum RuleType
{
    BusinessRule,       // 0
    Validation,         // 1
    Calculation,        // 2
    DataTransformation, // 3
    ControlFlow,        // 4
    HardcodedValue,     // 5 — appended for backward compat
    Workflow,           // 6
    CobolArtifact,      // 7
    Constraint,         // 8
    ErrorHandling,      // 9
    DataMapping         // 10
}

/// <summary>Confidence level reported by the LLM for an extracted rule.</summary>
public enum ConfidenceLevel { High, Medium, Low }

/// <summary>User decision on a reviewed rule.</summary>
public enum ReviewDecision { Pending, Accepted, Rejected, Flagged }

/// <summary>A single business rule or logic unit extracted from a COBOL chunk.</summary>
public sealed class ExtractedRule
{
    /// <summary>Unique identifier (UUID from LLM or generated).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Short descriptive label, eight words or fewer.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Semantic category of this rule.</summary>
    public RuleType Type { get; set; }

    /// <summary>Plain-English description, three sentences or fewer.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Paragraph name and line range in the source file.</summary>
    public string CobolReference { get; set; } = string.Empty;

    /// <summary>LLM-reported confidence in the extraction.</summary>
    public ConfidenceLevel Confidence { get; set; }

    /// <summary>Notes on what to watch for when rewriting in C#.</summary>
    public string MigrationNotes { get; set; } = string.Empty;

    /// <summary>Source file the rule was extracted from.</summary>
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>Chunk label this rule came from.</summary>
    public string SourceChunk { get; set; } = string.Empty;

    /// <summary>Risk level: high, medium, or low.</summary>
    public string Risk { get; set; } = "low";

    /// <summary>Source code snippet backing this rule.</summary>
    public string CodeSnippet { get; set; } = string.Empty;

    /// <summary>True if rule originates from COBOL-migrated code.</summary>
    public bool CobolOrigin { get; set; }

    /// <summary>Free-text analyst or migration notes per rule.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>Whether this rule appears in three or more chunks.</summary>
    public bool IsCrossCutting { get; set; }

    /// <summary>Analyst's review decision.</summary>
    public ReviewDecision Decision { get; set; } = ReviewDecision.Pending;

    /// <summary>Timestamp when the decision was recorded.</summary>
    public DateTime? DecidedAt { get; set; }
}
