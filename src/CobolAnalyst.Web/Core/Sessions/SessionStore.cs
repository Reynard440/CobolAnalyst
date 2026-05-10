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
                var (decided, accepted, rejected, flagged, pending, total) = ComputeCounts(root);
                summaries.Add(new SessionSummary
                {
                    Id = root.TryGetStringProp("Id") ?? Path.GetFileNameWithoutExtension(file),
                    Name = root.TryGetStringProp("Name") ?? "(unnamed)",
                    CreatedAt = root.TryGetProperty("CreatedAt", out var ts) && ts.TryGetDateTime(out var dt)
                        ? dt : File.GetCreationTimeUtc(file),
                    FileCount = root.TryGetProperty("Files", out var filesEl) ? filesEl.GetArrayLength() : 0,
                    RuleCount = total,
                    DecisionProgress = $"{decided}/{total}",
                    AcceptedCount    = accepted,
                    RejectedCount    = rejected,
                    FlaggedCount     = flagged,
                    PendingCount     = pending
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

    /// <summary>
    /// Reloads the session from disk, replaces its rules with <paramref name="rules"/>,
    /// and writes it back. Used to persist Accept / Reject / Flag decisions.
    /// </summary>
    public async Task UpdateDecisionsAsync(string id, List<ExtractedRule> rules)
    {
        var session = await LoadAsync(id);
        if (session is null)
        {
            _logger.LogWarning("UpdateDecisions: session {Id} not found on disk", id);
            return;
        }
        session.Rules = rules;
        await SaveAsync(session);
    }

    private string SessionPath(string id) => Path.Combine(_sessionsDir, $"{id}.json");

    /// <summary>Counts rule decisions from a raw JSON session element.</summary>
    private static (int decided, int accepted, int rejected, int flagged, int pending, int total)
        ComputeCounts(JsonElement root)
    {
        if (!root.TryGetProperty("Rules", out var rulesEl))
            return (0, 0, 0, 0, 0, 0);

        int total = 0, accepted = 0, rejected = 0, flagged = 0;
        foreach (var r in rulesEl.EnumerateArray())
        {
            total++;
            var decision = r.TryGetProperty("Decision", out var d) ? d.GetString() : "Pending";
            switch (decision)
            {
                case "Accepted": accepted++; break;
                case "Rejected": rejected++; break;
                case "Flagged":  flagged++;  break;
            }
        }
        int pending = total - accepted - rejected - flagged;
        int decided = accepted + rejected + flagged;
        return (decided, accepted, rejected, flagged, pending, total);
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

    /// <summary>Number of rules accepted by the analyst.</summary>
    public int AcceptedCount { get; init; }

    /// <summary>Number of rules rejected by the analyst.</summary>
    public int RejectedCount { get; init; }

    /// <summary>Number of rules flagged for further review.</summary>
    public int FlaggedCount { get; init; }

    /// <summary>Number of rules with no decision yet.</summary>
    public int PendingCount { get; init; }
}

file static class JsonElementSessionExtensions
{
    public static string? TryGetStringProp(this JsonElement el, string name)
        => el.TryGetProperty(name, out var prop) ? prop.GetString() : null;
}
