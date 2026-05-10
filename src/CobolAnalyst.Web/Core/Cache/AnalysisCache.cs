using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CobolAnalyst.Web.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CobolAnalyst.Web.Core.Cache;

/// <summary>
/// In-memory analysis cache backed by JSON files in the data directory.
/// Cache keys are SHA-256(file content + prompt + model name).
/// </summary>
public sealed class AnalysisCache
{
    private readonly Dictionary<string, List<ExtractedRule>> _memory = [];
    private readonly string _cacheDir;
    private readonly ILogger<AnalysisCache> _logger;
    private readonly object _lock = new();

    /// <summary>Initialises the cache and loads persisted entries from disk.</summary>
    public AnalysisCache(IConfiguration config, ILogger<AnalysisCache> logger)
    {
        _logger = logger;
        var dataPath = config["Storage:DataPath"] ?? "./data";
        _cacheDir = Path.Combine(dataPath, "cache");
        Directory.CreateDirectory(_cacheDir);
        LoadPersistedEntries();
    }

    /// <summary>Returns cached rules for the given content + prompt + model, or null on a miss.</summary>
    public List<ExtractedRule>? TryGet(string fileContent, string prompt, string model)
    {
        var key = ComputeKey(fileContent, prompt, model);
        lock (_lock)
        {
            return _memory.TryGetValue(key, out var rules) ? rules : null;
        }
    }

    /// <summary>Stores rules in memory and persists them to disk.</summary>
    public void Store(string fileContent, string prompt, string model, List<ExtractedRule> rules)
    {
        var key = ComputeKey(fileContent, prompt, model);
        lock (_lock)
        {
            _memory[key] = rules;
        }
        _ = PersistAsync(key, rules);
    }

    /// <summary>
    /// Removes one entry from the in-memory cache and deletes its backing file on disk.
    /// <paramref name="cacheKey"/> must be the hex SHA-256 key returned by <see cref="ComputeKey"/>.
    /// </summary>
    public void Invalidate(string cacheKey)
    {
        lock (_lock)
        {
            _memory.Remove(cacheKey);
        }
        var path = Path.Combine(_cacheDir, $"{cacheKey}.json");
        if (File.Exists(path))
        {
            try   { File.Delete(path); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete cache file for key {Key}", cacheKey); }
        }
        _logger.LogInformation("Cache invalidated for key {Prefix}…", cacheKey[..Math.Min(8, cacheKey.Length)]);
    }

    /// <summary>
    /// Clears the entire in-memory cache and deletes every .json file in the cache directory.
    /// </summary>
    public void InvalidateAll()
    {
        int count;
        lock (_lock)
        {
            count = _memory.Count;
            _memory.Clear();
        }
        foreach (var file in Directory.EnumerateFiles(_cacheDir, "*.json"))
        {
            try   { File.Delete(file); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete cache file {File}", file); }
        }
        _logger.LogInformation("Cache cleared — {Count} entries removed", count);
    }

    private static string ComputeKey(string fileContent, string prompt, string model)
    {
        var raw = fileContent + prompt + model;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void LoadPersistedEntries()
    {
        foreach (var file in Directory.EnumerateFiles(_cacheDir, "*.json"))
        {
            try
            {
                var key = Path.GetFileNameWithoutExtension(file);
                var json = File.ReadAllText(file);
                var rules = JsonSerializer.Deserialize<List<ExtractedRule>>(json);
                if (rules is not null)
                    _memory[key] = rules;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load cache file {File}", file);
            }
        }
        _logger.LogInformation("Loaded {Count} cached analysis results", _memory.Count);
    }

    private async Task PersistAsync(string key, List<ExtractedRule> rules)
    {
        try
        {
            var path = Path.Combine(_cacheDir, $"{key}.json");
            var json = JsonSerializer.Serialize(rules, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist cache entry {Key}", key);
        }
    }
}
