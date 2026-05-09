using System.Text.Json;
using CobolAnalyst.Web.Core.Llm;
using CobolAnalyst.Web.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CobolAnalyst.Web.Core.KnowledgeBase;

/// <summary>
/// Persists high-confidence accepted rules to a JSON file and provides
/// keyword-overlap-based retrieval for prompt injection.
/// </summary>
public sealed class KnowledgeBaseService
{
    private readonly string _filePath;
    private readonly ILogger<KnowledgeBaseService> _logger;
    private readonly object _lock = new();
    private List<KnowledgeEntry> _entries = [];

    /// <summary>Initialises the service and loads existing entries from disk.</summary>
    public KnowledgeBaseService(IConfiguration config, ILogger<KnowledgeBaseService> logger)
    {
        _logger = logger;
        var dataPath = config["Storage:DataPath"] ?? "./data";
        Directory.CreateDirectory(dataPath);
        _filePath = Path.Combine(dataPath, "knowledge-base.json");
        Load();
    }

    /// <summary>Number of entries currently in the knowledge base.</summary>
    public int EntryCount
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>UTC timestamp of the most recent entry, or null if empty.</summary>
    public DateTime? LastUpdated
    {
        get { lock (_lock) return _entries.Count > 0 ? _entries.Max(e => e.AddedAt) : null; }
    }

    /// <summary>Returns a snapshot of all entries.</summary>
    public IReadOnlyList<KnowledgeEntry> GetAll()
    {
        lock (_lock) return _entries.AsReadOnly();
    }

    /// <summary>
    /// Returns up to <paramref name="topN"/> entries most relevant to the chunk source
    /// text, ranked by keyword overlap (Jaccard intersection count).
    /// </summary>
    public IEnumerable<KnowledgeEntry> GetTopHints(string chunkSource, int topN)
    {
        var chunkWords = PromptBuilder.ContentWords(chunkSource);
        List<KnowledgeEntry> snapshot;
        lock (_lock) snapshot = [.. _entries];

        return snapshot
            .Select(e => (Entry: e, Score: chunkWords.Intersect(PromptBuilder.ContentWords(e.Description)).Count()))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(topN)
            .Select(x => x.Entry);
    }

    /// <summary>
    /// Adds a high-confidence accepted rule to the knowledge base and persists it.
    /// No-ops if the rule is already present.
    /// </summary>
    public void AddRule(ExtractedRule rule)
    {
        lock (_lock)
        {
            if (_entries.Any(e => e.Id == rule.Id)) return;
            _entries.Add(new KnowledgeEntry
            {
                Id = rule.Id,
                Label = rule.Label,
                Description = rule.Description,
                Type = rule.Type,
                MigrationNotes = rule.MigrationNotes,
                CobolReference = rule.CobolReference
            });
        }
        Save();
    }

    /// <summary>Removes all entries from the knowledge base.</summary>
    public void Clear()
    {
        lock (_lock) _entries.Clear();
        Save();
        _logger.LogInformation("Knowledge base cleared");
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            _entries = JsonSerializer.Deserialize<List<KnowledgeEntry>>(json) ?? [];
            _logger.LogInformation("Loaded {Count} knowledge base entries", _entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load knowledge base from {Path}", _filePath);
            _entries = [];
        }
    }

    private void Save()
    {
        try
        {
            List<KnowledgeEntry> snapshot;
            lock (_lock) snapshot = [.. _entries];
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save knowledge base to {Path}", _filePath);
        }
    }
}
