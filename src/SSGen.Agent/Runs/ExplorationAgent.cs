using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using SSGen.Agent.Graph;
using SSGen.Agent.Observation;
using SSGen.Agent.Safety;
using SSGen.Agent.Strategies;
using SSGen.Ai;
using SSGen.Core;
using SSGen.Core.Projects;
using SSGen.Emulator.Devices;

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

public sealed record AgentRunOptions
{
    public required string AppName { get; init; }
    public required string PackageName { get; init; }
    public string? LauncherActivity { get; init; }
    public string? Hints { get; init; }
    public int MaxSteps { get; init; } = 60;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>Delay between polls while waiting for the screen to settle.</summary>
    public TimeSpan SettlePoll { get; init; } = TimeSpan.FromMilliseconds(400);

    public TimeSpan SettleTimeout { get; init; } = TimeSpan.FromSeconds(5);
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
}

/// <summary>Pause/resume/stop for a running agent.</summary>
public sealed class AgentControl : IDisposable
{
    private readonly ManualResetEventSlim _running = new(true);

    public bool IsPaused => !_running.IsSet;

    public void Pause() => _running.Reset();

    public void Resume() => _running.Set();

    internal Task WaitIfPausedAsync(CancellationToken ct) =>
        _running.IsSet ? Task.CompletedTask : Task.Run(() => _running.Wait(ct), ct);

    public void Dispose() => _running.Dispose();
}

/// <summary>
/// The observe → decide → act loop. Observes the device (screenshot, UI tree, activity), looks the screen up in the
/// graph (new screens are saved into the project), asks the strategy for an action, checks it against the
/// guardrails, executes it and waits for the UI to settle. Handles leaving the app, getting stuck, and the step,
/// time and cost limits.
/// </summary>
public sealed class ExplorationAgent
{
    private const int StuckBackThreshold = 5;
    private const int StuckRelaunchThreshold = 9;

    private readonly IDevice _device;
    private readonly IAgentStrategy _strategy;
    private readonly Guardrails _guardrails;
    private readonly CostMeter _meter;
    private readonly Project _project;
    private readonly IAgentObserver _observer;
    private readonly ILogger _log;
    private readonly AgentRunStore _store;
    private readonly List<string> _recent = [];
    private long _runId;

    public ExplorationAgent(IDevice device, IAgentStrategy strategy, Guardrails guardrails, CostMeter meter, Project project, IAgentObserver observer, ILogger log)
    {
        _device = device;
        _strategy = strategy;
        _guardrails = guardrails;
        _meter = meter;
        _project = project;
        _observer = observer;
        _log = log;
        _store = new AgentRunStore(project);
    }

    public ScreenGraph Graph { get; } = new();

    public async Task<AgentRunSummary> RunAsync(AgentRunOptions options, AgentControl control, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(control);
        _runId = _store.StartRun(_strategy.Name);
        var clock = Stopwatch.StartNew();
        var step = 0;
        AgentRunSummary Summary(AgentRunStatus status, string message) =>
            new(status, message, step, Graph.Nodes.Count, _meter.Usage, _meter.IsPaid && _meter.PriceKnown ? _meter.Cost : null);

        AgentRunSummary result;
        try
        {
            Log(0, AgentLogKind.Info, $"Exploring {options.AppName} with {_strategy.Name}. Safe mode {(_guardrails.SafeMode ? "on" : "OFF")}.");
            // Start from a clean launch so leftovers (an open menu, a half-finished dialog) don't become the root.
            await _device.ForceStopAsync(options.PackageName, ct);
            await _device.LaunchAsync(options.PackageName, options.LauncherActivity, ct);
            await Task.Delay(1500, ct);

            ScreenNode? previous = null;
            AgentAction? previousAction = null;
            var justLaunched = true;
            ulong? lastHash = null;
            var sameNodeStreak = 0;
            var outsideStreak = 0;

            async Task RelaunchAsync()
            {
                await _device.LaunchAsync(options.PackageName, options.LauncherActivity, ct);
                justLaunched = true;
                previous = null;
                previousAction = null;
                outsideStreak = 0;
                sameNodeStreak = 0;
            }

            while (true)
            {
                await control.WaitIfPausedAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (step >= options.MaxSteps)
                {
                    result = Summary(AgentRunStatus.LimitReached, $"Reached the step limit ({options.MaxSteps}).");
                    break;
                }

                if (clock.Elapsed >= options.MaxDuration)
                {
                    result = Summary(AgentRunStatus.LimitReached, $"Reached the time limit ({options.MaxDuration.TotalMinutes:0} min).");
                    break;
                }

                if (_meter.CapReached)
                {
                    result = Summary(AgentRunStatus.LimitReached, $"Reached the cost cap (${_meter.Cap:0.00}).");
                    break;
                }

                step++;
                var (png, ui, activity) = await ObserveAsync(options, ct);

                // Outside the app under test (launcher, permission dialog, browser, store, another app)?
                var package = activity?.Split('/')[0] ?? ui.RootPackage;
                if (package is not null && package != options.PackageName)
                {
                    if (previous is not null && previousAction?.Kind == ActionKind.Back)
                    {
                        // Back closed the app: that screen is the root. Don't try Back there again.
                        previous.IsRoot = true;
                        Log(step, AgentLogKind.Info, $"Back from {previous.DisplayName} closes the app; reopening it.");
                        await RelaunchAsync();
                    }
                    else if (++outsideStreak <= 2)
                    {
                        Log(step, AgentLogKind.Warning, $"Left the app ({package} in front); pressing back.");
                        await _device.PressKeyAsync(AndroidKey.Back, ct);
                        previousAction = null;
                    }
                    else
                    {
                        Log(step, AgentLogKind.Warning, "Still outside the app; relaunching it.");
                        await RelaunchAsync();
                    }

                    continue;
                }

                outsideStreak = 0;
                var fingerprint = ScreenFingerprint.From(activity, png, ui);
                if (previousAction?.Kind == ActionKind.Tap && lastHash == fingerprint.DHash)
                {
                    // Diagnostic: a tap that changes nothing on screen is normal for some controls, but many in a row
                    // usually mean the device is busy or dropping input.
                    Log(step, AgentLogKind.Warning, $"The last tap ({previousAction.Element?.Label}) had no visible effect.");
                }

                lastHash = fingerprint.DHash;
                var (node, isNew) = Graph.Observe(fingerprint);
                if (justLaunched)
                {
                    node.IsRoot = true;
                    justLaunched = false;
                }

                node.Known.UnionWith(SystematicStrategy.Candidates(ui).Select(e => e.Key));
                node.HasScrollable |= ui.Elements.Any(e => e.Scrollable);
                if (isNew)
                {
                    SaveScreen(node, png, activity);
                }

                if (previous is not null && previousAction is not null && previous.Id != node.Id &&
                    Graph.AddEdge(previous.Id, node.Id, previousAction.EdgeLabel, previousAction.Kind == ActionKind.Tap ? previousAction.Element?.Key : null) is { } edge)
                {
                    _store.SaveEdge(_runId, edge);
                }

                _observer.OnScreen(png, node);
                _observer.OnProgress(step, _meter, Graph);

                // Stuck detection: same screen for many steps without discovering anything.
                sameNodeStreak = previous?.Id == node.Id ? sameNodeStreak + 1 : 0;
                if (sameNodeStreak >= StuckRelaunchThreshold)
                {
                    Log(step, AgentLogKind.Warning, "Stuck on the same screen; relaunching the app.");
                    await RelaunchAsync();
                    continue;
                }

                AgentDecision decision;
                if (sameNodeStreak >= StuckBackThreshold && !node.IsRoot)
                {
                    decision = new AgentDecision(AgentAction.Back(), "This screen isn't changing; backing out.");
                }
                else
                {
                    var context = new AgentContext(options.AppName, options.PackageName, options.Hints, step, options.MaxSteps,
                        Graph, node, ui, png, _recent.TakeLast(6).ToList());
                    decision = await _strategy.DecideAsync(context, ct);
                }

                if (!string.IsNullOrWhiteSpace(decision.ScreenLabel) && string.IsNullOrWhiteSpace(node.Label))
                {
                    node.Label = decision.ScreenLabel!;
                    _store.SaveNode(_runId, node);
                }

                Log(step, AgentLogKind.Thought, decision.Thought);
                if (decision.Action.Kind == ActionKind.Stop)
                {
                    result = Summary(AgentRunStatus.Completed, "The app looks fully explored.");
                    break;
                }

                var guard = _guardrails.Check(decision, ui);
                if (decision.Action.Element is { } target)
                {
                    node.Tried.Add(target.Key);
                }

                if (guard.Verdict == GuardVerdict.Blocked)
                {
                    Log(step, AgentLogKind.Blocked, $"Blocked: {decision.Action.Describe()} ({guard.Reason}).");
                    previous = node;
                    previousAction = null;
                    continue;
                }

                if (guard.Verdict == GuardVerdict.NeedsConfirmation &&
                    !await _observer.ConfirmAsync($"The agent wants to: {decision.Action.Describe()}\n\nReason flagged: {guard.Reason}.\n\nAllow it?"))
                {
                    Log(step, AgentLogKind.Blocked, $"Declined by you: {decision.Action.Describe()}.");
                    previous = node;
                    previousAction = null;
                    continue;
                }

                Log(step, AgentLogKind.Action, decision.Action.Describe());
                await ExecuteAsync(decision.Action, node, ct);
                _recent.Add(decision.Action.EdgeLabel);
                previous = node;
                previousAction = decision.Action;
            }
        }        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = Summary(AgentRunStatus.Stopped, "Stopped by you.");
        }
        catch (LlmException ex)
        {
            Log(step, AgentLogKind.Error, ex.Message);
            result = Summary(AgentRunStatus.Failed, ex.Message + (ex.SuggestedFix is { } fix ? " " + fix : ""));
        }
        catch (UserFacingException ex)
        {
            Log(step, AgentLogKind.Error, ex.Message);
            result = Summary(AgentRunStatus.Failed, ex.Message + (ex.SuggestedFix is { } fix ? " " + fix : ""));
        }

        foreach (var n in Graph.Nodes)
        {
            _store.SaveNode(_runId, n);
        }

        _store.FinishRun(_runId, result);
        Log(step, AgentLogKind.Info, $"{result.Message} {result.Screens} unique screens in {result.Steps} steps. {_meter.Describe()}");
        _observer.OnProgress(step, _meter, Graph);
        return result;
    }

    private async Task<(byte[] Png, UiSnapshot Ui, string? Activity)> ObserveAsync(AgentRunOptions options, CancellationToken ct)
    {
        var png = await SettleAsync(options, ct);
        var ui = UiHierarchyParser.Parse(await _device.DumpUiHierarchyAsync(ct));
        var activity = await _device.GetForegroundActivityAsync(ct);
        return (png, ui, activity);
    }

    /// <summary>
    /// Waits until two consecutive screenshots look the same (animations/loading finished) or a timeout, and
    /// returns the last one so observing doesn't need another capture.
    /// </summary>
    private async Task<byte[]> SettleAsync(AgentRunOptions options, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + options.SettleTimeout;
        await Task.Delay(options.SettlePoll, ct); // let the previous input start its transition
        ulong? last = null;
        while (true)
        {
            var png = await _device.CaptureScreenshotPngAsync(ct);
            var hash = ScreenFingerprint.DifferenceHash(png);
            if ((last is { } l && ScreenFingerprint.Hamming(l, hash) <= 2) || DateTime.UtcNow >= deadline)
            {
                return png;
            }

            last = hash;
            await Task.Delay(options.SettlePoll, ct);
        }
    }

    private async Task ExecuteAsync(AgentAction action, ScreenNode node, CancellationToken ct)
    {
        switch (action.Kind)
        {
            case ActionKind.Tap when action.Element is { } e:
                await _device.TapAsync(e.Bounds.CenterX, e.Bounds.CenterY, ct);
                break;
            case ActionKind.Type when action.Element is { } e:
                await _device.TapAsync(e.Bounds.CenterX, e.Bounds.CenterY, ct);
                await Task.Delay(300, ct);
                await _device.InputTextAsync(action.Text ?? "", ct);
                await _device.PressKeyAsync(AndroidKey.Escape, ct); // hide the keyboard so it doesn't cover the screen
                break;
            case ActionKind.Scroll:
                {
                    var (w, h) = await _device.GetScreenSizeAsync(ct);
                    var area = action.Element?.Bounds ?? new UiBounds(0, h / 5, w, h * 4 / 5);
                    var (x1, y1, x2, y2) = action.Direction switch
                    {
                        ScrollDirection.Up => (area.CenterX, area.Top + (area.Height / 5), area.CenterX, area.Bottom - (area.Height / 5)),
                        ScrollDirection.Left => (area.Left + (area.Width / 5), area.CenterY, area.Right - (area.Width / 5), area.CenterY),
                        ScrollDirection.Right => (area.Right - (area.Width / 5), area.CenterY, area.Left + (area.Width / 5), area.CenterY),
                        _ => (area.CenterX, area.Bottom - (area.Height / 5), area.CenterX, area.Top + (area.Height / 5)),
                    };
                    await _device.SwipeAsync(x1, y1, x2, y2, 400, ct);
                    if (action.Direction is null or ScrollDirection.Down)
                    {
                        node.ScrolledDown = true;
                    }

                    break;
                }

            case ActionKind.Back:
                await _device.PressKeyAsync(AndroidKey.Back, ct);
                break;
            case ActionKind.Wait:
                await Task.Delay(1500, ct);
                break;
        }
    }

    private void SaveScreen(ScreenNode node, byte[] png, string? activity)
    {
        using var bitmap = SKBitmap.Decode(png);
        var saved = ScreenRepository.Add(_project, png, "agent", activity, bitmap?.Width ?? 0, bitmap?.Height ?? 0);
        node.ScreenId = saved.Id;
        node.ScreenFile = saved.FullPath(_project);
        _store.SaveNode(_runId, node);
        Log(0, AgentLogKind.Screen, $"New screen #{node.Id}: {node.DisplayName}");
        _observer.OnNewScreen(node, png);
    }

    private void Log(int step, AgentLogKind kind, string message)
    {
        _log.LogInformation("[agent step {Step}] {Kind}: {Message}", step, kind, message);
        if (_runId > 0)
        {
            _store.Log(_runId, step, kind, message);
        }

        _observer.OnLog(new AgentLogEntry(step, kind, message, DateTime.Now));
    }
}
