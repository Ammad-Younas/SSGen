using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using SSGen.Scene;
using SSGen.Scene.Editing;

namespace SSGen.App.ViewModels;

/// <summary>A row in the editor's layer list (top layer first).</summary>
public sealed partial class LayerItemViewModel : ObservableObject
{
    public required string Id { get; init; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string KindName { get; set; } = "";

    [ObservableProperty]
    public partial string Glyph { get; set; } = "";

    [ObservableProperty]
    public partial bool Visible { get; set; } = true;

    [ObservableProperty]
    public partial bool Locked { get; set; }

    public void Update(Layer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        (KindName, Glyph) = layer switch
        {
            TextLayer => ("Text", ""),
            DeviceLayer => ("Device", ""),
            ImageLayer => ("Picture", ""),
            ShapeLayer { Shape: ShapeKind.Ellipse } => ("Ellipse", ""),
            _ => ("Rectangle", ""),
        };
        Name = layer switch
        {
            _ when layer.Name.Length > 0 && layer.Name != "text" && layer.Name != "shape" => Capitalise(layer.Name),
            TextLayer t when t.Text.Length > 0 => t.Text.Length > 24 ? t.Text[..24] + "…" : t.Text,
            _ => KindName,
        };
        Visible = layer.Visible;
        Locked = layer.Locked;
    }

    private static string Capitalise(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>Edits a fill (solid colour or a two-colour gradient) through get/set callbacks.</summary>
public sealed partial class FillEditorViewModel : ObservableObject
{
    private readonly Func<Fill?> _get;
    private readonly Action<Fill, string> _set;

    public FillEditorViewModel(Func<Fill?> get, Action<Fill, string> set)
    {
        _get = get;
        _set = set;
    }

    public static IReadOnlyList<string> Kinds { get; } = ["Solid colour", "Linear gradient", "Radial gradient"];

    private Fill Current => _get() ?? new SolidFill("#FFFFFFFF");

    public int KindIndex
    {
        get => Current switch { LinearGradientFill => 1, RadialGradientFill => 2, _ => 0 };
        set
        {
            if (value == KindIndex)
            {
                return;
            }

            var (c1, c2) = (Color1, Color2);
            _set(value switch
            {
                1 => new LinearGradientFill([c1, c2], 90),
                2 => new RadialGradientFill([c1, c2]),
                _ => new SolidFill(c1),
            }, "kind");
        }
    }

    public bool IsGradient => KindIndex != 0;

    public bool IsLinear => KindIndex == 1;

    public string Color1
    {
        get => Colors(Current) is [var first, ..] ? first : "#FFFFFFFF";
        set => SetColor(0, value);
    }

    public string Color2
    {
        get => Colors(Current) is [_, var second, ..] ? second : Colors(Current) is [var only] ? only : "#FF000000";
        set => SetColor(1, value);
    }

    public double Angle
    {
        get => Current is LinearGradientFill g ? g.Angle : 90;
        set
        {
            if (Current is LinearGradientFill g && Math.Abs(g.Angle - value) > 0.01)
            {
                _set(g with { Angle = Math.Round(value) }, "angle");
            }
        }
    }

    public void Refresh() => OnPropertyChanged(string.Empty);

    private void SetColor(int index, string value)
    {
        if (!Controls.ColorBox.IsHex(value))
        {
            return;
        }

        switch (Current)
        {
            case SolidFill when index == 0:
                _set(new SolidFill(value), "color1");
                break;
            case LinearGradientFill g:
                _set(g with { Colors = Replace(g.Colors, index, value) }, "color" + index);
                break;
            case RadialGradientFill r:
                _set(r with { Colors = Replace(r.Colors, index, value) }, "color" + index);
                break;
        }
    }

    private static IReadOnlyList<string> Colors(Fill fill) => fill switch
    {
        SolidFill s => [s.Color],
        LinearGradientFill g => g.Colors,
        RadialGradientFill r => r.Colors,
        _ => [],
    };

    private static List<string> Replace(IReadOnlyList<string> colors, int index, string value)
    {
        var list = colors.ToList();
        while (list.Count <= index)
        {
            list.Add(list.Count > 0 ? list[^1] : value);
        }

        list[index] = value;
        return list;
    }
}

/// <summary>A weight choice for the text weight picker.</summary>
public sealed record FontWeightOption(int Weight, string Name);

/// <summary>
/// Properties of the selected layer. Every setter goes through <see cref="SceneEditor.Modify"/> with a merge key
/// per field, so dragging a slider or typing in a box is one undo step.
/// </summary>
public sealed partial class LayerPropertiesViewModel : ObservableObject
{
    private readonly SceneEditor _editor;

    public LayerPropertiesViewModel(SceneEditor editor, string layerId)
    {
        _editor = editor;
        Id = layerId;
        Fill = new FillEditorViewModel(
            () => (Layer as ShapeLayer)?.Fill,
            (fill, key) => Set("Change fill", l => ((ShapeLayer)l).Fill = fill, "fill:" + key));
    }

    public string Id { get; }

    private Layer Layer => _editor.Find(Id) ?? new ShapeLayer();

    public FillEditorViewModel Fill { get; }

    public static IReadOnlyList<FontWeightOption> Weights { get; } =
    [
        new(300, "Light"), new(400, "Regular"), new(500, "Medium"), new(600, "Semibold"), new(700, "Bold"), new(800, "Extra bold"), new(900, "Black"),
    ];

    public static IReadOnlyList<TextAlign> Aligns { get; } = Enum.GetValues<TextAlign>();

    public static IReadOnlyList<VerticalAlign> VerticalAligns { get; } = Enum.GetValues<VerticalAlign>();

    public static IReadOnlyList<DeviceFrame> Frames { get; } = Enum.GetValues<DeviceFrame>();

    public static IReadOnlyList<ImageFit> Fits { get; } = Enum.GetValues<ImageFit>();

    public static IReadOnlyList<ShapeKind> Shapes { get; } = Enum.GetValues<ShapeKind>();

    public bool IsText => Layer is TextLayer;

    public bool IsShape => Layer is ShapeLayer;

    public bool IsImage => Layer is ImageLayer;

    public bool IsDevice => Layer is DeviceLayer;

    public bool HasAdjust => Layer is ImageLayer or DeviceLayer;

    public bool HasRadius => Layer is ShapeLayer { Shape: ShapeKind.Rectangle } or ImageLayer;

    public string KindTitle => Layer switch
    {
        TextLayer => "Text",
        DeviceLayer => "Device and screenshot",
        ImageLayer => "Picture",
        _ => "Shape",
    };

    // ------------------------------------------------------------------ common

    public string Name
    {
        get => Layer.Name;
        set => Set("Rename layer", l => l.Name = value.Trim());
    }

    public double X
    {
        get => Math.Round(Layer.X);
        set => Set("Move", l => l.X = value);
    }

    public double Y
    {
        get => Math.Round(Layer.Y);
        set => Set("Move", l => l.Y = value);
    }

    public double W
    {
        get => Math.Round(Layer.W);
        set => Set("Resize", l => l.W = Math.Max(1, value));
    }

    public double H
    {
        get => Math.Round(Layer.H);
        set => Set("Resize", l => l.H = Math.Max(1, value));
    }

    public double Rotation
    {
        get => Math.Round(Layer.Rotation, 1);
        set => Set("Rotate", l => l.Rotation = SceneGeometry.Normalize(value));
    }

    /// <summary>0 to 100.</summary>
    public double Opacity
    {
        get => Math.Round(Layer.Opacity * 100);
        set => Set("Opacity", l => l.Opacity = Math.Clamp(value / 100, 0, 1));
    }

    public bool HasShadow
    {
        get => Layer.Shadow is not null;
        set => Set(value ? "Add shadow" : "Remove shadow", l => l.Shadow = value ? new Shadow(Blur: Math.Round(_editor.Document.Width * 0.03), OffsetY: Math.Round(_editor.Document.Width * 0.015)) : null);
    }

    public double ShadowBlur
    {
        get => Layer.Shadow?.Blur ?? 0;
        set => Set("Shadow", l => l.Shadow = (l.Shadow ?? new Shadow()) with { Blur = Math.Max(0, Math.Round(value)) });
    }

    public double ShadowOffsetY
    {
        get => Layer.Shadow?.OffsetY ?? 0;
        set => Set("Shadow", l => l.Shadow = (l.Shadow ?? new Shadow()) with { OffsetY = Math.Round(value) });
    }

    public string ShadowColor
    {
        get => Layer.Shadow?.Color ?? "#59000000";
        set => Set("Shadow colour", l => l.Shadow = (l.Shadow ?? new Shadow()) with { Color = value });
    }

    // ------------------------------------------------------------------ text

    public string Text
    {
        get => (Layer as TextLayer)?.Text ?? "";
        set => SetText("Edit text", t => t.Text = value);
    }

    public string Font
    {
        get => (Layer as TextLayer)?.Font ?? "";
        set => SetText("Font", t => t.Font = value.Trim().Length > 0 ? value.Trim() : t.Font);
    }

    public int Weight
    {
        get => (Layer as TextLayer)?.Weight ?? 700;
        set => SetText("Font weight", t => t.Weight = value);
    }

    public double FontSize
    {
        get => Math.Round((Layer as TextLayer)?.Size ?? 0);
        set => SetText("Font size", t =>
        {
            t.Size = Math.Max(4, value);
            t.MinSize = Math.Min(t.MinSize, t.Size);
        });
    }

    public string TextColor
    {
        get => (Layer as TextLayer)?.Color ?? "#FFFFFFFF";
        set => SetText("Text colour", t => t.Color = value);
    }

    public TextAlign Align
    {
        get => (Layer as TextLayer)?.Align ?? TextAlign.Center;
        set => SetText("Align", t => t.Align = value);
    }

    public VerticalAlign VerticalAlign
    {
        get => (Layer as TextLayer)?.VerticalAlign ?? VerticalAlign.Top;
        set => SetText("Align", t => t.VerticalAlign = value);
    }

    public double LineHeight
    {
        get => Math.Round((Layer as TextLayer)?.LineHeight ?? 1.15, 2);
        set => SetText("Line height", t => t.LineHeight = Math.Clamp(value, 0.6, 3));
    }

    public double LetterSpacing
    {
        get => Math.Round((Layer as TextLayer)?.LetterSpacing ?? 0, 1);
        set => SetText("Letter spacing", t => t.LetterSpacing = Math.Clamp(value, -20, 100));
    }

    public int MaxLines
    {
        get => (Layer as TextLayer)?.MaxLines ?? 3;
        set => SetText("Lines", t => t.MaxLines = Math.Clamp(value, 1, 20));
    }

    // ------------------------------------------------------------------ shapes and pictures

    public ShapeKind ShapeKind
    {
        get => (Layer as ShapeLayer)?.Shape ?? ShapeKind.Rectangle;
        set => Set("Shape", l => ((ShapeLayer)l).Shape = value);
    }

    public double Radius
    {
        get => Math.Round(Layer switch { ShapeLayer s => s.Radius, ImageLayer i => i.Radius, _ => 0 });
        set => Set("Corner radius", l =>
        {
            switch (l)
            {
                case ShapeLayer s:
                    s.Radius = Math.Max(0, value);
                    break;
                case ImageLayer i:
                    i.Radius = Math.Max(0, value);
                    break;
            }
        });
    }

    public double Blur
    {
        get => Math.Round((Layer as ShapeLayer)?.Blur ?? 0);
        set => Set("Blur", l => ((ShapeLayer)l).Blur = Math.Max(0, value));
    }

    public bool HasStroke
    {
        get => (Layer as ShapeLayer)?.StrokeColor is { Length: > 0 };
        set => Set(value ? "Add outline" : "Remove outline", l =>
        {
            var s = (ShapeLayer)l;
            s.StrokeColor = value ? "#FFFFFFFF" : null;
            s.StrokeWidth = value ? Math.Max(2, Math.Round(_editor.Document.Width * 0.006)) : 0;
        });
    }

    public string StrokeColor
    {
        get => (Layer as ShapeLayer)?.StrokeColor ?? "#FFFFFFFF";
        set => Set("Outline colour", l => ((ShapeLayer)l).StrokeColor = value);
    }

    public double StrokeWidth
    {
        get => Math.Round((Layer as ShapeLayer)?.StrokeWidth ?? 0, 1);
        set => Set("Outline width", l => ((ShapeLayer)l).StrokeWidth = Math.Max(0, value));
    }

    public ImageFit Fit
    {
        get => (Layer as ImageLayer)?.Fit ?? ImageFit.Cover;
        set => Set("Fit", l => ((ImageLayer)l).Fit = value);
    }

    public DeviceFrame Frame
    {
        get => (Layer as DeviceLayer)?.Frame ?? DeviceFrame.PhonePunchHole;
        set => Set("Frame", l => ((DeviceLayer)l).Frame = value);
    }

    public string FrameColor
    {
        get => (Layer as DeviceLayer)?.FrameColor ?? "#FF101114";
        set => Set("Frame colour", l => ((DeviceLayer)l).FrameColor = value);
    }

    /// <summary>-100 to 100.</summary>
    public double Brightness
    {
        get => Math.Round((AdjustOf(Layer)?.Brightness ?? 0) * 100);
        set => SetAdjust("Brightness", a => a with { Brightness = value / 100 });
    }

    public double Contrast
    {
        get => Math.Round((AdjustOf(Layer)?.Contrast ?? 0) * 100);
        set => SetAdjust("Contrast", a => a with { Contrast = value / 100 });
    }

    public double Saturation
    {
        get => Math.Round((AdjustOf(Layer)?.Saturation ?? 0) * 100);
        set => SetAdjust("Saturation", a => a with { Saturation = value / 100 });
    }

    public void ResetAdjust() => Set("Reset filters", l =>
    {
        switch (l)
        {
            case ImageLayer i:
                i.Adjust = null;
                break;
            case DeviceLayer d:
                d.Adjust = null;
                break;
        }
    });

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        Fill.Refresh();
    }

    // ------------------------------------------------------------------ helpers

    private static ImageAdjust? AdjustOf(Layer layer) => layer switch
    {
        ImageLayer i => i.Adjust,
        DeviceLayer d => d.Adjust,
        _ => null,
    };

    private void SetAdjust(string name, Func<ImageAdjust, ImageAdjust> change, [CallerMemberName] string field = "") => Set(name, l =>
    {
        var adjusted = change(AdjustOf(l) ?? new ImageAdjust());
        var value = adjusted.IsIdentity ? null : adjusted;
        switch (l)
        {
            case ImageLayer i:
                i.Adjust = value;
                break;
            case DeviceLayer d:
                d.Adjust = value;
                break;
        }
    }, field);

    private void SetText(string name, Action<TextLayer> change, [CallerMemberName] string field = "") =>
        Set(name, l => change((TextLayer)l), field);

    private void Set(string name, Action<Layer> change, [CallerMemberName] string field = "")
    {
        if (_editor.Find(Id) is null)
        {
            return;
        }

        _editor.Modify(Id, change, name, $"{Id}:{field}");
    }
}
