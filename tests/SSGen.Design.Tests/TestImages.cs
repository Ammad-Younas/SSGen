using SkiaSharp;

namespace SSGen.Design.Tests;

/// <summary>Synthetic images for rendering tests: an app-like screenshot and an icon.</summary>
internal static class TestImages
{
    public static byte[] Screenshot(SKColor accent, int width = 1080, int height = 2400, int seed = 1)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var c = surface.Canvas;
        c.Clear(SKColors.White);
        using var bar = new SKPaint { Color = accent };
        c.DrawRect(0, 0, width, height * 0.11f, bar);
        using var card = new SKPaint { Color = new SKColor(0xF1, 0xF3, 0xF6) };
        using var line = new SKPaint { Color = new SKColor(0xCB, 0xD2, 0xDB) };
        var rng = new Random(seed);
        for (var i = 0; i < 7; i++)
        {
            var top = (height * 0.14f) + (i * height * 0.115f);
            c.DrawRoundRect(SKRect.Create(width * 0.05f, top, width * 0.9f, height * 0.1f), 24, 24, card);
            c.DrawRoundRect(SKRect.Create(width * 0.09f, top + (height * 0.025f), width * (0.3f + (float)rng.NextDouble() * 0.4f), height * 0.018f), 9, 9, line);
            c.DrawRoundRect(SKRect.Create(width * 0.09f, top + (height * 0.06f), width * 0.5f, height * 0.014f), 7, 7, line);
        }

        using var fab = new SKPaint { Color = accent, IsAntialias = true };
        c.DrawCircle(width * 0.84f, height * 0.92f, width * 0.07f, fab);
        using var img = surface.Snapshot();
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    public static byte[] Icon(SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(512, 512));
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        using var bg = new SKPaint { Color = color, IsAntialias = true };
        c.DrawRoundRect(SKRect.Create(16, 16, 480, 480), 110, 110, bg);
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        c.DrawRoundRect(SKRect.Create(150, 120, 212, 272), 30, 30, white);
        using var img = surface.Snapshot();
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    public static string Save(string folder, string name, byte[] data)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, data);
        return path;
    }
}
