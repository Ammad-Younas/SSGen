using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using SSGen.App.Services;
using SSGen.Core;
using SSGen.Core.Projects;
using SSGen.Core.Settings;
using SSGen.Scene;
using SSGen.Scene.Editing;
using SSGen.Templates;
using SceneColors = SSGen.Scene.Colors;

namespace SSGen.App.ViewModels;

/// <summary>An image that can be opened in the editor (from the picker).</summary>
public sealed record EditorImageOption(RenderRecord Record, StorePreset? Preset, string Title);

/// <summary>
/// Editor page: one rendered image's scene document with layers, properties, undo/redo, saving (scene JSON and
/// the image file again) and applying the style to the other images of the same size. The canvas is drawn by
/// SkiaSharp (the same renderer as export) into a WPF bitmap; selection handles are WPF, see EditorView.
/// </summary>
public sealed partial class EditorViewModel : ObservableObject, IPageActivated, IDisposable
{
    /// <summary>Longest side of the on-screen preview render, in pixels.</summary>
    private const double PreviewSide = 1600;

    private readonly IProjectService _projects;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly AppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly ILogger<EditorViewModel> _log;
    private readonly Lock _renderLock = new();
    private Project? _project;
    private RenderRecord? _record;
    private FileAssetResolver? _assets;
    private string? _pendingRender;
    private bool _rendering;
    private bool _switchingImage;

    public EditorViewModel(IProjectService projects, IDialogService dialogs, INavigationService navigation, AppPaths paths, ISettingsService settings, ILogger<EditorViewModel> log)
    {
        _settings = settings;
        _projects = projects;
        _dialogs = dialogs;
        _navigation = navigation;
        _paths = paths;
        _log = log;
        _projects.CurrentChanged += (_, p) => Application.Current.Dispatcher.Invoke(() => OnProjectChanged(p));
    }

    /// <summary>An image was saved (the gallery refreshes).</summary>
    public event EventHandler? Saved;

    /// <summary>The document or selection changed: the view redraws its selection handles.</summary>
    public event EventHandler? SceneChanged;

    public ObservableCollection<EditorImageOption> Images { get; } = [];

    public ObservableCollection<LayerItemViewModel> Layers { get; } = [];

    public SceneEditor? Editor { get; private set; }

    public SceneDocument? Document => Editor?.Document;

    [ObservableProperty]
    public partial EditorImageOption? SelectedImage { get; set; }

    [ObservableProperty]
    public partial LayerItemViewModel? SelectedLayer { get; set; }

    [ObservableProperty]
    public partial LayerPropertiesViewModel? Properties { get; set; }

    [ObservableProperty]
    public partial FillEditorViewModel? Background { get; set; }

    [ObservableProperty]
    public partial WriteableBitmap? Canvas { get; set; }

    [ObservableProperty]
    public partial double DocWidth { get; set; } = 1080;

    [ObservableProperty]
    public partial double DocHeight { get; set; } = 1920;

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial string? Blocker { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveChangesCommand), nameof(ApplyStyleToAllCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> Swatches { get; set; } = [];

    public bool HasBlocker => Blocker is not null;

    public bool HasDocument => Editor is not null;

    public bool IsDirty => Editor?.IsDirty ?? false;

    public bool CanUndo => Editor?.History.CanUndo ?? false;

    public bool CanRedo => Editor?.History.CanRedo ?? false;

    public string UndoText => Editor?.History.UndoName is { } n ? $"Undo {n.ToLowerInvariant()} (Ctrl+Z)" : "Undo (Ctrl+Z)";

    public string RedoText => Editor?.History.RedoName is { } n ? $"Redo {n.ToLowerInvariant()} (Ctrl+Y)" : "Redo (Ctrl+Y)";

    public bool HasSelection => SelectedLayer is not null;

    public string Title => _record is null ? "" : (IsDirty ? "● " : "") + (SelectedImage?.Title ?? _record.DisplayName);

    /// <summary>Fonts offered in the picker: bundled ones, then common Windows fonts.</summary>
    public IReadOnlyList<string> FontFamilies { get; } =
        SSGen.Scene.Fonts.BundledFamilies().Concat(["Segoe UI", "Bahnschrift", "Arial", "Verdana", "Trebuchet MS", "Georgia", "Times New Roman", "Consolas"])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The selected layer object (live).</summary>
    public Layer? Selected => Editor?.Find(SelectedLayer?.Id);

    partial void OnBlockerChanged(string? value) => OnPropertyChanged(nameof(HasBlocker));

    partial void OnSelectedImageChanged(EditorImageOption? oldValue, EditorImageOption? newValue)
    {
        if (_switchingImage || newValue is null || newValue.Record.Id == _record?.Id)
        {
            return;
        }

        if (!Open(newValue.Record.Id))
        {
            // Cancelled: put the picker back without reopening.
            _switchingImage = true;
            SelectedImage = oldValue;
            _switchingImage = false;
        }
    }

    partial void OnSelectedLayerChanged(LayerItemViewModel? value)
    {
        Properties = value is not null && Editor is not null ? new LayerPropertiesViewModel(Editor, value.Id) : null;
        OnPropertyChanged(nameof(HasSelection));
        DeleteLayerCommand.NotifyCanExecuteChanged();
        DuplicateLayerCommand.NotifyCanExecuteChanged();
        BringForwardCommand.NotifyCanExecuteChanged();
        SendBackwardCommand.NotifyCanExecuteChanged();
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OnActivated()
    {
        if (_projects.Current is not { } project)
        {
            Close();
            Blocker = "Open or create a project first.";
            return;
        }

        _project = project;
        LoadImages();
        if (_record is not null && Images.All(i => i.Record.Id != _record.Id))
        {
            // The set was rendered again on the Design page: open the image now in the same place.
            var replacement = Images.FirstOrDefault(i => i.Record.PresetId == _record.PresetId && i.Record.Position == _record.Position);
            if (IsDirty)
            {
                _dialogs.ShowInfo("Images were rendered again", "The Design page replaced this image while it had unsaved changes in the editor. The new image is open now; the unsaved changes were dropped.");
            }

            Close();
            if (replacement is not null)
            {
                OpenRecord(replacement);
            }
        }

        if (_record is null && Images.Count > 0)
        {
            OpenRecord(Images[0]);
        }

        Blocker = Images.Count == 0 ? "No store images to edit yet. Render them on the Design page first." : null;
    }

    // ------------------------------------------------------------------ opening and closing

    /// <summary>Opens a rendered image; asks about unsaved changes first. False = the user cancelled.</summary>
    public bool Open(long renderId)
    {
        if (_projects.Current is not { } project)
        {
            return false;
        }

        if (!ConfirmDiscard())
        {
            return false;
        }

        _project = project;
        LoadImages();
        if (Images.FirstOrDefault(i => i.Record.Id == renderId) is not { } option)
        {
            return false;
        }

        OpenRecord(option);
        return true;
    }

    /// <summary>Before closing the window or switching images: save, discard or cancel. True = go ahead.</summary>
    public bool ConfirmDiscard()
    {
        if (!IsDirty)
        {
            return true;
        }

        switch (_dialogs.AskSave("Unsaved changes", $"Save your changes to {_record?.DisplayName ?? "this image"}?"))
        {
            case true:
                return Save();
            case false:
                Editor?.History.Clear();
                return true;
            default:
                return false;
        }
    }

    private void LoadImages()
    {
        if (_project is not { } project)
        {
            return;
        }

        var catalog = StorePresetCatalog.Load(Path.Combine(_paths.DataRoot, "store-presets.json"));
        var order = catalog.AllPresets.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var options = RenderStore.List(project)
            .Where(r => r.Editable && File.Exists(r.SceneFullPath(project)))
            .OrderBy(r => r.Locale.Length > 0).ThenBy(r => r.Locale, StringComparer.Ordinal)
            .ThenBy(r => order.GetValueOrDefault(r.PresetId, int.MaxValue)).ThenBy(r => r.Position)
            .Select(r =>
            {
                var preset = catalog.Find(r.PresetId);
                var where = preset is null ? r.PresetId : $"{preset.Store.Name} · {preset.Name}";
                if (r.Locale.Length > 0)
                {
                    where += " · " + SSGen.Selection.Languages.Find(r.Locale).Name;
                }

                return new EditorImageOption(r, preset, preset?.Kind == PresetKind.FeatureGraphic ? where : $"{where} · {r.Position + 1}. {r.DisplayName}");
            })
            .ToList();

        _switchingImage = true;
        Images.Clear();
        foreach (var o in options)
        {
            Images.Add(o);
        }

        SelectedImage = _record is null ? null : Images.FirstOrDefault(i => i.Record.Id == _record.Id);
        _switchingImage = false;
    }

    private void OpenRecord(EditorImageOption option)
    {
        var project = _project!;
        SceneDocument scene;
        try
        {
            scene = RenderStore.LoadScene(project, option.Record);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Couldn't open scene {File}", option.Record.SceneFile);
            _dialogs.ShowError("Couldn't open the image", new UserFacingException($"The design file for {option.Record.DisplayName} couldn't be read: {ex.Message}", "Render the set again on the Design page."));
            return;
        }

        DetachEditor();
        _record = option.Record;
        lock (_renderLock)
        {
            _assets?.Dispose();
            _assets = new FileAssetResolver(project.Folder);
        }

        Editor = new SceneEditor(scene);
        Editor.Changed += OnDocumentChanged;
        Editor.Previewed += OnDocumentPreviewed;
        Background = new FillEditorViewModel(() => Editor?.Document.Background, (fill, key) => Editor?.SetBackground(fill, "background:" + key));
        DocWidth = scene.Width;
        DocHeight = scene.Height;
        Canvas = null;

        _switchingImage = true;
        SelectedImage = option;
        _switchingImage = false;

        RebuildLayers();
        SelectedLayer = null;
        Status = option.Record.IsEdited ? $"Edited {option.Record.EditedUtc!.Value.ToLocalTime():g}." : "As rendered from the template.";
        NotifyDocumentState();
        RequestRender();
    }

    private void Close()
    {
        DetachEditor();
        Editor = null;
        _record = null;
        Background = null;
        Properties = null;
        SelectedLayer = null;
        Layers.Clear();
        Canvas = null;
        Status = "";
        NotifyDocumentState();
    }

    private void DetachEditor()
    {
        if (Editor is not null)
        {
            Editor.Changed -= OnDocumentChanged;
            Editor.Previewed -= OnDocumentPreviewed;
        }
    }

    private void OnProjectChanged(Project? project)
    {
        if (IsDirty && _project is not null && _record is not null && _dialogs.Confirm("Unsaved changes", $"Save your changes to {_record.DisplayName} before the project closes?"))
        {
            Save();
        }

        Close();
        Images.Clear();
        _project = project;
    }

    // ------------------------------------------------------------------ document changes

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        var structure = Editor!.Document.Layers.Select(l => l.Id).SequenceEqual(Layers.Reverse().Select(l => l.Id));
        if (structure)
        {
            foreach (var item in Layers)
            {
                if (Editor.Find(item.Id) is { } layer)
                {
                    item.Update(layer);
                }
            }
        }
        else
        {
            RebuildLayers();
        }

        if (SelectedLayer is { } selected && Editor.Find(selected.Id) is null)
        {
            SelectedLayer = null;
        }

        Properties?.Refresh();
        Background?.Refresh();
        NotifyDocumentState();
        RequestRender();
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDocumentPreviewed(object? sender, EventArgs e)
    {
        Properties?.Refresh();
        RequestRender();
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildLayers()
    {
        var selected = SelectedLayer?.Id;
        Layers.Clear();
        if (Editor is null)
        {
            return;
        }

        foreach (var layer in Editor.Document.Layers.AsEnumerable().Reverse())
        {
            var item = new LayerItemViewModel { Id = layer.Id };
            item.Update(layer);
            Layers.Add(item);
        }

        SelectedLayer = Layers.FirstOrDefault(l => l.Id == selected);
        Swatches = CollectColours(Editor.Document);
    }

    private void NotifyDocumentState()
    {
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
        OnPropertyChanged(nameof(Title));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        SaveChangesCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Colours already used in the image, offered first in colour pickers.</summary>
    private static List<string> CollectColours(SceneDocument doc)
    {
        static IEnumerable<string> Of(Fill? fill) => fill switch
        {
            SolidFill s => [s.Color],
            LinearGradientFill g => g.Colors,
            RadialGradientFill r => r.Colors,
            _ => [],
        };

        return Of(doc.Background)
            .Concat(doc.Layers.SelectMany(l => l switch
            {
                ShapeLayer s => Of(s.Fill),
                TextLayer t => [t.Color],
                DeviceLayer d => [d.FrameColor],
                _ => [],
            }))
            .Where(Controls.ColorBox.IsHex)
            .Select(c => c.Length == 9 ? "#FF" + c[3..] : c) // offer the opaque version
            .Select(c => c.ToUpperInvariant())
            .Distinct()
            .Take(12)
            .ToList();
    }

    public IReadOnlyList<double>? PaneWeights =>
        _settings.Current.EditorPaneWeights is { Length: 3 } w && w.All(v => v > 0) ? w : null;

    public void SavePaneWeights(IEnumerable<double> weights) =>
        _settings.Save(_settings.Current with { EditorPaneWeights = weights.Select(w => Math.Round(w)).ToArray() });

    // ------------------------------------------------------------------ canvas interaction (called by the view)

    public void Select(string? layerId) => SelectedLayer = Layers.FirstOrDefault(l => l.Id == layerId);

    public Layer? LayerAt(Pt p, double tolerance) => Document is { } d ? SceneGeometry.HitTest(d, p, tolerance) : null;

    public void BeginGesture()
    {
        if (Editor is not null && SelectedLayer is { } s)
        {
            Editor.BeginGesture(s.Id);
        }
    }

    public void UpdateGesture(Action<Layer> change) => Editor?.UpdateGesture(change);

    public void EndGesture(string name) => Editor?.EndGesture(name);

    public void CancelGesture() => Editor?.CancelGesture();

    /// <summary>Arrow keys: move the selection by a few pixels (one undo step per burst of presses).</summary>
    public void Nudge(double dx, double dy)
    {
        if (Editor is not null && Selected is { Locked: false } layer)
        {
            Editor.Modify(layer.Id, l =>
            {
                l.X += dx;
                l.Y += dy;
            }, "Move", layer.Id + ":nudge");
        }
    }

    // ------------------------------------------------------------------ rendering

    private void RequestRender()
    {
        if (Editor is null)
        {
            return;
        }

        var json = Editor.Document.ToJson();
        lock (_renderLock)
        {
            _pendingRender = json;
            if (_rendering)
            {
                return;
            }

            _rendering = true;
        }

        _ = Task.Run(RenderLoopAsync);
    }

    /// <summary>Renders the newest document state; requests arriving meanwhile collapse into one more render.</summary>
    private async Task RenderLoopAsync()
    {
        while (true)
        {
            string json;
            lock (_renderLock)
            {
                if (_pendingRender is null)
                {
                    _rendering = false;
                    return;
                }

                json = _pendingRender;
                _pendingRender = null;
            }

            try
            {
                var doc = SceneDocument.FromJson(json);
                var scale = Math.Min(1, PreviewSide / Math.Max(doc.Width, doc.Height));
                byte[] pixels;
                int width, height, stride;
                lock (_renderLock)
                {
                    if (_assets is null)
                    {
                        continue;
                    }

                    using var image = SceneRenderer.Render(doc, _assets, scale);
                    using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                    image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
                    (pixels, width, height, stride) = (bitmap.Bytes, image.Width, image.Height, bitmap.RowBytes);
                }

                await Application.Current.Dispatcher.InvokeAsync(() => Blit(pixels, width, height, stride));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or ArgumentException)
            {
                _log.LogWarning(ex, "Editor preview failed");
            }
        }
    }

    private void Blit(byte[] pixels, int width, int height, int stride)
    {
        if (Canvas is not { } canvas || canvas.PixelWidth != width || canvas.PixelHeight != height)
        {
            canvas = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
            Canvas = canvas;
        }

        canvas.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
    }

    // ------------------------------------------------------------------ commands: history and files

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Editor?.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Editor?.Redo();

    private bool CanSave() => IsDirty && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveChanges() => Save();

    /// <summary>Writes the scene and renders the image file again. False if it failed.</summary>
    public bool Save()
    {
        if (Editor is null || _record is null || _project is null)
        {
            return false;
        }

        Editor.CancelGesture();
        try
        {
            var (saved, issues) = RenderStore.SaveScene(_project, _record, Editor.Document.Clone(), SelectedImage?.Preset);
            _record = saved;
            Editor.History.MarkSaved();
            Status = issues.Count == 0
                ? $"Saved {DateTime.Now:t}. Within the store rules."
                : $"Saved {DateTime.Now:t}. {string.Join(" ", issues.Select(i => i.Message))}";
            Saved?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Saving {File} failed", _record.File);
            _dialogs.ShowError("Couldn't save", new UserFacingException("Couldn't write the image: " + ex.Message, "Close any program that has the image open and try again."));
            return false;
        }
    }

    private bool CanRevert() => IsDirty;

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void Revert()
    {
        if (SelectedImage is { } option && _dialogs.Confirm("Discard changes", "Discard the changes since the last save?"))
        {
            Editor?.History.Clear();
            OpenRecord(option);
        }
    }

    [RelayCommand]
    private void PreviousImage() => Step(-1);

    [RelayCommand]
    private void NextImage() => Step(+1);

    private void Step(int delta)
    {
        if (SelectedImage is { } current && Images.IndexOf(current) is var i and >= 0)
        {
            var j = i + delta;
            if (j >= 0 && j < Images.Count)
            {
                SelectedImage = Images[j];
            }
        }
    }

    [RelayCommand]
    private void GoGallery() => _navigation.NavigateTo(PageKey.Gallery);

    [RelayCommand]
    private void GoDesign() => _navigation.NavigateTo(PageKey.Design);

    // ------------------------------------------------------------------ commands: layers

    [RelayCommand]
    private void AddText()
    {
        if (Document is not { } d)
        {
            return;
        }

        AddLayer(new TextLayer
        {
            Name = "Text",
            Text = "Your text",
            X = d.Width * 0.1,
            Y = d.Height * 0.44,
            W = d.Width * 0.8,
            H = d.Height * 0.12,
            Size = Math.Round(d.Width * 0.08),
            MinSize = Math.Round(d.Width * 0.035),
            Weight = 700,
            Color = SceneColors.TextOn(BackgroundColour(d)),
            VerticalAlign = VerticalAlign.Middle,
        }, "Add text");
    }

    [RelayCommand]
    private void AddRectangle() => AddShape(ShapeKind.Rectangle);

    [RelayCommand]
    private void AddEllipse() => AddShape(ShapeKind.Ellipse);

    private void AddShape(ShapeKind kind)
    {
        if (Document is not { } d)
        {
            return;
        }

        var size = Math.Min(d.Width, d.Height) * 0.3;
        AddLayer(new ShapeLayer
        {
            Name = kind == ShapeKind.Ellipse ? "Ellipse" : "Rectangle",
            Shape = kind,
            X = (d.Width - size) / 2,
            Y = (d.Height - size) / 2,
            W = size,
            H = size,
            Radius = kind == ShapeKind.Rectangle ? Math.Round(size * 0.12) : 0,
            Fill = new SolidFill(StandOutColour(d)),
        }, "Add shape");
    }

    /// <summary>The background's average colour (what new layers must stand out against).</summary>
    private static SKColor BackgroundColour(SceneDocument d) => d.Background switch
    {
        SolidFill s => SceneColors.Parse(s.Color),
        LinearGradientFill { Colors.Count: > 0 } g => SceneColors.Average(g.Colors.Select(SceneColors.Parse)),
        RadialGradientFill { Colors.Count: > 0 } r => SceneColors.Average(r.Colors.Select(SceneColors.Parse)),
        _ => SKColors.White,
    };

    /// <summary>Of the image's own colours (plus white and ink), the one with the most contrast to the background.</summary>
    private string StandOutColour(SceneDocument d)
    {
        var background = BackgroundColour(d);
        return Swatches.Append("#FFFFFFFF").Append("#FF111827")
            .MaxBy(c => SceneColors.Contrast(SceneColors.Parse(c), background))!;
    }

    [RelayCommand]
    private void AddPicture()
    {
        if (Document is not { } d || _project is not { } project)
        {
            return;
        }

        if (_dialogs.PickOpenFile("Add a picture", "Pictures|*.png;*.jpg;*.jpeg;*.webp;*.bmp") is not { } file)
        {
            return;
        }

        using var codec = SKCodec.Create(file);
        if (codec is null)
        {
            _dialogs.ShowInfo("Not a picture", "SSGen couldn't read that file as a picture (PNG, JPEG, WebP or BMP).");
            return;
        }

        // Pictures are copied into the project so the design keeps working if the original moves.
        var folder = Path.Combine(project.Folder, "assets");
        Directory.CreateDirectory(folder);
        var name = $"{Guid.NewGuid().ToString("N")[..6]}_{Path.GetFileName(file)}";
        File.Copy(file, Path.Combine(folder, name));

        var (w, h) = (codec.Info.Width, codec.Info.Height);
        var scale = Math.Min(d.Width * 0.4 / w, d.Height * 0.4 / h);
        AddLayer(new ImageLayer
        {
            Name = Path.GetFileNameWithoutExtension(file),
            Source = Path.Combine("assets", name),
            Fit = ImageFit.Contain,
            W = Math.Round(w * scale),
            H = Math.Round(h * scale),
            X = Math.Round((d.Width - (w * scale)) / 2),
            Y = Math.Round((d.Height - (h * scale)) / 2),
        }, "Add picture");
    }

    private void AddLayer(Layer layer, string name)
    {
        var added = Editor!.Add(layer, name: name);
        Select(added.Id);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteLayer()
    {
        if (Selected is { } layer)
        {
            Editor!.Remove(layer.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DuplicateLayer()
    {
        if (Selected is { } layer)
        {
            Select(Editor!.Duplicate(layer.Id).Id);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void BringForward()
    {
        if (Selected is { } layer)
        {
            Editor!.Reorder(layer.Id, +1);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SendBackward()
    {
        if (Selected is { } layer)
        {
            Editor!.Reorder(layer.Id, -1);
        }
    }

    [RelayCommand]
    private void ToggleVisible(LayerItemViewModel? item)
    {
        if (item is not null && Editor is not null)
        {
            Editor.Modify(item.Id, l => l.Visible = !l.Visible, "Show or hide layer");
        }
    }

    [RelayCommand]
    private void ToggleLock(LayerItemViewModel? item)
    {
        if (item is not null && Editor is not null)
        {
            Editor.Modify(item.Id, l => l.Locked = !l.Locked, "Lock or unlock layer");
        }
    }

    [RelayCommand]
    private void ResetFilters() => Properties?.ResetAdjust();

    // ------------------------------------------------------------------ apply style to all

    private bool CanApplyStyle() => HasDocument && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanApplyStyle))]
    private async Task ApplyStyleToAllAsync()
    {
        if (Editor is null || _record is null || _project is not { } project || SelectedImage is not { } current)
        {
            return;
        }

        // Same size and same language: other languages keep their own (possibly right-to-left) text styling.
        var others = Images.Where(i => i.Record.PresetId == _record.PresetId && i.Record.Locale == _record.Locale && i.Record.Id != _record.Id).ToList();
        if (others.Count == 0)
        {
            _dialogs.ShowInfo("Nothing else to update", "This is the only image of this size.");
            return;
        }

        var where = current.Preset is { } p ? $"{p.Store.Name} {p.Name}" : "this size";
        if (!_dialogs.Confirm("Apply style to all", $"Make the other {others.Count} {where} image(s) look like this one?\n\nThey take this image's background, colours, fonts, frame, positions and any layers you added; each keeps its own headline and screenshot. This image is saved first. Rendering again on the Design page brings back the template."))
        {
            return;
        }

        if (IsDirty && !Save())
        {
            return;
        }

        IsBusy = true;
        try
        {
            var source = Editor.Document.Clone();
            var preset = current.Preset;
            var done = 0;
            foreach (var other in others)
            {
                Status = $"Updating {other.Record.DisplayName} ({++done} of {others.Count})…";
                await Task.Run(() =>
                {
                    var target = RenderStore.LoadScene(project, other.Record);
                    RenderStore.SaveScene(project, other.Record, StyleTransfer.Apply(source, target), preset);
                });
            }

            Status = $"Applied this style to {others.Count} other image(s).";
            LoadImages();
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _log.LogWarning(ex, "Apply style failed");
            _dialogs.ShowError("Couldn't apply the style", new UserFacingException(ex.Message, "Close any program that has the renders open and try again."));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Dispose()
    {
        lock (_renderLock)
        {
            _assets?.Dispose();
            _assets = null;
        }
    }
}
