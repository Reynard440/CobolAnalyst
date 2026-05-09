using CobolAnalyst.Web.Models;

namespace CobolAnalyst.Web.Core.Analysis;

/// <summary>Status of a chunk within an analysis run.</summary>
public enum ChunkStatus { Queued, Running, Complete, Failed }

/// <summary>Progress event emitted by the orchestrator for each chunk.</summary>
public sealed class AnalysisProgressEvent
{
    /// <summary>Chunk being processed.</summary>
    public string ChunkLabel { get; init; } = string.Empty;

    /// <summary>Current processing status.</summary>
    public ChunkStatus Status { get; init; }

    /// <summary>Number of rules found so far (meaningful when Complete).</summary>
    public int RulesFound { get; init; }

    /// <summary>Milliseconds elapsed since analysis started.</summary>
    public long ElapsedMs { get; init; }

    /// <summary>Error message when Status is Failed.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>Runs LLM analysis across all chunks with concurrency control and deduplication.</summary>
public interface IAnalysisOrchestrator
{
    /// <summary>
    /// Analyses <paramref name="chunks"/> and returns the deduplicated list of extracted rules.
    /// Reports progress via <paramref name="progress"/>.
    /// </summary>
    Task<List<ExtractedRule>> AnalyseAsync(
        List<CobolChunk> chunks,
        IProgress<AnalysisProgressEvent>? progress,
        CancellationToken cancellationToken = default);
}
