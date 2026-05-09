namespace CobolAnalyst.Web.Models;

/// <summary>Result of C# scaffold generation for a set of accepted rules.</summary>
public sealed class GenerationResult
{
    /// <summary>Session this was generated from.</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>UTC timestamp of generation.</summary>
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Map of relative file name to generated C# source text.</summary>
    public Dictionary<string, string> Files { get; init; } = [];

    /// <summary>Path on disk where files were written.</summary>
    public string OutputDirectory { get; init; } = string.Empty;

    /// <summary>Number of rules that were scaffolded.</summary>
    public int RuleCount { get; init; }

    /// <summary>Any validation errors from Roslyn syntax checking.</summary>
    public List<string> ValidationErrors { get; init; } = [];
}
