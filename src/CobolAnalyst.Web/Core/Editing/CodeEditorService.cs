using CobolAnalyst.Web.Models;
using Microsoft.Extensions.Logging;

namespace CobolAnalyst.Web.Core.Editing;

/// <summary>Type of a diff line.</summary>
public enum DiffLineType { Context, Added, Removed }

/// <summary>A single line in a diff view.</summary>
public sealed record DiffLine(DiffLineType Type, string Text);

/// <summary>Result returned by <see cref="CodeEditorService.ApplyEditRequest"/>.</summary>
public sealed class EditApplyResult
{
    /// <summary>Whether the edits were applied successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Error description when <see cref="Success"/> is <c>false</c>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Path to the backup file created before modification.</summary>
    public string? BackupPath { get; init; }
}

/// <summary>Locates, diffs, and applies code edits to session source files.</summary>
public sealed class CodeEditorService
{
    private readonly ILogger<CodeEditorService> _logger;

    /// <summary>Initialises the service.</summary>
    public CodeEditorService(ILogger<CodeEditorService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Checks whether <paramref name="searchText"/> can be found in <paramref name="filePath"/>
    /// using exact match first, then normalised-whitespace match.
    /// Returns the file content string if found, or <c>null</c> if not found.
    /// </summary>
    public string? FindTextInFile(string filePath, string searchText)
    {
        if (!File.Exists(filePath)) return null;
        var content = File.ReadAllText(filePath);

        // Strategy 1: exact substring
        if (content.Contains(searchText, StringComparison.Ordinal))
            return content;

        // Strategy 2: normalise whitespace
        if (NormaliseWhitespace(content).Contains(NormaliseWhitespace(searchText), StringComparison.Ordinal))
            return content;

        return null;
    }

    /// <summary>
    /// Generates a unified-style line diff between <paramref name="original"/> and
    /// <paramref name="modified"/> using the longest-common-subsequence algorithm.
    /// </summary>
    public IReadOnlyList<DiffLine> GenerateDiff(string original, string modified)
    {
        var origLines = original.Split('\n');
        var modLines  = modified.Split('\n');
        var lcs = ComputeLcs(origLines, modLines);
        var result = new List<DiffLine>();

        int i = 0, j = 0, k = 0;
        while (i < origLines.Length || j < modLines.Length)
        {
            if (i < origLines.Length && j < modLines.Length &&
                k < lcs.Count && origLines[i] == lcs[k] && modLines[j] == lcs[k])
            {
                result.Add(new DiffLine(DiffLineType.Context, origLines[i]));
                i++; j++; k++;
            }
            else if (i < origLines.Length && (k >= lcs.Count || origLines[i] != lcs[k]))
            {
                result.Add(new DiffLine(DiffLineType.Removed, origLines[i]));
                i++;
            }
            else if (j < modLines.Length)
            {
                result.Add(new DiffLine(DiffLineType.Added, modLines[j]));
                j++;
            }
            else
            {
                break; // safety exit
            }
        }

        return result;
    }

    /// <summary>
    /// Applies all changes in <paramref name="request"/> to the matching source file in
    /// <paramref name="session"/>.  Creates a timestamped backup before modifying.
    /// All changes must be locatable before any file modification occurs.
    /// Never throws — failures are reported in the returned <see cref="EditApplyResult"/>.
    /// </summary>
    public async Task<EditApplyResult> ApplyEditRequest(EditRequest request, AnalysisSession session)
    {
        try
        {
            var sourceFile = session.SourceFiles.FirstOrDefault(f =>
                string.Equals(f.FileName, request.File, StringComparison.OrdinalIgnoreCase));

            if (sourceFile is null)
                return new EditApplyResult
                {
                    Success = false,
                    ErrorMessage = $"Source file '{request.File}' not found in session."
                };

            var filePath = sourceFile.PermanentPath;
            if (!File.Exists(filePath))
                return new EditApplyResult
                {
                    Success = false,
                    ErrorMessage = $"File not found on disk: {filePath}"
                };

            var originalContent = await File.ReadAllTextAsync(filePath);

            // Verify every change is locatable before touching the file
            foreach (var change in request.Changes)
            {
                if (FindTextInFile(filePath, change.OriginalText) is null)
                    return new EditApplyResult
                    {
                        Success = false,
                        ErrorMessage = $"Could not locate text for change: \"{change.Description}\""
                    };
            }

            // Backup
            var backupPath = filePath + $".bak_{DateTime.UtcNow:yyyyMMddHHmmss}";
            await File.WriteAllTextAsync(backupPath, originalContent);

            // Apply changes sequentially
            var updatedContent = originalContent;
            foreach (var change in request.Changes)
                updatedContent = ApplyChange(updatedContent, change.OriginalText, change.ModifiedText);

            await File.WriteAllTextAsync(filePath, updatedContent);
            _logger.LogInformation("Applied {Count} change(s) to {File}", request.Changes.Count, request.File);

            return new EditApplyResult { Success = true, BackupPath = backupPath };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyEditRequest failed for {File}", request.File);
            return new EditApplyResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────────

    private static string ApplyChange(string content, string original, string modified)
    {
        // Strategy 1: exact substring replacement
        var idx = content.IndexOf(original, StringComparison.Ordinal);
        if (idx >= 0)
            return content[..idx] + modified + content[(idx + original.Length)..];

        // Strategy 2: line-level normalised match
        var origLines    = original.Split('\n');
        var contentLines = content.Split('\n');
        var normOrig     = NormaliseWhitespace(original);

        for (int i = 0; i <= contentLines.Length - origLines.Length; i++)
        {
            var window = string.Join('\n', contentLines.Skip(i).Take(origLines.Length));
            if (NormaliseWhitespace(window) == normOrig)
            {
                var before = string.Join('\n', contentLines.Take(i));
                var after  = string.Join('\n', contentLines.Skip(i + origLines.Length));
                return (before.Length > 0 ? before + '\n' : "")
                     + modified
                     + (after.Length  > 0 ? '\n' + after  : "");
            }
        }

        return content; // could not locate — return unchanged
    }

    private static string NormaliseWhitespace(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");

    private static List<string> ComputeLcs(string[] a, string[] b)
    {
        int m = a.Length, n = b.Length;
        var dp = new int[m + 1, n + 1];

        for (int i = 1; i <= m; i++)
            for (int j = 1; j <= n; j++)
                dp[i, j] = a[i - 1] == b[j - 1]
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        var lcs = new List<string>();
        int r = m, c = n;
        while (r > 0 && c > 0)
        {
            if (a[r - 1] == b[c - 1]) { lcs.Insert(0, a[r - 1]); r--; c--; }
            else if (dp[r - 1, c] > dp[r, c - 1]) r--;
            else c--;
        }

        return lcs;
    }
}
