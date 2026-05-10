namespace CobolAnalyst.Web.Models;

/// <summary>
/// Determines how a staged file participates in an analysis run.
/// Mirrors the file-role concept from the legacy-agent.
/// </summary>
public enum FileRole
{
    /// <summary>Main file — the LLM extracts business rules from its chunks.</summary>
    Analyze,

    /// <summary>
    /// Definitions / constants file — its full text is injected as background
    /// context into every Analyze-file prompt but is not extracted itself.
    /// </summary>
    Context,

    /// <summary>Ignored entirely — excluded from both analysis and context.</summary>
    Skip
}
