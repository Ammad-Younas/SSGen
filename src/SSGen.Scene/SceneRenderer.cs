using SkiaSharp;

namespace SSGen.Scene;

/// <summary>Resolves an image layer's <c>Source</c> (a file path or an "asset:" key) to a decoded image.</summary>
public interface IAssetResolver
{
    SKImage? Resolve(string source);
}

/// <summary>Resolves sources as file paths (relative ones against a base folder), with an in-memory cache.</summary>
public sealed class FileAssetResolver : IAssetResolver, IDisposable
{
    private readonly string? _baseFolder;
    private readonly Dictionary<string, SKImage?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SKImage> _named = new(StringComparer.Ordinal);

    public FileAssetResolver(string? baseFolder = null) => _baseFolder = baseFolder;

    /// <summary>Registers an in-memory image under a key (e.g. "asset:icon").</summary>
    public void Add(string key, SKImage image) => _named[key] = image;

    public SKImage? Resolve(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        if (_named.TryGetValue(source, out var named))
        {
            return named;
        }

        if (!_cache.TryGetValue(source, out var image))
        {
            var path = Path.IsPathRooted(source) || _baseFolder is null ? source : Path.Combine(_baseFolder, source);
            image = File.Exists(path) ? SKImage.FromEncodedData(path) : null;
            _cache[source] = image;
        }

        return image;
    }

    public void Dispose()
    {
        foreach (var image in _cache.Values.Concat(_named.Values))
        {
            image?.Dispose();
        }

        _cache.Clear();
        _named.Clear();
    }
}

public enum ImageFormat
{
    Png,
    Jpeg,
}

/// <summary>
/// Draws a <see cref="SceneDocument"/> with SkiaSharp. Output is opaque (24-bit, no alpha), which is what both
/// Google Play and the App Store require for screenshots.
/// </summary>
public static class SceneRenderer
{
    public static SKImage Render(SceneDocument scene, IAssetResolver assets, double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(assets);
        var width = Math.Max(1, (int)Math.Round(scene.Width * scale));
        var height = Math.Max(1, (int)Math.Round(scene.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgb888x, SKAlphaType.Opaque))
                            ?? throw new InvalidOperationException($"Couldn't create a {width}×{height} drawing surface.");
        var canvas = surface.Canvas;
        canvas.Scale((float)scale);
        var full = SKRect.Create(scene.Width, scene.Height);
        canvas.Clear(SKColors.White);
        using (var bg = FillPaint(scene.Background, full))
        {
            canvas.DrawRect(full, bg);
        }

        foreach (var layer in scene.Layers.Where(l => l.Visible && l.Opacity > 0))
        {
            DrawLayer(canvas, layer, assets);
        }

        return surface.Snapshot();
    }

    public static byte[] Encode(SKImage image, ImageFormat format, int jpegQuality = 92)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var data = image.Encode(format == ImageFormat.Png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, format == ImageFormat.Png ? 100 : jpegQuality)
                         ?? throw new InvalidOperationException("Couldn't encode the image.");
        return data.ToArray();
    }

    private static void DrawLayer(SKCanvas canvas, Layer layer, IAssetResolver assets)
    {
        var rect = SKRect.Create((float)layer.X, (float)layer.Y, (float)layer.W, (float)layer.H);
        var count = canvas.Save();
        if (layer.Rotation != 0)
        {
            canvas.RotateDegrees((float)layer.Rotation, rect.MidX, rect.MidY);
        }

        if (layer.Opacity < 1)
        {
            using var alpha = new SKPaint { Color = SKColors.Black.WithAlpha((byte)(Math.Clamp(layer.Opacity, 0, 1) * 255)) };
            canvas.SaveLayer(alpha);
        }

        switch (layer)
        {
            case ShapeLayer shape:
                DrawShape(canvas, shape, rect);
                break;
            case ImageLayer image:
                DrawImage(canvas, image, rect, assets);
                break;
            case DeviceLayer device:
                DrawDevice(canvas, device, rect, assets);
                break;
            case TextLayer text:
                DrawText(canvas, text, rect);
                break;
        }

        canvas.RestoreToCount(count);
    }

    // ------------------------------------------------------------------ fills and shadows

    internal static SKPaint FillPaint(Fill fill, SKRect rect)
    {
        var paint = new SKPaint { IsAntialias = true };
        switch (fill)
        {
            case SolidFill s:
                paint.Color = Colors.Parse(s.Color);
                break;
            case LinearGradientFill g when g.Colors.Count > 0:
                {
                    // Gradient across Span×width, shifted by Offset widths (panorama sets).
                    var w = rect.Width * (float)Math.Max(0.01, g.Span);
                    var origin = new SKPoint(rect.Left - (float)(g.Offset * rect.Width), rect.Top);
                    var area = SKRect.Create(origin.X, origin.Y, w, rect.Height);
                    var rad = g.Angle * Math.PI / 180;
                    var (dx, dy) = ((float)Math.Cos(rad), (float)Math.Sin(rad));
                    var half = (Math.Abs(dx * area.Width) + Math.Abs(dy * area.Height)) / 2;
                    var c = new SKPoint(area.MidX, area.MidY);
                    paint.Shader = SKShader.CreateLinearGradient(
                        new SKPoint(c.X - (dx * half), c.Y - (dy * half)),
                        new SKPoint(c.X + (dx * half), c.Y + (dy * half)),
                        g.Colors.Select(Colors.Parse).ToArray(), null, SKShaderTileMode.Clamp);
                    break;
                }

            case RadialGradientFill r when r.Colors.Count > 0:
                paint.Shader = SKShader.CreateRadialGradient(
                    new SKPoint(rect.Left + (float)(r.Cx * rect.Width), rect.Top + (float)(r.Cy * rect.Height)),
                    (float)(r.Radius * Math.Max(rect.Width, rect.Height)),
                    r.Colors.Select(Colors.Parse).ToArray(), null, SKShaderTileMode.Clamp);
                break;
            default:
                paint.Color = SKColors.Magenta;
                break;
        }

        return paint;
    }

    private static void DrawShadow(SKCanvas canvas, Shadow? shadow, SKPath path)
    {
        if (shadow is null)
        {
            return;
        }

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = Colors.Parse(shadow.Color),
            ImageFilter = SKImageFilter.CreateBlur((float)(shadow.Blur / 2), (float)(shadow.Blur / 2)),
        };
        var count = canvas.Save();
        canvas.Translate((float)shadow.OffsetX, (float)shadow.OffsetY);
        canvas.DrawPath(path, paint);
        canvas.RestoreToCount(count);
    }

    // ------------------------------------------------------------------ layers

    private static void DrawShape(SKCanvas canvas, ShapeLayer shape, SKRect rect)
    {
        using var path = shape.Shape == ShapeKind.Ellipse ? OvalPath(rect) : RoundRectPath(rect, (float)shape.Radius);

        DrawShadow(canvas, shape.Shadow, path);
        using var paint = FillPaint(shape.Fill, rect);
        if (shape.Blur > 0)
        {
            paint.ImageFilter = SKImageFilter.CreateBlur((float)shape.Blur, (float)shape.Blur);
        }

        canvas.DrawPath(path, paint);
    }

    private static void DrawImage(SKCanvas canvas, ImageLayer layer, SKRect rect, IAssetResolver assets)
    {
        using var clip = RoundRectPath(rect, (float)layer.Radius);
        DrawShadow(canvas, layer.Shadow, clip);
        if (assets.Resolve(layer.Source) is not { } image)
        {
            DrawMissing(canvas, rect);
            return;
        }

        var count = canvas.Save();
        canvas.ClipPath(clip, antialias: true);
        canvas.DrawImage(image, FitRect(image.Width, image.Height, rect, layer.Fit), new SKSamplingOptions(SKCubicResampler.Mitchell));
        canvas.RestoreToCount(count);
    }

    private static void DrawDevice(SKCanvas canvas, DeviceLayer device, SKRect body, IAssetResolver assets)
    {
        var frame = device.Frame;
        var bodyRadius = DeviceGeometry.BodyRadius(frame, body);
        var (screen, screenRadius) = DeviceGeometry.Screen(frame, body);

        using var bodyPath = RoundRectPath(body, bodyRadius);
        DrawShadow(canvas, device.Shadow, bodyPath);

        if (frame != DeviceFrame.None)
        {
            var color = Colors.Parse(device.FrameColor);

            // Side buttons, drawn first so the body overlaps their inner half.
            using (var button = new SKPaint { IsAntialias = true, Color = Colors.Shade(color, 0.12) })
            {
                var bw = body.Width * 0.012f;
                var x = body.Right - (bw / 2);
                var top = body.Top + (body.Height * (frame == DeviceFrame.Tablet ? 0.08f : 0.2f));
                canvas.DrawRoundRect(SKRect.Create(x, top, bw * 1.5f, body.Height * 0.09f), bw, bw, button);
                if (frame != DeviceFrame.Tablet)
                {
                    canvas.DrawRoundRect(SKRect.Create(x, top + (body.Height * 0.12f), bw * 1.5f, body.Height * 0.06f), bw, bw, button);
                }
            }

            using var bodyPaint = new SKPaint { IsAntialias = true, Color = color };
            canvas.DrawPath(bodyPath, bodyPaint);

            // A thin lighter rim gives the edge definition on dark backgrounds.
            using var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, body.Width * 0.006f), Color = Colors.Shade(color, 0.22) };
            var inset = rim.StrokeWidth / 2;
            canvas.DrawRoundRect(SKRect.Inflate(body, -inset, -inset), bodyRadius - inset, bodyRadius - inset, rim);
        }

        // Screen: the screenshot, cover-fitted and clipped to the rounded screen.
        using var screenPath = RoundRectPath(screen, screenRadius);
        var count = canvas.Save();
        canvas.ClipPath(screenPath, antialias: true);
        canvas.DrawRect(screen, new SKPaint { Color = SKColors.Black });
        if (assets.Resolve(device.Source) is { } shot)
        {
            canvas.DrawImage(shot, FitRect(shot.Width, shot.Height, screen, ImageFit.Cover), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        else
        {
            DrawMissing(canvas, screen);
        }

        canvas.RestoreToCount(count);

        // Camera cut-outs (over the screenshot, as on a real phone).
        var (_, _, camera) = DeviceGeometry.Proportions(frame);
        using var cam = new SKPaint { IsAntialias = true, Color = new SKColor(8, 8, 10) };
        var d = (float)(camera * body.Width);
        switch (frame)
        {
            case DeviceFrame.PhonePunchHole:
                canvas.DrawCircle(screen.MidX, screen.Top + (d * 1.1f), d / 2, cam);
                break;
            case DeviceFrame.PhoneIsland:
                {
                    var pill = SKRect.Create(screen.MidX - (body.Width * 0.15f), screen.Top + (d * 0.55f), body.Width * 0.30f, d * 1.05f);
                    canvas.DrawRoundRect(pill, pill.Height / 2, pill.Height / 2, cam);
                    break;
                }

            case DeviceFrame.Tablet:
                canvas.DrawCircle(body.MidX, body.Top + ((screen.Top - body.Top) / 2), d / 2, new SKPaint { IsAntialias = true, Color = Colors.Shade(Colors.Parse(device.FrameColor), 0.25) });
                break;
        }
    }

    private static void DrawText(SKCanvas canvas, TextLayer layer, SKRect rect)
    {
        if (string.IsNullOrWhiteSpace(layer.Text))
        {
            return;
        }

        var block = TextLayout.Fit(layer);
        using var font = TextLayout.Font(layer, block.FontSize);
        using var paint = new SKPaint { IsAntialias = true, Color = Colors.Parse(layer.Color) };
        var metrics = font.Metrics;
        var top = layer.VerticalAlign switch
        {
            VerticalAlign.Middle => rect.MidY - (block.Height / 2),
            VerticalAlign.Bottom => rect.Bottom - block.Height,
            _ => rect.Top,
        };

        var align = layer.RightToLeft
            ? layer.Align switch { TextAlign.Left => TextAlign.Right, TextAlign.Right => TextAlign.Left, var a => a }
            : layer.Align;

        using var shadowPaint = layer.Shadow is { } s
            ? new SKPaint { IsAntialias = true, Color = Colors.Parse(s.Color), ImageFilter = SKImageFilter.CreateBlur((float)(s.Blur / 2), (float)(s.Blur / 2)) }
            : null;

        for (var i = 0; i < block.Lines.Count; i++)
        {
            var line = block.Lines[i];
            var width = TextLayout.Measure(font, line, layer.LetterSpacing);
            var x = align switch
            {
                TextAlign.Center => rect.MidX - (width / 2),
                TextAlign.Right => rect.Right - width,
                _ => rect.Left,
            };

            // Baseline: line top + ascent, with the extra line height split above and below.
            var baseline = top + (i * block.LineAdvance) + ((block.LineAdvance - (metrics.Descent - metrics.Ascent)) / 2) - metrics.Ascent;
            if (shadowPaint is not null)
            {
                DrawLine(canvas, line, x + (float)layer.Shadow!.OffsetX, baseline + (float)layer.Shadow.OffsetY, font, shadowPaint, layer.LetterSpacing);
            }

            DrawLine(canvas, line, x, baseline, font, paint, layer.LetterSpacing);
        }
    }

    private static void DrawLine(SKCanvas canvas, string text, float x, float baseline, SKFont font, SKPaint paint, double letterSpacing)
    {
        if (letterSpacing == 0)
        {
            canvas.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);
            return;
        }

        foreach (var ch in text)
        {
            var s = ch.ToString();
            canvas.DrawText(s, x, baseline, SKTextAlign.Left, font, paint);
            x += font.MeasureText(s) + (float)letterSpacing;
        }
    }

    internal static SKPath RoundRectPath(SKRect rect, float radius)
    {
        using var builder = new SKPathBuilder();
        builder.AddRoundRect(rect, radius, radius, SKPathDirection.Clockwise);
        return builder.Detach();
    }

    internal static SKPath OvalPath(SKRect rect)
    {
        using var builder = new SKPathBuilder();
        builder.AddOval(rect, SKPathDirection.Clockwise);
        return builder.Detach();
    }

    private static void DrawMissing(SKCanvas canvas, SKRect rect)
    {
        using var paint = new SKPaint { Color = new SKColor(0xE5, 0xE7, 0xEB) };
        canvas.DrawRect(rect, paint);
    }

    /// <summary>Where to draw an image of the given size into a box.</summary>
    internal static SKRect FitRect(int imageW, int imageH, SKRect box, ImageFit fit)
    {
        if (fit == ImageFit.Stretch || imageW <= 0 || imageH <= 0)
        {
            return box;
        }

        var scale = fit == ImageFit.Cover
            ? Math.Max(box.Width / imageW, box.Height / imageH)
            : Math.Min(box.Width / imageW, box.Height / imageH);
        var w = imageW * scale;
        var h = imageH * scale;

        // Cover keeps the top of the image (status bar and app bar matter more than the bottom).
        var y = fit == ImageFit.Cover ? box.Top : box.MidY - (h / 2);
        return SKRect.Create(box.MidX - (w / 2), y, w, h);
    }
}
