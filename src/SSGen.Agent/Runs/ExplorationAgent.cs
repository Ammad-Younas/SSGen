using System.Diagnostics;
using System.Text.RegularExpressions;
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

/// <summary>
/// The observe → decide → act loop. Each step observes the device (screenshot, UI tree, activity; an open keyboard
/// is closed first), handles interruptions the same way every time (permission dialogs, "not responding", crashes,
/// ads, popups, loading, sign-in walls), looks the screen up in the graph (new screens are saved into the project),
/// asks the strategy for an action, checks it against the guardrails, executes it and waits for the UI to settle.
/// Recovers from leaving the app, crashes, getting stuck and going in circles; honours step, time, cost and
/// no-progress limits; supports pause, manual takeover and resuming an earlier run.
/// </summary>
public sealed partial class ExplorationAgent
{
    private const int StuckBackThreshold = 5;
    private const int StuckRelaunchThreshold = 9;
    private const int MaxLoadingWaits = 5;
    private const int MaxLoginAttempts = 2;
    private const int CircleWindow = 12;

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

    public ScreenGraph Graph { get; private set; } = new();

    public long RunId => _runId;

    /// <summary>Per-run counters and the screen/action that led here.</summary>
    private sealed class RunState
    {
        public ScreenNode? Previous;
        public AgentAction? PreviousAction;
        public bool JustLaunched = true;
        public int SameNodeStreak;
        public int OutsideStreak;
        public ulong? LastHash;
        public int NoEffectStreak;
        public int StepsWithoutNewScreen;

        /// <summary>Steps with neither a new screen nor a first try of an element (the real "no progress").</summary>
        public int StepsWithoutProgress;
        public int Crashes;
        public int LoadingWaits;
        public int LoginAttempts;
        public int LoginTakeovers;
        public int LastTriedCount = -1;
        public readonly Queue<int> RecentNodes = new();

        public void ForgetPath()
        {
            Previous = null;
            PreviousAction = null;
        }
    }

    public async Task<AgentRunSummary> RunAsync(AgentRunOptions options, AgentControl control, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(control);
        _runId = _store.StartRun(_strategy.Name, options.ResumeRunId);
        var clock = Stopwatch.StartNew();
        var step = 0;
        var s = new RunState();
        AgentRunSummary Summary(AgentRunStatus status, string message) =>
            new(status, message, step, Graph.Nodes.Count, _meter.Usage, _meter.IsPaid && _meter.PriceKnown ? _meter.Cost : null);

        AgentRunSummary result;
        try
        {
            if (options.ResumeRunId is { } parent)
            {
                Graph = _store.LoadGraph(parent);
                foreach (var n in Graph.Nodes)
                {
                    _store.SaveNode(_runId, n);
                }

                foreach (var e in Graph.Edges)
                {
                    _store.SaveEdge(_runId, e);
                }

                Log(0, AgentLogKind.Info, $"Continuing the previous run: {Graph.Nodes.Count} screens already found; untried parts come first.");
            }

            Log(0, AgentLogKind.Info, $"Exploring {options.AppName} with {_strategy.Name}. Safe mode {(_guardrails.SafeMode ? "on" : "OFF")}.");

            // Start from a clean launch so leftovers (an open menu, a half-finished dialog) don't become the root,
            // and give a freshly booted device time to calm down before the first observation.
            if (options.CleanStatusBar)
            {
                await _device.SetStatusBarDemoAsync(true, ct);
            }

            await _device.ForceStopAsync(options.PackageName, ct);
            await _device.LaunchAsync(options.PackageName, options.LauncherActivity, ct);
            await SettleAsync(options with { SettleTimeout = options.WarmupTimeout }, null, ct);

            while (true)
            {
                if (control.TakeoverReason is { } reason && control.IsPaused)
                {
                    Log(step, AgentLogKind.Info, $"Paused while you have control: {reason}");
                }

                await control.WaitIfPausedAsync(ct);
                if (control.ConsumeTakeover())
                {
                    // Anything may have happened on the device; don't link the next screen to the last action.
                    s.ForgetPath();
                    s.SameNodeStreak = 0;
                    s.LoginAttempts = 0;
                    Log(step, AgentLogKind.Info, "Continuing after manual control.");
                }

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

                if (s.StepsWithoutProgress >= options.NoProgressLimit)
                {
                    result = Summary(AgentRunStatus.Completed, $"No new screens or untried elements in the last {options.NoProgressLimit} steps; the reachable app looks explored.");
                    break;
                }

                step++;
                var before = s.PreviousAction?.Kind is ActionKind.Tap or ActionKind.Type or ActionKind.Scroll or ActionKind.Back ? s.LastHash : null;
                var (png, ui, activity) = await ObserveAsync(options, before, ct);
                var package = activity?.Split('/')[0] ?? ui.RootPackage;

                // ---- Interruptions handled the same way every time, before any strategy decides.
                var interrupt = ScreenClassifier.Classify(ui, package, options.PackageName);
                if (interrupt.Kind != InterruptKind.Loading)
                {
                    s.LoadingWaits = 0;
                }

                switch (interrupt.Kind)
                {
                    case InterruptKind.PermissionDialog or InterruptKind.Ad or InterruptKind.Popup when interrupt.Target is { } target:
                        if (_guardrails.Check(new AgentDecision(new AgentAction(ActionKind.Tap, target), ""), ui).Verdict == GuardVerdict.Allowed)
                        {
                            Log(step, AgentLogKind.Info, interrupt.Description + ".");
                            await TapAsync(target, ct);
                            continue;
                        }

                        break;

                    case InterruptKind.NotResponding:
                        Log(step, AgentLogKind.Warning, interrupt.Description + ".");
                        if (interrupt.Target is { } wait)
                        {
                            await TapAsync(wait, ct);
                        }

                        await Task.Delay(options.Patience, ct);
                        continue;

                    case InterruptKind.CrashDialog:
                        if (interrupt.Target is { } close)
                        {
                            await TapAsync(close, ct);
                        }

                        if (await OnCrashAsync(s, step, options, ct) is { } crashed)
                        {
                            result = Summary(AgentRunStatus.Failed, crashed);
                            goto Finished;
                        }

                        continue;

                    case InterruptKind.Loading when s.LoadingWaits < MaxLoadingWaits:
                        if (s.LoadingWaits++ == 0)
                        {
                            Log(step, AgentLogKind.Info, "Waiting for the screen to finish loading.");
                        }

                        await Task.Delay(options.Patience, ct);
                        continue;
                }

                // ---- Outside the app under test (launcher, Settings, browser, store, another app)?
                if (package is not null && package != options.PackageName)
                {
                    if (s.Previous is not null && s.PreviousAction?.Kind == ActionKind.Back)
                    {
                        // Back closed the app: that screen is the root. Don't try Back there again.
                        s.Previous.IsRoot = true;
                        _store.SaveNode(_runId, s.Previous);
                        Log(step, AgentLogKind.Info, $"Back from {s.Previous.DisplayName} closes the app; reopening it.");
                        await RelaunchAsync(s, options, ct);
                    }
                    else if (s.PreviousAction is { Kind: not ActionKind.Back } && await _device.GetPidAsync(options.PackageName, ct) is null)
                    {
                        if (await OnCrashAsync(s, step, options, ct) is { } crashed)
                        {
                            result = Summary(AgentRunStatus.Failed, crashed);
                            break;
                        }
                    }
                    else if (++s.OutsideStreak <= 2)
                    {
                        Log(step, AgentLogKind.Warning, $"Left the app ({package} in front); pressing back.");
                        await _device.PressKeyAsync(AndroidKey.Back, ct);
                        s.PreviousAction = null;
                    }
                    else
                    {
                        Log(step, AgentLogKind.Warning, "Still outside the app; relaunching it.");
                        await RelaunchAsync(s, options, ct);
                    }

                    continue;
                }

                s.OutsideStreak = 0;
                var fingerprint = ScreenFingerprint.From(activity, png, ui);
                if (s.PreviousAction?.Kind is ActionKind.Tap && s.LastHash == fingerprint.DHash)
                {
                    // A tap that changes nothing is normal for some controls; several in a row usually mean the
                    // device is busy (e.g. just after booting) and dropping input.
                    if (++s.NoEffectStreak >= 3)
                    {
                        Log(step, AgentLogKind.Warning, "Several taps in a row had no visible effect; the device seems busy, so waiting a moment.");
                        await Task.Delay(options.Patience * 2, ct);
                        s.NoEffectStreak = 0;
                    }
                    else
                    {
                        Log(step, AgentLogKind.Warning, $"The last tap ({s.PreviousAction.Element?.Label}) had no visible effect.");
                    }
                }
                else
                {
                    s.NoEffectStreak = 0;
                }

                s.LastHash = fingerprint.DHash;
                var (node, isNew) = Graph.Observe(fingerprint);
                if (s.JustLaunched)
                {
                    node.IsRoot = true;
                    s.JustLaunched = false;
                }

                node.Known.UnionWith(SystematicStrategy.Candidates(ui).Select(e => e.Key));
                node.HasScrollable |= ui.Elements.Any(e => e.Scrollable);
                if (isNew)
                {
                    // Name it after its heading: single-activity apps (Jetpack Compose, one-activity Flutter or
                    // React Native) have the same activity on every screen.
                    if (string.IsNullOrWhiteSpace(node.Label) && ui.Title is { Length: > 0 } heading)
                    {
                        node.Label = heading;
                    }

                    SaveScreen(node, png, activity, ui);
                }

                s.StepsWithoutNewScreen = isNew ? 0 : s.StepsWithoutNewScreen + 1;
                if (isNew)
                {
                    s.StepsWithoutProgress = 0;
                }
                if (s.Previous is not null && s.PreviousAction is not null && s.Previous.Id != node.Id &&
                    Graph.AddEdge(s.Previous.Id, node.Id, s.PreviousAction.EdgeLabel, s.PreviousAction.Kind == ActionKind.Tap ? s.PreviousAction.Element?.Key : null) is { } edge)
                {
                    _store.SaveEdge(_runId, edge);
                }

                _observer.OnScreen(png, node);
                _observer.OnProgress(step, _meter, Graph);

                // ---- Sign-in walls: test account, guest option, or hand over to the user.
                if (interrupt.Kind == InterruptKind.Login)
                {
                    var outcome = await HandleLoginAsync(s, node, ui, step, options, control, ct);
                    if (outcome is { } stop)
                    {
                        result = Summary(AgentRunStatus.Completed, stop);
                        break;
                    }

                    _store.SaveNode(_runId, node);
                    continue;
                }

                // ---- Payment and banking screens: the user's call, never the agent's.
                if (interrupt.Kind == InterruptKind.Sensitive)
                {
                    await HandleSensitiveAsync(s, node, step, options, control, ct);
                    _store.SaveNode(_runId, node);
                    continue;
                }

                // ---- Stuck: the same screen for many steps, or going back and forth between two screens.
                // Staying on one screen is only "stuck" when nothing new gets tried there (forms, toggles, dialogs
                // legitimately take several in-place actions).
                var triedSomethingNew = s.Previous?.Id == node.Id && node.Tried.Count > s.LastTriedCount;
                s.SameNodeStreak = s.Previous?.Id == node.Id && !triedSomethingNew ? s.SameNodeStreak + 1 : 0;
                s.LastTriedCount = node.Tried.Count;
                s.RecentNodes.Enqueue(node.Id);
                while (s.RecentNodes.Count > CircleWindow)
                {
                    s.RecentNodes.Dequeue();
                }

                if (s.SameNodeStreak >= StuckRelaunchThreshold)
                {
                    Log(step, AgentLogKind.Warning, "Stuck on the same screen; relaunching the app.");
                    await RelaunchAsync(s, options, ct);
                    continue;
                }

                if (s.RecentNodes.Count == CircleWindow && s.RecentNodes.Distinct().Count() <= 2 && s.StepsWithoutProgress >= CircleWindow)
                {
                    Log(step, AgentLogKind.Warning, "Going back and forth between the same screens; restarting from the start screen.");
                    s.RecentNodes.Clear();
                    await RelaunchAsync(s, options, ct);
                    continue;
                }

                AgentDecision decision;
                if (s.SameNodeStreak >= StuckBackThreshold && !node.IsRoot)
                {
                    decision = new AgentDecision(AgentAction.Back(), "This screen isn't changing; backing out.");
                }
                else
                {
                    var context = new AgentContext(options.AppName, options.PackageName, options.Hints, step, options.MaxSteps,
                        Graph, node, ui, png, _recent.TakeLast(6).ToList())
                    {
                        StepsWithoutNewScreen = s.StepsWithoutNewScreen,
                        TargetScreens = options.TargetScreens,
                        Secrets = Secrets(options),
                    };
                    decision = await _strategy.DecideAsync(context, ct);
                }

                if (!string.IsNullOrWhiteSpace(decision.ScreenLabel) && string.IsNullOrWhiteSpace(node.Label))
                {
                    node.Label = decision.ScreenLabel!;
                }

                Log(step, AgentLogKind.Thought, decision.Thought);
                if (decision.Action.Kind == ActionKind.Stop)
                {
                    result = Summary(AgentRunStatus.Completed, "The app looks fully explored.");
                    break;
                }

                var guard = _guardrails.Check(decision, ui);
                var firstTry = decision.Action.Element is { } chosen && node.Tried.Add(chosen.Key);
                s.StepsWithoutProgress = firstTry || isNew ? 0 : s.StepsWithoutProgress + 1;

                _store.SaveNode(_runId, node); // keep what was tried, so a crash or stop can be resumed
                if (guard.Verdict == GuardVerdict.Blocked)
                {
                    Log(step, AgentLogKind.Blocked, $"Blocked: {decision.Action.Describe()} ({guard.Reason}).");
                    s.Previous = node;
                    s.PreviousAction = null;
                    continue;
                }

                if (guard.Verdict == GuardVerdict.NeedsConfirmation &&
                    !await _observer.ConfirmAsync($"The agent wants to: {decision.Action.Describe()}\n\nReason flagged: {guard.Reason}.\n\nAllow it?"))
                {
                    Log(step, AgentLogKind.Blocked, $"Declined by you: {decision.Action.Describe()}.");
                    s.Previous = node;
                    s.PreviousAction = null;
                    continue;
                }

                Log(step, AgentLogKind.Action, Mask(decision.Action.Describe(), options));
                await ExecuteAsync(decision.Action, node, options.Patience, ct);
                _recent.Add(decision.Action.EdgeLabel);
                s.Previous = node;
                s.PreviousAction = decision.Action;
            }

        Finished:;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
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

        if (options.CleanStatusBar)
        {
            try
            {
                await _device.SetStatusBarDemoAsync(false, CancellationToken.None); // give the user their real status bar back
            }
            catch (UserFacingException ex)
            {
                _log.LogWarning(ex, "Couldn't leave status bar demo mode");
            }
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

    /// <summary>Counts a crash and relaunches; returns a failure message once crashes reach the limit.</summary>
    private async Task<string?> OnCrashAsync(RunState s, int step, AgentRunOptions options, CancellationToken ct)
    {
        s.Crashes++;
        var after = s.PreviousAction is { } a ? $" after: {Mask(a.Describe(), options)}" : "";
        Log(step, AgentLogKind.Warning, $"The app closed unexpectedly{after} (crash {s.Crashes} of at most {options.MaxCrashes}).");
        if (s.Crashes >= options.MaxCrashes)
        {
            return $"The app crashed {s.Crashes} times; stopping. Check Logcat on the Emulator page for the cause.";
        }

        await RelaunchAsync(s, options, ct);
        return null;
    }

    private async Task RelaunchAsync(RunState s, AgentRunOptions options, CancellationToken ct)
    {
        await _device.LaunchAsync(options.PackageName, options.LauncherActivity, ct);
        s.JustLaunched = true;
        s.ForgetPath();
        s.OutsideStreak = 0;
        s.SameNodeStreak = 0;
    }

    // ------------------------------------------------------------------ sign-in walls

    [GeneratedRegex(@"e-?mail|user|login|phone|account|mail", RegexOptions.IgnoreCase)]
    private static partial Regex UserFieldHint();

    [GeneratedRegex(@"^(sign in|log in|login|log on|sign-in|log-in|continue|next|submit|done|enter|go)$", RegexOptions.IgnoreCase)]
    private static partial Regex SubmitLabel();

    [GeneratedRegex(@"google|facebook|apple|twitter|github|microsoft|forgot|sign up|create|register|with ", RegexOptions.IgnoreCase)]
    private static partial Regex NotSubmit();

    [GeneratedRegex(@"guest|skip|not now|later|explore|browse|without (an )?account|demo|try it|look around", RegexOptions.IgnoreCase)]
    private static partial Regex GuestLabel();

    [GeneratedRegex(@"['""“‘]([^'""”’]{3,})['""”’]")]
    private static partial Regex Quoted();

    /// <summary>
    /// Gets past a sign-in screen: type the test account (never through the AI), else take a guest/demo route,
    /// else (on the start screen) hand over to the user or finish. Returns a stop message, or null to continue.
    /// </summary>
    /// <summary>
    /// A payment, checkout or banking screen: hand control to the user once (they can fill it in or leave, then
    /// Resume); if the agent finds itself there again, it leaves without touching anything.
    /// </summary>
    private async Task HandleSensitiveAsync(RunState s, ScreenNode node, int step, AgentRunOptions options, AgentControl control, CancellationToken ct)
    {
        if (options.PauseOnSensitive && !node.HandedOver)
        {
            node.HandedOver = true;
            const string reason = "This screen asks for payment or banking details, which SSGen never fills in. You have control: fill it in yourself or leave the screen, then press Resume.";
            Log(step, AgentLogKind.Warning, reason);
            control.TakeOver(reason);
            _observer.OnTakeoverRequested(reason);
            s.Previous = node;
            s.PreviousAction = null;
            return;
        }

        node.Tried.UnionWith(node.Known);
        Log(step, AgentLogKind.Info, "Leaving the payment screen without touching it.");
        await _device.PressKeyAsync(AndroidKey.Back, ct);
        s.Previous = node;
        s.PreviousAction = AgentAction.Back();
    }

    private async Task<string?> HandleLoginAsync(RunState s, ScreenNode node, UiSnapshot ui, int step, AgentRunOptions options, AgentControl control, CancellationToken ct)
    {
        var login = options.Login;
        var password = ui.Elements.FirstOrDefault(e => e.Password);
        var fields = ui.Elements.Where(e => e.Editable && !e.Password).ToList();
        var user = fields.FirstOrDefault(e => UserFieldHint().IsMatch($"{e.ResourceName} {e.Hint} {e.Label} {e.Description}"))
                   ?? fields.Where(e => password is null || e.Bounds.Top < password.Bounds.Top).OrderByDescending(e => e.Bounds.Top).FirstOrDefault()
                   ?? fields.FirstOrDefault();

        // 1. Test account.
        if (login.HasCredentials && s.LoginAttempts < MaxLoginAttempts && (user is not null || password is not null))
        {
            s.LoginAttempts++;
            if (user is not null && !string.IsNullOrEmpty(login.Username))
            {
                await FillAsync(user, login.Username, ct);
                node.Tried.Add(user.Key);
            }

            if (password is not null && !string.IsNullOrEmpty(login.Password))
            {
                await FillAsync(password, login.Password, ct);
                node.Tried.Add(password.Key);
            }

            var submit = ui.Elements.Where(e => e.Clickable && !e.Editable && !NotSubmit().IsMatch(e.Label))
                .OrderByDescending(e => e.Label.Contains("sign in", StringComparison.OrdinalIgnoreCase) || e.Label.Contains("log in", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(e => SubmitLabel().IsMatch(e.Label.Trim()) || e.Label.Contains("sign in", StringComparison.OrdinalIgnoreCase) || e.Label.Contains("log in", StringComparison.OrdinalIgnoreCase));
            if (submit is not null && _guardrails.Check(new AgentDecision(new AgentAction(ActionKind.Tap, submit), ""), ui).Verdict == GuardVerdict.Allowed)
            {
                await TapAsync(submit, ct);
            }
            else
            {
                await _device.PressKeyAsync(AndroidKey.Enter, ct);
            }

            Log(step, AgentLogKind.Action, $"Signing in with your test account (attempt {s.LoginAttempts} of {MaxLoginAttempts}).");
            s.Previous = node;
            s.PreviousAction = submit is null ? null : new AgentAction(ActionKind.Tap, submit);
            return null;
        }

        // 2. A way in without an account: the user's hint first, then the usual wording.
        var hintWords = login.GuestHint is { } hint
            ? (Quoted().Match(hint) is { Success: true } q ? [q.Groups[1].Value] : hint.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4).ToArray())
            : [];
        var guest = ui.Elements.Where(e => e.Clickable && !e.Editable && !node.Tried.Contains(e.Key))
            .FirstOrDefault(e => hintWords.Any(w => e.Label.Contains(w.Trim(), StringComparison.OrdinalIgnoreCase)))
            ?? ui.Elements.Where(e => e.Clickable && !e.Editable && !node.Tried.Contains(e.Key)).FirstOrDefault(e => GuestLabel().IsMatch(e.Label));
        if (guest is not null && _guardrails.Check(new AgentDecision(new AgentAction(ActionKind.Tap, guest), ""), ui).Verdict == GuardVerdict.Allowed)
        {
            node.Tried.Add(guest.Key);
            Log(step, AgentLogKind.Action, $"Continuing without an account ('{guest.Label}').");
            await TapAsync(guest, ct);
            s.Previous = node;
            s.PreviousAction = new AgentAction(ActionKind.Tap, guest);
            return null;
        }

        // 3. A sign-in panel inside the app (not the start screen) is just a dead end.
        if (!node.IsRoot)
        {
            node.Tried.UnionWith(node.Known);
            Log(step, AgentLogKind.Info, "A sign-in screen with no test account to use; going back.");
            await _device.PressKeyAsync(AndroidKey.Back, ct);
            s.Previous = node;
            s.PreviousAction = AgentAction.Back();
            return null;
        }

        // 4. Already got past this screen earlier (guest route, a login that worked): nothing left to do here.
        if (Graph.EdgesFrom(node.Id).Any())
        {
            return "The app looks fully explored.";
        }

        // 5. The app can't be used without signing in: ask the user (once), or finish.
        if (login.AskUser && s.LoginTakeovers == 0)
        {
            s.LoginTakeovers++;
            var reason = s.LoginAttempts > 0
                ? "Signing in with the test account didn't work. Log in yourself on the Emulator page, then press Resume."
                : "This app asks you to sign in. Log in on the Emulator page (or add a test account in Explore settings), then press Resume.";
            Log(step, AgentLogKind.Warning, reason);
            control.TakeOver(reason);
            _observer.OnTakeoverRequested(reason);
            return null;
        }

        return s.LoginAttempts > 0
            ? "Couldn't get past the sign-in screen with the test account. Check it in Explore settings, or take over and log in yourself."
            : "The app requires signing in. Add a test account in Explore settings, or take over and log in yourself.";
    }

    /// <summary>Types into a field, replacing what's there, then hides the keyboard.</summary>
    private async Task FillAsync(UiElement field, string value, CancellationToken ct)
    {
        await TapAsync(field, ct);
        await Task.Delay(300, ct);
        if (!field.IsEmptyField && field.Text.Length > 0)
        {
            await _device.PressKeyAsync(AndroidKey.MoveEnd, ct);
            for (var i = 0; i < Math.Min(field.Text.Length, 64); i++)
            {
                await _device.PressKeyAsync(AndroidKey.Delete, ct);
            }
        }

        await _device.InputTextAsync(value, ct);
        await _device.PressKeyAsync(AndroidKey.Escape, ct);
    }

    private static string[] Secrets(AgentRunOptions o) =>
        new[] { o.Login.Username, o.Login.Password }.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToArray();

    /// <summary>Hides the test account in anything written to the log.</summary>
    private static string Mask(string text, AgentRunOptions o)
    {
        foreach (var secret in Secrets(o))
        {
            text = text.Replace(secret, "[hidden]", StringComparison.Ordinal);
        }

        return text;
    }

    // ------------------------------------------------------------------ observing and acting

    /// <summary>
    /// Settles, reads the UI tree and activity, and makes sure the screenshot matches them: the UI dump takes a
    /// moment, and a slow transition can still be under way. An open keyboard (only possible with text fields) is
    /// closed first. <paramref name="before"/> is the previous screen's hash, to wait for an action's effect.
    /// </summary>
    private async Task<(byte[] Png, UiSnapshot Ui, string? Activity)> ObserveAsync(AgentRunOptions options, ulong? before, CancellationToken ct)
    {
        var png = await SettleAsync(options, before, ct);
        var ui = UiHierarchyParser.Parse(await _device.DumpUiHierarchyAsync(ct));
        if (ui.Elements.Any(e => e.Editable) && await _device.IsKeyboardShownAsync(ct))
        {
            // Escape, not Back: Back could navigate away. The keyboard covers half the screen and isn't the app.
            await _device.PressKeyAsync(AndroidKey.Escape, ct);
            png = await SettleAsync(options, null, ct);
            ui = UiHierarchyParser.Parse(await _device.DumpUiHierarchyAsync(ct));
        }

        var activity = await _device.GetForegroundActivityAsync(ct);
        var check = await _device.CaptureScreenshotPngAsync(ct);
        if (ScreenFingerprint.Hamming(ScreenFingerprint.DifferenceHash(png), ScreenFingerprint.DifferenceHash(check)) > 4)
        {
            // The screen changed while reading the UI: observe again so image, tree and activity agree.
            png = await SettleAsync(options, null, ct);
            ui = UiHierarchyParser.Parse(await _device.DumpUiHierarchyAsync(ct));
            activity = await _device.GetForegroundActivityAsync(ct);
        }
        else
        {
            png = check;
        }

        return (png, ui, activity);
    }

    /// <summary>
    /// Waits for the screen to change from <paramref name="before"/> (an action's effect; bounded), then until two
    /// consecutive screenshots look the same (animations/loading finished) or a timeout. Returns the last capture.
    /// </summary>
    private async Task<byte[]> SettleAsync(AgentRunOptions options, ulong? before, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        var deadline = start + options.SettleTimeout;
        var changeDeadline = start + TimeSpan.FromTicks(Math.Min(options.SettleTimeout.Ticks, TimeSpan.FromSeconds(1.5).Ticks));
        await Task.Delay(options.SettlePoll, ct); // let the previous input start its transition
        var waitingForChange = before is not null;
        ulong? last = null;
        while (true)
        {
            var png = await _device.CaptureScreenshotPngAsync(ct);
            var hash = ScreenFingerprint.DifferenceHash(png);
            if (waitingForChange)
            {
                if (ScreenFingerprint.Hamming(before!.Value, hash) > 2 || DateTime.UtcNow >= changeDeadline)
                {
                    waitingForChange = false;
                }
                else
                {
                    await Task.Delay(options.SettlePoll, ct);
                    continue;
                }
            }

            if ((last is { } l && ScreenFingerprint.Hamming(l, hash) <= 2) || DateTime.UtcNow >= deadline)
            {
                return png;
            }

            last = hash;
            await Task.Delay(options.SettlePoll, ct);
        }
    }

    private Task TapAsync(UiElement e, CancellationToken ct) => _device.TapAsync(e.Bounds.CenterX, e.Bounds.CenterY, ct);

    private async Task ExecuteAsync(AgentAction action, ScreenNode node, TimeSpan patience, CancellationToken ct)
    {
        switch (action.Kind)
        {
            case ActionKind.Tap when action.Element is { } e:
                await TapAsync(e, ct);
                break;
            case ActionKind.Type when action.Element is { } e:
                await FillAsync(e, action.Text ?? "", ct);
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
                await Task.Delay(patience, ct);
                break;
        }
    }

    private void SaveScreen(ScreenNode node, byte[] png, string? activity, UiSnapshot ui)
    {
        using var bitmap = SKBitmap.Decode(png);
        var saved = ScreenRepository.Add(_project, png, "agent", activity, bitmap?.Width ?? 0, bitmap?.Height ?? 0);
        ScreenRepository.SetText(_project, saved.Id, ui.Title, string.Join("\n", ui.AllText.Distinct().Take(60)));
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
