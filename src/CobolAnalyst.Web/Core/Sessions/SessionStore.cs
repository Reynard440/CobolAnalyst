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

    /// <summary>
    /// Deletes a session's JSON file and any associated source files.
    /// Returns <c>true</c> if the session was found and deleted; <c>false</c> otherwise.
    /// Never throws.
    /// </summary>
    public Task<bool> DeleteSessionAsync(string id)
    {
        try
        {
            var path = SessionPath(id);
            if (!File.Exists(path)) return Task.FromResult(false);

            File.Delete(path);

            var sourceFilesDir = Path.Combine(_sessionsDir, "source_files");
            if (Directory.Exists(sourceFilesDir))
            {
                foreach (var f in Directory.EnumerateFiles(sourceFilesDir, $"{id}_*"))
                {
                    try { File.Delete(f); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Could not delete source file {F}", f); }
                }
            }

            _logger.LogInformation("Session {Id} deleted", id);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete session {Id}", id);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Reloads the session from disk, replaces its conversation history, and writes it back.
    /// </summary>
    public async Task UpdateConversationAsync(string id, List<ConversationTurn> history)
    {
        var session = await LoadAsync(id);
        if (session is null)
        {
            _logger.LogWarning("UpdateConversation: session {Id} not found on disk", id);
            return;
        }
        session.ConversationHistory = history;
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
            // ReadStringOrEnum handles both string ("Accepted") and integer (1) enum
            // representations.  Sessions saved before the JSON serialiser was configured
            // to emit string names will have numeric values.
            var decision = r.TryGetProperty("Decision", out var d)
                ? ReadStringOrEnum(d)
                : "Pending";

            // ReviewDecision: Pending=0, Accepted=1, Rejected=2, Flagged=3
            switch (decision)
            {
                case "Accepted": case "1": accepted++; break;
                case "Rejected": case "2": rejected++; break;
                case "Flagged":  case "3": flagged++;  break;
                // "Pending" / "0" / anything else → counts as pending (no-op here)
            }
        }
        int pending = total - accepted - rejected - flagged;
        int decided = accepted + rejected + flagged;
        return (decided, accepted, rejected, flagged, pending, total);
    }

    /// <summary>
    /// Reads a JSON element as a string regardless of whether it was serialised as a
    /// JSON string or a JSON number (integer enum).  Returns an empty string for any
    /// other kind (null, object, array, …).
    /// </summary>
    private static string ReadStringOrEnum(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Number => el.GetInt32().ToString(),
        _                    => ""
    };
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
