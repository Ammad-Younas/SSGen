using SkiaSharp;

namespace SSGen.Scene;

/// <summary>Brand colours used by templates (#RRGGBB).</summary>
public sealed record BrandPalette(string Primary, string Secondary, string Dark, string Light)
{
    /// <summary>Used when nothing better is known (a calm blue on slate).</summary>
    public static readonly BrandPalette Default = From(new SKColor(0x25, 0x63, 0xEB), new SKColor(0x7C, 0x3A, 0xED));

    public static BrandPalette From(SKColor primary, SKColor secondary) => new(
        Colors.ToHex(primary),
        Colors.ToHex(secondary),
        Colors.ToHex(Colors.Shade(primary, -0.72)),
        Colors.ToHex(Colors.Shade(primary, 0.9)));
}

/// <summary>Extracts a brand palette from an image (normally the app icon).</summary>
public static class PaletteExtractor
{
    private const int Sample = 64;
    private const int HueBins = 24;

    public static BrandPalette FromImage(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        using var bitmap = SKBitmap.Decode(encoded);
        if (bitmap is null)
        {
            return BrandPalette.Default;
        }

        using var small = bitmap.Resize(new SKImageInfo(Sample, Sample, SKColorType.Rgba8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        if (small is null)
        {
            return BrandPalette.Default;
        }

        // Bucket saturated, mid-brightness pixels by hue; weight favours vivid colours.
        var buckets = new (double Weight, double R, double G, double B, int N)[HueBins];
        for (var y = 0; y < Sample; y++)
        {
            for (var x = 0; x < Sample; x++)
            {
                var c = small.GetPixel(x, y);
                if (c.Alpha < 200)
                {
                    continue;
                }

                c.ToHsv(out var h, out var s, out var v);
                if (s < 22 || v < 18 || (v > 94 && s < 14))
                {
                    continue; // grey, black or white
                }

                var bin = (int)(h / 360 * HueBins) % HueBins;
                var w = 0.4 + (s / 100.0);
                ref var b = ref buckets[bin];
                b = (b.Weight + w, b.R + c.Red, b.G + c.Green, b.B + c.Blue, b.N + 1);
            }
        }

        var ranked = buckets.Select((b, i) => (b, i)).Where(x => x.b.N > 0).OrderByDescending(x => x.b.Weight).ToList();
        if (ranked.Count == 0)
        {
            return BrandPalette.Default;
        }

        static SKColor Mean((double Weight, double R, double G, double B, int N) b) => new((byte)(b.R / b.N), (byte)(b.G / b.N), (byte)(b.B / b.N));
        var primary = Mean(ranked[0].b);

        // Second colour: the strongest other hue at least 3 bins (45°) away and reasonably common, else a deeper shade.
        var other = ranked.Skip(1).FirstOrDefault(x => HueDistance(x.i, ranked[0].i) >= 3 && x.b.Weight >= ranked[0].b.Weight * 0.15);
        var secondary = other.b.N > 0 ? Mean(other.b) : Colors.Shade(primary, -0.35);
        return BrandPalette.From(primary, secondary);
    }

    private static int HueDistance(int a, int b)
    {
        var d = Math.Abs(a - b);
        return Math.Min(d, HueBins - d);
    }
}
