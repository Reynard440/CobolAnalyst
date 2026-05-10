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

    /// <summary>
    /// Permanent paths to Analyze-role source files copied into data storage.
    /// Used by the Query page to load source code for edit proposals.
    /// </summary>
    public List<SessionSourceFile> SourceFiles { get; set; } = [];

    /// <summary>Conversation turns from the Query tab for this session.</summary>
    public List<ConversationTurn> ConversationHistory { get; set; } = [];
}

// ─── Supporting types ────────────────────────────────────────────────────────

/// <summary>A permanent copy of an Analyze-role source file stored with the session.</summary>
public sealed class SessionSourceFile
{
    public string FileName { get; set; } = string.Empty;
    public string PermanentPath { get; set; } = string.Empty;
}

/// <summary>A single turn in the Query-tab conversation.</summary>
public sealed class ConversationTurn
{
    /// <summary>Message role: "user", "assistant", or "system".</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Full message text.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>UTC timestamp when this turn was recorded.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>True when this turn resulted in an applied code edit.</summary>
    public bool IsEditApplied { get; set; }
}
