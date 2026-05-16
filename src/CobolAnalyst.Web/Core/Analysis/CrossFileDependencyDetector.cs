namespace CobolAnalyst.Web.Core.Analysis;

/// <summary>
/// Detects cross-file symbol references. For each chunk, identifies which symbols
/// from other files are referenced, enabling prompt enrichment with
/// "Calls cross-file: OtherFile::ProcName" context.
/// </summary>
public static class CrossFileDependencyDetector
{
    /// <summary>
    /// Finds cross-file symbol references in the given code.
    /// Returns a dictionary of fileName → list of matched symbols.
    /// </summary>
    public static Dictionary<string, List<string>> DetectDependencies(
        string code,
        Dictionary<string, List<string>> symbolTable,
        string currentFile)
    {
        if (string.IsNullOrWhiteSpace(code) || symbolTable.Count == 0)
            return new();

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (file, symbols) in symbolTable)
        {
            if (file.Equals(currentFile, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var sym in symbols)
            {
                if (code.Contains(sym, StringComparison.OrdinalIgnoreCase))
                {
                    if (!result.TryGetValue(file, out var list))
                    {
                        list = [];
                        result[file] = list;
                    }
                    list.Add(sym);
                }
            }
        }

        return result;
    }

    /// <summary>Formats cross-file dependencies for prompt injection.</summary>
    public static string FormatForPrompt(Dictionary<string, List<string>> deps)
    {
        if (deps.Count == 0) return string.Empty;

        var lines = new List<string> { "Cross-file dependencies detected:" };
        foreach (var (file, symbols) in deps.OrderBy(kv => kv.Key))
        {
            foreach (var sym in symbols)
                lines.Add($"  Calls cross-file: {Path.GetFileNameWithoutExtension(file)}::{sym}");
        }
        return string.Join("\n", lines) + "\n";
    }
}
