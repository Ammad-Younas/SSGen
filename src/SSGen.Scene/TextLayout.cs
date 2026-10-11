using SkiaSharp;

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
        var face = Fonts.Get(layer.Font, layer.Weight);
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

    public static SKFont Font(TextLayer layer, float size) => new(Fonts.Get(layer.Font, layer.Weight), size) { Subpixel = true, Edging = SKFontEdging.Antialias };

    public static float Measure(SKFont font, string text, double letterSpacing) =>
        font.MeasureText(text) + (float)(letterSpacing * Math.Max(0, text.Length - 1));

    private static TextBlock Wrap(TextLayer layer, SKTypeface face, float size)
    {
        using var font = new SKFont(face, size);
        var width = (float)layer.W;
        var lines = new List<string>();
        foreach (var paragraph in layer.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var current = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
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
