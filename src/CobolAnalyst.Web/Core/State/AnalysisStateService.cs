using CobolAnalyst.Web.Models;

namespace CobolAnalyst.Web.Core.State;

/// <summary>Analysis lifecycle states.</summary>
public enum AnalysisStatus { Idle, Running, Complete, Failed }

/// <summary>
/// Circuit-scoped (Scoped DI) service that preserves analysis state across Blazor
/// page navigations.  One instance per SignalR circuit (browser tab).
/// </summary>
public sealed class AnalysisStateService
{
    // ── Persisted state ──────────────────────────────────────────────────────────

    /// <summary>The active analysis session, or null if none has been run yet.</summary>
    public AnalysisSession? Session { get; set; }

    /// <summary>Files staged for analysis — temp-file paths plus any read error.</summary>
    public List<(string Path, string? Error)> StagedPaths { get; set; } = [];

    /// <summary>Whether an analysis is currently in flight.</summary>
    public bool IsAnalysing { get; set; }

    /// <summary>Current lifecycle status of the analysis.</summary>
    public AnalysisStatus Status { get; set; } = AnalysisStatus.Idle;

    /// <summary>Elapsed time of the last (or current) analysis run.</summary>
    public TimeSpan Elapsed { get; set; }

    /// <summary>Whether the elapsed timer is currently ticking.</summary>
    public bool TimerRunning { get; set; }

    /// <summary>Currently selected Ollama model name.</summary>
    public string SelectedModel { get; set; } = "";

    /// <summary>Name to assign to the next analysis session.</summary>
    public string SessionName { get; set; } = $"Session {DateTime.Now:yyyy-MM-dd HH:mm}";

    // ── Reactive notification ────────────────────────────────────────────────────

    /// <summary>
    /// Raised whenever analysable state changes.  Blazor components subscribe and
    /// call <c>InvokeAsync(StateHasChanged)</c> in their handler.
    /// </summary>
    public event Action? OnStateChanged;

    /// <summary>Raise <see cref="OnStateChanged"/> so subscribed components re-render.</summary>
    public void NotifyChanged() => OnStateChanged?.Invoke();

    // ── Convenience ──────────────────────────────────────────────────────────────

    /// <summary>Clears all transient state and notifies subscribers.</summary>
    public void Clear()
    {
        Session     = null;
        StagedPaths = [];
        IsAnalysing = false;
        Status      = AnalysisStatus.Idle;
        Elapsed     = TimeSpan.Zero;
        TimerRunning = false;
        SessionName = $"Session {DateTime.Now:yyyy-MM-dd HH:mm}";
        NotifyChanged();
    }
}
