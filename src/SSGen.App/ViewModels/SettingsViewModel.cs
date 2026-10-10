using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SSGen.App.Services;
using SSGen.Core;
using SSGen.Core.Settings;

namespace SSGen.App.ViewModels;

/// <summary>Static values for XAML fallbacks.</summary>
public static class SettingsViewModelDefaults
{
    public static string ProjectsFolder => AppPaths.DefaultProjectsDir;
}

public sealed partial class SettingsViewModel : ObservableObject, IPageActivated
{
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly ILogger<SettingsViewModel> _log;
    private bool _loading;

    public SettingsViewModel(ISettingsService settings, IDialogService dialogs, AppPaths paths, AiProvidersViewModel ai, ILogger<SettingsViewModel> log)
    {
        Ai = ai;
        _settings = settings;
        _dialogs = dialogs;
        _paths = paths;
        _log = log;
        Load();
    }

    public AiProvidersViewModel Ai { get; }

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();

    public string DataFolder => _paths.DataRoot;

    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "dev";

    public string DefaultProjectsFolder => AppPaths.DefaultProjectsDir;

    [ObservableProperty]
    public partial AppTheme Theme { get; set; }

    [ObservableProperty]
    public partial string? AndroidSdkPath { get; set; }

    [ObservableProperty]
    public partial string? JavaHomePath { get; set; }

    [ObservableProperty]
    public partial string? ProjectsFolder { get; set; }

    [ObservableProperty]
    public partial bool SafeMode { get; set; }

    [ObservableProperty]
    public partial string CacheSize { get; set; } = "";

    public void OnActivated()
    {
        Load();
        Ai.Reload();
        UpdateCacheSize();
    }

    partial void OnThemeChanged(AppTheme value) => Persist();

    partial void OnAndroidSdkPathChanged(string? value) => Persist();

    partial void OnJavaHomePathChanged(string? value) => Persist();

    partial void OnProjectsFolderChanged(string? value) => Persist();

    partial void OnSafeModeChanged(bool value)
    {
        if (!_loading && !value && !_dialogs.Confirm(
                "Turn off safe mode?",
                "With safe mode off, the agent may tap buttons such as Buy, Pay, Delete or Send without asking you first.\n\nTurn it off?"))
        {
            SafeMode = true;
            return;
        }

        Persist();
    }

    [RelayCommand]
    private void BrowseSdk()
    {
        if (_dialogs.PickFolder("Select the Android SDK folder", AndroidSdkPath) is { } folder)
        {
            if (!Core.Setup.AndroidSdkLocator.LooksLikeSdk(folder))
            {
                _dialogs.ShowError("Not an Android SDK folder", new UserFacingException(
                    $"\"{folder}\" doesn't contain platform-tools, emulator, cmdline-tools or build-tools.",
                    "Pick the SDK root folder (the one that contains platform-tools)."));
                return;
            }

            AndroidSdkPath = folder;
        }
    }

    [RelayCommand]
    private void BrowseJava()
    {
        if (_dialogs.PickFolder("Select the Java home folder (contains bin\\java.exe)", JavaHomePath) is { } folder)
        {
            if (!File.Exists(Path.Combine(folder, "bin", "java.exe")))
            {
                _dialogs.ShowError("Not a Java folder", new UserFacingException(
                    $"\"{folder}\" doesn't contain bin\\java.exe.",
                    "Pick the JDK's root folder, e.g. C:\\Program Files\\Android\\Android Studio\\jbr."));
                return;
            }

            JavaHomePath = folder;
        }
    }

    [RelayCommand]
    private void BrowseProjects()
    {
        if (_dialogs.PickFolder("Default folder for new projects", ProjectsFolder ?? DefaultProjectsFolder) is { } folder)
        {
            ProjectsFolder = folder;
        }
    }

    [RelayCommand]
    private void ResetSdk() => AndroidSdkPath = null;

    [RelayCommand]
    private void ResetJava() => JavaHomePath = null;

    [RelayCommand]
    private void ResetProjects() => ProjectsFolder = null;

    [RelayCommand]
    private void OpenDataFolder() => _dialogs.OpenInExplorer(_paths.DataRoot);

    [RelayCommand]
    private void OpenLogsFolder() => _dialogs.OpenInExplorer(_paths.LogsDir);

    [RelayCommand]
    private void ClearCache()
    {
        var freed = 0L;
        var failed = 0;
        foreach (var file in Directory.EnumerateFiles(_paths.CacheDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                var size = new FileInfo(file).Length;
                File.Delete(file);
                freed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(_paths.CacheDir))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        _log.LogInformation("Cleared cache: {Bytes} bytes, {Failed} items skipped", freed, failed);
        UpdateCacheSize();
        _dialogs.ShowInfo("Cache cleared", $"Freed {FormatBytes(freed)}." + (failed > 0 ? $" {failed} item(s) were in use and were skipped." : ""));
    }

    [RelayCommand]
    private void ExportLogs()
    {
        var target = _dialogs.PickSaveFile("Export logs", "ZIP archive|*.zip", $"ssgen-logs-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        if (target is null)
        {
            return;
        }

        try
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }

            using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
            foreach (var file in Directory.EnumerateFiles(_paths.LogsDir))
            {
                // The current log is held open by the logger, so share read/write access.
                using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var entry = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal).Open();
                source.CopyTo(entry);
            }

            _dialogs.ShowInfo("Logs exported", $"Saved to {target}.\n\nLogs never contain API keys, but may include file paths and app package names. Review before sharing.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Couldn't export logs", ex);
        }
    }

    private void Load()
    {
        _loading = true;
        var s = _settings.Current;
        Theme = s.Theme;
        AndroidSdkPath = s.AndroidSdkPath;
        JavaHomePath = s.JavaHomePath;
        ProjectsFolder = s.ProjectsFolder;
        SafeMode = s.SafeMode;
        _loading = false;
    }

    private void Persist()
    {
        if (_loading)
        {
            return;
        }

        _settings.Save(_settings.Current with
        {
            Theme = Theme,
            AndroidSdkPath = string.IsNullOrWhiteSpace(AndroidSdkPath) ? null : AndroidSdkPath,
            JavaHomePath = string.IsNullOrWhiteSpace(JavaHomePath) ? null : JavaHomePath,
            ProjectsFolder = string.IsNullOrWhiteSpace(ProjectsFolder) ? null : ProjectsFolder,
            SafeMode = SafeMode,
        });
    }

    private void UpdateCacheSize()
    {
        var bytes = Directory.EnumerateFiles(_paths.CacheDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        CacheSize = FormatBytes(bytes);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
