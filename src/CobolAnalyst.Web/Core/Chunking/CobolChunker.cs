using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CobolAnalyst.Web.Core.Analysis;
using CobolAnalyst.Web.Core.Config;
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
    private static readonly string[] SqlExtensions  = [".sql"];
    private static readonly string[] AllExtensions  = [.. CobolExtensions, .. VbExtensions, .. SqlExtensions];

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

    // SQL: CREATE [OR ALTER] PROCEDURE/PROC/FUNCTION boundary
    [GeneratedRegex(
        @"^\s*CREATE\s+(?:OR\s+ALTER\s+)?(?:PROCEDURE|PROC|FUNCTION)\s+(?:\[?[\w.]+\]?\.)?(\[?[\w]+\]?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SqlBoundary();

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
            if (!AllExtensions.Contains(ext) && !Config.LanguageConfig.IsSupported(ext))
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
            bool isSql = SqlExtensions.Contains(ext);
            bool isCobol = CobolExtensions.Contains(ext);
            List<CobolChunk> raw;
            if (isSql)
                raw = SplitSqlIntoRawChunks(Path.GetFileName(path), lines);
            else if (isVb || isCobol)
                raw = SplitIntoRawChunks(Path.GetFileName(path), isVb, lines);
            else
                raw = [new CobolChunk { FileName = Path.GetFileName(path), Label = "FULL-FILE", StartLine = 1, EndLine = lines.Length, SourceText = text }];
            var merged = MergeSmallChunks(raw);
            var final = new List<CobolChunk>();
            foreach (var c in merged)
            {
                c.Complexity = _scorer.Score(c);
                var report = ComplexityAnalyzer.Analyze(c.SourceText);
                var tier = ComplexityAnalyzer.ToComplexityTier(report.NestingComplexity);
                c.Complexity = tier;

                if (report.IsHighComplexity && ChunkSplitter.EstimateTokens(c.SourceText) > ChunkSplitter.DefaultTokenThreshold)
                {
                    var splits = ChunkSplitter.AutoSplitIfNeeded(c.SourceText, ChunkSplitter.DefaultTokenThreshold, report);
                    if (splits.Count > 1)
                    {
                        int branchNum = 0;
                        foreach (var sub in splits)
                        {
                            branchNum++;
                            final.Add(new CobolChunk
                            {
                                FileName = c.FileName,
                                Label = $"{c.Label} [{sub.Label}]",
                                StartLine = c.StartLine,
                                EndLine = c.EndLine,
                                SourceText = sub.Code,
                                Complexity = tier
                            });
                        }
                        continue;
                    }
                }
                final.Add(c);
            }
            allChunks.AddRange(final);

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

    private static List<CobolChunk> SplitSqlIntoRawChunks(string fileName, string[] lines)
    {
        var chunks = new List<CobolChunk>();
        var currentLabel = "SQL-PREAMBLE";
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
            var m = SqlBoundary().Match(line);
            if (m.Success)
            {
                Flush(lineNo - 1);
                currentLabel = m.Groups[1].Value.Trim('[', ']');
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
