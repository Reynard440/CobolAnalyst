using System.Text;
using System.Text.RegularExpressions;
using CobolAnalyst.Web.Models;

namespace CobolAnalyst.Web.Core.Analysis;

/// <summary>
/// Stateful line-by-line complexity analyser for VBA/COBOL-migrated code.
/// Ports the Python complexity_analyzer.py. Detects nesting depth, branch counts,
/// interdependent variables, and loop-embedded business conditions.
/// Produces tier-specific extraction instructions for prompt injection.
/// </summary>
public static partial class ComplexityAnalyzer
{
    private const int DepthLowMax = 2;
    private const int DepthMediumMax = 4;
    private const int DepthHighMax = 8;
    private const int InterdependentVarsLimit = 12;
    private const int MaxLinesScanned = 5000;

    // ── Block patterns: (kind, open, close) ─────────────────────────────────
    private static readonly (string Kind, Regex Open, Regex Close)[] BlockPatterns =
    [
        ("if",     ReMultiLineIf(), ReEndIf()),
        ("for",    ReForOpen(),     ReNext()),
        ("do",     ReDoOpen(),      ReLoop()),
        ("select", ReSelectCase(),  ReEndSelect()),
        ("with",   ReWith(),        ReEndWith()),
        ("while",  ReWhileOpen(),   ReWend()),
    ];

    private static readonly HashSet<string> LoopKinds = ["for", "do", "while"];

    // Multi-line If: ends with "Then" and nothing else after comment stripping.
    [GeneratedRegex(@"^If\b.*\bThen\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReMultiLineIf();

    [GeneratedRegex(@"^End\s+If\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReEndIf();

    [GeneratedRegex(@"^For\b(?:\s+Each\b)?", RegexOptions.IgnoreCase)]
    private static partial Regex ReForOpen();

    [GeneratedRegex(@"^Next\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReNext();

    [GeneratedRegex(@"^Do\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReDoOpen();

    [GeneratedRegex(@"^Loop\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReLoop();

    [GeneratedRegex(@"^Select\s+Case\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReSelectCase();

    [GeneratedRegex(@"^End\s+Select\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReEndSelect();

    [GeneratedRegex(@"^With\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReWith();

    [GeneratedRegex(@"^End\s+With\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReEndWith();

    [GeneratedRegex(@"^While\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReWhileOpen();

    [GeneratedRegex(@"^Wend\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReWend();

    // Sibling-branch markers
    [GeneratedRegex(@"^ElseIf\b.*\bThen\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReElseIf();

    [GeneratedRegex(@"^ElseIf\b.*?\bThen\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ReElseIfInline();

    [GeneratedRegex(@"^Else\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReElse();

    [GeneratedRegex(@"^Else\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ReElseInline();

    [GeneratedRegex(@"^Case\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReCase();

    // One-line If body: "If X Then Y = 1"
    [GeneratedRegex(@"^If\b.*?\bThen\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ReInlineIf();

    // GoTo label
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\s*:\s*$")]
    private static partial Regex ReLabel();

    // Assignment: "Var = ..." | "Set Var = ..."
    [GeneratedRegex(@"^(?:Set\s+)?([A-Za-z_][A-Za-z0-9_]*)(?:\s*\([^)]*\))?(?:\.[A-Za-z_][A-Za-z0-9_]*)*\s*=(?!=)", RegexOptions.IgnoreCase)]
    private static partial Regex ReAssign();

    // Identifier finder
    [GeneratedRegex(@"\b([A-Za-z_][A-Za-z0-9_]*)\b")]
    private static partial Regex ReWord();

    private static readonly HashSet<string> VbaKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "then", "else", "elseif", "end", "endif", "for", "to", "step",
        "next", "do", "loop", "while", "wend", "until", "select", "case", "with",
        "sub", "function", "dim", "as", "set", "let", "call", "goto", "gosub",
        "return", "exit", "public", "private", "protected", "friend", "shared",
        "static", "const", "byval", "byref", "optional", "paramarray", "redim",
        "preserve", "mod", "and", "or", "xor", "not", "eqv", "imp", "is", "like",
        "new", "nothing", "true", "false", "null", "empty", "me", "this", "on",
        "error", "resume", "err", "each", "in", "of",
        "string", "integer", "long", "double", "single", "boolean", "byte",
        "currency", "decimal", "variant", "object", "date", "collection",
        "msgbox", "inputbox", "debug", "print", "format", "len", "ubound",
        "lbound", "isarray", "isdate", "isempty", "isnull", "isnumeric",
        "isobject", "vartype", "typename", "trim", "ltrim", "rtrim", "left",
        "right", "mid", "instr", "replace", "split", "join", "ucase", "lcase",
        "space", "chr", "asc", "val", "str", "cstr", "cint", "clng", "cdbl",
        "csng", "cbool", "cdate", "cvar", "cbyte", "iif", "choose", "switch",
        "now", "time", "timer", "dateadd", "datediff", "datepart",
        "year", "month", "day", "hour", "minute", "second"
    };

    private static readonly Dictionary<string, string> ExtractionInstructions = new()
    {
        ["LOW"] = "Extract each conditional block as one rule.",
        ["MEDIUM"] = "Extract each distinct branch outcome as a separate rule. Do not collapse ElseIf chains into one rule.",
        ["HIGH"] = "Work outside-in. Identify the outermost condition first — this is the rule category. Then extract each leaf branch (innermost outcome) as a separate rule. Include the full condition path in the rule_text (e.g. 'When IsExport AND CustomerClass=PREMIUM AND WeightTons > 100...').",
        ["CRITICAL"] = "Split this chunk into logical sub-sections by top-level branch before extracting. Extract each sub-section independently. Never combine conditions from different top-level branches into one rule. Flag all extracted rules with confidence=medium and add a note listing the full condition chain."
    };

    /// <summary>Analyse a VBA code chunk and return a <see cref="ComplexityReport"/>.</summary>
    public static ComplexityReport Analyze(string code)
    {
        var report = new ComplexityReport();
        if (string.IsNullOrWhiteSpace(code))
            return report;

        var lines = JoinContinuations(code);
        if (lines.Count > MaxLinesScanned)
        {
            lines = lines.GetRange(0, MaxLinesScanned);
            report.TruncatedDueToSize = true;
        }

        var blockStack = new List<string>();
        var branchPath = new List<int>();
        var nextBranchId = new Dictionary<int, int>();

        var assigns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var reads = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        string CurrentPath()
        {
            return string.Join(",", branchPath);
        }

        void EnterBlock(string kind)
        {
            blockStack.Add(kind);
            int depth = blockStack.Count;
            if (kind == "select")
            {
                branchPath.Add(0);
                nextBranchId[depth] = 1;
            }
            else
            {
                branchPath.Add(1);
                nextBranchId[depth] = 2;
            }
            if (depth > report.MaxNestingDepth)
                report.MaxNestingDepth = depth;
            report.BlockKindCounts.TryGetValue(kind, out int cnt);
            report.BlockKindCounts[kind] = cnt + 1;
        }

        void ExitBlock()
        {
            if (blockStack.Count > 0)
            {
                blockStack.RemoveAt(blockStack.Count - 1);
                branchPath.RemoveAt(branchPath.Count - 1);
            }
        }

        void NewSiblingBranch()
        {
            if (blockStack.Count == 0) return;
            int depth = blockStack.Count;
            int nbid = nextBranchId.GetValueOrDefault(depth, 1);
            branchPath[^1] = nbid;
            nextBranchId[depth] = nbid + 1;
            report.BranchCount++;
        }

        void RecordAssign(string variable)
        {
            if (VbaKeywords.Contains(variable)) return;
            var key = variable.ToLowerInvariant();
            if (!assigns.TryGetValue(key, out var set))
            {
                set = [];
                assigns[key] = set;
            }
            set.Add(CurrentPath());
        }

        void RecordReads(string line, string? lhsVar)
        {
            string? skipLhs = lhsVar?.ToLowerInvariant();
            bool firstOccurrence = true;
            foreach (Match m in ReWord().Matches(line))
            {
                var wl = m.Groups[1].Value.ToLowerInvariant();
                if (VbaKeywords.Contains(wl)) continue;
                if (skipLhs != null && wl == skipLhs && firstOccurrence)
                {
                    firstOccurrence = false;
                    continue;
                }
                if (!reads.TryGetValue(wl, out var set))
                {
                    set = [];
                    reads[wl] = set;
                }
                set.Add(CurrentPath());
            }
        }

        void ProcessStatement(string stmt)
        {
            stmt = stmt.Trim();
            if (stmt.Length == 0) return;
            var am = ReAssign().Match(stmt);
            if (am.Success)
            {
                var lhs = am.Groups[1].Value;
                if (!VbaKeywords.Contains(lhs))
                {
                    RecordAssign(lhs);
                    RecordReads(stmt, lhs);
                    return;
                }
            }
            RecordReads(stmt, null);
        }

        void ProcessInlineBody(string body)
        {
            foreach (var piece in SplitOnTopLevelColon(body))
                ProcessStatement(piece);
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // GoTo label — skip
            if (ReLabel().IsMatch(line)) continue;

            // Block-close patterns first
            bool closed = false;
            foreach (var (_, _, closeRe) in BlockPatterns)
            {
                if (closeRe.IsMatch(line))
                {
                    ExitBlock();
                    closed = true;
                    break;
                }
            }
            if (closed) continue;

            // Sibling-branch markers
            var mElseIfInline = ReElseIfInline().Match(line);
            if (mElseIfInline.Success)
            {
                NewSiblingBranch();
                RecordReads(line, null);
                ProcessInlineBody(mElseIfInline.Groups[1].Value);
                continue;
            }
            if (ReElseIf().IsMatch(line))
            {
                NewSiblingBranch();
                RecordReads(line, null);
                continue;
            }
            var mElseInline = ReElseInline().Match(line);
            if (mElseInline.Success)
            {
                NewSiblingBranch();
                ProcessInlineBody(mElseInline.Groups[1].Value);
                continue;
            }
            if (ReElse().IsMatch(line))
            {
                NewSiblingBranch();
                continue;
            }
            if (ReCase().IsMatch(line))
            {
                NewSiblingBranch();
                RecordReads(line, null);
                continue;
            }

            // Block-open patterns
            bool opened = false;
            foreach (var (kind, openRe, _) in BlockPatterns)
            {
                if (openRe.IsMatch(line))
                {
                    EnterBlock(kind);
                    if (kind is "if" or "select" &&
                        blockStack.Count >= 2 &&
                        blockStack.Take(blockStack.Count - 1).Any(k => LoopKinds.Contains(k)))
                    {
                        report.LoopEmbeddedConditions++;
                    }
                    if (kind is "if" or "select")
                        RecordReads(line, null);
                    opened = true;
                    break;
                }
            }
            if (opened) continue;

            // One-line If
            var mInlineIf = ReInlineIf().Match(line);
            if (mInlineIf.Success)
            {
                RecordReads(line, null);
                ProcessInlineBody(mInlineIf.Groups[1].Value);
                continue;
            }

            // Plain executable line
            foreach (var piece in SplitOnTopLevelColon(line))
                ProcessStatement(piece);
        }

        report.NestingComplexity = ClassifyDepth(report.MaxNestingDepth);

        // Interdependency detection
        var interdependent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (varName, asgPaths) in assigns)
        {
            if (!reads.TryGetValue(varName, out var rdPaths)) continue;
            var nonTopAsgs = asgPaths.Where(p => p.Length > 0).ToList();
            if (nonTopAsgs.Count == 0) continue;
            if (asgPaths.Contains(string.Empty)) continue; // top-level assignment exists

            foreach (var rp in rdPaths)
            {
                bool cross = false;
                foreach (var ap in nonTopAsgs)
                {
                    if (!IsPrefix(ap, rp) && !IsPrefix(rp, ap))
                    {
                        cross = true;
                        break;
                    }
                }
                if (cross)
                {
                    interdependent.Add(varName);
                    break;
                }
            }
        }

        report.InterdependentVars = interdependent.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        return report;
    }

    // ── Pre-processing helpers ──────────────────────────────────────────────

    internal static string StripComments(string line)
    {
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"') inString = !inString;
            else if (ch == '\'' && !inString) return line[..i].TrimEnd();
        }
        return line.TrimEnd();
    }

    internal static List<string> JoinContinuations(string code)
    {
        var physical = code.Split('\n');
        var logical = new List<string>();
        var buf = new StringBuilder();

        foreach (var raw in physical)
        {
            var noComment = StripComments(raw);
            var stripped = noComment.TrimEnd();
            if (stripped.EndsWith(" _"))
            {
                buf.Append(stripped[..^2]);
                buf.Append(' ');
            }
            else
            {
                buf.Append(stripped);
                logical.Add(buf.ToString());
                buf.Clear();
            }
        }
        if (buf.Length > 0) logical.Add(buf.ToString());
        return logical;
    }

    private static List<string> SplitOnTopLevelColon(string line)
    {
        var parts = new List<string>();
        var buf = new StringBuilder();
        bool inString = false;

        foreach (char ch in line)
        {
            if (ch == '"') { inString = !inString; buf.Append(ch); }
            else if (ch == ':' && !inString)
            {
                var piece = buf.ToString().Trim();
                if (piece.Length > 0) parts.Add(piece);
                buf.Clear();
            }
            else buf.Append(ch);
        }

        var last = buf.ToString().Trim();
        if (last.Length > 0) parts.Add(last);
        return parts;
    }

    private static string ClassifyDepth(int depth)
    {
        if (depth <= DepthLowMax) return "LOW";
        if (depth <= DepthMediumMax) return "MEDIUM";
        if (depth <= DepthHighMax) return "HIGH";
        return "CRITICAL";
    }

    private static bool IsPrefix(string shorter, string longer)
    {
        return shorter.Length <= longer.Length && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    /// <summary>Maps a complexity tier string to the <see cref="ComplexityTier"/> enum.</summary>
    public static ComplexityTier ToComplexityTier(string tier) => tier switch
    {
        "LOW"      => ComplexityTier.Low,
        "MEDIUM"   => ComplexityTier.Medium,
        "HIGH"     => ComplexityTier.High,
        "CRITICAL" => ComplexityTier.Critical,
        _          => ComplexityTier.Low
    };

    /// <summary>Gets extraction instructions for a tier.</summary>
    public static string GetExtractionInstructions(string tier) =>
        ExtractionInstructions.GetValueOrDefault(tier, ExtractionInstructions["LOW"]);
}

/// <summary>Structured complexity profile for a single code chunk.</summary>
public sealed class ComplexityReport
{
    public int MaxNestingDepth { get; set; }
    public string NestingComplexity { get; set; } = "LOW";
    public int BranchCount { get; set; }
    public Dictionary<string, int> BlockKindCounts { get; set; } = new();
    public List<string> InterdependentVars { get; set; } = [];
    public int LoopEmbeddedConditions { get; set; }
    public bool TruncatedDueToSize { get; set; }

    public string Tier => NestingComplexity;

    public bool IsHighComplexity => NestingComplexity is "HIGH" or "CRITICAL";

    public string ToPromptBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine("COMPLEXITY CONTEXT (computed pre-analysis of this chunk):");
        sb.AppendLine($"  nesting_depth: {MaxNestingDepth} ({NestingComplexity})");
        sb.AppendLine($"  branch_count:  {BranchCount}");

        if (BlockKindCounts.Count > 0)
        {
            var kinds = string.Join(", ", BlockKindCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
            sb.AppendLine($"  block_kinds:   {kinds}");
        }
        if (InterdependentVars.Count > 0)
        {
            var shown = string.Join(", ", InterdependentVars.Take(12));
            var extra = InterdependentVars.Count > 12
                ? $" (+{InterdependentVars.Count - 12} more)" : "";
            sb.AppendLine($"  interdependent_vars: {shown}{extra}");
        }
        if (LoopEmbeddedConditions > 0)
            sb.AppendLine($"  loop_embedded_conditions: {LoopEmbeddedConditions}");
        if (TruncatedDueToSize)
            sb.AppendLine("  (note: chunk exceeded analysis size cap; counts are partial)");

        sb.AppendLine("EXTRACTION INSTRUCTIONS:");
        sb.AppendLine($"  {ComplexityAnalyzer.GetExtractionInstructions(Tier)}");

        var strategy = new List<string>();
        if (InterdependentVars.Count > 0)
        {
            strategy.Add(
                "  - Interdependent variables listed above are SET in one " +
                "branch and READ in another. When a rule mentions one of " +
                "these variables, name BOTH the setter context and the " +
                "reader context in the description — the rule loses meaning " +
                "without that linkage.");
        }
        if (LoopEmbeddedConditions > 0)
        {
            strategy.Add(
                "  - Loop-embedded conditions detected. These are per-row / " +
                "batch business rules, NOT iteration mechanics. Extract them " +
                "as their own rules; do NOT collapse them under DO NOT " +
                "EXTRACT #4 (LOOP / ITERATION MECHANICS).");
        }
        if (strategy.Count > 0)
        {
            sb.AppendLine("STRATEGY FOR THIS CHUNK:");
            foreach (var s in strategy)
                sb.AppendLine(s);
        }

        return sb.ToString();
    }
}
