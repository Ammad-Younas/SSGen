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

/// <summary>One prerequisite in the "get ready" checklist.</summary>
public sealed record ReadyStep(string Title, string Hint, bool Done);

/// <summary>One line of the run log.</summary>
public sealed record AgentLogLine(string Time, string Glyph, string Kind, string Message);

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
    private PageKey _fixPage = PageKey.Home;
    private CancellationTokenSource? _cts;
    private AgentControl? _control;

    public AgentViewModel(EmulatorViewModel emulator, IProjectService projects, AiService ai, ISettingsService settings, IDialogService dialogs, INavigationService navigation, ILogger<AgentViewModel> log)
    {
        _navigation = navigation;
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
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(PauseCommand))]
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

        var resolved = _ai.ResolveTask(AiTask.Exploration);
        AiAvailable = resolved is not null;
        if (resolved is { } r)
        {
            var local = r.Profile.Type == ProviderType.Ollama || ProviderProfileIsLoopback(r.Profile);
            AiDescription = $"{r.Profile.Name} · {r.Model}{(local ? " · Free (local)" : "")}";
            UseAi = true;
        }
        else
        {
            AiDescription = "No AI provider configured (Settings > AI providers).";
            UseAi = false;
        }
    }

    private static bool ProviderProfileIsLoopback(ProviderProfile p) =>
        Uri.TryCreate(p.EffectiveBaseUrl, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost");

    private bool CanStart() => !IsRunning && Blocker is null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var project = _projects.Current!;
        var device = _emulator.Device!;
        var apk = _emulator.Apk!;
        var settings = _settings.Current;

        IAgentStrategy strategy = new SystematicStrategy();
        var meter = new CostMeter(false, null, null);
        Log.Clear();
        Screens.Clear();
        Map.Clear();
        CurrentScreen = null;
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
                    if (PrepareAi(resolved) is not { } prepared)
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
            };

            Status = $"Exploring with {strategy.Name}…";
            var control = _control;
            var token = _cts.Token;
            var summary = await Task.Run(() => agent.RunAsync(options, control, token));
            Status = $"{summary.Message} {summary.Screens} unique screens in {summary.Steps} steps.";
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
            _cts?.Dispose();
            _cts = null;
            _control?.Dispose();
            _control = null;
            Refresh();
        }
    }

    /// <summary>Consent for cloud vision, the first-paid-use cost cap, and the vision-fallback warning.</summary>
    private (CostMeter Meter, bool Vision)? PrepareAi(ResolvedProvider resolved)
    {
        var vision = resolved.Vision;
        if (vision && resolved.IsPaid && !_settings.Current.CloudScreenshotConsent)
        {
            var allow = _dialogs.Confirm("Send screenshots to the cloud?",
                $"\"{resolved.Profile.Name}\" is a cloud service. To decide where to tap, SSGen sends it downscaled screenshots of the app under test plus the screen's UI text.\n\n" +
                "Yes = allow (remembered; change it in Settings).\nNo = continue with UI text only (no screenshots).");
            if (allow)
            {
                _settings.Save(_settings.Current with { CloudScreenshotConsent = true });
            }
            else
            {
                vision = false;
            }
        }

        if (!vision)
        {
            AddLine(new AgentLogEntry(0, AgentLogKind.Warning,
                "The model won't see screenshots (no vision support, or not allowed). It explores from the UI text only; screen ranking will be weaker.", DateTime.Now));
        }

        if (!resolved.IsPaid)
        {
            return (new CostMeter(false, null, null), vision);
        }

        var price = _ai.Profiles.GetPrice(resolved.Provider.Model);
        var cap = _settings.Current.CostCapUsd;
        if (cap is null)
        {
            var answer = _dialogs.Prompt("Spending cap",
                "This is the first run with a paid provider. Set a spending cap per run in US dollars; the run stops when the estimated cost reaches it. You can change it in Settings.",
                "",
                v => decimal.TryParse(v.TrimStart('$'), NumberStyles.Number, CultureInfo.CurrentCulture, out var d) && d > 0 ? null : "Enter an amount greater than 0, e.g. 1.50");
            if (answer is null)
            {
                return null;
            }

            cap = decimal.Parse(answer.TrimStart('$'), NumberStyles.Number, CultureInfo.CurrentCulture);
            _settings.Save(_settings.Current with { CostCapUsd = cap });
        }

        if (price is null && !_dialogs.Confirm("Price unknown",
                $"There's no price for \"{resolved.Provider.Model}\" in Settings > AI cost control, so SSGen can count tokens but can't estimate cost or enforce the ${cap:0.00} cap.\n\nThe step and time limits still apply. Continue?"))
        {
            return null;
        }

        return (new CostMeter(true, price, cap), vision);
    }

    private bool CanControl() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanControl))]
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
        var titles = graph.Nodes.ToDictionary(n => n.Id, n => n.DisplayName);
        var map = graph.Edges.Select(e => $"{titles[e.From]}  →  {titles[e.To]}   ({e.Action})").ToList();
        OnUi(() =>
        {
            Meter = meterText;
            Progress = progress;
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

    void IAgentObserver.OnTakeoverRequested(string reason) => OnUi(() => Status = reason);

    Task<bool> IAgentObserver.ConfirmAsync(string question) =>
        Application.Current.Dispatcher.InvokeAsync(() => _dialogs.Confirm("Allow this action?", question)).Task;

    private void AddLine(AgentLogEntry e)
    {
        var glyph = e.Kind switch
        {
            AgentLogKind.Thought => "",
            AgentLogKind.Action => "",
            AgentLogKind.Blocked => "",
            AgentLogKind.Screen => "",
            AgentLogKind.Warning => "",
            AgentLogKind.Error => "",
            _ => "",
        };
        Log.Add(new AgentLogLine(e.Time.ToString("HH:mm:ss", CultureInfo.CurrentCulture), glyph, e.Kind.ToString(), e.Message));
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
