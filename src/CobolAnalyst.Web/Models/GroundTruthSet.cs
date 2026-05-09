namespace CobolAnalyst.Web.Models;

/// <summary>A single hand-verified rule used as ground truth for accuracy measurement.</summary>
public sealed class GroundTruthRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>A curated set of ground truth rules for a specific source file or session.</summary>
public sealed class GroundTruthSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<GroundTruthRule> Rules { get; set; } = [];
}
