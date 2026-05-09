using CobolAnalyst.Web.Models;

namespace CobolAnalyst.Web.Core.Chunking;

/// <summary>Splits COBOL source files into paragraph-boundary chunks for LLM analysis.</summary>
public interface ICobolChunker
{
    /// <summary>
    /// Reads, validates, and chunks the files at <paramref name="filePaths"/>.
    /// Returns chunks ordered by file then by line number.
    /// Throws <see cref="ArgumentException"/> if a file fails validation.
    /// </summary>
    Task<(List<CobolFile> Files, List<CobolChunk> Chunks)> ChunkFilesAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default);
}
