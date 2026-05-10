using CobolAnalyst.Web.Models;
using Microsoft.Extensions.Logging;

namespace CobolAnalyst.Web.Core.Query;

/// <summary>Manages system-prompt construction, response parsing, and edit-intent detection for the Query tab.</summary>
public sealed class QueryService
{
    private static readonly string[] EditKeywords =
        ["fix", "change", "update", "modify", "refactor", "replace", "edit",
         "rewrite", "rename", "add", "remove", "delete", "extract", "move"];

    private readonly ILogger<QueryService> _logger;

    /// <summary>Initialises the service.</summary>
    public QueryService(ILogger<QueryService> logger)
    {
        _logger = logger;
    }

    /// <summary>Returns <c>true</c> if the user message looks like a code-edit request.</summary>
    public bool IsEditIntent(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant();
        return EditKeywords.Any(kw => lower.Contains(kw, StringComparison.Ordinal));
    }

    /// <summary>Builds the system prompt for the analysis chat session.</summary>
    public string BuildSystemPrompt(AnalysisSession session)
    {
        var rulesSummary = string.Join("\n", session.Rules.Select((r, i) =>
            $"{i + 1}. [{r.Type}] {r.Label} ({r.Confidence}) — {r.Description}"));

        var fileList = string.Join(", ", session.Files.Select(f => f.FileName));

        var sourceSection = session.SourceFiles.Count > 0
            ? string.Join("\n", session.SourceFiles.Select(f => $"- {f.FileName}"))
            : "No source files stored.";

        return $"""
            You are a legacy code modernisation analyst. You have analysed the following source files: {fileList}

            ## Extracted Business Rules

            {rulesSummary}

            ## Source Files Available

            {sourceSection}

            ## Instructions

            Answer questions about the business rules and source code. Be specific: reference rule labels and types by name. If no rules match a question, say so clearly.

            If the user asks you to make a code change, respond with your explanation first, then an edit proposal in this exact format:

            [EDIT_REQUEST]
            INTENT: <one-sentence description of the overall change>
            FILE: <filename only — not a full path>
            CHANGE:
            DESCRIPTION: <plain-English explanation of what this change does and why>
            ORIGINAL:
            <verbatim text from the source file to replace — must match exactly>
            MODIFIED:
            <replacement text — preserve original indentation style>
            END_CHANGE
            [/EDIT_REQUEST]

            You may include multiple CHANGE blocks within one [EDIT_REQUEST] block.
            Only propose changes you are confident about. Preserve indentation exactly.
            """;
    }

    /// <summary>Loads source file contents from <paramref name="session"/>.SourceFiles paths.</summary>
    public async Task<string> LoadSourceCode(AnalysisSession session)
    {
        if (session.SourceFiles.Count == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        foreach (var sf in session.SourceFiles)
        {
            if (!File.Exists(sf.PermanentPath)) continue;
            var content = await File.ReadAllTextAsync(sf.PermanentPath);
            sb.AppendLine($"=== {sf.FileName} ===");
            sb.AppendLine(content);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses an LLM response — extracts any <c>[EDIT_REQUEST]</c> block.
    /// Returns the plain-text portion and the parsed <see cref="EditRequest"/> (or <c>null</c>).
    /// </summary>
    public (string PlainText, EditRequest? EditRequest) ParseResponse(string response)
    {
        const string openTag  = "[EDIT_REQUEST]";
        const string closeTag = "[/EDIT_REQUEST]";

        var openIdx = response.IndexOf(openTag, StringComparison.Ordinal);
        if (openIdx < 0)
            return (response, null);

        var closeIdx = response.IndexOf(closeTag, openIdx, StringComparison.Ordinal);
        if (closeIdx < 0)
            return (response, null);

        var plainText  = (response[..openIdx] + response[(closeIdx + closeTag.Length)..]).Trim();
        var block      = response[(openIdx + openTag.Length)..closeIdx];
        var editRequest = ParseEditRequest(block);
        return (plainText, editRequest);
    }

    /// <summary>Parses the inner text of an <c>[EDIT_REQUEST]…[/EDIT_REQUEST]</c> block.</summary>
    public EditRequest ParseEditRequest(string block)
    {
        var request = new EditRequest();
        var lines = block.Split('\n');
        int i = 0;

        while (i < lines.Length)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("INTENT:", StringComparison.OrdinalIgnoreCase))
            {
                request.Intent = line["INTENT:".Length..].Trim();
                i++;
            }
            else if (line.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
            {
                request.File = line["FILE:".Length..].Trim();
                i++;
            }
            else if (line == "CHANGE:")
            {
                i++;
                var change = ParseChange(lines, ref i);
                if (change is not null) request.Changes.Add(change);
            }
            else
            {
                i++;
            }
        }

        return request;
    }

    private static EditChange? ParseChange(string[] lines, ref int i)
    {
        var change = new EditChange();

        while (i < lines.Length)
        {
            var line = lines[i].Trim();

            if (line == "END_CHANGE")
            {
                i++;
                return change.OriginalText.Length > 0 ? change : null;
            }

            if (line.StartsWith("DESCRIPTION:", StringComparison.OrdinalIgnoreCase))
            {
                change.Description = line["DESCRIPTION:".Length..].Trim();
                i++;
            }
            else if (line == "ORIGINAL:")
            {
                i++;
                var sb = new System.Text.StringBuilder();
                while (i < lines.Length && lines[i].Trim() != "MODIFIED:")
                {
                    sb.AppendLine(lines[i]);
                    i++;
                }
                change.OriginalText = sb.ToString().TrimEnd('\r', '\n');
            }
            else if (line == "MODIFIED:")
            {
                i++;
                var sb = new System.Text.StringBuilder();
                while (i < lines.Length && lines[i].Trim() != "END_CHANGE")
                {
                    sb.AppendLine(lines[i]);
                    i++;
                }
                change.ModifiedText = sb.ToString().TrimEnd('\r', '\n');
            }
            else
            {
                i++;
            }
        }

        return change.OriginalText.Length > 0 ? change : null;
    }
}
