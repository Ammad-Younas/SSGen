using System.Collections.Concurrent;
using SkiaSharp;

namespace SSGen.Scene;

/// <summary>
/// Finds typefaces: bundled open-licence fonts first (files named Family-Weight.ttf, e.g. Poppins-Bold.ttf, in a
/// "fonts" folder next to the app or in third_party/fonts), then installed Windows fonts, then Segoe UI.
/// </summary>
public static class Fonts
{
    private static readonly ConcurrentDictionary<(string, int), SKTypeface> Cache = new();

    /// <summary>Weight names used in static font file names (OpenType usWeightClass).</summary>
    private static readonly (int Weight, string Name)[] WeightNames =
    [
        (100, "Thin"), (200, "ExtraLight"), (300, "Light"), (400, "Regular"), (500, "Medium"),
        (600, "SemiBold"), (700, "Bold"), (800, "ExtraBold"), (900, "Black"),
    ];

    /// <summary>Extra folders to search (tests, the app's own folder).</summary>
    public static List<string> SearchFolders { get; } = DefaultFolders();

    public static SKTypeface Get(string family, int weight) =>
        Cache.GetOrAdd((family, weight), key => Load(key.Item1, key.Item2));

    /// <summary>
    /// The family's typeface if it has glyphs for all of <paramref name="text"/>; otherwise an installed font that
    /// does (Poppins has no Arabic or Urdu letters, for example; Windows finds Segoe UI or a Naskh font).
    /// </summary>
    public static SKTypeface ForText(string family, int weight, string text)
    {
        var face = Get(family, weight);
        if (string.IsNullOrEmpty(text) || Covers(face, text))
        {
            return face;
        }

        var missing = FirstMissing(face, text);
        return Cache.GetOrAdd(("fallback:" + char.ConvertFromUtf32(missing), weight), _ =>
        {
            var style = new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            return SKFontManager.Default.MatchCharacter(null, style, null, missing) ?? face;
        });
    }

    /// <summary>Whether the typeface has a glyph for every non-space character.</summary>
    public static bool Covers(SKTypeface face, string text) => FirstMissing(face, text) == 0;

    private static int FirstMissing(SKTypeface face, string text)
    {
        ArgumentNullException.ThrowIfNull(face);
        using var font = new SKFont(face);
        for (var i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            var cp = char.ConvertToUtf32(text, i);
            if (cp > 0x20 && !char.IsWhiteSpace(text, i) && cp != 0x2026 && font.GetGlyph(cp) == 0)
            {
                return cp;
            }
        }

        return 0;
    }

    /// <summary>Families available as bundled files (for pickers).</summary>
    public static IReadOnlyList<string> BundledFamilies() =>
        SearchFolders.Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.ttf"))
            .Select(f => Path.GetFileNameWithoutExtension(f).Split('-')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static SKTypeface Load(string family, int weight)
    {
        // Nearest available weight file: exact, then heavier, then lighter.
        foreach (var (w, name) in WeightNames.OrderBy(x => Math.Abs(x.Weight - weight)).ThenByDescending(x => x.Weight))
        {
            foreach (var folder in SearchFolders)
            {
                var file = Path.Combine(folder, $"{family}-{name}.ttf");
                if (File.Exists(file) && SKTypeface.FromFile(file) is { } face)
                {
                    return face;
                }
            }
        }

        var style = new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        var installed = SKTypeface.FromFamilyName(family, style);
        if (installed is not null && string.Equals(installed.FamilyName, family, StringComparison.OrdinalIgnoreCase))
        {
            return installed;
        }

        return SKTypeface.FromFamilyName("Segoe UI", style) ?? SKTypeface.Default;
    }

    private static List<string> DefaultFolders()
    {
        var folders = new List<string> { Path.Combine(AppContext.BaseDirectory, "fonts") };

        // Development: <repo>/third_party/fonts, found by walking up from the binaries.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "third_party", "fonts");
            if (Directory.Exists(candidate))
            {
                folders.Add(candidate);
                break;
            }
        }

        return folders;
    }
}
