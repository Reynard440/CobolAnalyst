namespace CobolAnalyst.Web.Models;

/// <summary>
/// A structured code-edit request parsed from an [EDIT_REQUEST]…[/EDIT_REQUEST] block
/// in an LLM response.
/// </summary>
public sealed class EditRequest
{
    /// <summary>One-sentence description of the overall change.</summary>
    public string Intent { get; set; } = string.Empty;

    /// <summary>Filename of the file to be modified.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>Individual text replacements that make up the edit.</summary>
    public List<EditChange> Changes { get; set; } = [];
}

/// <summary>A single text replacement within an <see cref="EditRequest"/>.</summary>
public sealed class EditChange
{
    /// <summary>Type of change — currently always "replace".</summary>
    public string Type { get; set; } = "replace";

    /// <summary>Plain-English explanation of what this change does and why.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Verbatim text from the source file to be replaced.</summary>
    public string OriginalText { get; set; } = string.Empty;

    /// <summary>Replacement text (must preserve original indentation style).</summary>
    public string ModifiedText { get; set; } = string.Empty;

    /// <summary>Approximate 1-based line number as a search hint.</summary>
    public int LineHint { get; set; }

    /// <summary>Function / paragraph name where the change occurs.</summary>
    public string Context { get; set; } = string.Empty;
}
