using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CobolAnalyst.Web.Core.Analysis;
using CobolAnalyst.Web.Models;
using Microsoft.Extensions.Logging;

namespace CobolAnalyst.Web.Core.Chunking;

/// <summary>
/// Reads COBOL and Visual Basic source files and splits them at logical boundaries.
/// Adjacent chunks under 300 tokens are merged to reduce API round-trips.
/// </summary>
public sealed partial class CobolChunker : ICobolChunker
{
    private const long MaxFileSizeBytes = 500 * 1024;
    private const int BatchTokenThreshold = 300;

    private static readonly string[] CobolExtensions = [".cbl", ".cob", ".cpy"];
    private static readonly string[] VbExtensions   = [".vb", ".bas", ".cls", ".frm", ".vbs"];
    private static readonly string[] AllExtensions  = [.. CobolExtensions, .. VbExtensions];

    // COBOL: line whose Area A contains a paragraph label followed by a period
    [GeneratedRegex(@"^\s{6,7}([A-Z][A-Z0-9\-]*)\s*\.\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphLabel();

    // COBOL: DIVISION headers
    [GeneratedRegex(@"^\s{6,7}(IDENTIFICATION|ENVIRONMENT|DATA|PROCEDURE)\s+DIVISION", RegexOptions.IgnoreCase)]
    private static partial Regex DivisionHeader();

    // VB: Sub / Function / Property / Class / Module / Namespace / Interface / Structure / Enum declarations
    [GeneratedRegex(
        @"^\s*(?:(?:Public|Private|Protected|Friend|Shared|Overrides|Overridable|MustOverride|" +
        @"Shadows|Static|ReadOnly|WriteOnly|NotInheritable|MustInherit|Partial|Abstract)\s+)*" +
        @"(?:Sub|Function|Property|Class|Module|Namespace|Interface|Structure|Enum)\s+(\w+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex VbBoundary();

    private readonly ComplexityScorer _scorer;
    private readonly ILogger<CobolChunker> _logger;

    /// <summary>Initialises the chunker with a complexity scorer.</summary>
    public CobolChunker(ComplexityScorer scorer, ILogger<CobolChunker> logger)
    {
        _scorer = scorer;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<(List<CobolFile> Files, List<CobolChunk> Chunks)> ChunkFilesAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var files = new List<CobolFile>();
        var allChunks = new List<CobolChunk>();

        foreach (var path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!AllExtensions.Contains(ext))
                throw new ArgumentException(
                    $"Unsupported extension '{ext}'. Supported: {string.Join(", ", AllExtensions)}");

            var info = new FileInfo(path);
            if (!info.Exists)
                throw new FileNotFoundException($"File not found: {path}");
            if (info.Length > MaxFileSizeBytes)
                throw new ArgumentException($"File exceeds 500 KB limit: {path}");

            var text = await ReadWithFallbackAsync(path, cancellationToken);
            var hash = ComputeSha256(text);
            var lines = text.Split('\n');

            var cobolFile = new CobolFile
            {
                FilePath = path,
                SourceText = text,
                LineCount = lines.Length,
                SizeBytes = info.Length,
                ContentHash = hash
            };
            files.Add(cobolFile);

            bool isVb = VbExtensions.Contains(ext);
            var raw = SplitIntoRawChunks(Path.GetFileName(path), isVb, lines);
            var merged = MergeSmallChunks(raw);
            foreach (var c in merged)
                c.Complexity = _scorer.Score(c);
            allChunks.AddRange(merged);

            _logger.LogInformation("Chunked {File} ({Lang}): {Count} chunks",
                cobolFile.FileName, isVb ? "VB" : "COBOL", merged.Count);
        }

        return (files, allChunks);
    }

    private static List<CobolChunk> SplitIntoRawChunks(string fileName, bool isVb, string[] lines)
    {
        var chunks = new List<CobolChunk>();
        var currentLabel = isVb ? "MODULE-PREAMBLE" : "PREAMBLE";
        var currentStart = 1;
        var currentLines = new List<string>();

        void Flush(int endLine)
        {
            if (currentLines.Count == 0) return;
            chunks.Add(new CobolChunk
            {
                FileName = fileName,
                Label = currentLabel,
                StartLine = currentStart,
                EndLine = endLine,
                SourceText = string.Join("\n", currentLines)
            });
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNo = i + 1;

            bool isBoundary = isVb
                ? VbBoundary().IsMatch(line)
                : DivisionHeader().IsMatch(line) || ParagraphLabel().IsMatch(line);

            if (isBoundary)
            {
                Flush(lineNo - 1);
                currentLabel = ExtractLabel(line, isVb);
                currentStart = lineNo;
                currentLines = [line];
            }
            else
            {
                currentLines.Add(line);
            }
        }

        Flush(lines.Length);
        return chunks.Where(c => c.SourceText.Trim().Length > 0).ToList();
    }

    private static List<CobolChunk> MergeSmallChunks(List<CobolChunk> raw)
    {
        var result = new List<CobolChunk>();
        CobolChunk? pending = null;

        foreach (var chunk in raw)
        {
            if (pending is null) { pending = chunk; continue; }

            if (pending.EstimatedTokens + chunk.EstimatedTokens < BatchTokenThreshold)
            {
                pending = new CobolChunk
                {
                    FileName = pending.FileName,
                    Label = pending.Label + " + " + chunk.Label,
                    StartLine = pending.StartLine,
                    EndLine = chunk.EndLine,
                    SourceText = pending.SourceText + "\n" + chunk.SourceText
                };
            }
            else
            {
                result.Add(pending);
                pending = chunk;
            }
        }

        if (pending is not null) result.Add(pending);
        return result;
    }

    private static string ExtractLabel(string line, bool isVb)
    {
        var trimmed = line.Trim();
        if (isVb)
        {
            var m = VbBoundary().Match(trimmed);
            return m.Success ? m.Groups[1].Value : trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? trimmed;
        }

        var divMatch = Regex.Match(trimmed, @"(IDENTIFICATION|ENVIRONMENT|DATA|PROCEDURE)\s+DIVISION", RegexOptions.IgnoreCase);
        if (divMatch.Success) return divMatch.Value.ToUpperInvariant();

        var paraMatch = Regex.Match(trimmed, @"^([A-Z][A-Z0-9\-]*)\s*\.", RegexOptions.IgnoreCase);
        return paraMatch.Success ? paraMatch.Groups[1].Value.ToUpperInvariant() : trimmed;
    }

    private static async Task<string> ReadWithFallbackAsync(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        }
        catch (DecoderFallbackException)
        {
            return await File.ReadAllTextAsync(path, Encoding.Latin1, ct);
        }
    }

    private static string ComputeSha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
