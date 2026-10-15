using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SSGen.Agent.Graph;
using SSGen.Agent.Runs;
using SSGen.Agent.Safety;
using SSGen.Agent.Strategies;
using SSGen.Ai;
using SSGen.Ai.Profiles;
using SSGen.App.Services;
using SSGen.Core;
using SSGen.Core.Projects;
using SSGen.Core.Security;
using SSGen.Core.Settings;
using SSGen.Emulator.Apk;

namespace SSGen.App.ViewModels;

/// <summary>A discovered screen shown as a thumbnail.</summary>
public sealed partial class ScreenThumbViewModel : ObservableObject
{
    public required int NodeId { get; init; }

    public required BitmapSource Image { get; init; }

    [ObservableProperty]
    public partial string Title { get; set; } = "";
}

/// <summary>A screen in the map diagram.</summary>
public sealed record MapNode(int Id, string Label, bool IsRoot);

/// <summary>A link in the map diagram (the action that leads from one screen to another).</summary>
public sealed record MapEdge(int From, int To, string Label);

/// <summary>One prerequisite in the "get ready" checklist.</summary>
public sealed record ReadyStep(string Title, string Hint, bool Done);

/// <summary>One line of the run log.</summary>
public sealed record AgentLogLine(string Time, string Kind, string Message);

/// <summary>Explore page: runs the exploration agent (AI or systematic) on the running device.</summary>
public sealed partial class AgentViewModel : ObservableObject, IPageActivated, IAgentObserver, IDisposable
{
    private readonly EmulatorViewModel _emulator;
    private readonly IProjectService _projects;
    private readonly AiService _ai;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly ILogger<AgentViewModel> _log;
    private readonly ISecretStore _secrets;
    private readonly AiRunGate _gate;
    private PageKey _fixPage = PageKey.Home;
    private CancellationTokenSource? _cts;
    private AgentControl? _control;
    private bool _aiChoiceMade;
    private string? _loadedProject;
    private bool _loadingOptions;
    private long? _lastRunId;

    public AgentViewModel(EmulatorViewModel emulator, IProjectService projects, AiService ai, ISettingsService settings, IDialogService dialogs, INavigationService navigation, ISecretStore secrets, AiRunGate gate, ILogger<AgentViewModel> log)
    {
        _gate = gate;
        _navigation = navigation;
        _secrets = secrets;
        _emulator = emulator;
        _projects = projects;
        _ai = ai;
        _settings = settings;
        _dialogs = dialogs;
        _log = log;
        _emulator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EmulatorViewModel.Device) or nameof(EmulatorViewModel.Apk))
            {
                Refresh();
            }
        };
        _projects.CurrentChanged += (_, _) => Application.Current.Dispatcher.Invoke(Refresh);
        Refresh();
    }

    public ObservableCollection<AgentLogLine> Log { get; } = [];

    public ObservableCollection<ScreenThumbViewModel> Screens { get; } = [];

    public ObservableCollection<string> Map { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(ResumeLastCommand))]
    public partial string? Blocker { get; set; }

    public bool HasBlocker => Blocker is not null;

    /// <summary>Project, APK, device: what must be ready before exploring, in order.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ReadyStep> ReadySteps { get; set; } = [];

    /// <summary>Button text for the first unfinished step ("Go to Home", "Open Emulator").</summary>
    [ObservableProperty]
    public partial string FixActionText { get; set; } = "";

    [RelayCommand]
    private void GoToFix() => _navigation.NavigateTo(_fixPage);

    partial void OnBlockerChanged(string? value) => OnPropertyChanged(nameof(HasBlocker));

    [ObservableProperty]
    public partial string AiDescription { get; set; } = "";

    [ObservableProperty]
    public partial bool AiAvailable { get; set; }

    [ObservableProperty]
    public partial bool UseAi { get; set; }

    [ObservableProperty]
    public partial string Hints { get; set; } = "";

    [ObservableProperty]
    public partial int MaxSteps { get; set; } = 60;

    [ObservableProperty]
    public partial int MaxMinutes { get; set; } = 20;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(PauseCommand), nameof(ResumeLastCommand), nameof(TakeOverCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsRunning { get; set; }

    public bool IsIdle => !IsRunning;

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Ready.";

    [ObservableProperty]
    public partial string Meter { get; set; } = "";

    [ObservableProperty]
    public partial string Progress { get; set; } = "";

    [ObservableProperty]
    public partial BitmapSource? CurrentScreen { get; set; }

    [ObservableProperty]
    public partial string CurrentScreenName { get; set; } = "";

    // ---- Per-project options (saved in the project; the password in the encrypted secret store)

    /// <summary>How many store screenshots the user wants (guides the AI; used for selection in M5).</summary>
    [ObservableProperty]
    public partial int TargetScreens { get; set; } = 8;

    [ObservableProperty]
    public partial string LoginUsername { get; set; } = "";

    [ObservableProperty]
    public partial string LoginGuestHint { get; set; } = "";

    /// <summary>When no way past a sign-in wall works: true = pause and ask me; false = finish.</summary>
    [ObservableProperty]
    public partial bool LoginAskUser { get; set; } = true;

    [ObservableProperty]
    public partial bool HasSavedPassword { get; set; }

    /// <summary>Android demo mode while exploring: 9:41, full battery, no notifications.</summary>
    [ObservableProperty]
    public partial bool CleanStatusBar { get; set; } = true;

    partial void OnCleanStatusBarChanged(bool value) => SaveOption("explore.cleanStatusBar", value ? "1" : "0");

    partial void OnHintsChanged(string value) => SaveOption("explore.hints", value.Trim());

    partial void OnTargetScreensChanged(int value) => SaveOption("explore.targetScreens", value.ToString(CultureInfo.InvariantCulture));

    partial void OnLoginUsernameChanged(string value) => SaveOption("explore.login.username", value.Trim());

    partial void OnLoginGuestHintChanged(string value) => SaveOption("explore.login.guestHint", value.Trim());

    partial void OnLoginAskUserChanged(bool value) => SaveOption("explore.login.askUser", value ? "1" : "0");

    partial void OnUseAiChanged(bool value) => _aiChoiceMade = true;

    /// <summary>Called by the view's PasswordBox (it can't be bound). Empty clears nothing; use ClearPassword.</summary>
    public void SetPassword(string password)
    {
        if (_projects.Current is { } p && password.Length > 0)
        {
            _secrets.Save(PasswordSecretName(p), password);
            HasSavedPassword = true;
        }
    }

    [RelayCommand]
    private void ClearPassword()
    {
        if (_projects.Current is { } p)
        {
            _secrets.Delete(PasswordSecretName(p));
            HasSavedPassword = false;
        }
    }

    /// <summary>Each project gets a stable id (stored in the project) to key its secrets.</summary>
    private static string PasswordSecretName(Project p)
    {
        var id = ProjectMeta.Get(p, "id");
        if (string.IsNullOrEmpty(id))
        {
            id = Guid.NewGuid().ToString("N");
            ProjectMeta.Set(p, "id", id);
        }

        return $"project-login:{id}";
    }

    private void SaveOption(string key, string value)
    {
        if (!_loadingOptions && _projects.Current is { } p)
        {
            ProjectMeta.Set(p, key, value);
        }
    }

    private void LoadProjectOptions(Project p)
    {
        _loadingOptions = true;
        TargetScreens = int.TryParse(ProjectMeta.Get(p, "explore.targetScreens"), CultureInfo.InvariantCulture, out var t) ? t : 8;
        LoginUsername = ProjectMeta.Get(p, "explore.login.username") ?? "";
        LoginGuestHint = ProjectMeta.Get(p, "explore.login.guestHint") ?? "";
        LoginAskUser = ProjectMeta.Get(p, "explore.login.askUser") != "0";
        HasSavedPassword = _secrets.Load(PasswordSecretName(p)) is { Length: > 0 };
        CleanStatusBar = ProjectMeta.Get(p, "explore.cleanStatusBar") != "0";
        Hints = ProjectMeta.Get(p, "explore.hints") ?? "";
        _loadingOptions = false;
    }

    // ---- Manual takeover

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TakeOverCommand), nameof(PauseCommand))]
    public partial bool IsTakenOver { get; set; }

    [ObservableProperty]
    public partial string TakeoverReason { get; set; } = "";

    private bool CanTakeOver() => IsRunning && !IsTakenOver;

    [RelayCommand(CanExecute = nameof(CanTakeOver))]
    private void TakeOver()
    {
        const string reason = "Use the Emulator page to do anything the agent can't (log in, set something up), then press Resume.";
        _control?.TakeOver(reason);
        EnterTakeover(reason);
    }

    [RelayCommand]
    private void ResumeFromTakeover()
    {
        _control?.Resume();
        IsTakenOver = false;
        IsPaused = false;
        Status = "Exploring…";
    }

    [RelayCommand]
    private void OpenEmulator() => _navigation.NavigateTo(PageKey.Emulator);

    /// <summary>Explorer settings card open. Starts closed when no AI provider is set (only Run matters then).</summary>
    [ObservableProperty]
    public partial bool ShowSettings { get; set; }

    /// <summary>Sign-in card open (same default as <see cref="ShowSettings"/>).</summary>
    [ObservableProperty]
    public partial bool ShowSignIn { get; set; }

    private bool _panelsChosen;
    private bool _settingPanels;

    partial void OnShowSettingsChanged(bool value) => _panelsChosen |= !_settingPanels;

    partial void OnShowSignInChanged(bool value) => _panelsChosen |= !_settingPanels;

    /// <summary>Opens the cards when AI is available, closes them when not, unless the user already chose.</summary>
    private void DefaultPanels()
    {
        if (_panelsChosen)
        {
            return;
        }

        _settingPanels = true;
        ShowSettings = AiAvailable;
        ShowSignIn = AiAvailable;
        _settingPanels = false;
    }

    // ---- Finished banner and moving on to Design

    /// <summary>Shown after a run: what was found, with a button to continue on the Design page.</summary>
    [ObservableProperty]
    public partial string? FinishedMessage { get; set; }

    [RelayCommand]
    private void DismissFinished() => FinishedMessage = null;

    /// <summary>The captured screens are already in the project: the Design page ranks and uses them.</summary>
    [RelayCommand]
    private void ContinueToDesign()
    {
        FinishedMessage = null;
        _navigation.NavigateTo(PageKey.Design);
    }

    private void EnterTakeover(string reason)
    {
        TakeoverReason = reason;
        IsTakenOver = true;
        IsPaused = true;
        Status = "Waiting for you: " + reason;
    }

    // ---- Last run (shown when idle; can be continued)

    [ObservableProperty]
    public partial string LastRunText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeLastCommand))]
    public partial bool CanResumeLast { get; set; }

    /// <summary>The screen graph for the map view (snapshots; replaced as the run progresses).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<MapNode> MapNodes { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<MapEdge> MapEdges { get; set; } = [];

    /// <summary>Screen map shown as a diagram (true) or a list of links (false).</summary>
    [ObservableProperty]
    public partial bool MapAsGraph { get; set; } = true;

    private void LoadLastRun(Project p)
    {
        var store = new AgentRunStore(p);
        var last = store.LatestRun();
        _lastRunId = last?.Id;
        CanResumeLast = last?.CanResume == true;
        LastRunText = last is null ? "" : $"Last run {last.StartedUtc.ToLocalTime():g}: {last.Screens} screens, {last.Steps} steps, {last.Status switch
        {
            "completed" => "finished",
            "limitreached" => "stopped at a limit",
            "interrupted" => "interrupted (SSGen closed)",
            var s => s,
        }}.";

        Screens.Clear();
        Map.Clear();
        MapNodes = [];
        MapEdges = [];
        if (last is null)
        {
            return;
        }

        foreach (var screen in store.Screens(last.Id))
        {
            if (screen.File is { } file && File.Exists(file))
            {
                Screens.Add(new ScreenThumbViewModel { NodeId = screen.NodeId, Image = Decode(File.ReadAllBytes(file), 160), Title = ScreenTitle(screen.Label, screen.Activity, screen.NodeId) });
            }
        }

        ShowGraph(store.LoadGraph(last.Id));
    }

    private static string ScreenTitle(string label, string? activity, int id) =>
        !string.IsNullOrWhiteSpace(label) ? label : activity?.Split('/').LastOrDefault()?.TrimStart('.') ?? $"Screen {id}";

    /// <summary>Screen names, with " #id" added where several screens share a name (same activity, no AI label).</summary>
    internal static Dictionary<int, string> UniqueTitles(ScreenGraph graph)
    {
        var dupes = graph.Nodes.GroupBy(n => n.DisplayName).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return graph.Nodes.ToDictionary(n => n.Id, n => dupes.Contains(n.DisplayName) ? $"{n.DisplayName} #{n.Id}" : n.DisplayName);
    }

    private void ShowGraph(ScreenGraph graph)
    {
        var titles = UniqueTitles(graph);
        foreach (var s in Screens)
        {
            if (titles.TryGetValue(s.NodeId, out var t))
            {
                s.Title = t;
            }
        }

        MapNodes = graph.Nodes.Select(n => new MapNode(n.Id, titles[n.Id], n.IsRoot)).ToList();
        MapEdges = graph.Edges.Select(e => new MapEdge(e.From, e.To, e.Action)).ToList();
        Map.Clear();
        foreach (var e in graph.Edges)
        {
            Map.Add($"{titles[e.From]}  →  {titles[e.To]}   ({e.Action})");
        }
    }

    public void OnActivated() => Refresh();

    /// <summary>Saved Explore split weights (8 values, see <see cref="AppSettings.ExplorePaneWeights"/>), or null.</summary>
    public IReadOnlyList<double>? PaneWeights =>
        _settings.Current.ExplorePaneWeights is { Length: 8 } w && w.All(v => v > 0) ? w : null;

    public void SavePaneWeights(IEnumerable<double> weights) =>
        _settings.Save(_settings.Current with { ExplorePaneWeights = weights.Select(w => Math.Round(w)).ToArray() });

    private void Refresh()
    {
        if (IsRunning)
        {
            return;
        }

        var hasProject = _projects.Current is not null;
        var hasApk = hasProject && _emulator.Apk is not null;
        var hasDevice = _emulator.Device is not null;
        ReadySteps =
        [
            new("Open a project", "Create or open one on Home.", hasProject),
            new("Import the app's APK", "On the Emulator page: Import APK.", hasApk),
            new("Start a device", "On the Emulator page: pick a device and Start.", hasDevice),
        ];
        Blocker = !hasProject ? "Open or create a project first."
            : !hasApk ? "Import the APK of the app you want to explore."
            : !hasDevice ? "Start a virtual device or connect a phone."
            : null;
        _fixPage = hasProject ? PageKey.Emulator : PageKey.Home;
        FixActionText = hasProject ? "Open Emulator" : "Go to Home";

        // A different (or no) project: its own options and last run.
        if (_projects.Current?.Folder != _loadedProject)
        {
            _loadedProject = _projects.Current?.Folder;
            if (_projects.Current is { } p)
            {
                LoadProjectOptions(p);
                LoadLastRun(p);
            }
            else
            {
                Screens.Clear();
                Map.Clear();
                MapNodes = [];
                MapEdges = [];
                LastRunText = "";
                CanResumeLast = false;
            }
        }
        else if (_projects.Current is { } current)
        {
            var last = new AgentRunStore(current).LatestRun();
            CanResumeLast = last?.CanResume == true;
            _lastRunId = last?.Id;
        }

        var resolved = _ai.ResolveTask(AiTask.Exploration);
        AiAvailable = resolved is not null;
        DefaultPanels();
        if (resolved is { } r)
        {
            var local = r.Profile.Type == ProviderType.Ollama || ProviderProfileIsLoopback(r.Profile);
            AiDescription = $"{r.Profile.Name} · {r.Model}{(local ? " · Free (local)" : "")}";
            if (!_aiChoiceMade)
            {
                UseAi = true;
                _aiChoiceMade = false; // a default, not the user's choice
            }
        }
        else
        {
            AiDescription = "No AI provider configured (Settings > AI providers).";
            UseAi = false;
            _aiChoiceMade = false;
        }
    }

    private static bool ProviderProfileIsLoopback(ProviderProfile p) =>
        Uri.TryCreate(p.EffectiveBaseUrl, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost");

    private bool CanStart() => !IsRunning && Blocker is null;

    private bool CanResume() => CanStart() && CanResumeLast && _lastRunId is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunAsync(resumeRunId: null);

    /// <summary>Continues the last run that ended early: its screens and what was tried are kept.</summary>
    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task ResumeLastAsync() => RunAsync(_lastRunId);

    private async Task RunAsync(long? resumeRunId)
    {
        var project = _projects.Current!;
        var device = _emulator.Device!;
        var apk = _emulator.Apk!;
        var settings = _settings.Current;

        IAgentStrategy strategy = new SystematicStrategy();
        var meter = new CostMeter(false, null, null);
        Log.Clear();
        if (resumeRunId is null)
        {
            Screens.Clear();
            Map.Clear();
            MapNodes = [];
            MapEdges = [];
        }

        CurrentScreen = null;
        IsTakenOver = false;
        FinishedMessage = null;
        LastRunText = "";
        IsRunning = true;
        IsPaused = false;
        Status = "Preparing…";
        _cts = new CancellationTokenSource();
        _control = new AgentControl();

        try
        {
            if (!await Task.Run(() => device.IsInstalledAsync(apk.Info.PackageName)))
            {
                Status = $"Installing {apk.Info.DisplayName}…";
                await Task.Run(() => device.InstallAsync([apk.ApkPath(project)]));
            }

            if (UseAi && AiAvailable)
            {
                Status = "Connecting to the AI provider…";
                var resolved = await _ai.ResolveAsync(AiTask.Exploration, _cts.Token);
                if (resolved is not null)
                {
                    if (_gate.Prepare(resolved, "To decide where to tap", w => AddLine(new AgentLogEntry(0, AgentLogKind.Warning, w, DateTime.Now))) is not { } prepared)
                    {
                        Status = "Cancelled.";
                        return;
                    }

                    meter = prepared.Meter;
                    strategy = new AiStrategy(resolved.Provider, prepared.Vision, meter.Add);
                }
            }

            var guardrails = new Guardrails(settings.SafeMode, Guardrails.ParseExtra(settings.AgentBlocklistExtra));
            var agent = new ExplorationAgent(device, strategy, guardrails, meter, project, this, _log);
            var options = new AgentRunOptions
            {
                AppName = apk.Info.DisplayName,
                PackageName = apk.Info.PackageName,
                LauncherActivity = apk.Info.LauncherActivity,
                Hints = string.IsNullOrWhiteSpace(Hints) ? null : Hints.Trim(),
                MaxSteps = Math.Clamp(MaxSteps, 1, 1000),
                MaxDuration = TimeSpan.FromMinutes(Math.Clamp(MaxMinutes, 1, 240)),
                TargetScreens = Math.Clamp(TargetScreens, 1, 10),
                CleanStatusBar = CleanStatusBar,
                ResumeRunId = resumeRunId,
                Login = new LoginOptions(
                    string.IsNullOrWhiteSpace(LoginUsername) ? null : LoginUsername.Trim(),
                    _secrets.Load(PasswordSecretName(project)),
                    string.IsNullOrWhiteSpace(LoginGuestHint) ? null : LoginGuestHint.Trim(),
                    LoginAskUser),
            };

            Status = $"Exploring with {strategy.Name}…";
            var control = _control;
            var token = _cts.Token;
            var summary = await Task.Run(() => agent.RunAsync(options, control, token));
            Status = $"{summary.Message} {summary.Screens} unique screens in {summary.Steps} steps.";
            FinishedMessage = $"Exploration finished: {summary.Screens} screen(s) found in {summary.Steps} steps. {summary.Message}";
            Attention.Request();
            Meter = meter.Describe();
            if (summary.Screens > 0)
            {
                Status += " Screens were saved to the project.";
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (Exception ex) when (ex is UserFacingException or LlmException)
        {
            _log.LogWarning(ex, "Exploration failed");
            Status = "Failed: " + ex.Message;
            _dialogs.ShowError("Exploration failed", ex is LlmException l ? new UserFacingException(l.Message, l.SuggestedFix) : ex);
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            IsTakenOver = false;
            _loadedProject = null; // reload the last run (now this one) on Refresh
            _cts?.Dispose();
            _cts = null;
            _control?.Dispose();
            _control = null;
            Refresh();
        }
    }

    private bool CanControl() => IsRunning;

    private bool CanPause() => IsRunning && !IsTakenOver;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        if (_control is null)
        {
            return;
        }

        if (_control.IsPaused)
        {
            _control.Resume();
            IsPaused = false;
            Status = "Exploring…";
        }
        else
        {
            _control.Pause();
            IsPaused = true;
            Status = "Paused after the current step.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanControl))]
    private void Stop()
    {
        Status = "Stopping…";
        _control?.Resume();
        _cts?.Cancel();
    }

    // IAgentObserver: called from the agent's thread.
    void IAgentObserver.OnLog(AgentLogEntry entry) => OnUi(() => AddLine(entry));

    void IAgentObserver.OnScreen(byte[] png, ScreenNode node)
    {
        var image = Decode(png, 480);
        var name = node.DisplayName;
        OnUi(() =>
        {
            CurrentScreen = image;
            CurrentScreenName = name;
        });
    }

    void IAgentObserver.OnNewScreen(ScreenNode node, byte[] png)
    {
        var image = Decode(png, 160);
        var thumb = new ScreenThumbViewModel { NodeId = node.Id, Image = image, Title = node.DisplayName };
        OnUi(() => Screens.Add(thumb));
    }

    void IAgentObserver.OnProgress(int stepNumber, CostMeter meter, ScreenGraph graph)
    {
        var meterText = meter.Describe();
        var progress = $"Step {stepNumber} · {graph.Nodes.Count} screens · {graph.Edges.Count} links";
        var titles = UniqueTitles(graph);
        var map = graph.Edges.Select(e => $"{titles[e.From]}  →  {titles[e.To]}   ({e.Action})").ToList();
        var nodes = graph.Nodes.Select(n => new MapNode(n.Id, titles[n.Id], n.IsRoot)).ToList();
        var edges = graph.Edges.Select(e => new MapEdge(e.From, e.To, e.Action)).ToList();
        OnUi(() =>
        {
            Meter = meterText;
            Progress = progress;
            if (nodes.Count != MapNodes.Count || edges.Count != MapEdges.Count || nodes.Zip(MapNodes).Any(p => p.First != p.Second))
            {
                MapNodes = nodes;
                MapEdges = edges;
            }
            foreach (var s in Screens)
            {
                if (titles.TryGetValue(s.NodeId, out var t))
                {
                    s.Title = t;
                }
            }

            for (var i = Map.Count; i < map.Count; i++)
            {
                Map.Add(map[i]);
            }
        });
    }

    void IAgentObserver.OnTakeoverRequested(string reason) => OnUi(() =>
    {
        EnterTakeover(reason);
        Attention.Request(); // the user may be looking elsewhere
        Application.Current?.MainWindow?.Activate();
    });

    Task<bool> IAgentObserver.ConfirmAsync(string question) =>
        Application.Current.Dispatcher.InvokeAsync(() => _dialogs.Confirm("Allow this action?", question)).Task;

    private void AddLine(AgentLogEntry e)
    {
        Log.Add(new AgentLogLine(e.Time.ToString("HH:mm:ss", CultureInfo.CurrentCulture), e.Kind.ToString(), e.Message));
        if (Log.Count > 2000)
        {
            Log.RemoveAt(0);
        }
    }

    private static BitmapImage Decode(byte[] png, int width)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(png);
        image.DecodePixelWidth = width;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void OnUi(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _control?.Dispose();
    }
}
