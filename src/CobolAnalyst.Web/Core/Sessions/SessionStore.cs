using System.Text.Json;
using CobolAnalyst.Web.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CobolAnalyst.Web.Core.Sessions;

/// <summary>Persists and retrieves analysis sessions as JSON files.</summary>
public sealed class SessionStore
{
    private readonly string _sessionsDir;
    private readonly ILogger<SessionStore> _logger;

    /// <summary>Initialises the store, creating the sessions directory if needed.</summary>
    public SessionStore(IConfiguration config, ILogger<SessionStore> logger)
    {
        _logger = logger;
        var dataPath = config["Storage:DataPath"] ?? "./data";
        _sessionsDir = Path.Combine(dataPath, "sessions");
        Directory.CreateDirectory(_sessionsDir);
    }

    /// <summary>Persists <paramref name="session"/> to disk, updating UpdatedAt.</summary>
    public async Task SaveAsync(AnalysisSession session)
    {
        session.UpdatedAt = DateTime.UtcNow;
        var path = SessionPath(session.Id);
        var json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
        _logger.LogDebug("Session {Id} saved to {Path}", session.Id, path);
    }

    /// <summary>Returns a lightweight summary list of all saved sessions.</summary>
    public async Task<List<SessionSummary>> ListAsync()
    {
        var summaries = new List<SessionSummary>();
        foreach (var file in Directory.EnumerateFiles(_sessionsDir, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                summaries.Add(new SessionSummary
                {
                    Id = root.TryGetStringProp("Id") ?? Path.GetFileNameWithoutExtension(file),
                    Name = root.TryGetStringProp("Name") ?? "(unnamed)",
                    CreatedAt = root.TryGetProperty("CreatedAt", out var ts) && ts.TryGetDateTime(out var dt)
                        ? dt : File.GetCreationTimeUtc(file),
                    FileCount = root.TryGetProperty("Files", out var files) ? files.GetArrayLength() : 0,
                    RuleCount = root.TryGetProperty("Rules", out var rules) ? rules.GetArrayLength() : 0,
                    DecisionProgress = ComputeProgress(root)
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read session file {File}", file);
            }
        }
        return summaries.OrderByDescending(s => s.CreatedAt).ToList();
    }

    /// <summary>Loads a full session by <paramref name="id"/>.</summary>
    public async Task<AnalysisSession?> LoadAsync(string id)
    {
        var path = SessionPath(id);
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<AnalysisSession>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load session {Id}", id);
            return null;
        }
    }

    private string SessionPath(string id) => Path.Combine(_sessionsDir, $"{id}.json");

    private static string ComputeProgress(JsonElement root)
    {
        if (!root.TryGetProperty("Rules", out var rulesEl)) return "0/0";
        int total = rulesEl.GetArrayLength();
        int decided = 0;
        foreach (var r in rulesEl.EnumerateArray())
        {
            if (r.TryGetProperty("Decision", out var d) && d.GetString() != "Pending")
                decided++;
        }
        return $"{decided}/{total}";
    }
}

/// <summary>Lightweight session summary for the sessions list page.</summary>
public sealed class SessionSummary
{
    /// <summary>Session identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable session name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>UTC creation timestamp.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Number of files in this session.</summary>
    public int FileCount { get; init; }

    /// <summary>Number of extracted rules.</summary>
    public int RuleCount { get; init; }

    /// <summary>Decision progress string e.g. "12/20".</summary>
    public string DecisionProgress { get; init; } = string.Empty;
}

file static class JsonElementSessionExtensions
{
    public static string? TryGetStringProp(this JsonElement el, string name)
        => el.TryGetProperty(name, out var prop) ? prop.GetString() : null;
}
