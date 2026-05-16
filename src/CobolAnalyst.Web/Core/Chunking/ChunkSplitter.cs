using System.Text;
using System.Text.RegularExpressions;
using CobolAnalyst.Web.Core.Analysis;

namespace CobolAnalyst.Web.Core.Chunking;

/// <summary>
/// Pre-extraction splitter for high-complexity chunks. Splits HIGH/CRITICAL
/// chunks at top-level If/Select-Case branch boundaries so each branch can
/// be analysed independently. Ports chunk_splitter.py.
/// </summary>
public static partial class ChunkSplitter
{
    private const int CharsPerToken = 4;
    public const int DefaultTokenThreshold = 800;

    // Reuse the same block patterns as the complexity analyzer for consistency.
    private static readonly (string Kind, Regex Open, Regex Close)[] BlockPatterns =
    [
        ("if",     ReMultiLineIf(), ReEndIf()),
        ("for",    ReForOpen(),     ReNext()),
        ("do",     ReDoOpen(),      ReLoop()),
        ("select", ReSelectCase(),  ReEndSelect()),
        ("with",   ReWith(),        ReEndWith()),
        ("while",  ReWhileOpen(),   ReWend()),
    ];

    [GeneratedRegex(@"^If\b.*\bThen\s*$",    RegexOptions.IgnoreCase)] private static partial Regex ReMultiLineIf();
    [GeneratedRegex(@"^End\s+If\b",           RegexOptions.IgnoreCase)] private static partial Regex ReEndIf();
    [GeneratedRegex(@"^For\b(?:\s+Each\b)?",  RegexOptions.IgnoreCase)] private static partial Regex ReForOpen();
    [GeneratedRegex(@"^Next\b",               RegexOptions.IgnoreCase)] private static partial Regex ReNext();
    [GeneratedRegex(@"^Do\b",                 RegexOptions.IgnoreCase)] private static partial Regex ReDoOpen();
    [GeneratedRegex(@"^Loop\b",               RegexOptions.IgnoreCase)] private static partial Regex ReLoop();
    [GeneratedRegex(@"^Select\s+Case\b",      RegexOptions.IgnoreCase)] private static partial Regex ReSelectCase();
    [GeneratedRegex(@"^End\s+Select\b",       RegexOptions.IgnoreCase)] private static partial Regex ReEndSelect();
    [GeneratedRegex(@"^With\b",               RegexOptions.IgnoreCase)] private static partial Regex ReWith();
    [GeneratedRegex(@"^End\s+With\b",         RegexOptions.IgnoreCase)] private static partial Regex ReEndWith();
    [GeneratedRegex(@"^While\b",              RegexOptions.IgnoreCase)] private static partial Regex ReWhileOpen();
    [GeneratedRegex(@"^Wend\b",               RegexOptions.IgnoreCase)] private static partial Regex ReWend();

    // Branch markers
    [GeneratedRegex(@"^ElseIf\b.*\bThen\s*$",      RegexOptions.IgnoreCase)] private static partial Regex ReElseIf();
    [GeneratedRegex(@"^ElseIf\b.*?\bThen\s+(.+)$",  RegexOptions.IgnoreCase)] private static partial Regex ReElseIfInline();
    [GeneratedRegex(@"^Else\s*$",                   RegexOptions.IgnoreCase)] private static partial Regex ReElse();
    [GeneratedRegex(@"^Else\s+(.+)$",               RegexOptions.IgnoreCase)] private static partial Regex ReElseInline();
    [GeneratedRegex(@"^Case\b",                     RegexOptions.IgnoreCase)] private static partial Regex ReCase();

    public static int EstimateTokens(string code)
    {
        if (string.IsNullOrEmpty(code)) return 0;
        return Math.Max(1, code.Length / CharsPerToken);
    }

    /// <summary>
    /// Top-level entry: returns one or more <see cref="SubChunk"/>s.
    /// Only splits when the chunk is HIGH/CRITICAL and exceeds the token threshold.
    /// </summary>
    public static List<SubChunk> AutoSplitIfNeeded(
        string code,
        int tokenThreshold = DefaultTokenThreshold,
        ComplexityReport? report = null)
    {
        report ??= ComplexityAnalyzer.Analyze(code);

        if (!report.IsHighComplexity || EstimateTokens(code) <= tokenThreshold)
            return [new SubChunk { Label = "whole", Code = code, Branch = 0 }];

        var splits = SplitByTopLevelBranch(code);
        if (splits.Count <= 1)
            return [new SubChunk { Label = "whole", Code = code, Branch = 0 }];

        return splits;
    }

    /// <summary>
    /// Split the code at the top-level If/Select branches. Each sub-chunk includes
    /// preamble, branch body wrapped in its control structure, and epilogue.
    /// </summary>
    public static List<SubChunk> SplitByTopLevelBranch(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return [new SubChunk { Label = "whole", Code = code, Branch = 0 }];

        var lines = ComplexityAnalyzer.JoinContinuations(code);
        var bounds = FindOuterBlockBounds(lines);
        if (bounds is null)
            return [new SubChunk { Label = "whole", Code = code, Branch = 0 }];

        var (start, end, kind) = bounds.Value;
        var preamble = lines.GetRange(0, start);
        var outerOpenLine = lines[start];
        var epilogue = lines.GetRange(end + 1, lines.Count - end - 1);
        var body = lines.GetRange(start + 1, end - start - 1);

        // Walk body to find top-level branch boundaries
        var blockStack = new List<string>();
        var branchStarts = new List<int> { 0 };
        var branchOpenerLines = new List<string> { outerOpenLine };

        for (int i = 0; i < body.Count; i++)
        {
            var line = body[i].Trim();

            // Close patterns first
            foreach (var (_, _, closeRe) in BlockPatterns)
            {
                if (closeRe.IsMatch(line))
                {
                    if (blockStack.Count > 0) blockStack.RemoveAt(blockStack.Count - 1);
                    break;
                }
            }

            // Top-level branch markers (only at depth 0 within body)
            if (blockStack.Count == 0)
            {
                bool isBranchMarker =
                    ReElseIf().IsMatch(line) ||
                    ReElseIfInline().IsMatch(line) ||
                    ReElse().IsMatch(line) ||
                    ReElseInline().IsMatch(line) ||
                    (kind == "select" && ReCase().IsMatch(line));

                if (isBranchMarker)
                {
                    branchStarts.Add(i);
                    branchOpenerLines.Add(line);
                }
            }

            // Push opens AFTER branch detection
            foreach (var (k, openRe, _) in BlockPatterns)
            {
                if (openRe.IsMatch(line))
                {
                    blockStack.Add(k);
                    break;
                }
            }
        }

        if (branchStarts.Count <= 1)
            return [new SubChunk { Label = "whole", Code = code, Branch = 0 }];

        branchStarts.Add(body.Count); // sentinel

        var subChunks = new List<SubChunk>();
        int total = branchStarts.Count - 1;
        var preambleText = string.Join("\n", preamble).TrimEnd();
        var epilogueText = string.Join("\n", epilogue).Trim();

        for (int idx = 0; idx < total; idx++)
        {
            int bStart = branchStarts[idx];
            int bEnd = branchStarts[idx + 1];
            var branchBody = body.GetRange(bStart, bEnd - bStart);
            var opener = branchOpenerLines[idx];
            var label = $"branch {idx + 1}: {opener.Trim()}";

            var header =
                $"' [Auto-split sub-chunk {idx + 1} of {total} — " +
                $"top-level {kind.ToUpperInvariant()} branch]\n" +
                $"' Branch opener: {opener.Trim()}";

            List<string> fragmentLines;
            if (kind == "if")
            {
                if (idx == 0)
                {
                    fragmentLines = [opener, .. branchBody, "End If"];
                }
                else
                {
                    var op = opener.Trim();
                    if (op.StartsWith("ElseIf", StringComparison.OrdinalIgnoreCase))
                    {
                        var converted = Regex.Replace(opener, @"^ElseIf\b", "If",
                            RegexOptions.IgnoreCase);
                        fragmentLines = [converted, .. branchBody, "End If"];
                    }
                    else
                    {
                        fragmentLines =
                        [
                            "If True Then  ' (Else branch from outer If)",
                            .. branchBody,
                            "End If"
                        ];
                    }
                }
            }
            else // select
            {
                fragmentLines =
                [
                    outerOpenLine.Trim(),
                    opener,
                    .. branchBody,
                    "End Select"
                ];
            }

            var fragment = string.Join("\n", fragmentLines);

            var parts = new List<string>();
            if (preambleText.Length > 0) parts.Add(preambleText);
            parts.Add(header);
            parts.Add(fragment);
            if (epilogueText.Length > 0) parts.Add(epilogueText);

            subChunks.Add(new SubChunk
            {
                Label = label,
                Code = string.Join("\n", parts) + "\n",
                Branch = idx + 1
            });
        }

        return subChunks;
    }

    private static (int Start, int End, string Kind)? FindOuterBlockBounds(List<string> lines)
    {
        var blockStack = new List<(string Kind, int StartIdx)>();
        var candidates = new List<(int Start, int End, string Kind, int Span)>();

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();

            // Close first
            foreach (var (kind, _, closeRe) in BlockPatterns)
            {
                if (!closeRe.IsMatch(line)) continue;
                if (blockStack.Count > 0 && blockStack[^1].Kind == kind)
                {
                    var (startKind, startIdx) = blockStack[^1];
                    blockStack.RemoveAt(blockStack.Count - 1);
                    if (blockStack.Count == 0 && startKind is "if" or "select")
                        candidates.Add((startIdx, i, startKind, i - startIdx));
                }
                else if (blockStack.Count > 0)
                {
                    blockStack.RemoveAt(blockStack.Count - 1);
                }
                break;
            }

            // Open
            foreach (var (kind, openRe, _) in BlockPatterns)
            {
                if (openRe.IsMatch(line))
                {
                    blockStack.Add((kind, i));
                    break;
                }
            }
        }

        if (candidates.Count == 0) return null;
        candidates.Sort((a, b) => b.Span.CompareTo(a.Span));
        var best = candidates[0];
        return (best.Start, best.End, best.Kind);
    }
}

/// <summary>A single output piece from chunk splitting.</summary>
public sealed class SubChunk
{
    public string Label { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public int Branch { get; init; }
}
