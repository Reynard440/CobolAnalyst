namespace CobolAnalyst.Web.Models;

/// <summary>Represents a COBOL source file loaded for analysis.</summary>
public sealed class CobolFile
{
    /// <summary>Absolute path to the source file.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>File name without directory.</summary>
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>Raw source text decoded from the file.</summary>
    public string SourceText { get; init; } = string.Empty;

    /// <summary>Number of lines in the source file.</summary>
    public int LineCount { get; init; }

    /// <summary>File size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>SHA-256 hash of the file content (hex).</summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>
    /// Role assigned to this file: Analyze (extract rules), Context (inject as background),
    /// or Skip (ignore). Set by the analyst before analysis starts.
    /// </summary>
    public FileRole Role { get; set; } = FileRole.Analyze;
}
