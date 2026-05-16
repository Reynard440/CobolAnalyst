using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CobolAnalyst.Web.Core.Security;
using CobolAnalyst.Web.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CobolAnalyst.Web.Core.Llm;

/// <summary>
/// Talks to a local Ollama instance at the configured base URL.
/// Uses the /api/chat endpoint with streaming (newline-delimited JSON).
/// </summary>
public sealed class OllamaClient : ILlmClient
{
    private const int DefaultNumPredict = 1500;
    private const int NumPredictChat   = 4096;
    private const double Temperature   = 0.1;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ILogger<OllamaClient> _logger;
    private readonly NetworkAuditLogger _audit;

    // Volatile so UI-thread writes are visible to background analysis tasks.
    private volatile string _selectedModel;

    /// <inheritdoc/>
    public string SelectedModel
    {
        get => _selectedModel;
        set => _selectedModel = value;
    }

    /// <summary>Initialises the client from configuration.</summary>
    public OllamaClient(HttpClient http, IConfiguration config, ILogger<OllamaClient> logger, NetworkAuditLogger audit)
    {
        _http = http;
        _logger = logger;
        _audit = audit;
        _baseUrl = (config["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
        _selectedModel = config["Ollama:DefaultModel"] ?? "qwen2.5-coder:32b";
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = _selectedModel;
        _audit.LogOllamaRequest();

        var numPredict = GetNumPredictForModel(model);
        var body = new
        {
            model,
            messages = new[] { new { role = "user", content = prompt } },
            stream = true,
            options = new { temperature = Temperature, num_predict = numPredict }
        };

        var json = JsonSerializer.Serialize(body);
        _logger.LogDebug("Ollama request: model={Model}, num_predict={NumPredict}", model, numPredict);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{_baseUrl}/api/chat");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Ollama connection failed — is Ollama running at {Url}?", _baseUrl);
            throw new InvalidOperationException(
                $"Could not connect to Ollama at {_baseUrl}. " +
                "Make sure Ollama is running (`ollama serve`).", ex);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await ReadLineGuarded(reader, cancellationToken)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            string? token = null;
            bool done = false;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("done", out var doneProp))
                    done = doneProp.GetBoolean();
                if (!done &&
                    root.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var content))
                {
                    token = content.GetString();
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Skipping unparseable line from Ollama stream");
            }

            if (token is { Length: > 0 })
                yield return token;

            if (done) break;
        }

        response.Dispose();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> StreamChatAsync(
        string systemPrompt,
        IReadOnlyList<ConversationTurn> history,
        string? model = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var effectiveModel = model ?? _selectedModel;
        _audit.LogOllamaRequest();

        // Build messages: system + conversation history (exclude system turns)
        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };
        foreach (var turn in history)
        {
            if (turn.Role == "user" || turn.Role == "assistant")
                messages.Add(new { role = turn.Role, content = turn.Content });
        }

        var body = new
        {
            model   = effectiveModel,
            messages,
            stream  = true,
            options = new { temperature = Temperature, num_predict = NumPredictChat }
        };

        var json = JsonSerializer.Serialize(body);
        _logger.LogDebug("Ollama chat request: model={Model}, turns={Turns}", effectiveModel, messages.Count);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Ollama chat connection failed");
            throw new InvalidOperationException(
                $"Could not connect to Ollama at {_baseUrl}. Make sure Ollama is running.", ex);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await ReadLineGuarded(reader, cancellationToken)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            string? token = null;
            bool done = false;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("done", out var doneProp))
                    done = doneProp.GetBoolean();
                if (!done &&
                    root.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var content))
                    token = content.GetString();
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Skipping unparseable chat line");
            }

            if (token is { Length: > 0 })
                yield return token;

            if (done) break;
        }

        response.Dispose();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{_baseUrl}/api/tags", cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var models))
                return [];

            return models.EnumerateArray()
                .Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(n => n)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not retrieve model list from Ollama at {Url}", _baseUrl);
            return [];
        }
    }

    private int GetNumPredictForModel(string model)
    {
        var lower = model.ToLowerInvariant();
        if (lower.Contains("3b") || lower.Contains("1b")) return 800;
        if (lower.Contains("7b") || lower.Contains("8b")) return 1200;
        if (lower.Contains("14b")) return 1500;
        if (lower.Contains("32b") || lower.Contains("34b")) return 2000;
        if (lower.Contains("70b") || lower.Contains("72b")) return 2500;
        return DefaultNumPredict;
    }

    private static TimeSpan GetTimeoutForModel(string model)
    {
        var lower = model.ToLowerInvariant();
        if (lower.Contains("3b") || lower.Contains("1b")) return TimeSpan.FromSeconds(60);
        if (lower.Contains("7b") || lower.Contains("8b")) return TimeSpan.FromSeconds(120);
        if (lower.Contains("14b")) return TimeSpan.FromSeconds(200);
        if (lower.Contains("32b") || lower.Contains("34b")) return TimeSpan.FromSeconds(400);
        if (lower.Contains("70b") || lower.Contains("72b")) return TimeSpan.FromSeconds(600);
        return TimeSpan.FromSeconds(200);
    }

    private static async Task<string?> ReadLineGuarded(StreamReader reader, CancellationToken ct)
    {
        try { return await reader.ReadLineAsync(ct); }
        catch (IOException) { return null; }
    }
}
