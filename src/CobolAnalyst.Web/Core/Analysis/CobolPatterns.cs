namespace CobolAnalyst.Web.Core.Analysis;

/// <summary>
/// Known COBOL-to-VB translation patterns and risk indicators.
/// Used to enrich prompts and flag high-risk code sections.
/// Ports cobol_patterns.py.
/// </summary>
public static class CobolPatterns
{
    private static readonly Dictionary<string, CobolPatternDef> Patterns = new()
    {
        ["sequential_loop"] = new(
            "PERFORM UNTIL translated to Do/While loop",
            ["Do While", "Do Until", "Loop While", "Loop Until"],
            [],
            "medium",
            "May contain off-by-one errors from COBOL translation"),
        ["goto_spaghetti"] = new(
            "COBOL GO TO translated to VB GoTo/Label",
            ["GoTo ", "GoSub ", "Label:"],
            [],
            "high",
            "COBOL-origin spaghetti code - trace all jump paths carefully"),
        ["fixed_length_strings"] = new(
            "COBOL PIC clause fixed-length fields",
            ["Space(", "PadLeft", "PadRight", "Mid(", "Left(", "Right("],
            [],
            "medium",
            "Hardcoded field lengths likely match COBOL PIC definitions - verify with original spec"),
        ["flat_record_struct"] = new(
            "COBOL WORKING-STORAGE flat record translated to module-level vars",
            ["Public ", "Dim ", "Module "],
            [],
            "low",
            "Module-level variables often map 1:1 to COBOL WORKING-STORAGE fields"),
        ["date_arithmetic"] = new(
            "COBOL date manipulation patterns",
            ["DateDiff", "DateAdd", "DateSerial", "CDate", "Format("],
            [],
            "high",
            "COBOL date arithmetic is error-prone when translated - verify century handling (Y2K artifacts)"),
        ["magic_numbers"] = new(
            "COBOL level 88 condition values as hardcoded literals",
            [],
            [],
            "medium",
            "Hardcoded values likely from COBOL level 88 conditions - find original COBOL for intent"),
        ["error_handling"] = new(
            "COBOL status code checking (FILE STATUS, RETURN-CODE)",
            ["On Error", "Err.Number", "Resume Next", "Resume"],
            [],
            "high",
            "COBOL error handling is fundamentally different - On Error Resume Next masks failures"),
        ["cursor_processing"] = new(
            "Row-by-row SQL processing mirroring COBOL READ loop",
            [],
            ["DECLARE", "CURSOR", "FETCH", "@@FETCH_STATUS"],
            "medium",
            "COBOL-style sequential file processing - consider set-based SQL alternatives"),
        ["working_storage_sql"] = new(
            "Temp tables used as COBOL WORKING-STORAGE equivalent",
            [],
            ["#temp", "##temp", "CREATE TABLE #", "INSERT INTO #"],
            "low",
            "Temp tables often mirror COBOL intermediate storage - verify cleanup on error paths"),
        ["compute_arithmetic"] = new(
            "COBOL COMPUTE statement translated to SQL/VB arithmetic",
            [],
            ["ROUND(", "CAST(", "CONVERT(", "* 100", "/ 100"],
            "medium",
            "Verify decimal precision - COBOL COMP-3 packed decimal vs SQL DECIMAL may differ"),
    };

    /// <summary>
    /// Scans code for COBOL-to-VB translation patterns and returns matched flags.
    /// </summary>
    public static List<CobolPatternFlag> PrescanPatterns(string code, string fileType)
    {
        if (string.IsNullOrWhiteSpace(code)) return [];

        bool isSql = fileType.Equals("sql", StringComparison.OrdinalIgnoreCase) ||
                     fileType.StartsWith(".sql", StringComparison.OrdinalIgnoreCase);

        var flags = new List<CobolPatternFlag>();

        foreach (var (name, def) in Patterns)
        {
            var indicators = isSql ? def.SqlIndicators : def.VbIndicators;
            if (indicators.Length == 0) continue;

            foreach (var indicator in indicators)
            {
                if (code.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                {
                    flags.Add(new CobolPatternFlag
                    {
                        PatternName = name,
                        Description = def.Description,
                        Risk = def.Risk,
                        Note = def.Note,
                        MatchedIndicator = indicator
                    });
                    break;
                }
            }
        }

        return flags;
    }

    /// <summary>
    /// Formats prescan flags as a prompt section for injection.
    /// </summary>
    public static string FormatForPrompt(List<CobolPatternFlag> flags)
    {
        if (flags.Count == 0) return string.Empty;

        var lines = new List<string> { "COBOL translation patterns detected in this chunk:" };
        foreach (var f in flags)
            lines.Add($"  - [{f.Risk.ToUpperInvariant()}] {f.Description}: {f.Note}");

        return string.Join("\n", lines) + "\n";
    }
}

public sealed record CobolPatternDef(
    string Description,
    string[] VbIndicators,
    string[] SqlIndicators,
    string Risk,
    string Note);

public sealed class CobolPatternFlag
{
    public string PatternName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Risk { get; init; } = "low";
    public string Note { get; init; } = string.Empty;
    public string MatchedIndicator { get; init; } = string.Empty;
}
