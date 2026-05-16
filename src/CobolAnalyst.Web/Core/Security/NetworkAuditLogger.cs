using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CobolAnalyst.Web.Core.Security;

/// <summary>
/// Logs all Ollama connection attempts and provides an audit summary.
/// Ensures all network activity stays on the allowed host.
/// </summary>
public sealed class NetworkAuditLogger
{
    private readonly string _allowedHost;
    private readonly int _allowedPort;
    private readonly ILogger<NetworkAuditLogger> _logger;

    private int _totalConnections;
    private int _violations;

    public NetworkAuditLogger(IConfiguration config, ILogger<NetworkAuditLogger> logger)
    {
        _logger = logger;
        var baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
        var uri = new Uri(baseUrl);
        _allowedHost = uri.Host;
        _allowedPort = uri.Port;

        _logger.LogInformation(
            "NetworkAuditLogger started. Allowed endpoint: {Host}:{Port}",
            _allowedHost, _allowedPort);
    }

    public void CheckConnection(string targetHost, int targetPort)
    {
        Interlocked.Increment(ref _totalConnections);

        if (!targetHost.Equals(_allowedHost, StringComparison.OrdinalIgnoreCase) ||
            targetPort != _allowedPort)
        {
            Interlocked.Increment(ref _violations);
            _logger.LogWarning(
                "NETWORK VIOLATION: attempted connection to {Host}:{Port} — only {AllowedHost}:{AllowedPort} is permitted",
                targetHost, targetPort, _allowedHost, _allowedPort);
        }
    }

    public void LogOllamaRequest()
    {
        CheckConnection(_allowedHost, _allowedPort);
    }

    public NetworkAuditSummary GetSummary() => new()
    {
        TotalConnections = _totalConnections,
        Violations = _violations,
        AllowedHost = _allowedHost,
        AllowedPort = _allowedPort
    };
}

public sealed class NetworkAuditSummary
{
    public int TotalConnections { get; init; }
    public int Violations { get; init; }
    public string AllowedHost { get; init; } = string.Empty;
    public int AllowedPort { get; init; }
}
