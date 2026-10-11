using System.Globalization;
using SkiaSharp;

namespace SSGen.Scene;

/// <summary>Colour parsing/formatting and WCAG contrast helpers.</summary>
public static class Colors
{
    public const string White = "#FFFFFF";

    /// <summary>Near-black used for text on light backgrounds (softer than pure black).</summary>
    public const string Ink = "#111827";

    /// <summary>Parses #RGB, #RRGGBB or #AARRGGBB. Invalid input becomes magenta, so mistakes are visible.</summary>
    public static SKColor Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return SKColors.Magenta;
        }

        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3)
        {
            s = string.Concat(s.Select(c => $"{c}{c}"));
        }

        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
        {
            return SKColors.Magenta;
        }

        return s.Length switch
        {
            6 => new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v),
            8 => new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v, (byte)(v >> 24)),
            _ => SKColors.Magenta,
        };
    }

    public static string ToHex(SKColor c) =>
        c.Alpha == 255 ? $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}" : $"#{c.Alpha:X2}{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

    /// <summary>WCAG relative luminance (0 = black, 1 = white).</summary>
    public static double Luminance(SKColor c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.Red)) + (0.7152 * Channel(c.Green)) + (0.0722 * Channel(c.Blue));
    }

    /// <summary>WCAG contrast ratio, 1 to 21.</summary>
    public static double Contrast(SKColor a, SKColor b)
    {
        var (l1, l2) = (Luminance(a), Luminance(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    /// <summary>White or ink, whichever reads better on <paramref name="background"/>.</summary>
    public static string TextOn(SKColor background) =>
        Contrast(background, Parse(White)) >= Contrast(background, Parse(Ink)) ? White : Ink;

    /// <summary>Average of several colours (e.g. the stops of a gradient).</summary>
    public static SKColor Average(IEnumerable<SKColor> colors)
    {
        var list = colors.ToList();
        if (list.Count == 0)
        {
            return SKColors.White;
        }

        return new SKColor(
            (byte)list.Average(c => c.Red),
            (byte)list.Average(c => c.Green),
            (byte)list.Average(c => c.Blue));
    }

    /// <summary>Mixes towards white (amount &gt; 0) or black (amount &lt; 0), amount in -1..1.</summary>
    public static SKColor Shade(SKColor c, double amount)
    {
        byte Mix(byte v) => (byte)Math.Clamp(amount >= 0 ? v + ((255 - v) * amount) : v * (1 + amount), 0, 255);
        return new SKColor(Mix(c.Red), Mix(c.Green), Mix(c.Blue), c.Alpha);
    }

    public static SKColor WithAlpha(SKColor c, double alpha) => c.WithAlpha((byte)Math.Clamp(alpha * 255, 0, 255));
}
