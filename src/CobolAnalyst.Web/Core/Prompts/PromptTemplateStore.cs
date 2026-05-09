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

    // Generic COBOL business-rule extraction prompt — no proprietary content.
    private const string BaselineAdditionalInstructions =
        "COBOL business rule analyst. Standard ANSI COBOL 85 and IBM Enterprise COBOL.\n\n" +
        "Method: BASELINE\n\n" +
        "Watch for: implicit numeric truncation, REDEFINES aliasing, level-88 condition\n" +
        "names used as flags, PERFORM THRU with fall-through, ALTER statements,\n" +
        "hardcoded literal values that should be table-driven, date arithmetic without\n" +
        "century handling.\n\n" +
        "EXTRACTION MANDATE: Your primary task is to find and extract business rules.\n" +
        "When uncertain whether something is a rule, INCLUDE IT. A missed rule is a\n" +
        "worse error than a borderline inclusion. Do not self-filter aggressively.\n\n" +
        "EXTRACTION ABSTRACTION LEVEL:\n" +
        "Extract the WHAT, not the HOW. A rule describes business intent — a policy,\n" +
        "threshold, mapping, workflow step, or constraint. Implementation mechanics\n" +
        "(counters, MOVE statements, PERFORM iteration, COMPUTE intermediate\n" +
        "arithmetic) are NOT rules. If your description reads like a code comment for\n" +
        "one statement, back up to the paragraph's purpose and extract that instead.\n\n" +
        "PARAGRAPH / SECTION HANDLING:\n" +
        "Each named paragraph is a logical unit. Extract rules at the paragraph level,\n" +
        "not the statement level. A paragraph that performs three validations yields\n" +
        "three rules, not thirty.\n\n" +
        "NESTED IF HANDLING:\n" +
        "When code contains deeply nested IF/ELSE blocks (4+ levels):\n" +
        "1. Identify the ROOT decision point (outermost IF condition)\n" +
        "2. Extract ONE rule per distinct logical outcome, not per combination\n" +
        "3. For code appearing in ALL branches: extract ONCE, note \"applies regardless of path\"\n" +
        "4. Use notation: \"When [condition]: [outcome]\"\n" +
        "5. If nesting exceeds 6 levels: extract confident rules only, flag others\n" +
        "   as NEEDS_HUMAN_REVIEW\n\n" +
        "EVALUATE HANDLING:\n" +
        "When you encounter an EVALUATE block enforcing different limits, thresholds,\n" +
        "or behaviours per code or category, extract one rule per WHEN branch.\n" +
        "Each branch enforces a distinct business rule.\n\n" +
        "CONSTRAINT TYPE DETECTION:\n" +
        "TYPE: constraint — Use for hard limits on quantities, amounts, dates, or\n" +
        "counts enforced with explicit boundary checks. Do NOT route these to\n" +
        "business_rule just because they enforce a business policy. If they check a\n" +
        "numeric boundary they are constraints.\n\n" +
        "TYPE: business_rule — Use when a condition governs a process decision or\n" +
        "consequence: what the program DOES in response to a state.\n\n" +
        "DATA MAPPING TYPE DETECTION:\n" +
        "A list of hardcoded values that maps codes or identifiers to named entities\n" +
        "is a Data Mapping, not a Business Rule. Examples: holiday date tables,\n" +
        "plant-code-to-name mappings, return-code-to-meaning tables.\n\n" +
        "DO NOT EXTRACT:\n" +
        "1. Stubs — procedures whose body contains only error scaffolding and a\n" +
        "   default return value with no conditional logic\n" +
        "2. Bare return-code assignments with no condition\n" +
        "3. Comment-only evidence (no executable statement in the snippet)\n" +
        "4. Error handler label blocks (PARAGRAPH-NAME-ERROR: followed only by\n" +
        "   error description capture and a system return code)\n" +
        "5. STOP RUN, GOBACK, EXIT PROGRAM statements alone\n\n" +
        "PRECISION-CRITICAL EXTRACTION:\n" +
        "1. THRESHOLD + CONSEQUENCE — include BOTH the threshold value AND what\n" +
        "   happens when it is breached. \"If more than 15% of records fail, the\n" +
        "   batch is rejected and processing halts\" — not just \"there is a 15% threshold.\"\n" +
        "2. NAMED CONSTANTS — use the constant name in the rule description, not\n" +
        "   a derived numeric value. Write \"exceeds MAX-ORDER-QTY\" not \"exceeds 999.\"\n" +
        "3. ALGORITHM IDENTIFICATION — when extracting a recognisable algorithm\n" +
        "   (Luhn check digit, modulus-11, ISO week number), name the algorithm.\n" +
        "4. COMPOUND FUNCTIONS — when a paragraph encodes both a fixed lookup table\n" +
        "   AND a dynamic algorithm, extract exactly two rules: one data_mapping\n" +
        "   and one calculation.\n\n" +
        "Extract EVERY business rule, calculation, validation, workflow, and\n" +
        "data mapping. Plain English — a non-technical business analyst must\n" +
        "understand each rule.\n" +
        "RETURN ONLY VALID JSON.";

    private void EnsureBaseline()
    {
        var files = Directory.GetFiles(_dir, "*.json");

        // No templates yet — create fresh baseline with the COBOL prompt.
        if (files.Length == 0)
        {
            var baseline = new PromptTemplate
            {
                Name                   = "Baseline",
                Description            = "Default COBOL extraction prompt.",
                IsActive               = true,
                AdditionalInstructions = BaselineAdditionalInstructions
            };
            File.WriteAllText(FilePath(baseline.Id),
                JsonSerializer.Serialize(baseline, JsonOpts));
            return;
        }

        // Migration: update any existing baseline that has no instructions yet.
        foreach (var file in files)
        {
            var t = Load(file);
            if (t is null) continue;
            if (t.Name == "Baseline" && string.IsNullOrWhiteSpace(t.AdditionalInstructions))
            {
                t.AdditionalInstructions = BaselineAdditionalInstructions;
                t.Description            = "Default COBOL extraction prompt.";
                File.WriteAllText(file, JsonSerializer.Serialize(t, JsonOpts));
            }
        }
    }
}
