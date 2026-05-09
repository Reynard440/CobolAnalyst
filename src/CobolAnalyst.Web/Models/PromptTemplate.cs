namespace CobolAnalyst.Web.Models;

/// <summary>A recorded F1 score from running analysis with a specific prompt template.</summary>
public sealed class TemplateValidationRecord
{
    public DateTime RunAt { get; set; } = DateTime.UtcNow;
    public string SessionId { get; set; } = string.Empty;
    public string SessionName { get; set; } = string.Empty;
    public double F1 { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public int TruePositives { get; set; }
    public int FalsePositives { get; set; }
    public int FalseNegatives { get; set; }
}

/// <summary>A versioned prompt template that overrides parts of the default extraction prompt.</summary>
public sealed class PromptTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>ID of the template this was derived from, if any.</summary>
    public string? ParentId { get; set; }

    /// <summary>Name of the parent template for display.</summary>
    public string? ParentName { get; set; }

    /// <summary>Whether this is the currently active template used for all new analyses.</summary>
    public bool IsActive { get; set; }

    /// <summary>Overrides the default analyst persona sentence. Null = use default.</summary>
    public string? PersonaOverride { get; set; }

    /// <summary>Overrides the suppression list. Null = use default for detected language.</summary>
    public string? SuppressionOverride { get; set; }

    /// <summary>Extra instructions appended before the Output Instructions section.</summary>
    public string? AdditionalInstructions { get; set; }

    /// <summary>Validation history — F1 scores recorded each time this template was used in a validated run.</summary>
    public List<TemplateValidationRecord> ValidationHistory { get; set; } = [];

    public double? BestF1 => ValidationHistory.Count == 0 ? null : ValidationHistory.Max(r => r.F1);
    public double? LatestF1 => ValidationHistory.Count == 0 ? null : ValidationHistory[^1].F1;
}
