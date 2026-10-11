using System.Globalization;
using SkiaSharp;
using SSGen.Scene;

namespace SSGen.Templates;

/// <summary>What fills a template for one image.</summary>
public sealed record TemplateInputs
{
    public string Headline { get; init; } = "";
    public string Subheadline { get; init; } = "";

    /// <summary>Screenshot file (or asset key) and its pixel size.</summary>
    public required string Screenshot { get; init; }
    public int ScreenshotWidth { get; init; } = 1080;
    public int ScreenshotHeight { get; init; } = 2400;

    /// <summary>Second screenshot for two-screen designs (falls back to the first).</summary>
    public string? Screenshot2 { get; init; }

    public string? Icon { get; init; }
    public string AppName { get; init; } = "";
    public BrandPalette Palette { get; init; } = BrandPalette.Default;

    /// <summary>Position in the set and its size (panorama designs).</summary>
    public int Index { get; init; }
    public int Count { get; init; } = 1;

    public bool RightToLeft { get; init; }
}

/// <summary>Turns a <see cref="TemplateDefinition"/> plus inputs into a pixel-exact <see cref="SceneDocument"/> for a preset.</summary>
public static class TemplateEngine
{
    /// <summary>Tablet screens are about 3:4; a phone screenshot is cover-cropped into that.</summary>
    private const double TabletScreenAspect = 0.75;

    public static SceneDocument Build(TemplateDefinition template, TemplateInputs inputs, int width, int height, DeviceFrame presetFrame = DeviceFrame.PhonePunchHole, string? presetId = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(inputs);
        var tokens = Tokens(inputs.Palette);
        var scene = new SceneDocument
        {
            Width = width,
            Height = height,
            Background = Fill(template.Background, tokens, template.Panorama ? inputs : null),
            Meta =
            {
                ["template"] = template.Id,
                ["preset"] = presetId ?? "",
                ["screenshot"] = inputs.Screenshot,
            },
        };

        var stripOffset = template.Panorama ? inputs.Index : 0;
        foreach (var t in template.Layers)
        {
            var layer = t.Type switch
            {
                "shape" => (Layer?)Shape(t, tokens, width, height, stripOffset, template.Panorama ? inputs : null),
                "text" => Text(t, inputs, tokens, width, height),
                "device" => Device(t, inputs, tokens, width, height, presetFrame),
                "image" => Image(t, inputs, width, height),
                _ => null,
            };
            if (layer is not null)
            {
                layer.Name = t.Name ?? t.Role ?? t.Type;
                layer.Id = t.Role ?? $"{t.Type}-{scene.Layers.Count + 1}";
                layer.Rotation = t.Rotation;
                layer.Opacity = t.Opacity;
                scene.Layers.Add(layer);
            }
        }

        return scene;
    }

    // ------------------------------------------------------------------ tokens and fills

    /// <summary>Named colours a template can use; text colours are chosen for contrast with the brand colours.</summary>
    public static Dictionary<string, string> Tokens(BrandPalette p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var primary = Colors.Parse(p.Primary);
        var secondary = Colors.Parse(p.Secondary);
        var brandMix = Colors.Average([primary, secondary]);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["brand.primary"] = p.Primary,
            ["brand.secondary"] = p.Secondary,
            ["brand.dark"] = p.Dark,
            ["brand.light"] = p.Light,
            ["brand.deep"] = Colors.ToHex(Colors.Shade(primary, -0.45)),
            ["brand.soft"] = Colors.ToHex(Colors.Shade(primary, 0.55)),
            ["text.onBrand"] = Colors.TextOn(brandMix),
            ["text.onPrimary"] = Colors.TextOn(primary),
            ["text.onDark"] = Colors.White,
            ["text.onLight"] = Colors.Ink,
            ["white"] = Colors.White,
            ["ink"] = Colors.Ink,
            ["black"] = "#000000",
        };
    }

    /// <summary>"{name}" or "{name@40}" (40 % opacity) or a literal colour.</summary>
    public static string Resolve(string value, IReadOnlyDictionary<string, string> tokens)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(tokens);
        if (!value.StartsWith('{') || !value.EndsWith('}'))
        {
            return value;
        }

        var inner = value[1..^1];
        var at = inner.IndexOf('@', StringComparison.Ordinal);
        var name = at < 0 ? inner : inner[..at];
        var color = tokens.TryGetValue(name, out var hex) ? hex : "#FF00FF";
        if (at < 0)
        {
            return color;
        }

        var alpha = double.Parse(inner[(at + 1)..], CultureInfo.InvariantCulture) / 100;
        return Colors.ToHex(Colors.WithAlpha(Colors.Parse(color), alpha));
    }

    private static Fill Fill(TemplateFill f, IReadOnlyDictionary<string, string> tokens, TemplateInputs? panorama)
    {
        var colors = f.Colors.Select(c => Resolve(c, tokens)).ToList();
        return f.Type switch
        {
            // Panorama: one gradient spans the whole set; each image shows its slice.
            "linear" when panorama is not null => new LinearGradientFill(colors, f.Angle, Math.Max(1, panorama.Count), panorama.Index),
            "linear" => new LinearGradientFill(colors, f.Angle),
            "radial" => new RadialGradientFill(colors, f.Cx, f.Cy, f.Radius),
            _ => new SolidFill(colors.FirstOrDefault() ?? "#FFFFFF"),
        };
    }

    // ------------------------------------------------------------------ layers

    private static ShapeLayer Shape(TemplateLayer t, IReadOnlyDictionary<string, string> tokens, int width, int height, int stripOffset, TemplateInputs? panorama)
    {
        // Panorama shapes are positioned on the whole strip (x in set widths), so they continue across images.
        var x = (t.X - stripOffset) * width;
        return new ShapeLayer
        {
            Shape = string.Equals(t.Shape, "ellipse", StringComparison.OrdinalIgnoreCase) ? ShapeKind.Ellipse : ShapeKind.Rectangle,
            X = x,
            Y = t.Y * height,
            W = t.W * width,
            H = t.H * height,
            Radius = t.Radius * Math.Min(width, height),
            Fill = t.Fill is { } f ? Fill(f, tokens, null) : new SolidFill("#FFFFFF"),
            Blur = t.Blur * width,
        };
    }

    private static TextLayer? Text(TemplateLayer t, TemplateInputs inputs, IReadOnlyDictionary<string, string> tokens, int width, int height)
    {
        var text = t.Role switch
        {
            "headline" => inputs.Headline,
            "subheadline" => inputs.Subheadline,
            "appName" => inputs.AppName,
            _ => t.Name ?? "",
        };

        if (string.IsNullOrWhiteSpace(text) && t.Role is "subheadline")
        {
            return null; // optional
        }

        // Size relative to the shorter side keeps text proportionate on landscape feature graphics too.
        var basis = Math.Min(width, height);
        return new TextLayer
        {
            Text = t.Uppercase ? text.ToUpper(CultureInfo.CurrentCulture) : text,
            X = t.X * width,
            Y = t.Y * height,
            W = t.W * width,
            H = t.H * height,
            Font = t.Font,
            Weight = t.Weight,
            Size = t.Size * basis,
            MinSize = t.MinSize * basis,
            Color = Resolve(t.Color, tokens),
            Align = Enum.Parse<TextAlign>(t.Align, ignoreCase: true),
            VerticalAlign = Enum.Parse<VerticalAlign>(t.VerticalAlign, ignoreCase: true),
            LineHeight = t.LineHeight,
            MaxLines = t.MaxLines,
            LetterSpacing = t.LetterSpacing * t.Size * basis,
            RightToLeft = inputs.RightToLeft,
        };
    }

    private static DeviceLayer Device(TemplateLayer t, TemplateInputs inputs, IReadOnlyDictionary<string, string> tokens, int width, int height, DeviceFrame presetFrame)
    {
        var frame = t.Frame switch
        {
            "auto" => presetFrame,
            "phone" => presetFrame == DeviceFrame.Tablet ? DeviceFrame.PhonePunchHole : presetFrame,
            var f => Enum.Parse<DeviceFrame>(f, ignoreCase: true),
        };
        var screenAspect = frame == DeviceFrame.Tablet ? TabletScreenAspect : inputs.ScreenshotWidth / (double)Math.Max(1, inputs.ScreenshotHeight);
        var aspect = DeviceGeometry.BodyAspect(frame, screenAspect);

        double w, h;
        if (t.Height is { } hf)
        {
            h = hf * height;
            w = h * aspect;
            if (t.MaxWidth is { } mw && w > mw * width)
            {
                w = mw * width;
                h = w / aspect;
            }
        }
        else
        {
            w = t.W * width;
            h = w / aspect;
        }

        var x = t.Cx is { } cx ? (cx * width) - (w / 2) : t.X * width;
        var y = t.Top is { } top ? top * height
            : t.Bottom is { } bottom ? (bottom * height) - h
            : t.Cy is { } cy ? (cy * height) - (h / 2)
            : t.Y * height;

        return new DeviceLayer
        {
            Frame = frame,
            Source = t.Role == "screenshot2" ? inputs.Screenshot2 ?? inputs.Screenshot : inputs.Screenshot,
            FrameColor = Resolve(t.FrameColor, tokens),
            X = x,
            Y = y,
            W = w,
            H = h,
            Shadow = t.Shadow ? new Shadow("#55000000", Math.Max(8, w * 0.10), 0, Math.Max(4, h * 0.02)) : null,
        };
    }

    private static ImageLayer? Image(TemplateLayer t, TemplateInputs inputs, int width, int height)
    {
        var source = t.Role switch
        {
            "icon" => inputs.Icon,
            "screenshot2" => inputs.Screenshot2 ?? inputs.Screenshot,
            _ => inputs.Screenshot,
        };
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        return new ImageLayer
        {
            Source = source,
            X = t.X * width,
            Y = t.Y * height,
            W = t.W * width,
            H = t.H * height,
            Fit = Enum.Parse<ImageFit>(t.Fit, ignoreCase: true),
            Radius = t.Radius * Math.Min(width, height),
            Shadow = t.Shadow && t.Role == "icon" ? new Shadow("#40000000", Math.Min(width, height) * 0.04, 0, Math.Min(width, height) * 0.015) : null,
        };
    }
}
