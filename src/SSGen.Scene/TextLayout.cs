using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace SSGen.Scene;

/// <summary>A laid-out text block: the font size chosen and the lines at that size.</summary>
public sealed record TextBlock(float FontSize, IReadOnlyList<string> Lines, float LineAdvance, bool Truncated)
{
    public float Height => Lines.Count == 0 ? 0 : LineAdvance * Lines.Count;
}

/// <summary>Word wrapping and shrink-to-fit for <see cref="TextLayer"/>.</summary>
public static class TextLayout
{
    /// <summary>
    /// Largest size between <see cref="TextLayer.MinSize"/> and <see cref="TextLayer.Size"/> at which the text wraps
    /// into the box within <see cref="TextLayer.MaxLines"/>; at the minimum size, extra text is cut with "…".
    /// </summary>
    public static TextBlock Fit(TextLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var face = Face(layer);
        var max = (float)Math.Max(1, layer.Size);
        var min = (float)Math.Clamp(layer.MinSize, 1, max);
        for (var size = max; size >= min; size -= Math.Max(1, size * 0.04f))
        {
            var block = Wrap(layer, face, size);
            if (!block.Truncated && block.Lines.Count <= layer.MaxLines && block.Height <= layer.H + (size * 0.25f))
            {
                return block;
            }
        }

        return Truncate(Wrap(layer, face, min), layer, face, min);
    }

    public static SKFont Font(TextLayer layer, float size) => new(Face(layer), size) { Subpixel = true, Edging = SKFontEdging.Antialias };

    /// <summary>The layer's font, or a fallback that has every character (other scripts).</summary>
    public static SKTypeface Face(TextLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return Fonts.ForText(layer.Font, layer.Weight, layer.Text);
    }

    public static float Measure(SKFont font, string text, double letterSpacing)
    {
        ArgumentNullException.ThrowIfNull(font);
        return NeedsShaping(text)
            ? Shaper(font.Typeface).Shape(text, font).Width
            : font.MeasureText(text) + (float)(letterSpacing * Math.Max(0, text.Length - 1));
    }

    /// <summary>
    /// Scripts whose letters join or reorder (Arabic, Urdu, Persian, Hebrew, Indic, Thai and others) and emoji
    /// must go through HarfBuzz; plain Latin, Greek and Cyrillic don't need it.
    /// </summary>
    public static bool NeedsShaping(string text)
    {
        foreach (var c in text ?? "")
        {
            if (c is (>= '\u0590' and <= '\u08FF') or (>= '\u0900' and <= '\u0DFF') or (>= '\u0E00' and <= '\u0FFF')
                or (>= '\u1000' and <= '\u109F') or (>= '\u1780' and <= '\u17FF') or (>= '\uFB1D' and <= '\uFDFF')
                or (>= '\uFE70' and <= '\uFEFF') || char.IsSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One HarfBuzz shaper per typeface and thread (shapers hold native state).</summary>
    [ThreadStatic]
    private static Dictionary<SKTypeface, SKShaper>? _shapers;

    public static SKShaper Shaper(SKTypeface face)
    {
        _shapers ??= [];
        if (!_shapers.TryGetValue(face, out var shaper))
        {
            shaper = new SKShaper(face);
            _shapers[face] = shaper;
        }

        return shaper;
    }

    private static TextBlock Wrap(TextLayer layer, SKTypeface face, float size)
    {
        using var font = new SKFont(face, size);
        var width = (float)layer.W;
        var lines = new List<string>();
        foreach (var paragraph in layer.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var current = "";
            foreach (var (word, spaced) in Units(paragraph))
            {
                var candidate = current.Length == 0 ? word : current + (spaced ? " " : "") + word;
                if (Measure(font, candidate, layer.LetterSpacing) <= width || current.Length == 0)
                {
                    current = candidate;
                }
                else
                {
                    lines.Add(current);
                    current = word;
                }
            }

            lines.Add(current);
        }

        // A single word wider than the box can't wrap: that size doesn't fit.
        var tooWide = lines.Any(l => Measure(font, l, layer.LetterSpacing) > width * 1.001f);
        return new TextBlock(size, lines, size * (float)layer.LineHeight, tooWide);
    }

    /// <summary>
    /// Wrapping units: words separated by spaces, except in scripts written without spaces (Chinese, Japanese,
    /// Thai), where any character may start a new line. "Spaced" = a space goes before this unit.
    /// </summary>
    private static IEnumerable<(string Text, bool Spaced)> Units(string paragraph)
    {
        foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!word.Any(IsUnspacedScript))
            {
                yield return (word, true);
                continue;
            }

            for (var i = 0; i < word.Length; i += char.IsSurrogatePair(word, i) ? 2 : 1)
            {
                yield return (word.Substring(i, char.IsSurrogatePair(word, i) ? 2 : 1), i == 0);
            }
        }
    }

    private static bool IsUnspacedScript(char c) =>
        c is (>= '฀' and <= '໿') or (>= '぀' and <= 'ヿ') or (>= '㐀' and <= '鿿') or (>= '豈' and <= '﫿') or (>= '＀' and <= '￯');

    private static TextBlock Truncate(TextBlock block, TextLayer layer, SKTypeface face, float size)
    {
        using var font = new SKFont(face, size);
        var maxLines = Math.Max(1, Math.Min(layer.MaxLines, (int)Math.Floor((layer.H + (size * 0.25f)) / block.LineAdvance)));
        var lines = block.Lines.Take(maxLines).ToList();
        var cut = block.Lines.Count > maxLines;
        for (var i = 0; i < lines.Count; i++)
        {
            var isLast = i == lines.Count - 1;
            var line = lines[i];
            if (Measure(font, line, layer.LetterSpacing) <= layer.W && !(isLast && cut))
            {
                continue;
            }

            while (line.Length > 1 && Measure(font, line + "…", layer.LetterSpacing) > layer.W)
            {
                line = line[..^1];
            }

            lines[i] = line.TrimEnd() + "…";
        }

        return new TextBlock(size, lines, block.LineAdvance, true);
    }
}
