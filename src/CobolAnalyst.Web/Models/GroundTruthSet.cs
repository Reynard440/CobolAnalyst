namespace CobolAnalyst.Web.Models;

/// <summary>A single hand-verified rule used as ground truth for accuracy measurement.</summary>
public sealed class GroundTruthRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Rule category matching the extraction prompt categories (e.g. Calculation, Validation).</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Plain-English description of the business rule.</summary>
    public string RuleText { get; set; } = string.Empty;

    /// <summary>Optional reference to source document, paragraph, or section.</summary>
    public string? SourceRef { get; set; }
}

/// <summary>A curated set of ground truth rules for a specific source file or session.</summary>
public sealed class GroundTruthSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<GroundTruthRule> Rules { get; set; } = [];
}
