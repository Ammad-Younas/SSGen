using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using StoreShot.App.Services;
using StoreShot.Core;
using StoreShot.Core.Projects;
using StoreShot.Core.Settings;
using StoreShot.Core.Setup;
using StoreShot.Emulator;
using StoreShot.Emulator.Apk;
using StoreShot.Emulator.Avd;
using StoreShot.Emulator.Devices;
using StoreShot.Emulator.Logcat;
using StoreShot.Emulator.Mirror;
using LogLevel = StoreShot.Emulator.Logcat.LogLevel;

namespace StoreShot.App.ViewModels;

/// <summary>Emulator page: pick/start a device, mirror it live, control it, install the project's APK, logcat.</summary>
public sealed partial class EmulatorViewModel : ObservableObject, IPageActivated, IAsyncDisposable
{
    private const int MaxLogEntries = 20_000;

    private readonly DeviceHub _hub;
    private readonly IProjectService _projects;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILogger<EmulatorViewModel> _log;
    private readonly List<LogcatEntry> _allLogs = [];
    private LogcatSession? _logcat;
    private int? _appPid;
    private bool _stopping;

    public EmulatorViewModel(DeviceHub hub, IProjectService projects, ISettingsService settings, IDialogService dialogs, ILogger<EmulatorViewModel> log)
    {
        _hub = hub;
        _projects = projects;
        _settings = settings;
        _dialogs = dialogs;
        _log = log;
        ShowEmulatorWindow = settings.Current.ShowEmulatorWindow;
        _projects.CurrentChanged += (_, _) => Application.Current.Dispatcher.Invoke(LoadProjectApk);
        LoadProjectApk();
    }

    // ---------------------------------------------------------------- devices

    public ObservableCollection<DeviceTarget> Targets { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(ResetCommand), nameof(DeleteDeviceCommand))]
    public partial DeviceTarget? SelectedTarget { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(ResetCommand), nameof(DeleteDeviceCommand), nameof(InstallAndLaunchCommand), nameof(LaunchAppCommand), nameof(CaptureScreenCommand), nameof(CreateDeviceCommand), nameof(ImportApkCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    public partial string Status { get; set; } = "No device running.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(InstallAndLaunchCommand), nameof(LaunchAppCommand), nameof(CaptureScreenCommand), nameof(StartCommand), nameof(ResetCommand), nameof(DeleteDeviceCommand))]
    [NotifyPropertyChangedFor(nameof(HasDevice))]
    public partial IDevice? Device { get; set; }

    public bool HasDevice => Device is not null;

    [ObservableProperty]
    public partial IScreenMirror? Mirror { get; set; }

    [ObservableProperty]
    public partial string MirrorInfo { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowEmulatorWindow { get; set; }

    partial void OnShowEmulatorWindowChanged(bool value) => _settings.Save(_settings.Current with { ShowEmulatorWindow = value });

    /// <summary>Remembered pane proportions (device, screen, app/logcat), or null for layout defaults.</summary>
    public IReadOnlyList<double>? PaneWeights =>
        _settings.Current.EmulatorPaneWeights is { Length: 3 } w && w.All(v => v > 0) ? w : null;

    public void SavePaneWeights(double left, double center, double right) =>
        _settings.Save(_settings.Current with { EmulatorPaneWeights = [Math.Round(left), Math.Round(center), Math.Round(right)] });

    // ------------------------------------------------------ create device

    public IReadOnlyList<DeviceProfile> Profiles => DeviceProfile.All;

    public ObservableCollection<SystemImage> Images { get; } = [];

    [ObservableProperty]
    public partial DeviceProfile SelectedProfile { get; set; } = DeviceProfile.PixelPhone;

    [ObservableProperty]
    public partial SystemImage? SelectedImage { get; set; }

    [ObservableProperty]
    public partial string NewDeviceName { get; set; } = "";

    partial void OnSelectedProfileChanged(DeviceProfile value) => SuggestName();

    partial void OnSelectedImageChanged(SystemImage? value) => SuggestName();

    // ------------------------------------------------------------- APK

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallAndLaunchCommand), nameof(LaunchAppCommand))]
    [NotifyPropertyChangedFor(nameof(HasApk), nameof(ApkSummary))]
    public partial ProjectApk? Apk { get; set; }

    [ObservableProperty]
    public partial BitmapImage? ApkIcon { get; set; }

    public bool HasApk => Apk is not null;

    public bool HasProject => _projects.Current is not null;

    public string ApkSummary => Apk is null ? "" : ApkInspector.Describe(Apk.Info);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CapturedText))]
    public partial int CapturedCount { get; set; }

    public string CapturedText => CapturedCount switch
    {
        0 => "No screens captured in this project yet",
        1 => "1 screen captured in this project",
        _ => $"{CapturedCount} screens captured in this project",
    };

    [ObservableProperty]
    public partial string TextToSend { get; set; } = "";

    // ------------------------------------------------------------ logcat

    public ObservableCollection<LogcatEntry> Logs { get; } = [];

    public IReadOnlyList<LogLevel> LogLevels { get; } = Enum.GetValues<LogLevel>();

    [ObservableProperty]
    public partial LogLevel MinLogLevel { get; set; } = LogLevel.Info;

    [ObservableProperty]
    public partial string LogFilter { get; set; } = "";

    [ObservableProperty]
    public partial bool OnlyAppLogs { get; set; } = true;

    [ObservableProperty]
    public partial bool LogsPaused { get; set; }

    partial void OnMinLogLevelChanged(LogLevel value) => RebuildLogView();

    partial void OnLogFilterChanged(string value) => RebuildLogView();

    partial void OnOnlyAppLogsChanged(bool value) => RebuildLogView();

    // ----------------------------------------------------------- lifecycle

    public void OnActivated()
    {
        OnPropertyChanged(nameof(HasProject));
        if (!IsBusy)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>
    /// App shutdown: releases the mirror and logcat. Runs off the UI thread, so it must not touch bindable state.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        _logcat?.Dispose();
        _logcat = null;
        if (Mirror is { } mirror)
        {
            await mirror.DisposeAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var selectedId = SelectedTarget?.Id;
            var targets = await Task.Run(() => _hub.ListTargetsAsync());
            Targets.Clear();
            foreach (var t in targets)
            {
                Targets.Add(t);
            }

            SelectedTarget = Targets.FirstOrDefault(t => t.Id == selectedId)
                ?? Targets.FirstOrDefault(t => t.IsRunning)
                ?? Targets.FirstOrDefault(t => t.IsManaged)
                ?? Targets.FirstOrDefault(t => t.Kind == DeviceTargetKind.Avd);

            Images.Clear();
            var abi = SetupContext.HostAbi;
            foreach (var image in _hub.RequireSdk().GetSystemImages().Where(i => i.Abi == abi && i.IsPhoneImage).OrderByDescending(i => i.ApiLevel))
            {
                Images.Add(image);
            }

            SelectedImage ??= Images.FirstOrDefault(i => i.Platform == "android-36" && i.Tag == "google_apis_playstore") ?? Images.FirstOrDefault();
            if (Targets.Count == 0)
            {
                Status = "No virtual devices yet. Create one below.";
            }
        }
        catch (UserFacingException ex)
        {
            Status = ex.Message;
        }
    }

    private bool CanStart() => !IsBusy && SelectedTarget is not null && Device is null && SelectedTarget.Problem is null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => ConnectAsync(wipe: false);

    private bool CanReset() => !IsBusy && Device is null && SelectedTarget is { IsManaged: true, IsRunning: false };

    [RelayCommand(CanExecute = nameof(CanReset))]
    private Task ResetAsync()
    {
        if (!_dialogs.Confirm("Reset virtual device", $"This erases all apps and data on \"{SelectedTarget!.DisplayName}\" and starts it fresh.\n\nContinue?"))
        {
            return Task.CompletedTask;
        }

        return ConnectAsync(wipe: true);
    }

    private bool CanStop() => !IsBusy && Device is not null;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        var target = SelectedTarget;
        IsBusy = true;
        Status = "Stopping…";
        try
        {
            await DisconnectAsync();
            if (target is not null)
            {
                await Task.Run(() => _hub.ProviderFor(target).StopAsync(target));
            }

            Status = "Stopped.";
        }
        catch (UserFacingException ex)
        {
            _dialogs.ShowError("Couldn't stop the device", ex);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    private bool CanCreateDevice() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCreateDevice))]
    private async Task CreateDeviceAsync()
    {
        if (SelectedImage is null)
        {
            _dialogs.ShowError("No system image", new UserFacingException("No phone system image is installed.", "Install one on the Environment page."));
            return;
        }

        try
        {
            var avd = _hub.Avds.Create(NewDeviceName.Trim(), SelectedProfile, SelectedImage, _hub.RequireSdk().Root);
            _log.LogInformation("Created AVD {Name}", avd.Name);
            await RefreshAsync();
            SelectedTarget = Targets.FirstOrDefault(t => t.Avd?.Name == avd.Name);
            Status = $"Created {avd.Name}. Press Start to boot it (the first boot takes a minute or two).";
        }
        catch (Exception ex) when (ex is UserFacingException or IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Couldn't create the virtual device", ex);
        }
    }

    private bool CanDeleteDevice() => !IsBusy && Device is null && SelectedTarget is { IsManaged: true, IsRunning: false };

    [RelayCommand(CanExecute = nameof(CanDeleteDevice))]
    private async Task DeleteDeviceAsync()
    {
        var avd = SelectedTarget!.Avd!;
        if (!_dialogs.Confirm("Delete virtual device", $"Delete \"{avd.Name}\" and all its data?"))
        {
            return;
        }

        try
        {
            AvdManager.Delete(avd);
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is UserFacingException or IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Couldn't delete the virtual device", ex);
        }
    }

    // ------------------------------------------------------------- input

    [RelayCommand]
    private Task Back() => Mirror?.KeyAsync(AndroidKey.Back) ?? Task.CompletedTask;

    [RelayCommand]
    private Task Home() => Mirror?.KeyAsync(AndroidKey.Home) ?? Task.CompletedTask;

    [RelayCommand]
    private Task Recents() => Mirror?.KeyAsync(AndroidKey.AppSwitch) ?? Task.CompletedTask;

    [RelayCommand]
    private async Task SendTextAsync()
    {
        if (Mirror is null || string.IsNullOrEmpty(TextToSend))
        {
            return;
        }

        await Mirror.TextAsync(TextToSend);
        TextToSend = "";
    }

    // --------------------------------------------------------------- APK

    private bool CanImportApk() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanImportApk))]
    private async Task ImportApkAsync()
    {
        if (_projects.Current is not { } project)
        {
            _dialogs.ShowInfo("Open a project first", "Create or open a project on the Home page, then import the APK into it.");
            return;
        }

        var dialog = new OpenFileDialog { Title = "Import an Android app", Filter = "Android app (*.apk)|*.apk|Android App Bundle (*.aab)|*.aab|All files|*.*" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        Status = "Reading the APK…";
        try
        {
            var sdk = _hub.RequireSdk();
            var inspection = await Task.Run(() => _hub.Apks.InspectAsync(dialog.FileName, sdk.Aapt2Path));
            Apk = await Task.Run(() => ProjectApkStore.Import(project, dialog.FileName, inspection));
            LoadIcon(project);
            Status = $"Imported {Apk.Info.DisplayName}.";
            if (inspection.Warnings.Count > 0)
            {
                _dialogs.ShowInfo("Imported with warnings", string.Join("\n\n", inspection.Warnings));
            }
        }
        catch (Exception ex) when (ex is UserFacingException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "APK import failed");
            _dialogs.ShowError("Couldn't import the APK", ex);
            Status = "Import failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInstall() => !IsBusy && Device is not null && Apk is not null;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAndLaunchAsync()
    {
        var device = Device!;
        var apk = Apk!;
        var project = _projects.Current!;
        IsBusy = true;
        try
        {
            Status = "Checking compatibility…";
            var (abi, api) = await Task.Run(async () => (await device.GetAbiAsync(), await device.GetApiLevelAsync()));
            if (!apk.Info.SupportsAbi(abi))
            {
                throw new UserFacingException(
                    $"This APK only contains native code for {string.Join(", ", apk.Info.NativeAbis)}, but the device is {abi}.",
                    "Use a universal or x86_64 build of the app, or run it on a matching phone.");
            }

            if (apk.Info.MinSdk is { } min && api > 0 && api < min)
            {
                throw new UserFacingException($"The app needs Android API {min}+, but the device runs API {api}.", "Create a virtual device with a newer system image.");
            }

            Status = $"Installing {apk.Info.DisplayName}…";
            await Task.Run(() => device.InstallAsync([apk.ApkPath(project)]));
            Status = $"Opening {apk.Info.DisplayName}…";
            await Task.Run(() => device.LaunchAsync(apk.Info.PackageName, apk.Info.LauncherActivity));
            _appPid = await Task.Run(() => device.GetPidAsync(apk.Info.PackageName));
            RebuildLogView();
            Status = $"{apk.Info.DisplayName} is running.";
        }
        catch (UserFacingException ex)
        {
            _log.LogWarning(ex, "Install failed");
            _dialogs.ShowError("Couldn't install the app", ex);
            Status = "Install failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task LaunchAppAsync()
    {
        try
        {
            await Task.Run(() => Device!.LaunchAsync(Apk!.Info.PackageName, Apk.Info.LauncherActivity));
            _appPid = await Task.Run(() => Device!.GetPidAsync(Apk!.Info.PackageName));
            RebuildLogView();
        }
        catch (UserFacingException ex)
        {
            _dialogs.ShowError("Couldn't open the app", ex);
        }
    }

    private bool CanCapture() => !IsBusy && Device is not null;

    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task CaptureScreenAsync()
    {
        if (_projects.Current is not { } project)
        {
            _dialogs.ShowInfo("Open a project first", "Captured screens are saved into the open project.");
            return;
        }

        try
        {
            var device = Device!;
            var (png, activity) = await Task.Run(async () => (await device.CaptureScreenshotPngAsync(), await device.GetForegroundActivityAsync()));
            using var bitmap = SkiaSharp.SKBitmap.Decode(png);
            var saved = ScreenRepository.Add(project, png, "manual", activity, bitmap?.Width ?? 0, bitmap?.Height ?? 0);
            CapturedCount = ScreenRepository.List(project).Count;
            Status = $"Captured {Path.GetFileName(saved.File)} ({bitmap?.Width}×{bitmap?.Height}).";
        }
        catch (UserFacingException ex)
        {
            _dialogs.ShowError("Couldn't capture the screen", ex);
        }
    }

    [RelayCommand]
    private async Task ClearLogsAsync()
    {
        _allLogs.Clear();
        Logs.Clear();
        if (Device is { Kind: not DeviceKind.Mock } device)
        {
            await LogcatSession.ClearAsync(_hub.Adb, device.Serial);
        }
    }

    // ---------------------------------------------------------- internals

    private async Task ConnectAsync(bool wipe)
    {
        var target = SelectedTarget!;
        IsBusy = true;
        var progress = new Progress<string>(m => Status = m);
        try
        {
            var options = new DeviceStartOptions { Headless = !ShowEmulatorWindow, WipeData = wipe };
            var device = await Task.Run(() => _hub.ProviderFor(target).ConnectAsync(target, options, progress));
            Device = device;
            Status = "Starting live preview…";
            var mirror = await Task.Run(() => _hub.StartMirrorAsync(device));
            AttachMirror(mirror);
            StartLogcat(device);
            Status = $"{device.DisplayName} is running.";
            if (Apk is not null && await Task.Run(() => device.IsInstalledAsync(Apk.Info.PackageName)))
            {
                _appPid = await Task.Run(() => device.GetPidAsync(Apk.Info.PackageName));
            }
        }
        catch (Exception ex) when (ex is UserFacingException or IOException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Starting {Target} failed", target.DisplayName);
            _dialogs.ShowError($"Couldn't start {target.DisplayName}", ex);
            Status = "Not running.";
            await DisconnectAsync();
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    private void AttachMirror(IScreenMirror mirror)
    {
        Mirror = mirror;
        MirrorInfo = mirror.Mode;
        mirror.Failed += async (_, ex) =>
        {
            if (_stopping || Device is not { } device)
            {
                return;
            }

            _log.LogWarning(ex, "Mirror {Mode} failed; switching to screenshot polling", mirror.Mode);
            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                if (Mirror != mirror)
                {
                    return;
                }

                await mirror.DisposeAsync();
                var fallback = new ScreencapMirror(device, _log);
                await fallback.StartAsync();
                Mirror = fallback;
                MirrorInfo = fallback.Mode + " (the faster stream stopped)";
            });
        };
    }

    private async Task DisconnectAsync()
    {
        _logcat?.Dispose();
        _logcat = null;
        var mirror = Mirror;
        Mirror = null;
        MirrorInfo = "";
        if (mirror is not null)
        {
            await mirror.DisposeAsync();
        }

        Device = null;
        _appPid = null;
    }

    private void StartLogcat(IDevice device)
    {
        if (device.Kind == DeviceKind.Mock)
        {
            return;
        }

        _logcat = LogcatSession.Start(_hub.Adb, device.Serial);
        _logcat.EntriesReceived += (_, batch) => Application.Current.Dispatcher.BeginInvoke(() => AppendLogs(batch));
    }

    private void AppendLogs(IReadOnlyList<LogcatEntry> batch)
    {
        if (LogsPaused)
        {
            return;
        }

        _allLogs.AddRange(batch);
        if (_allLogs.Count > MaxLogEntries)
        {
            _allLogs.RemoveRange(0, _allLogs.Count - MaxLogEntries);
        }

        foreach (var e in batch.Where(Matches))
        {
            Logs.Add(e);
        }

        while (Logs.Count > 5000)
        {
            Logs.RemoveAt(0);
        }
    }

    private bool Matches(LogcatEntry e) =>
        e.Level >= MinLogLevel &&
        (!OnlyAppLogs || _appPid is null || e.Pid == _appPid || e.Level >= LogLevel.Error && e.Tag == "AndroidRuntime") &&
        (LogFilter.Length == 0 || e.Tag.Contains(LogFilter, StringComparison.OrdinalIgnoreCase) || e.Message.Contains(LogFilter, StringComparison.OrdinalIgnoreCase));

    private void RebuildLogView()
    {
        Logs.Clear();
        foreach (var e in _allLogs.Where(Matches).TakeLast(5000))
        {
            Logs.Add(e);
        }
    }

    private void LoadProjectApk()
    {
        OnPropertyChanged(nameof(HasProject));
        if (_projects.Current is not { } project)
        {
            Apk = null;
            ApkIcon = null;
            CapturedCount = 0;
            return;
        }

        Apk = ProjectApkStore.Load(project);
        LoadIcon(project);
        CapturedCount = ScreenRepository.List(project).Count;
    }

    private void LoadIcon(Project project)
    {
        if (Apk?.IconPath(project) is { } path && File.Exists(path))
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            ApkIcon = image;
        }
        else
        {
            ApkIcon = null;
        }
    }

    private void SuggestName()
    {
        if (SelectedImage is not null)
        {
            NewDeviceName = _hub.Avds.SuggestName(SelectedProfile, SelectedImage);
        }
    }
}
