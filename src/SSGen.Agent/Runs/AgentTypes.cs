using SSGen.Agent.Graph;
using SSGen.Ai;

namespace SSGen.Agent.Runs;

public enum AgentLogKind
{
    Info,
    Thought,
    Action,
    Blocked,
    Screen,
    Warning,
    Error,
}

public enum AgentRunStatus
{
    Running,
    Completed,
    Stopped,
    LimitReached,
    Failed,
}

public sealed record AgentLogEntry(int Step, AgentLogKind Kind, string Message, DateTime Time);

public sealed record AgentRunSummary(AgentRunStatus Status, string Message, int Steps, int Screens, LlmUsage Usage, decimal? CostUsd);

/// <summary>How to get past sign-in screens.</summary>
/// <param name="Username">Test account user name or email (typed by the agent; never sent to an AI).</param>
/// <param name="Password">Test account password (typed by the agent; never sent to an AI or logged).</param>
/// <param name="GuestHint">How to continue without an account, e.g. "Tap 'Continue as guest'".</param>
/// <param name="AskUser">With no working way in: true = pause and ask the user to log in; false = skip the wall.</param>
public sealed record LoginOptions(string? Username = null, string? Password = null, string? GuestHint = null, bool AskUser = true)
{
    public bool HasCredentials => !string.IsNullOrEmpty(Username) || !string.IsNullOrEmpty(Password);
}

public sealed record AgentRunOptions
{
    public required string AppName { get; init; }
    public required string PackageName { get; init; }
    public string? LauncherActivity { get; init; }
    public string? Hints { get; init; }
    public int MaxSteps { get; init; } = 60;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>How many store screenshots the user wants; guides the AI.</summary>
    public int? TargetScreens { get; init; }

    public LoginOptions Login { get; init; } = new();

    /// <summary>Clean status bar while exploring (Android demo mode: 9:41, full battery, no notifications).</summary>
    public bool CleanStatusBar { get; init; } = true;

    /// <summary>Continue a previous run: its screens and what was tried on them are loaded first.</summary>
    public long? ResumeRunId { get; init; }

    /// <summary>Finish when this many steps in a row find no new screen.</summary>
    public int NoProgressLimit { get; init; } = 25;

    /// <summary>Give up when the app crashes this many times.</summary>
    public int MaxCrashes { get; init; } = 3;

    /// <summary>Delay between polls while waiting for the screen to settle.</summary>
    public TimeSpan SettlePoll { get; init; } = TimeSpan.FromMilliseconds(400);

    public TimeSpan SettleTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for loading content, an unresponsive app, a busy device, or a Wait action.</summary>
    public TimeSpan Patience { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Longer settle after launching, for a device that's still busy right after booting.</summary>
    public TimeSpan WarmupTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>Live updates from a run. Called on the agent's thread; UI implementations must marshal.</summary>
public interface IAgentObserver
{
    void OnLog(AgentLogEntry entry);

    void OnScreen(byte[] png, ScreenNode node);

    void OnNewScreen(ScreenNode node, byte[] png);

    void OnProgress(int stepNumber, CostMeter meter, ScreenGraph graph);

    /// <summary>Safe mode off: ask the user whether to perform a flagged action.</summary>
    Task<bool> ConfirmAsync(string question);

    /// <summary>The agent paused and handed control to the user (e.g. to log in). It continues on Resume.</summary>
    void OnTakeoverRequested(string reason);
}

/// <summary>Pause/resume/stop for a running agent, and manual takeover (the user drives the device meanwhile).</summary>
public sealed class AgentControl : IDisposable
{
    private readonly ManualResetEventSlim _running = new(true);
    private volatile string? _takeoverReason;

    public bool IsPaused => !_running.IsSet;

    /// <summary>Why the user has control right now (null when not taken over).</summary>
    public string? TakeoverReason => _takeoverReason;

    public void Pause() => _running.Reset();

    /// <summary>Pauses after the current step so the user can operate the device (e.g. to log in).</summary>
    public void TakeOver(string reason)
    {
        _takeoverReason = reason;
        _running.Reset();
    }

    public void Resume() => _running.Set();

    /// <summary>True once after resuming from a takeover: the screen may have changed in any way meanwhile.</summary>
    internal bool ConsumeTakeover()
    {
        if (_takeoverReason is null || !_running.IsSet)
        {
            return false;
        }

        _takeoverReason = null;
        return true;
    }

    internal Task WaitIfPausedAsync(CancellationToken ct) =>
        _running.IsSet ? Task.CompletedTask : Task.Run(() => _running.Wait(ct), ct);

    public void Dispose() => _running.Dispose();
}
