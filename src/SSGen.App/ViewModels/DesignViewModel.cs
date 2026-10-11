using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SSGen.Ai;
using SSGen.Ai.Profiles;
using SSGen.App.Services;
using SSGen.Core;
using SSGen.Core.Projects;
using SSGen.Core.Settings;
using SSGen.Emulator.Apk;
using SSGen.Scene;
using SSGen.Selection;
using SSGen.Templates;
using SceneColors = SSGen.Scene.Colors;

namespace SSGen.App.ViewModels;

/// <summary>A captured screen in the Design list: score, verdict, inclusion and captions.</summary>
public sealed partial class CandidateViewModel : ObservableObject
{
    public required long ScreenId { get; init; }
    public required string File { get; init; }
    public int PixelWidth { get; init; } = 1080;
    public int PixelHeight { get; init; } = 2400;
    public required BitmapSource Thumb { get; init; }

    [ObservableProperty]
    public partial string Label { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScoreText))]
    public partial double? Score { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RejectText), nameof(IsRejected))]
    public partial string? Reject { get; set; }

    [ObservableProperty]
    public partial bool Included { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    public partial int? Position { get; set; }

    [ObservableProperty]
    public partial string Headline { get; set; } = "";

    [ObservableProperty]
    public partial string Subheadline { get; set; } = "";

    [ObservableProperty]
    public partial string Reasoning { get; set; } = "";

    [ObservableProperty]
    public partial string Source { get; set; } = "";

    /// <summary>This screen's own design (null = the set's template).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemplateText), nameof(HasOwnTemplate))]
    public partial string? TemplateId { get; set; }

    public bool HasOwnTemplate => TemplateId is not null;

    public string TemplateText => TemplateId is { } id && TemplateLibrary.Find(id) is { } t ? "Design: " + t.Name : "";

    public string ScoreText => Score is { } s ? "Score " + s.ToString("0", CultureInfo.CurrentCulture) : "Not ranked yet";

    public string PositionText => Position is { } p ? (p + 1).ToString(CultureInfo.CurrentCulture) : "";

    public bool IsRejected => Reject is not null;

    public string RejectText => RejectReason.Describe(Reject);
}

/// <summary>A template choice with a thumbnail rendered from the user's own screenshot.</summary>
public sealed partial class TemplateCardViewModel : ObservableObject
{
    public required TemplateDefinition Template { get; init; }

    public string Name => Template.Name;

    public string Description => Template.Description;

    [ObservableProperty]
    public partial BitmapSource? Preview { get; set; }
}

/// <summary>A store size that can be switched on or off.</summary>
public sealed partial class PresetOptionViewModel : ObservableObject
{
    private readonly Action _changed;

    public PresetOptionViewModel(StorePreset preset, bool selected, Action changed)
    {
        Preset = preset;
        _changed = changed;
        IsSelected = selected;
    }

    public StorePreset Preset { get; }

    public string Name => Preset.Name;

    public string StoreName => Preset.Store.Name;

    public string Size => Preset.Width + " × " + Preset.Height;

    public string Notes => Preset.Notes ?? "";

    public bool Required => Preset.Required;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => _changed();
}

/// <summary>One screen's caption in an additional language (edited in the Languages card).</summary>
public sealed partial class TranslationRowViewModel : ObservableObject
{
    private readonly Action<TranslationRowViewModel> _changed;
    private readonly bool _loading = true;

    public TranslationRowViewModel(long screenId, string label, string sourceHeadline, string sourceSubheadline, string headline, string subheadline, bool rightToLeft, Action<TranslationRowViewModel> changed)
    {
        ScreenId = screenId;
        Label = label;
        SourceHeadline = sourceHeadline;
        SourceSubheadline = sourceSubheadline;
        Headline = headline;
        Subheadline = subheadline;
        Direction = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _changed = changed;
        _loading = false;
    }

    public long ScreenId { get; }

    public string Label { get; }

    public string SourceHeadline { get; }

    public string SourceSubheadline { get; }

    public FlowDirection Direction { get; }

    public string SourceText => SourceSubheadline.Length == 0 ? SourceHeadline : $"{SourceHeadline} · {SourceSubheadline}";

    [ObservableProperty]
    public partial string Headline { get; set; }

    [ObservableProperty]
    public partial string Subheadline { get; set; }

    partial void OnHeadlineChanged(string value)
    {
        if (!_loading)
        {
            _changed(this);
        }
    }

    partial void OnSubheadlineChanged(string value)
    {
        if (!_loading)
        {
            _changed(this);
        }
    }
}

/// <summary>A design choice for one screen: null id = the set's template.</summary>
public sealed record TemplateChoice(string? Id, string Name);

/// <summary>A line in the render report.</summary>
public sealed record RenderReportLine(bool IsError, string Text);

/// <summary>
/// Design page: rank the captured screens (built-in or AI), choose and order the set, edit captions, pick a
/// template, brand colours and store sizes, preview, and render everything into the project's renders folder.
/// </summary>
public sealed partial class DesignViewModel : ObservableObject, IPageActivated, IDisposable
{
    private readonly IProjectService _projects;
    private readonly AiService _ai;
    private readonly AiRunGate _gate;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly AppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly ILogger<DesignViewModel> _log;
    private Project? _project;
    private ProjectApk? _apk;
    private bool _loading;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _thumbsCts;

    public DesignViewModel(IProjectService projects, AiService ai, AiRunGate gate, IDialogService dialogs, INavigationService navigation, AppPaths paths, ISettingsService settings, ILogger<DesignViewModel> log)
    {
        _settings = settings;
        _projects = projects;
        _ai = ai;
        _gate = gate;
        _dialogs = dialogs;
        _navigation = navigation;
        _paths = paths;
        _log = log;
        Templates = TemplateLibrary.Screenshots.Select(t => new TemplateCardViewModel { Template = t }).ToList();
        _projects.CurrentChanged += (_, _) => Application.Current.Dispatcher.Invoke(Load);
    }

    public ObservableCollection<CandidateViewModel> Candidates { get; } = [];

    public IReadOnlyList<TemplateCardViewModel> Templates { get; }

    public ObservableCollection<PresetOptionViewModel> Presets { get; } = [];

    public ObservableCollection<RenderReportLine> Report { get; } = [];

    /// <summary>Every language offered for the store listing.</summary>
    public IReadOnlyList<Language> AllLanguages { get; } = Languages.All;

    /// <summary>Languages besides the primary one.</summary>
    public ObservableCollection<Language> ExtraLanguages { get; } = [];

    /// <summary>The primary language followed by the extra ones (preview picker).</summary>
    public IReadOnlyList<Language> SetLanguages => [PrimaryLanguage, .. ExtraLanguages];

    public ObservableCollection<TranslationRowViewModel> TranslationRows { get; } = [];

    /// <summary>"Same as the set" followed by every screenshot template.</summary>
    public IReadOnlyList<TemplateChoice> ScreenTemplateChoices =>
        [new(null, $"Same as the set ({SelectedTemplate?.Name ?? "template"})"), .. Templates.Select(t => new TemplateChoice(t.Template.Id, t.Name))];

    /// <summary>The selected screen's design choice (bound to the picker under the captions).</summary>
    public TemplateChoice? ScreenTemplate
    {
        get => SelectedCandidate is { } c ? ScreenTemplateChoices.FirstOrDefault(x => x.Id == c.TemplateId) : null;
        set
        {
            if (value is null || SelectedCandidate is not { } c || c.TemplateId == value.Id || _project is not { } p)
            {
                return;
            }

            c.TemplateId = value.Id;
            new SelectionStore(p).SetTemplate(c.ScreenId, value.Id);
            OnPropertyChanged();
            RequestPreview();
        }
    }

    [ObservableProperty]
    public partial Language PrimaryLanguage { get; set; } = Languages.Find("en-US");

    [ObservableProperty]
    public partial Language? LanguageToAdd { get; set; }

    [ObservableProperty]
    public partial Language? TranslationLanguage { get; set; }

    [ObservableProperty]
    public partial Language? PreviewLanguage { get; set; }

    [ObservableProperty]
    public partial string TranslatedTagline { get; set; } = "";

    [ObservableProperty]
    public partial bool TranslationAvailable { get; set; }

    public bool HasExtraLanguages => ExtraLanguages.Count > 0;

    [ObservableProperty]
    public partial string? Blocker { get; set; }

    [ObservableProperty]
    public partial CandidateViewModel? SelectedCandidate { get; set; }

    [ObservableProperty]
    public partial TemplateCardViewModel? SelectedTemplate { get; set; }

    [ObservableProperty]
    public partial string Primary { get; set; } = BrandPalette.Default.Primary;

    [ObservableProperty]
    public partial string Secondary { get; set; } = BrandPalette.Default.Secondary;

    [ObservableProperty]
    public partial string Tagline { get; set; } = "";

    [ObservableProperty]
    public partial int TargetCount { get; set; } = 6;

    [ObservableProperty]
    public partial bool AiAvailable { get; set; }

    [ObservableProperty]
    public partial string AiDescription { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RankCommand), nameof(RankWithAiCommand), nameof(RenderCommand), nameof(TranslateCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial BitmapSource? Preview { get; set; }

    [ObservableProperty]
    public partial PresetOptionViewModel? PreviewPreset { get; set; }

    [ObservableProperty]
    public partial string? RenderFolder { get; set; }

    public bool HasBlocker => Blocker is not null;

    public bool ShowAppStoreWarning => Presets.Any(p => p.IsSelected && p.Preset.Store.Warning is not null);

    public string AppStoreWarning => Presets.Select(p => p.Preset.Store.Warning).FirstOrDefault(w => w is not null) ?? "";

    public int IncludedCount => Candidates.Count(c => c.Included);

    public string PresetSource { get; private set; } = "";

    public SolidColorBrush PrimaryBrush => Brush(Primary);

    public SolidColorBrush SecondaryBrush => Brush(Secondary);

    public IEnumerable<PresetOptionViewModel> PreviewPresets => Presets.Where(p => p.IsSelected && p.Preset.Kind == PresetKind.Screenshot);

    partial void OnBlockerChanged(string? value) => OnPropertyChanged(nameof(HasBlocker));

    partial void OnSelectedCandidateChanged(CandidateViewModel? value)
    {
        OnPropertyChanged(nameof(ScreenTemplate));
        RequestPreview();
        RequestThumbnails();
    }

    partial void OnSelectedTemplateChanged(TemplateCardViewModel? value)
    {
        OnPropertyChanged(nameof(ScreenTemplateChoices));
        OnPropertyChanged(nameof(ScreenTemplate));
        SaveMeta("design.template", value?.Template.Id ?? "");
        RequestPreview();
    }

    partial void OnPrimaryChanged(string value) => OnPaletteChanged();

    partial void OnSecondaryChanged(string value) => OnPaletteChanged();

    partial void OnTaglineChanged(string value) => SaveMeta("design.tagline", value);

    partial void OnPreviewPresetChanged(PresetOptionViewModel? value) => RequestPreview();

    partial void OnPreviewLanguageChanged(Language? value) => RequestPreview();

    partial void OnPrimaryLanguageChanged(Language value)
    {
        if (!_loading && _project is { } p)
        {
            Languages.SetPrimary(p, value.Code);
            if (ExtraLanguages.FirstOrDefault(l => l.Code == value.Code) is { } same)
            {
                ExtraLanguages.Remove(same);
                SaveLanguages();
            }

            LanguagesChanged();
        }
    }

    partial void OnTranslationLanguageChanged(Language? value) => LoadTranslations();

    partial void OnTranslatedTaglineChanged(string value)
    {
        if (!_loading && _project is { } p && TranslationLanguage is { } l)
        {
            ProjectMeta.Set(p, "design.tagline@" + l.Code, value);
        }
    }

    private void OnPaletteChanged()
    {
        OnPropertyChanged(nameof(PrimaryBrush));
        OnPropertyChanged(nameof(SecondaryBrush));
        if (!_loading && IsHex(Primary) && IsHex(Secondary))
        {
            SaveMeta("design.primary", Primary);
            SaveMeta("design.secondary", Secondary);
            RequestPreview();
            RequestThumbnails();
        }
    }

    public void OnActivated()
    {
        if (_project?.Folder != _projects.Current?.Folder || Candidates.Count != ScreenCountOnDisk())
        {
            Load();
        }

        RefreshAi();
        LoadTranslations();
    }

    private int ScreenCountOnDisk() => _projects.Current is { } p ? ScreenRepository.List(p).Count : 0;

    // ------------------------------------------------------------------ loading

    private void Load()
    {
        _loading = true;
        try
        {
            _project = _projects.Current;
            Candidates.Clear();
            Report.Clear();
            Preview = null;
            RenderFolder = null;
            if (_project is not { } p)
            {
                Blocker = "Open or create a project first, then explore your app to capture screens.";
                return;
            }

            _apk = ProjectApkStore.Load(p);
            var screens = ScreenRepository.List(p);
            Blocker = screens.Count == 0 ? "No screens captured yet. Explore the app (or use Capture on the Emulator page) first." : null;

            var saved = new SelectionStore(p).Load().ToDictionary(i => i.ScreenId);
            foreach (var s in screens)
            {
                var path = s.FullPath(p);
                if (!System.IO.File.Exists(path))
                {
                    continue;
                }

                saved.TryGetValue(s.Id, out var item);
                Candidates.Add(new CandidateViewModel
                {
                    ScreenId = s.Id,
                    File = path,
                    PixelWidth = s.Width > 0 ? s.Width : 1080,
                    PixelHeight = s.Height > 0 ? s.Height : 2400,
                    Thumb = Decode(System.IO.File.ReadAllBytes(path), 120),
                    Label = item?.Label is { Length: > 0 } l ? l : SSGen.Selection.ScreenCandidate.NameFor(s),
                    Score = item?.Score,
                    Reject = item?.Reject,
                    Included = item?.Included ?? false,
                    Position = item?.Position,
                    Headline = item?.Headline ?? "",
                    Subheadline = item?.Subheadline ?? "",
                    Reasoning = item?.Reasoning ?? "",
                    Source = item?.Source ?? "",
                    TemplateId = item?.TemplateId,
                });
            }

            Sort();
            TargetCount = int.TryParse(ProjectMeta.Get(p, "explore.targetScreens"), CultureInfo.InvariantCulture, out var t) ? Math.Clamp(t, 1, 10) : 6;

            // Brand colours: saved, else from the app icon.
            var palette = _apk?.IconPath(p) is { } icon && System.IO.File.Exists(icon) ? PaletteExtractor.FromImage(System.IO.File.ReadAllBytes(icon)) : BrandPalette.Default;
            Primary = ProjectMeta.Get(p, "design.primary") is { Length: > 0 } pr ? pr : palette.Primary;
            Secondary = ProjectMeta.Get(p, "design.secondary") is { Length: > 0 } se ? se : palette.Secondary;
            Tagline = ProjectMeta.Get(p, "design.tagline") ?? "";
            SelectedTemplate = Templates.FirstOrDefault(c => c.Template.Id == ProjectMeta.Get(p, "design.template")) ?? Templates[0];

            LoadPresets(p);
            LoadLanguages(p);
            SelectedCandidate = Candidates.FirstOrDefault(c => c.Included) ?? Candidates.FirstOrDefault(c => !c.IsRejected) ?? Candidates.FirstOrDefault();
            UpdateCounts();
        }
        finally
        {
            _loading = false;
        }

        RequestPreview();
        RequestThumbnails();
    }

    private void LoadPresets(Project p)
    {
        var overrideFile = System.IO.Path.Combine(_paths.DataRoot, "store-presets.json");
        var catalog = StorePresetCatalog.Load(overrideFile);
        PresetSource = $"Store sizes verified {catalog.Verified} against the official Google Play and App Store pages ({(catalog.Origin == "built in" ? "built-in list" : "your edited list")}).";
        OnPropertyChanged(nameof(PresetSource));
        var chosen = ProjectMeta.Get(p, "design.presets") is { Length: > 0 } ids ? ids.Split(',').ToHashSet() : null;
        Presets.Clear();
        foreach (var preset in catalog.AllPresets)
        {
            Presets.Add(new PresetOptionViewModel(preset, chosen?.Contains(preset.Id) ?? preset.DefaultSelected, OnPresetsChanged));
        }

        OnPresetsChanged();
    }

    private void OnPresetsChanged()
    {
        if (!_loading && _project is { } p)
        {
            ProjectMeta.Set(p, "design.presets", string.Join(',', Presets.Where(x => x.IsSelected).Select(x => x.Preset.Id)));
        }

        OnPropertyChanged(nameof(ShowAppStoreWarning));

        // Rebuilding the list clears the combo's selection: re-select the same size (or the first one).
        var keep = PreviewPreset is { IsSelected: true } current ? current : PreviewPresets.FirstOrDefault();
        OnPropertyChanged(nameof(PreviewPresets));
        PreviewPreset = null;
        PreviewPreset = keep;
    }

    private void RefreshAi()
    {
        TranslationAvailable = _ai.ResolveTask(AiTask.Translation) is not null;
        var resolved = _ai.ResolveTask(AiTask.Ranking);
        AiAvailable = resolved is not null;
        AiDescription = resolved is { } r ? $"{r.Profile.Name} · {r.Model}" : "No AI provider (Settings > AI providers). The built-in ranking works without one.";
    }

    // ------------------------------------------------------------------ ranking

    private bool CanWork() => !IsBusy && _project is not null && Candidates.Count > 0;

    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task RankAsync() => RunRankingAsync(useAi: false);

    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task RankWithAiAsync() => RunRankingAsync(useAi: true);

    private async Task RunRankingAsync(bool useAi)
    {
        var project = _project!;
        IsBusy = true;
        Report.Clear();
        try
        {
            Status = "Measuring screens…";
            var screens = ScreenRepository.List(project);
            var candidates = await Task.Run(() => screens.Where(s => System.IO.File.Exists(s.FullPath(project))).Select(s => SSGen.Selection.ScreenCandidate.Load(project, s)).ToList());
            var context = new RankingContext(_apk?.Info.DisplayName ?? project.Name, ProjectMeta.Get(project, "explore.hints"), TargetCount);
            IReadOnlyList<ScreenAssessment> ranked;
            IReadOnlyList<ScreenAssessment> picked;
            if (useAi && await _ai.ResolveAsync(AiTask.Ranking) is { } resolved)
            {
                if (_gate.Prepare(resolved, "To judge which screens to show", w => Report.Add(new RenderReportLine(false, w))) is not { } prepared)
                {
                    Status = "Cancelled.";
                    return;
                }

                var ranker = new AiRanker(resolved.Provider, prepared.Vision, new VisionCache(project), prepared.Meter.Add);
                Status = $"Ranking with {resolved.Profile.Name}…";
                ranked = await ranker.RankAsync(candidates, context, CancellationToken.None);
                picked = StoryPicker.Pick(ranked, TargetCount, MaxForSelectedStores());
                Status = "Writing captions…";
                picked = await ranker.PolishAsync(picked, context, CancellationToken.None);
                Status = $"Ranked with {ranker.Name}{(ranker.CachedBatches > 0 ? $" ({ranker.CachedBatches} batch(es) from cache)" : "")}. {prepared.Meter.Describe()}";
            }
            else
            {
                ranked = HeuristicRanker.Rank(candidates);
                picked = StoryPicker.Pick(ranked, TargetCount, MaxForSelectedStores());
                Status = "Ranked with the built-in checks. Edit the headlines: without AI they're just the screen names.";
            }

            new SelectionStore(project).SaveRanking(ranked, picked);
            ApplySaved(project);
        }
        catch (LlmException ex)
        {
            _log.LogWarning(ex, "AI ranking failed");
            Status = "AI ranking failed: " + ex.Message;
            _dialogs.ShowError("AI ranking failed", new UserFacingException(ex.Message, ex.SuggestedFix));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The smallest maximum count among the selected screenshot sizes (Google Play phones: 8; App Store: 10).</summary>
    private int MaxForSelectedStores() =>
        Presets.Where(p => p.IsSelected && p.Preset.Kind == PresetKind.Screenshot).Select(p => p.Preset.EffectiveRules.MaxCount ?? 10).DefaultIfEmpty(8).Min();

    private void ApplySaved(Project project)
    {
        var saved = new SelectionStore(project).Load().ToDictionary(i => i.ScreenId);
        foreach (var c in Candidates)
        {
            if (saved.TryGetValue(c.ScreenId, out var i))
            {
                c.Label = i.Label;
                c.Score = i.Score;
                c.Reject = i.Reject;
                c.Included = i.Included;
                c.Position = i.Position;
                c.Headline = i.Headline;
                c.Subheadline = i.Subheadline;
                c.Reasoning = i.Reasoning;
                c.Source = i.Source;
            }
        }

        Sort();
        SelectedCandidate = Candidates.FirstOrDefault(c => c.Included) ?? Candidates.FirstOrDefault();
        UpdateCounts();
        RequestPreview();
    }

    // ------------------------------------------------------------------ editing the set

    [RelayCommand]
    private void ToggleInclude(CandidateViewModel? c)
    {
        if (c is null || _project is null)
        {
            return;
        }

        c.Included = !c.Included;
        if (c.Included && string.IsNullOrWhiteSpace(c.Headline))
        {
            c.Headline = c.Label;
        }

        SaveOrder();
    }

    [RelayCommand]
    private void MoveUp(CandidateViewModel? c) => Move(c, -1);

    [RelayCommand]
    private void MoveDown(CandidateViewModel? c) => Move(c, +1);

    private void Move(CandidateViewModel? c, int delta)
    {
        if (c is not { Included: true })
        {
            return;
        }

        var included = Candidates.Where(x => x.Included).OrderBy(x => x.Position ?? int.MaxValue).ToList();
        var i = included.IndexOf(c);
        var j = i + delta;
        if (j < 0 || j >= included.Count)
        {
            return;
        }

        (included[i], included[j]) = (included[j], included[i]);
        for (var k = 0; k < included.Count; k++)
        {
            included[k].Position = k;
        }

        SaveOrder();
    }

    /// <summary>Persists inclusion/order and the captions of every screen.</summary>
    private void SaveOrder()
    {
        if (_project is not { } p)
        {
            return;
        }

        var included = Candidates.Where(x => x.Included).OrderBy(x => x.Position ?? int.MaxValue).ToList();
        for (var k = 0; k < included.Count; k++)
        {
            included[k].Position = k;
        }

        foreach (var c in Candidates.Where(x => !x.Included))
        {
            c.Position = null;
        }

        var store = new SelectionStore(p);
        foreach (var c in Candidates)
        {
            store.Save(new SelectionItem(c.ScreenId, c.Included, c.Position, c.Score, c.Reject, c.Label, c.Headline, c.Subheadline, c.Reasoning, c.Source.Length == 0 ? "manual" : c.Source));
        }

        Sort();
        UpdateCounts();
        RequestPreview();
    }

    public IReadOnlyList<double>? PaneWeights =>
        _settings.Current.DesignPaneWeights is { Length: 3 } w && w.All(v => v > 0) ? w : null;

    public void SavePaneWeights(IEnumerable<double> weights) =>
        _settings.Save(_settings.Current with { DesignPaneWeights = weights.Select(w => Math.Round(w)).ToArray() });

    /// <summary>Called by the view while a caption is typed: refresh the preview only.</summary>
    public void CaptionsTyping() => RequestPreview();

    /// <summary>Called by the view when a caption box loses focus.</summary>
    public void CaptionsEdited()
    {
        SaveOrder();
        RequestThumbnails();
    }

    private void Sort()
    {
        var ordered = Candidates
            .OrderByDescending(c => c.Included)
            .ThenBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.IsRejected)
            .ThenByDescending(c => c.Score ?? -1)
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var from = Candidates.IndexOf(ordered[i]);
            if (from != i)
            {
                Candidates.Move(from, i);
            }
        }
    }

    private void UpdateCounts() => OnPropertyChanged(nameof(IncludedCount));

    [RelayCommand]
    private void ColorsFromIcon()
    {
        if (_project is { } p && _apk?.IconPath(p) is { } icon && System.IO.File.Exists(icon))
        {
            var palette = PaletteExtractor.FromImage(System.IO.File.ReadAllBytes(icon));
            Primary = palette.Primary;
            Secondary = palette.Secondary;
        }
        else
        {
            _dialogs.ShowInfo("No app icon", "Import the APK on the Emulator page so SSGen can read its icon.");
        }
    }

    // ------------------------------------------------------------------ preview and thumbnails

    private TemplateInputs Inputs(CandidateViewModel c, List<CandidateViewModel> set, int index, Language? language = null)
    {
        var (headline, subheadline) = Captions(c, language);
        return Inputs(c, set, index) with { Headline = headline, Subheadline = subheadline, RightToLeft = (language ?? PrimaryLanguage).RightToLeft };
    }

    /// <summary>A screen's captions in a language (the primary ones when there's no translation yet).</summary>
    private (string Headline, string Subheadline) Captions(CandidateViewModel c, Language? language)
    {
        var primary = (c.Headline.Length > 0 ? c.Headline : c.Label, c.Subheadline);
        if (language is null || language.Code == PrimaryLanguage.Code || _project is not { } p)
        {
            return primary;
        }

        return new CaptionStore(p).Load(language.Code).TryGetValue(c.ScreenId, out var t) && t.Headline.Length > 0 ? (t.Headline, t.Subheadline) : primary;
    }

    private TemplateInputs Inputs(CandidateViewModel c, List<CandidateViewModel> set, int index) => new()
    {
        Headline = c.Headline.Length > 0 ? c.Headline : c.Label,
        Subheadline = c.Subheadline,
        Screenshot = c.File,
        ScreenshotWidth = c.PixelWidth,
        ScreenshotHeight = c.PixelHeight,
        Screenshot2 = set.Count > 1 ? set[(index + 1) % set.Count].File : c.File,
        Icon = _project is { } p ? _apk?.IconPath(p) : null,
        AppName = _apk?.Info.DisplayName ?? "",
        Palette = Palette(),
        Index = index,
        Count = Math.Max(1, set.Count),
    };

    private BrandPalette Palette() => IsHex(Primary) && IsHex(Secondary)
        ? BrandPalette.From(SceneColors.Parse(Primary), SceneColors.Parse(Secondary))
        : BrandPalette.Default;

    private List<CandidateViewModel> SetOrSelection()
    {
        var set = Candidates.Where(c => c.Included).OrderBy(c => c.Position ?? int.MaxValue).ToList();
        return set.Count > 0 ? set : SelectedCandidate is { } s ? [s] : [];
    }

    private void RequestPreview()
    {
        if (_loading || SelectedCandidate is not { } c || SelectedTemplate is not { } t)
        {
            return;
        }

        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        var preset = PreviewPreset?.Preset;
        var (w, h, frame) = preset is not null ? (preset.Width, preset.Height, preset.Frame) : (1080, 1920, DeviceFrame.PhonePunchHole);
        var set = SetOrSelection();
        var index = Math.Max(0, set.IndexOf(c));
        var inputs = Inputs(c, set, index, PreviewLanguage);
        _ = Task.Run(async () =>
        {
            await Task.Delay(120, cts.Token); // debounce typing
            var bytes = RenderPng(TemplateFor(c) ?? t.Template, inputs, w, h, frame, maxHeight: 900);
            var image = Decode(bytes, 0);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!cts.IsCancellationRequested)
                {
                    Preview = image;
                }
            });
        }, cts.Token).ContinueWith(task => _log.LogWarning(task.Exception, "Preview failed"), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private void RequestThumbnails()
    {
        if (_loading || SelectedCandidate is not { } c)
        {
            return;
        }

        _thumbsCts?.Cancel();
        var cts = _thumbsCts = new CancellationTokenSource();
        var inputs = Inputs(c, SetOrSelection(), 0);
        var cards = Templates.ToList();
        _ = Task.Run(async () =>
        {
            await Task.Delay(300, cts.Token);
            foreach (var card in cards)
            {
                cts.Token.ThrowIfCancellationRequested();
                var image = Decode(RenderPng(card.Template, inputs, 1080, 1920, DeviceFrame.PhonePunchHole, maxHeight: 300), 0);
                await Application.Current.Dispatcher.InvokeAsync(() => card.Preview = image);
            }
        }, cts.Token);
    }

    private static byte[] RenderPng(TemplateDefinition template, TemplateInputs inputs, int width, int height, DeviceFrame frame, int maxHeight)
    {
        var scene = TemplateEngine.Build(template, inputs, width, height, frame);
        using var assets = new FileAssetResolver();
        using var image = SceneRenderer.Render(scene, assets, Math.Min(1, maxHeight / (double)height));
        return SceneRenderer.Encode(image, ImageFormat.Png);
    }

    // ------------------------------------------------------------------ rendering

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task RenderAsync()
    {
        var project = _project!;
        var set = Candidates.Where(c => c.Included).OrderBy(c => c.Position ?? int.MaxValue).ToList();
        if (set.Count == 0)
        {
            _dialogs.ShowInfo("Nothing chosen", "Rank the screens or tick the ones to use first.");
            return;
        }

        var presets = Presets.Where(p => p.IsSelected).Select(p => p.Preset).ToList();
        if (presets.Count == 0)
        {
            _dialogs.ShowInfo("No store sizes", "Tick at least one store size under Store sizes.");
            return;
        }

        var edited = RenderStore.CountEdited(project, presets.Select(p => p.Id));
        if (edited > 0 && !_dialogs.Confirm("Replace edited images?", $"{edited} image(s) of these sizes were changed in the editor. Rendering again replaces them with fresh designs from the template.\n\nContinue?"))
        {
            return;
        }

        IsBusy = true;
        Report.Clear();
        try
        {
            // One set per language: the primary captions, then each extra language's (untranslated screens fall
            // back to the primary captions, which the report mentions).
            var languages = SetLanguages;
            var sw = Stopwatch.StartNew();
            var count = 0;
            var hasErrors = false;
            for (var li = 0; li < languages.Count; li++)
            {
                var language = languages[li];
                var isPrimary = li == 0;
                var translations = isPrimary ? null : new CaptionStore(project).Load(language.Code);
                var missing = isPrimary ? 0 : set.Count(c => translations is null || !translations.TryGetValue(c.ScreenId, out var t) || t.Headline.Length == 0);
                if (missing > 0)
                {
                    Report.Add(new RenderReportLine(false, $"{language.Name}: {missing} screen(s) have no translation yet, so they use the {PrimaryLanguage.Name} captions."));
                }

                var tagline = isPrimary ? Tagline : ProjectMeta.Get(project, "design.tagline@" + language.Code) ?? "";
                var request = new RenderRequest
                {
                    Presets = presets,
                    Template = SelectedTemplate?.Template ?? Templates[0].Template,
                    Items = set.Select(c =>
                    {
                        var (headline, subheadline) = Captions(c, language);
                        return new RenderItem(c.ScreenId, c.File, c.PixelWidth, c.PixelHeight, c.Label, headline, subheadline) { Template = TemplateFor(c) };
                    }).ToList(),
                    Palette = Palette(),
                    IconFile = _apk?.IconPath(project),
                    AppName = _apk?.Info.DisplayName ?? project.Name,
                    Tagline = tagline.Length > 0 ? tagline : Captions(set[0], language).Subheadline,
                    Locale = isPrimary ? "" : language.Code,
                    RightToLeft = language.RightToLeft,
                };
                var step = li;
                var progress = new Progress<(int Done, int Total, string Message)>(p =>
                {
                    ProgressValue = ((step + (p.Total == 0 ? 0 : p.Done / (double)p.Total)) * 100.0) / languages.Count;
                    Status = languages.Count > 1 ? $"{language.Name}: {p.Message}" : p.Message;
                });
                var result = await Task.Run(() => RenderService.Render(project, request, progress));
                count += result.Images.Count;
                hasErrors |= result.HasErrors;
                foreach (var issue in result.SetIssues.Concat(result.Images.SelectMany(i => i.Issues)).DistinctBy(i => i.Message))
                {
                    Report.Add(new RenderReportLine(issue.IsError, languages.Count > 1 ? $"{language.Name}: {issue.Message}" : issue.Message));
                }
            }

            RenderFolder = System.IO.Path.Combine(project.Folder, "renders");
            var inLanguages = languages.Count > 1 ? $" in {languages.Count} languages" : "";
            Status = $"Rendered {count} image(s){inLanguages} in {sw.Elapsed.TotalSeconds:0} s{(hasErrors ? ", with problems (see below)" : ", all within the store rules")}.";
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Render failed");
            _dialogs.ShowError("Rendering failed", new UserFacingException("Couldn't write the images: " + ex.Message, "Close any program that has the renders folder open and try again."));
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
        }
    }

    // ------------------------------------------------------------------ languages

    private void LoadLanguages(Project p)
    {
        PrimaryLanguage = Languages.Primary(p);
        ExtraLanguages.Clear();
        foreach (var l in Languages.Additional(p))
        {
            ExtraLanguages.Add(l);
        }

        LanguagesChanged();
    }

    private void SaveLanguages()
    {
        if (_project is { } p)
        {
            Languages.SetAdditional(p, ExtraLanguages.Select(l => l.Code));
        }
    }

    private void LanguagesChanged()
    {
        OnPropertyChanged(nameof(SetLanguages));
        OnPropertyChanged(nameof(HasExtraLanguages));
        PreviewLanguage = SetLanguages.FirstOrDefault(l => l.Code == PreviewLanguage?.Code) ?? PrimaryLanguage;
        TranslationLanguage = ExtraLanguages.FirstOrDefault(l => l.Code == TranslationLanguage?.Code) ?? ExtraLanguages.FirstOrDefault();
    }

    [RelayCommand]
    private void AddLanguage()
    {
        if (LanguageToAdd is not { } l || _project is null || l.Code == PrimaryLanguage.Code || ExtraLanguages.Any(x => x.Code == l.Code))
        {
            return;
        }

        ExtraLanguages.Add(l);
        SaveLanguages();
        LanguageToAdd = null;
        LanguagesChanged();
        TranslationLanguage = l;
    }

    [RelayCommand]
    private void RemoveLanguage(Language? l)
    {
        if (l is null || !ExtraLanguages.Contains(l))
        {
            return;
        }

        ExtraLanguages.Remove(l);
        SaveLanguages();
        LanguagesChanged();
    }

    /// <summary>The translation rows for the chosen language: every screen in the set, in order.</summary>
    private void LoadTranslations()
    {
        TranslationRows.Clear();
        if (_project is not { } p || TranslationLanguage is not { } language)
        {
            TranslateCommand.NotifyCanExecuteChanged();
            return;
        }

        var saved = new CaptionStore(p).Load(language.Code);
        foreach (var c in Candidates.Where(c => c.Included).OrderBy(c => c.Position ?? int.MaxValue))
        {
            saved.TryGetValue(c.ScreenId, out var t);
            TranslationRows.Add(new TranslationRowViewModel(c.ScreenId, c.Label, c.Headline.Length > 0 ? c.Headline : c.Label, c.Subheadline,
                t?.Headline ?? "", t?.Subheadline ?? "", language.RightToLeft, SaveTranslation));
        }

        var wasLoading = _loading;
        _loading = true;
        TranslatedTagline = ProjectMeta.Get(p, "design.tagline@" + language.Code) ?? "";
        _loading = wasLoading;
        TranslateCommand.NotifyCanExecuteChanged();
    }

    private void SaveTranslation(TranslationRowViewModel row)
    {
        if (_project is { } p && TranslationLanguage is { } language)
        {
            new CaptionStore(p).Save(new LocalizedCaption(row.ScreenId, language.Code, row.Headline.Trim(), row.Subheadline.Trim(), "manual"));
            if (PreviewLanguage?.Code == language.Code)
            {
                RequestPreview();
            }
        }
    }

    private bool CanTranslate() => !IsBusy && TranslationLanguage is not null && TranslationRows.Count > 0;

    /// <summary>Translates the set's captions (and the tagline) into the chosen language. Text only: no screenshots.</summary>
    [RelayCommand(CanExecute = nameof(CanTranslate))]
    private async Task TranslateAsync()
    {
        if (_project is not { } project || TranslationLanguage is not { } language)
        {
            return;
        }

        if (await _ai.ResolveAsync(AiTask.Translation) is not { } resolved)
        {
            _dialogs.ShowInfo("No AI provider", "Add an AI provider in Settings > AI providers to translate automatically, or type the translations here.");
            return;
        }

        var filled = TranslationRows.Count(r => r.Headline.Length > 0);
        if (filled > 0 && !_dialogs.Confirm("Replace translations?", $"{filled} caption(s) in {language.Name} already have text. Replace them with new AI translations?"))
        {
            return;
        }

        if (_gate.Prepare(resolved, "", textOnly: true) is not { } prepared)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Status = $"Translating into {language.Name} with {resolved.Profile.Name}…";
            var sources = TranslationRows.Select(r => new CaptionSource(r.ScreenId, r.SourceHeadline, r.SourceSubheadline)).ToList();
            if (Tagline.Length > 0)
            {
                sources.Add(new CaptionSource(-1, Tagline, ""));
            }

            var translator = new CaptionTranslator(resolved.Provider, prepared.Meter.Add);
            var result = await translator.TranslateAsync(sources, PrimaryLanguage, language, _apk?.Info.DisplayName ?? project.Name, CancellationToken.None);
            var store = new CaptionStore(project);
            foreach (var caption in result)
            {
                if (caption.ScreenId == -1)
                {
                    ProjectMeta.Set(project, "design.tagline@" + language.Code, caption.Headline);
                }
                else
                {
                    store.Save(caption);
                }
            }

            LoadTranslations();
            RequestPreview();
            Status = $"Translated {result.Count} caption(s) into {language.Name}. Have a native speaker check store text before publishing. {prepared.Meter.Describe()}";
        }
        catch (LlmException ex)
        {
            _log.LogWarning(ex, "Translation failed");
            Status = "Translation failed: " + ex.Message;
            _dialogs.ShowError("Translation failed", new UserFacingException(ex.Message, ex.SuggestedFix));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static TemplateDefinition? TemplateFor(CandidateViewModel c) => c.TemplateId is { } id ? TemplateLibrary.Find(id) : null;

    /// <summary>Removes a captured screen from the project (its image, captions and place in the set).</summary>
    [RelayCommand]
    private void DeleteScreen(CandidateViewModel? c)
    {
        if (c is null || _project is not { } p || IsBusy)
        {
            return;
        }

        if (!_dialogs.Confirm("Delete screenshot", $"Delete \"{c.Label}\" from this project? The captured image and its captions are removed. Store images already rendered from it stay until you render again."))
        {
            return;
        }

        var index = Candidates.IndexOf(c);
        ScreenRepository.Delete(p, c.ScreenId);
        Candidates.Remove(c);
        SaveOrder();
        if (SelectedCandidate is null || SelectedCandidate == c)
        {
            SelectedCandidate = Candidates.Count == 0 ? null : Candidates[Math.Clamp(index, 0, Candidates.Count - 1)];
        }

        LoadTranslations();
        Status = $"Deleted {c.Label}.";
        if (Candidates.Count == 0)
        {
            Blocker = "No screens captured yet. Explore the app (or use Capture on the Emulator page) first.";
        }
    }

    [RelayCommand]
    private void OpenRenders()
    {
        if (RenderFolder is { } f)
        {
            _dialogs.OpenInExplorer(f);
        }
    }

    [RelayCommand]
    private void GoExplore() => _navigation.NavigateTo(PageKey.Agent);

    [RelayCommand]
    private void GoGallery() => _navigation.NavigateTo(PageKey.Gallery);

    public void Dispose()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _thumbsCts?.Cancel();
        _thumbsCts?.Dispose();
    }

    // ------------------------------------------------------------------ helpers

    private void SaveMeta(string key, string value)
    {
        if (!_loading && _project is { } p)
        {
            ProjectMeta.Set(p, key, value);
        }
    }

    private static bool IsHex(string s) => System.Text.RegularExpressions.Regex.IsMatch(s ?? "", "^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$");

    private static SolidColorBrush Brush(string hex)
    {
        var c = SceneColors.Parse(IsHex(hex) ? hex : "#808080");
        var brush = new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));
        brush.Freeze();
        return brush;
    }

    private static BitmapImage Decode(byte[] bytes, int width)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new System.IO.MemoryStream(bytes);
        if (width > 0)
        {
            image.DecodePixelWidth = width;
        }

        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
