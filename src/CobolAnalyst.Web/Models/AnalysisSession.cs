namespace CobolAnalyst.Web.Models;

/// <summary>A complete analysis session: files, chunks, extracted rules, and review decisions.</summary>
public sealed class AnalysisSession
{
    /// <summary>Unique session identifier.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Human-readable session name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>UTC timestamp when analysis started.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>UTC timestamp of last modification.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Model identifier used for this session.</summary>
    public string ModelUsed { get; set; } = string.Empty;

    /// <summary>Source files included in the session.</summary>
    public List<CobolFile> Files { get; set; } = [];

    /// <summary>All chunks produced by the chunker.</summary>
    public List<CobolChunk> Chunks { get; set; } = [];

    /// <summary>All deduplicated rules extracted by the LLM.</summary>
    public List<ExtractedRule> Rules { get; set; } = [];

    /// <summary>Count of accepted rules.</summary>
    public int AcceptedCount => Rules.Count(r => r.Decision == ReviewDecision.Accepted);

    /// <summary>Count of rejected rules.</summary>
    public int RejectedCount => Rules.Count(r => r.Decision == ReviewDecision.Rejected);

    /// <summary>Count of rules flagged for review.</summary>
    public int FlaggedCount => Rules.Count(r => r.Decision == ReviewDecision.Flagged);

    /// <summary>Count of rules with no decision yet.</summary>
    public int PendingCount => Rules.Count(r => r.Decision == ReviewDecision.Pending);

    /// <summary>ID of the prompt template active when this session was analysed.</summary>
    public string? PromptTemplateId { get; set; }

    /// <summary>Display name of the prompt template used.</summary>
    public string? PromptTemplateName { get; set; }
}
