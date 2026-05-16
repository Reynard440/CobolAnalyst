using System.Text.RegularExpressions;

namespace CobolAnalyst.Web.Core.Analysis;

/// <summary>
/// Extracts public method/procedure names from VB and SQL files for cross-file dependency detection.
/// </summary>
public static partial class SymbolExtractor
{
    // VB: Sub / Function / Property declarations
    [GeneratedRegex(
        @"^\s*(?:Public|Friend)?\s*(?:Sub|Function|Property)\s+(\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex VbSymbol();

    // SQL: CREATE PROCEDURE/FUNCTION
    [GeneratedRegex(
        @"CREATE\s+(?:OR\s+ALTER\s+)?(?:PROCEDURE|PROC|FUNCTION)\s+(?:\[?\w+\]?\.)?(\[?\w+\]?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SqlSymbol();

    // COBOL: paragraph names in Area A
    [GeneratedRegex(@"^\s{6,7}([A-Z][A-Z0-9\-]+)\s*\.", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CobolSymbol();

    public static List<string> ExtractSymbols(string filePath, string sourceText)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var regex = ext switch
        {
            ".sql" => SqlSymbol(),
            ".cbl" or ".cob" or ".cpy" => CobolSymbol(),
            _ => VbSymbol()
        };

        return regex.Matches(sourceText)
            .Select(m => m.Groups[1].Value.Trim('[', ']'))
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
