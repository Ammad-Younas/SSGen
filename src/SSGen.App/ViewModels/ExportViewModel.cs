using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SSGen.App.Services;
using SSGen.Core;
using SSGen.Core.Projects;
using SSGen.Emulator.Apk;
using SSGen.Selection;
using SSGen.Templates;

namespace SSGen.App.ViewModels;

/// <summary>A store size or a language that can be included in the export.</summary>
public sealed partial class ExportOptionViewModel : ObservableObject
{
    private readonly Action _changed;

    public ExportOptionViewModel(string id, string title, string detail, int count, bool hasProblems, Action changed)
    {
        Id = id;
        Title = title;
        Detail = detail;
        Count = count;
        HasProblems = hasProblems;
        _changed = changed;
        IsSelected = true;
    }

    public string Id { get; }

    public string Title { get; }

    public string Detail { get; }

    public int Count { get; }

    public bool HasProblems { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => _changed();
}

/// <summary>
/// Export page: choose store sizes and languages, check everything against the store rules once more, and copy
/// the files into an upload-ready folder with manifest.json (and optionally a ZIP).
/// </summary>
public sealed partial class ExportViewModel : ObservableObject, IPageActivated
{
    private readonly IProjectService _projects;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly AppPaths _paths;
    private readonly ILogger<ExportViewModel> _log;
    private IReadOnlyList<RenderRecord> _records = [];
    private StorePresetCatalog? _catalog;

    public ExportViewModel(IProjectService projects, IDialogService dialogs, INavigationService navigation, AppPaths paths, ILogger<ExportViewModel> log)
    {
        _projects = projects;
        _dialogs = dialogs;
        _navigation = navigation;
        _paths = paths;
        _log = log;
    }

    public ObservableCollection<ExportOptionViewModel> Sizes { get; } = [];

    public ObservableCollection<ExportOptionViewModel> LanguageOptions { get; } = [];

    public ObservableCollection<RenderReportLine> Report { get; } = [];

    [ObservableProperty]
    public partial string? Blocker { get; set; }

    [ObservableProperty]
    public partial string Destination { get; set; } = "";

    [ObservableProperty]
    public partial bool MakeZip { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeInvalid { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial string? LastFolder { get; set; }

    [ObservableProperty]
    public partial string? LastZip { get; set; }

    public bool HasBlocker => Blocker is not null;

    public bool HasLanguages => LanguageOptions.Count > 1;

    partial void OnBlockerChanged(string? value) => OnPropertyChanged(nameof(HasBlocker));

    public void OnActivated()
    {
        Sizes.Clear();
        LanguageOptions.Clear();
        Report.Clear();
        if (_projects.Current is not { } project)
        {
            Blocker = "Open or create a project first.";
            return;
        }

        _catalog = StorePresetCatalog.Load(Path.Combine(_paths.DataRoot, "store-presets.json"));
        _records = RenderStore.List(project);
        Blocker = _records.Count == 0 ? "Nothing to export yet. Render your store images on the Design page first." : null;
        if (string.IsNullOrEmpty(Destination) || !Directory.Exists(Path.GetDirectoryName(Destination.TrimEnd('\\')) ?? Destination))
        {
            Destination = project.ExportDir;
        }

        var order = _catalog.AllPresets.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        foreach (var group in _records.GroupBy(r => r.PresetId).OrderBy(g => order.GetValueOrDefault(g.Key, int.MaxValue)))
        {
            var preset = _catalog.Find(group.Key);
            var problems = preset is not null && group.Any(r => RenderStore.Validate(project, r, preset).Any(i => i.IsError));
            var title = preset is null ? group.Key : $"{preset.Store.Name} · {preset.Name}";
            var detail = preset is null ? "" : $"{preset.Width} × {preset.Height}";
            Sizes.Add(new ExportOptionViewModel(group.Key, title, detail, group.Count(), problems, UpdateSummary));
        }

        var primary = Languages.Primary(project);
        foreach (var locale in _records.Select(r => r.Locale).Distinct().OrderBy(l => l.Length > 0).ThenBy(l => l, StringComparer.Ordinal))
        {
            var language = locale.Length == 0 ? primary : Languages.Find(locale);
            LanguageOptions.Add(new ExportOptionViewModel(locale, language.Name + (locale.Length == 0 ? " (primary)" : ""), language.Code,
                _records.Count(r => r.Locale == locale), false, UpdateSummary));
        }

        OnPropertyChanged(nameof(HasLanguages));
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var sizes = Sizes.Where(s => s.IsSelected).Select(s => s.Id).ToHashSet();
        var locales = LanguageOptions.Where(l => l.IsSelected).Select(l => l.Id).ToHashSet();
        var count = _records.Count(r => sizes.Contains(r.PresetId) && locales.Contains(r.Locale));
        Summary = count == 0 ? "Choose at least one size and language." : $"{count} file(s) will be exported.";
        ExportCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Browse()
    {
        if (_dialogs.PickFolder("Export to", Destination) is { } folder)
        {
            Destination = folder;
        }
    }

    private bool CanExport() => !IsBusy && Sizes.Any(s => s.IsSelected) && LanguageOptions.Any(l => l.IsSelected);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (_projects.Current is not { } project || _catalog is not { } catalog)
        {
            return;
        }

        if (Sizes.Any(s => s.IsSelected && s.HasProblems) && !IncludeInvalid &&
            !_dialogs.Confirm("Some images break a store rule", "Some of the chosen sizes have images that break a store rule. They'll be left out (see the report). Fix them on the Design page or in the editor, or tick \"Include images with problems\".\n\nExport the rest?"))
        {
            return;
        }

        var apk = ProjectApkStore.Load(project);
        var request = new ExportRequest
        {
            PresetIds = Sizes.Where(s => s.IsSelected).Select(s => s.Id).ToList(),
            Locales = LanguageOptions.Where(l => l.IsSelected).Select(l => l.Id).ToList(),
            Destination = Destination,
            PrimaryLocale = Languages.Primary(project).Code,
            Zip = MakeZip,
            IncludeInvalid = IncludeInvalid,
            AppName = apk?.Info.DisplayName ?? project.Name,
            PackageName = apk?.Info.PackageName ?? "",
            AppVersion = apk?.Info.VersionName ?? "",
        };

        IsBusy = true;
        Report.Clear();
        try
        {
            Directory.CreateDirectory(Destination);
            var progress = new Progress<(int Done, int Total)>(p => ProgressValue = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total);
            var result = await Task.Run(() => ExportService.Export(project, catalog, request, progress));
            LastFolder = result.Folder;
            LastZip = result.ZipFile;
            foreach (var issue in result.Issues.Concat(result.Files.SelectMany(f => f.Issues)).DistinctBy(i => i.Message))
            {
                Report.Add(new RenderReportLine(issue.IsError, issue.Message));
            }

            Status = $"Exported {result.Files.Count} file(s){(result.Skipped > 0 ? $", left out {result.Skipped}" : "")}{(result.ZipFile is null ? "" : " and made a ZIP")}." +
                     (result.HasErrors ? " Some store rules aren't met: see below." : " Everything is within the store rules.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Export failed");
            _dialogs.ShowError("Export failed", new UserFacingException(ex.Message, "Choose another folder, or close programs that have the files open."));
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (LastFolder is { } f && Directory.Exists(f))
        {
            _dialogs.OpenInExplorer(f);
        }
    }

    [RelayCommand]
    private void GoDesign() => _navigation.NavigateTo(PageKey.Design);
}
