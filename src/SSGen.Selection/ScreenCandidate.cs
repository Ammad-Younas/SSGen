using System.Numerics;
using System.Text.RegularExpressions;
using SkiaSharp;
using SSGen.Core.Projects;

namespace SSGen.Selection;

/// <summary>Image measurements used for ranking without AI.</summary>
/// <param name="Richness">0..1: how much is on screen (edge density).</param>
/// <param name="Colorfulness">0..1 (Hasler-Süsstrunk).</param>
/// <param name="Uniformity">0..1: share of pixels with the most common brightness (1 = blank).</param>
/// <param name="DHash">Perceptual hash (status bar excluded) for spotting near-duplicates.</param>
/// <param name="BottomKeyboard">The lower part looks like a soft keyboard (dense, grey, uniform rows).</param>
public sealed record ScreenMetrics(double Richness, double Colorfulness, double Uniformity, ulong DHash, bool BottomKeyboard)
{
    public bool IsBlank => Uniformity > 0.96;

    public static int Hamming(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    public static ScreenMetrics Analyze(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        using var bitmap = SKBitmap.Decode(encoded);
        if (bitmap is null)
        {
            return new ScreenMetrics(0, 0, 1, 0, false);
        }

        // Ignore the status bar (top 6%): the clock and icons are on every screen.
        var top = (int)(bitmap.Height * 0.06);
        using var body = new SKBitmap(bitmap.Width, bitmap.Height - top);
        bitmap.ExtractSubset(body, new SKRectI(0, top, bitmap.Width, bitmap.Height));
        const int w = 180, h = 380;
        using var small = body.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));

        var luma = new double[w, h];
        var histogram = new int[32];
        double rg = 0, yb = 0, rg2 = 0, yb2 = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = small.GetPixel(x, y);
                var l = (0.299 * c.Red) + (0.587 * c.Green) + (0.114 * c.Blue);
                luma[x, y] = l;
                histogram[(int)l / 8]++;
                var a = c.Red - (double)c.Green;
                var b = (0.5 * (c.Red + c.Green)) - c.Blue;
                rg += a;
                yb += b;
                rg2 += a * a;
                yb2 += b * b;
            }
        }

        var n = w * h;
        double edges = 0;
        for (var y = 1; y < h - 1; y++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                var gx = luma[x + 1, y] - luma[x - 1, y];
                var gy = luma[x, y + 1] - luma[x, y - 1];
                if ((gx * gx) + (gy * gy) > 400)
                {
                    edges++;
                }
            }
        }

        var richness = Math.Clamp(edges / n / 0.18, 0, 1);
        var (mrg, myb) = (rg / n, yb / n);
        var std = Math.Sqrt(Math.Max(0, (rg2 / n) - (mrg * mrg)) + Math.Max(0, (yb2 / n) - (myb * myb)));
        var mean = Math.Sqrt((mrg * mrg) + (myb * myb));
        var colorfulness = Math.Clamp((std + (0.3 * mean)) / 110, 0, 1);
        var uniformity = histogram.Max() / (double)n;

        return new ScreenMetrics(richness, colorfulness, uniformity, DifferenceHash(small), LooksLikeKeyboard(small));
    }

    private static ulong DifferenceHash(SKBitmap image)
    {
        using var tiny = image.Resize(new SKImageInfo(9, 8, SKColorType.Gray8), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                if (tiny.GetPixel(x, y).Red > tiny.GetPixel(x + 1, y).Red)
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }

    /// <summary>Soft keyboards: the bottom ~38% is a mostly grey, low-saturation panel full of small keys.</summary>
    private static bool LooksLikeKeyboard(SKBitmap image)
    {
        var start = (int)(image.Height * 0.62);
        int grey = 0, total = 0;
        for (var y = start; y < image.Height; y += 2)
        {
            for (var x = 0; x < image.Width; x += 2)
            {
                var c = image.GetPixel(x, y);
                c.ToHsv(out _, out var s, out var v);
                total++;
                if (s < 12 && v is > 12 and < 80)
                {
                    grey++;
                }
            }
        }

        return total > 0 && grey / (double)total > 0.7;
    }
}

/// <summary>A captured screen being considered for the store set.</summary>
public sealed record ScreenCandidate(CapturedScreen Screen, string FullPath, ScreenMetrics Metrics)
{
    public long Id => Screen.Id;

    /// <summary>Readable name: the app bar title, else the activity name in words ("ManageCategoriesActivity" → "Manage categories").</summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(Screen.Title) ? Screen.Title.Trim() : Humanize(Screen.Activity);

    internal static string Humanize(string? activity)
    {
        if (string.IsNullOrWhiteSpace(activity))
        {
            return "Screen";
        }

        var name = activity.Split('/').Last().Split('.').Last();
        name = Regex.Replace(name, "(Activity|Fragment|Screen|Page)$", "");
        var words = Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ").Trim();
        return words.Length == 0 ? "Screen" : char.ToUpperInvariant(words[0]) + words[1..].ToLowerInvariant();
    }

    public static ScreenCandidate Load(Project project, CapturedScreen screen)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(screen);
        var path = screen.FullPath(project);
        var metrics = File.Exists(path) ? ScreenMetrics.Analyze(File.ReadAllBytes(path)) : new ScreenMetrics(0, 0, 1, 0, false);
        return new ScreenCandidate(screen, path, metrics);
    }
}
