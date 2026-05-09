using System.Text.Json;
using CobolAnalyst.Web.Models;

namespace CobolAnalyst.Web.Core.Prompts;

/// <summary>
/// Persists prompt templates as JSON files under {DataPath}/prompts/.
/// Exactly one template is active at a time.
/// </summary>
public sealed class PromptTemplateStore
{
    private readonly string _dir;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PromptTemplateStore(IConfiguration cfg)
    {
        var root = cfg["Storage:DataPath"] ?? "./data";
        _dir = Path.Combine(root, "prompts");
        Directory.CreateDirectory(_dir);
        EnsureBaseline();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public List<PromptTemplate> GetAll()
    {
        lock (_lock)
        {
            return Directory.GetFiles(_dir, "*.json")
                            .Select(Load)
                            .OfType<PromptTemplate>()
                            .OrderBy(t => t.CreatedAt)
                            .ToList();
        }
    }

    public PromptTemplate? GetById(string id)
    {
        lock (_lock) { return Load(FilePath(id)); }
    }

    public PromptTemplate? GetActive()
    {
        lock (_lock)
        {
            return Directory.GetFiles(_dir, "*.json")
                            .Select(Load)
                            .OfType<PromptTemplate>()
                            .FirstOrDefault(t => t.IsActive);
        }
    }

    public void Save(PromptTemplate template)
    {
        lock (_lock)
        {
            File.WriteAllText(FilePath(template.Id),
                JsonSerializer.Serialize(template, JsonOpts));
        }
    }

    public void Activate(string id)
    {
        lock (_lock)
        {
            foreach (var file in Directory.GetFiles(_dir, "*.json"))
            {
                var t = Load(file);
                if (t is null) continue;
                bool target = t.Id == id;
                if (t.IsActive == target) continue;
                t.IsActive = target;
                File.WriteAllText(file, JsonSerializer.Serialize(t, JsonOpts));
            }
        }
    }

    public PromptTemplate Derive(string parentId, string newName)
    {
        var parent = GetById(parentId)
            ?? throw new InvalidOperationException($"Template {parentId} not found.");

        var child = new PromptTemplate
        {
            Name                  = newName,
            Description           = $"Derived from '{parent.Name}'",
            ParentId              = parent.Id,
            ParentName            = parent.Name,
            PersonaOverride       = parent.PersonaOverride,
            SuppressionOverride   = parent.SuppressionOverride,
            AdditionalInstructions = parent.AdditionalInstructions,
            IsActive              = false
        };

        lock (_lock)
        {
            File.WriteAllText(FilePath(child.Id),
                JsonSerializer.Serialize(child, JsonOpts));
        }
        return child;
    }

    public void RecordValidation(string templateId, TemplateValidationRecord record)
    {
        lock (_lock)
        {
            var t = Load(FilePath(templateId));
            if (t is null) return;
            t.ValidationHistory.Add(record);
            File.WriteAllText(FilePath(t.Id), JsonSerializer.Serialize(t, JsonOpts));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string FilePath(string id) => Path.Combine(_dir, $"{id}.json");

    private static PromptTemplate? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<PromptTemplate>(File.ReadAllText(path), JsonOpts);
        }
        catch { return null; }
    }

    private void EnsureBaseline()
    {
        if (Directory.GetFiles(_dir, "*.json").Length > 0) return;

        var baseline = new PromptTemplate
        {
            Name        = "Baseline",
            Description = "Default extraction prompt — no overrides applied.",
            IsActive    = true
        };
        File.WriteAllText(FilePath(baseline.Id),
            JsonSerializer.Serialize(baseline, JsonOpts));
    }
}
