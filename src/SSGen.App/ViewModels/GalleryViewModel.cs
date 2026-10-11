using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SSGen.App.Services;
using SSGen.Core;
using SSGen.Core.Projects;
using SSGen.Emulator.Apk;
using SSGen.Templates;

namespace SSGen.App.ViewModels;

/// <summary>One rendered image in the gallery.</summary>
public sealed partial class GalleryImageViewModel : ObservableObject
{
    public required RenderRecord Record { get; init; }

    public required string Path { get; init; }

    public required IReadOnlyList<ValidationIssue> Issues { get; init; }

    public string Name => Record.DisplayName;

    public string SizeText => $"{Record.Width} × {Record.Height}";

    public bool IsEdited => Record.IsEdited;

    public bool Editable => Record.Editable;

    public bool HasError => Issues.Any(i => i.IsError);

    public bool HasWarning => !HasError && Issues.Count > 0;

    public bool IsValid => Issues.Count == 0;

    public string IssueText => Issues.Count == 0 ? "Within the store rules" : string.Join("\n", Issues.Select(i => i.Message));

    [ObservableProperty]
    public partial BitmapSource? Thumb { get; set; }
}

/// <summary>All images of one store size (preset).</summary>
public sealed partial class GalleryGroupViewModel : ObservableObject
{
    public required string PresetId { get; init; }

    /// <summary>"" = the primary language.</summary>
    public string Locale { get; init; } = "";

    public string LanguageName { get; init; } = "";

    public StorePreset? Preset { get; init; }

    public required IReadOnlyList<GalleryImageViewModel> Images { get; init; }

    public IReadOnlyList<ValidationIssue> SetIssues { get; init; } = [];

    public string StoreName => Preset?.Store.Name ?? "Other";

    public string Name => Preset?.Name ?? PresetId;

    public string Title => Locale.Length == 0 ? $"{StoreName} · {Name}" : $"{StoreName} · {Name} · {LanguageName}";

    public string Key => PresetId + "@" + Locale;

    public string SizeText => Preset is { } p ? $"{p.Width} × {p.Height}" : "";

    public bool IsScreenshots => Preset?.Kind is null or PresetKind.Screenshot;

    public string CountText => Images.Count == 1 ? "1 image" : $"{Images.Count} images";

    public bool HasProblems => SetIssues.Count > 0 || Images.Any(i => !i.IsValid);

    public string SetIssueText => string.Join("\n", SetIssues.Select(i => i.Message));

    public bool HasSetIssues => SetIssues.Count > 0;
}

/// <summary>
/// Gallery page: the rendered store images grouped by store and size, checked against the store rules, with a
/// store-listing style preview, opening any image in the editor.
/// </summary>
public sealed partial class GalleryViewModel : ObservableObject, IPageActivated, IDisposable
{
    private readonly IProjectService _projects;
    private readonly EditorViewModel _editor;
    private readonly INavigationService _navigation;
    private readonly IDialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly ILogger<GalleryViewModel> _log;
    private readonly Dictionary<(string, DateTime), BitmapSource> _thumbCache = [];
    private CancellationTokenSource? _thumbsCts;
    private string? _restoreGroup;
    private Project? _project;

    public GalleryViewModel(IProjectService projects, EditorViewModel editor, INavigationService navigation, IDialogService dialogs, AppPaths paths, ILogger<GalleryViewModel> log)
    {
        _projects = projects;
        _editor = editor;
        _navigation = navigation;
        _dialogs = dialogs;
        _paths = paths;
        _log = log;
        _editor.Saved += (_, _) => Application.Current.Dispatcher.InvokeAsync(Load);
    }

    public ObservableCollection<GalleryGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListingFeatureGraphic))]
    public partial GalleryGroupViewModel? SelectedGroup { get; set; }

    [ObservableProperty]
    public partial GalleryImageViewModel? SelectedImage { get; set; }

    /// <summary>False = image grid, true = store-listing style preview.</summary>
    [ObservableProperty]
    public partial bool ShowListing { get; set; }

    [ObservableProperty]
    public partial string? Blocker { get; set; }

    [ObservableProperty]
    public partial string AppName { get; set; } = "";

    [ObservableProperty]
    public partial string Tagline { get; set; } = "";

    [ObservableProperty]
    public partial BitmapSource? AppIcon { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    public bool HasBlocker => Blocker is not null;

    /// <summary>Google Play shows the feature graphic above the screenshots.</summary>
    public BitmapSource? ListingFeatureGraphic =>
        SelectedGroup is { Preset.Store.Id: { } store } selected
            ? Groups.FirstOrDefault(g => g.Preset is { Kind: PresetKind.FeatureGraphic } p && p.Store.Id == store && g.Locale == selected.Locale)?.Images is [var first, ..] ? first.Thumb : null
            : null;

    partial void OnBlockerChanged(string? value) => OnPropertyChanged(nameof(HasBlocker));

    partial void OnSelectedGroupChanged(GalleryGroupViewModel? value)
    {
        SelectedImage = value?.Images.FirstOrDefault();
        if (value is not null)
        {
            _restoreGroup = value.Key;
            if (_project is { } p)
            {
                // The listing shows the tagline in the set's language.
                var primary = ProjectMeta.Get(p, "design.tagline") ?? "";
                Tagline = value.Locale.Length == 0 ? primary : ProjectMeta.Get(p, "design.tagline@" + value.Locale) is { Length: > 0 } t ? t : primary;
            }
        }
    }

    public void OnActivated() => Load();

    private void Load()
    {
        _thumbsCts?.Cancel();
        Groups.Clear();
        SelectedGroup = null;
        if (_projects.Current is not { } project)
        {
            Blocker = "Open or create a project first.";
            Summary = "";
            return;
        }

        var apk = ProjectApkStore.Load(project);
        AppName = apk?.Info.DisplayName ?? project.Name;
        Tagline = ProjectMeta.Get(project, "design.tagline") ?? "";
        _project = project;
        AppIcon = apk?.IconPath(project) is { } icon && File.Exists(icon) ? Decode(icon, 128) : null;

        var catalog = StorePresetCatalog.Load(Path.Combine(_paths.DataRoot, "store-presets.json"));
        var order = catalog.AllPresets.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var records = RenderStore.List(project);
        Blocker = records.Count == 0 ? "No store images yet. Choose screens and a template on the Design page, then Render all." : null;

        foreach (var group in records.GroupBy(r => (r.PresetId, r.Locale)).OrderBy(g => g.Key.Locale.Length > 0).ThenBy(g => g.Key.Locale, StringComparer.Ordinal).ThenBy(g => order.GetValueOrDefault(g.Key.PresetId, int.MaxValue)))
        {
            var preset = catalog.Find(group.Key.PresetId);
            var images = group.OrderBy(r => r.Position).Select(r => new GalleryImageViewModel
            {
                Record = r,
                Path = r.FullPath(project),
                Issues = preset is null ? [] : RenderStore.Validate(project, r, preset),
            }).ToList();
            Groups.Add(new GalleryGroupViewModel
            {
                PresetId = group.Key.PresetId,
                Locale = group.Key.Locale,
                LanguageName = group.Key.Locale.Length == 0 ? "" : SSGen.Selection.Languages.Find(group.Key.Locale).Name,
                Preset = preset,
                Images = images,
                SetIssues = preset is { Kind: PresetKind.Screenshot } ? PresetValidator.ValidateCount(preset, images.Count) : [],
            });
        }

        var total = Groups.Sum(g => g.Images.Count);
        var problems = Groups.Count(g => g.HasProblems);
        Summary = total == 0 ? "" : $"{total} image(s) in {Groups.Count} store size(s). {(problems == 0 ? "Everything is within the store rules." : $"{problems} size(s) need attention.")}";
        SelectedGroup = Groups.FirstOrDefault(g => g.Key == _restoreGroup) ?? Groups.FirstOrDefault(g => g.IsScreenshots) ?? Groups.FirstOrDefault();
        LoadThumbnails();
    }

    private void LoadThumbnails()
    {
        var cts = _thumbsCts = new CancellationTokenSource();
        var images = Groups.OrderByDescending(g => g == SelectedGroup).SelectMany(g => g.Images).ToList();
        _ = Task.Run(async () =>
        {
            foreach (var image in images)
            {
                cts.Token.ThrowIfCancellationRequested();
                var thumb = Thumb(image.Path);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    image.Thumb = thumb;
                    OnPropertyChanged(nameof(ListingFeatureGraphic));
                });
            }
        }, cts.Token).ContinueWith(t => _log.LogWarning(t.Exception, "Gallery thumbnails failed"), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private BitmapSource? Thumb(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var key = (path, File.GetLastWriteTimeUtc(path));
        lock (_thumbCache)
        {
            if (_thumbCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var thumb = Decode(path, 0, 720);
        lock (_thumbCache)
        {
            _thumbCache[key] = thumb;
        }

        return thumb;
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void Edit(GalleryImageViewModel? image)
    {
        image ??= SelectedImage;
        if (image is not { Editable: true })
        {
            if (image is not null)
            {
                _dialogs.ShowInfo("Not editable", "The store icon is your app's own icon scaled to the store size, so there's nothing to edit here.");
            }

            return;
        }

        if (_editor.Open(image.Record.Id))
        {
            _navigation.NavigateTo(PageKey.Editor);
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (_projects.Current is not { } p)
        {
            return;
        }

        var folder = SelectedImage is { } i && File.Exists(i.Path) ? Path.GetDirectoryName(i.Path)! : p.RendersDir;
        if (Directory.Exists(folder))
        {
            _dialogs.OpenInExplorer(folder);
        }
    }

    [RelayCommand]
    private void GoDesign() => _navigation.NavigateTo(PageKey.Design);

    [RelayCommand]
    private void Refresh() => Load();

    public void Dispose()
    {
        _thumbsCts?.Cancel();
        _thumbsCts?.Dispose();
    }

    /// <summary>Loads without locking the file (the editor rewrites images in place).</summary>
    private static BitmapImage Decode(string path, int width, int height = 0)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(File.ReadAllBytes(path));
        if (width > 0)
        {
            image.DecodePixelWidth = width;
        }
        else if (height > 0)
        {
            image.DecodePixelHeight = height;
        }

        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
