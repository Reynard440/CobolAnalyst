namespace CobolAnalyst.Web.Models;

/// <summary>A high-confidence rule persisted to the knowledge base for future prompt hints.</summary>
public sealed class KnowledgeEntry
{
    /// <summary>Unique identifier.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Short rule label.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Plain-English rule description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Rule category.</summary>
    public RuleType Type { get; init; }

    /// <summary>Notes on C# migration concerns.</summary>
    public string MigrationNotes { get; init; } = string.Empty;

    /// <summary>COBOL reference where this rule originated.</summary>
    public string CobolReference { get; init; } = string.Empty;

    /// <summary>UTC timestamp when this entry was added.</summary>
    public DateTime AddedAt { get; init; } = DateTime.UtcNow;
}
