using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SSGen.App.Services;
using SSGen.Core;
using SSGen.Core.Settings;
using SSGen.Core.Setup;

namespace SSGen.App.ViewModels;

/// <summary>One row in the setup assistant.</summary>
public sealed partial class CheckItemViewModel : ObservableObject
{
    public CheckItemViewModel(ISetupCheck check) => Check = check;

    public ISetupCheck Check { get; }
    public string Title => Check.Title;
    public string Purpose => Check.Purpose;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    public partial CheckStatus Status { get; set; } = CheckStatus.Pending;

    [ObservableProperty]
    public partial string Summary { get; set; } = "Not checked yet";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    public partial string? Details { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFixInstructions))]
    public partial string? FixInstructions { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFix))]
    public partial SetupFix? Fix { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFix))]
    public partial bool IsFixing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFixIndeterminate))]
    public partial double? FixPercent { get; set; }

    public bool IsFixIndeterminate => FixPercent is null;

    [ObservableProperty]
    public partial string? FixMessage { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public bool IsRunning => Status == CheckStatus.Running;
    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);
    public bool HasFixInstructions => !string.IsNullOrWhiteSpace(FixInstructions);
    public bool HasFix => Fix is not null && !IsFixing;

    public void Apply(CheckResult result)
    {
        Status = result.Status;
        Summary = result.Summary;
        Details = result.Details;
        FixInstructions = result.FixInstructions;
        Fix = result.Fix;
        IsExpanded = result.Status is CheckStatus.Fail or CheckStatus.Warning;
    }
}

public sealed partial class SetupViewModel : ObservableObject, IPageActivated
{
    private const string SdkLicenseUrl = "https://developer.android.com/studio/terms";

    private readonly ISetupService _setup;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly ILogger<SetupViewModel> _log;
    private bool _hasRun;

    public SetupViewModel(
        ISetupService setup,
        ISettingsService settings,
        IDialogService dialogs,
        INavigationService navigation,
        ILogger<SetupViewModel> log)
    {
        _setup = setup;
        _settings = settings;
        _dialogs = dialogs;
        _navigation = navigation;
        _log = log;
        Items = new ObservableCollection<CheckItemViewModel>(setup.Checks.Select(c => new CheckItemViewModel(c)));
    }

    public ObservableCollection<CheckItemViewModel> Items { get; }

    public bool IsFirstRun => !_settings.Current.SetupCompleted;

    [ObservableProperty]
    public partial string OverallSummary { get; set; } = "Checking your PC…";

    [ObservableProperty]
    public partial CheckStatus OverallStatus { get; set; } = CheckStatus.Pending;

    [ObservableProperty]
    public partial double Progress { get; set; }

    public void OnActivated()
    {
        if (!_hasRun && RunChecksCommand.CanExecute(null))
        {
            RunChecksCommand.Execute(null);
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunChecksAsync(CancellationToken ct)
    {
        _hasRun = true;
        OverallStatus = CheckStatus.Running;
        OverallSummary = "Checking your PC…";
        Progress = 0;
        foreach (var item in Items)
        {
            item.Status = CheckStatus.Pending;
            item.Summary = "Waiting…";
        }

        try
        {
            // Locating Java runs a process; keep all of this off the UI thread.
            var context = await Task.Run(() => _setup.CreateContextAsync(ct), ct);
            for (var i = 0; i < Items.Count; i++)
            {
                var item = Items[i];
                item.Status = CheckStatus.Running;
                item.Summary = "Checking…";
                var result = await Task.Run(() => _setup.RunCheckAsync(item.Check, context, ct), ct);
                item.Apply(result);
                _log.LogInformation("Setup check {Id}: {Status} – {Summary}", item.Check.Id, result.Status, result.Summary);
                Progress = (i + 1) * 100.0 / Items.Count;
            }

            Summarize();
        }
        catch (OperationCanceledException)
        {
            foreach (var item in Items.Where(i => i.Status is CheckStatus.Running or CheckStatus.Pending))
            {
                item.Status = CheckStatus.Pending;
                item.Summary = "Cancelled";
            }

            OverallStatus = CheckStatus.Warning;
            OverallSummary = "Checks cancelled.";
        }
    }

    [RelayCommand]
    private async Task RunFixAsync(CheckItemViewModel? item)
    {
        if (item?.Fix is not { } fix)
        {
            return;
        }

        if (fix.RequiresSdkLicense && !_dialogs.Confirm(
                "Download from Google",
                $"\"{fix.Label}\" downloads Android SDK components from Google.\n\n" +
                $"By continuing you accept the Android Software Development Kit License Agreement ({SdkLicenseUrl}).\n\nContinue?"))
        {
            return;
        }

        item.IsFixing = true;
        item.FixPercent = null;
        item.FixMessage = "Starting…";
        var progress = new Progress<InstallProgress>(p =>
        {
            item.FixPercent = p.Percent;
            item.FixMessage = p.Message;
        });
        try
        {
            await Task.Run(() => fix.RunAsync(progress, CancellationToken.None));
            _log.LogInformation("Fix {Label} succeeded", fix.Label);
        }
        catch (Exception ex) when (ex is UserFacingException or IOException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Fix {Label} failed", fix.Label);
            _dialogs.ShowError(fix.Label, ex);
        }
        finally
        {
            item.IsFixing = false;
            item.FixMessage = null;
        }

        // Installing one component can satisfy several checks; re-check everything.
        await RunChecksCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void OpenSettings() => _navigation.NavigateTo(PageKey.Settings);

    [RelayCommand]
    private void Continue()
    {
        if (OverallStatus == CheckStatus.Fail && !_dialogs.Confirm(
                "Some checks failed",
                "The emulator won't work until the failed checks are fixed. You can still create projects and use the editor.\n\nContinue anyway?"))
        {
            return;
        }

        if (!_settings.Current.SetupCompleted)
        {
            _settings.Save(_settings.Current with { SetupCompleted = true });
            OnPropertyChanged(nameof(IsFirstRun));
        }

        _navigation.NavigateTo(PageKey.Home);
    }

    private void Summarize()
    {
        var fails = Items.Count(i => i.Status == CheckStatus.Fail);
        var warnings = Items.Count(i => i.Status == CheckStatus.Warning);
        (OverallStatus, OverallSummary) = (fails, warnings) switch
        {
            (0, 0) => (CheckStatus.Pass, "Everything is ready."),
            (0, _) => (CheckStatus.Warning, $"Ready, with {warnings} warning{(warnings == 1 ? "" : "s")}."),
            _ => (CheckStatus.Fail, $"{fails} problem{(fails == 1 ? "" : "s")} to fix" + (warnings > 0 ? $", {warnings} warning{(warnings == 1 ? "" : "s")}." : ".")),
        };
    }
}
