using System.Numerics;
using SkiaSharp;

namespace SSGen.Agent.Observation;

/// <summary>
/// What identifies a screen for de-duplication: a perceptual hash of the pixels (status bar excluded, so the clock
/// doesn't make every frame "new"), a text-free structural signature of the interactive layout (so the same screen
/// with different data still matches), the selected/checked items (so sibling tabs stay distinct) and the
/// screen's heading.
/// </summary>
public sealed record ScreenFingerprint(string? Activity, ulong DHash, IReadOnlySet<string> Structure, IReadOnlySet<string> Selection, string? Title = null)
{
    public static ScreenFingerprint From(string? activity, byte[] screenshotPng, UiSnapshot ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        return new ScreenFingerprint(activity, DifferenceHash(screenshotPng), StructureOf(ui), SelectionOf(ui), ui.Title);
    }

    /// <summary>
    /// Layout tokens: element type + resource name + coarse position band. Elements without a resource name
    /// (Jetpack Compose and other UI toolkits that don't set ids, where nearly everything is a "View") add the
    /// short label of tappable controls instead, or every screen would look alike.
    /// </summary>
    internal static HashSet<string> StructureOf(UiSnapshot ui) =>
        ui.Elements.Select(e => $"{e.ShortClass}|{e.ResourceName}|{(e.Scrollable ? "s" : "")}|{e.Bounds.Top / 400}{ControlLabel(e)}")
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>"|Save" for an id-less tappable control with a short label; empty otherwise (data rows vary).</summary>
    private static string ControlLabel(UiElement e)
    {
        if (!string.IsNullOrEmpty(e.ResourceName) || e.Editable || !(e.Clickable || e.Checkable))
        {
            return "";
        }

        var label = e.Label.Trim();
        return label.Length is > 0 and <= 30 && label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4 ? "|" + label.ToLowerInvariant() : "";
    }

    internal static HashSet<string> SelectionOf(UiSnapshot ui) =>
        ui.Elements.Where(e => e.Selected || e.Checked).Select(e => $"{e.ResourceName}|{e.Label}").ToHashSet(StringComparer.Ordinal);

    /// <summary>64-bit dHash of the image below the status bar (top 6% cropped).</summary>
    public static ulong DifferenceHash(byte[] png)
    {
        using var bitmap = SKBitmap.Decode(png);
        if (bitmap is null)
        {
            return 0;
        }

        var top = (int)(bitmap.Height * 0.06);
        using var cropped = new SKBitmap(bitmap.Width, bitmap.Height - top);
        bitmap.ExtractSubset(cropped, new SKRectI(0, top, bitmap.Width, bitmap.Height));
        using var small = cropped.Resize(new SKImageInfo(9, 8, SKColorType.Gray8), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var left = small.GetPixel(x, y).Red;
                var right = small.GetPixel(x + 1, y).Red;
                if (left > right)
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }

    public static int Hamming(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    public static double Jaccard(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count == 0 && b.Count == 0)
        {
            return 1;
        }

        var inter = a.Count(b.Contains);
        return (double)inter / (a.Count + b.Count - inter);
    }

    /// <summary>
    /// Same screen if the activity matches and either the pixels are near-identical with a similar element set, or
    /// the interactive structure is very similar with the same selection (same screen, different data).
    /// </summary>
    public bool Matches(ScreenFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(Activity, other.Activity, StringComparison.Ordinal))
        {
            return false;
        }

        // Different headings ("My Tasks" and "Edit Profile") are different screens, whatever the layout says.
        if (!string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(other.Title) &&
            !string.Equals(Title.Trim(), other.Title.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hamming = Hamming(DHash, other.DHash);

        // Screens without a usable UI tree (games, webviews) can only be compared by pixels.
        if (Structure.Count < 3 || other.Structure.Count < 3)
        {
            return hamming <= 10;
        }

        var jaccard = Jaccard(Structure, other.Structure);

        // Near-identical pixels still need a broadly similar element set: a small popup menu or dialog barely
        // changes the image but replaces what can be tapped, and is a screen of its own.
        if (hamming <= 6)
        {
            return jaccard >= 0.6;
        }

        return jaccard >= 0.85 && Selection.SetEquals(other.Selection);
    }
}
