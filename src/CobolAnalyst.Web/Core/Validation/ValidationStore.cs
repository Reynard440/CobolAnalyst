using System.Text.Json;
using CobolAnalyst.Web.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CobolAnalyst.Web.Core.Validation;

/// <summary>
/// Persists <see cref="ValidationResult"/> objects to disk under {DataPath}/validation/.
/// One JSON file per run, named by the result's GUID.
/// </summary>
public sealed class ValidationStore
{
    private readonly string _dir;
    private readonly ILogger<ValidationStore> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public ValidationStore(IConfiguration config, ILogger<ValidationStore> logger)
    {
        _logger = logger;
        var dataPath = config["Storage:DataPath"] ?? "./data";
        _dir = Path.Combine(dataPath, "validation");
        Directory.CreateDirectory(_dir);
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>Persists <paramref name="result"/> to disk.</summary>
    public async Task SaveAsync(ValidationResult result)
    {
        var path = Path.Combine(_dir, $"{result.Id}.json");
        var json = JsonSerializer.Serialize(result, JsonOpts);
        await File.WriteAllTextAsync(path, json);
        _logger.LogInformation("Validation result {Id} saved ({Tp}TP {Fp}FP {Fn}FN F1={F1:F2})",
            result.Id, result.TpCount, result.FpCount, result.FnCount, result.F1);
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>Loads a full <see cref="ValidationResult"/> by id, or null if not found.</summary>
    public async Task<ValidationResult?> LoadAsync(string id)
    {
        var path = Path.Combine(_dir, $"{id}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<ValidationResult>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load validation result {Id}", id);
            return null;
        }
    }

    /// <summary>
    /// Returns lightweight summaries of all saved validation runs, newest first.
    /// Reads metadata from each JSON file without deserialising the full result.
    /// </summary>
    public async Task<List<ValidationSummary>> ListAsync()
    {
        var summaries = new List<ValidationSummary>();

        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                summaries.Add(new ValidationSummary
                {
                    Id               = root.TryGetStringProp("Id")          ?? Path.GetFileNameWithoutExtension(file),
                    SessionId        = root.TryGetStringProp("SessionId")    ?? "",
                    SessionName      = root.TryGetStringProp("SessionName")  ?? "",
                    ModelName        = root.TryGetStringProp("ModelName")    ?? "",
                    RunAt            = root.TryGetProperty("RunAt", out var ts) && ts.TryGetDateTime(out var dt)
                                           ? dt : File.GetCreationTimeUtc(file),
                    Threshold        = root.TryGetFloatProp("Threshold"),
                    GroundTruthCount = root.TryGetIntProp("GroundTruthCount"),
                    TpCount          = root.TryGetIntProp("TpCount"),
                    FpCount          = root.TryGetIntProp("FpCount"),
                    FnCount          = root.TryGetIntProp("FnCount"),
                    // Precision / Recall / F1 are computed properties serialised into JSON
                    F1               = root.TryGetFloatProp("F1"),
                    Precision        = root.TryGetFloatProp("Precision"),
                    Recall           = root.TryGetFloatProp("Recall"),
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read validation file {File}", file);
            }
        }

        return summaries.OrderByDescending(s => s.RunAt).ToList();
    }
}

// ── Local extension helpers ───────────────────────────────────────────────────

file static class JsonElementVsExtensions
{
    public static string? TryGetStringProp(this JsonElement el, string name)
        => el.TryGetProperty(name, out var prop) ? prop.GetString() : null;

    public static float TryGetFloatProp(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return 0f;
        return prop.ValueKind == JsonValueKind.Number ? (float)prop.GetDouble() : 0f;
    }

    public static int TryGetIntProp(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return 0;
        return prop.ValueKind == JsonValueKind.Number ? prop.GetInt32() : 0;
    }
}
