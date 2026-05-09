namespace CobolAnalyst.Web.Models;

/// <summary>Complexity tier assigned to a COBOL chunk before LLM analysis.</summary>
public enum ComplexityTier { Low, Medium, High }

/// <summary>A paragraph-boundary slice of a COBOL file ready for LLM analysis.</summary>
public sealed class CobolChunk
{
    /// <summary>Unique identifier for this chunk.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Source file name.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Division or paragraph label that begins this chunk.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>1-based start line within the source file.</summary>
    public int StartLine { get; init; }

    /// <summary>1-based end line within the source file (inclusive).</summary>
    public int EndLine { get; init; }

    /// <summary>Raw COBOL source text for this chunk.</summary>
    public string SourceText { get; init; } = string.Empty;

    /// <summary>Estimated token count (characters / 4).</summary>
    public int EstimatedTokens => SourceText.Length / 4;

    /// <summary>Pre-computed complexity tier assigned by ComplexityScorer.</summary>
    public ComplexityTier Complexity { get; set; } = ComplexityTier.Low;
}
