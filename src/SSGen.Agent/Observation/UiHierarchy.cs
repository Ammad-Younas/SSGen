using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SSGen.Agent.Observation;

public readonly record struct UiBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public int CenterX => (Left + Right) / 2;
    public int CenterY => (Top + Bottom) / 2;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"[{Left},{Top}][{Right},{Bottom}]");
}

/// <summary>An element the agent can act on, with a short id used in prompts ("[7] Button 'Workouts'").</summary>
public sealed record UiElement
{
    public int Id { get; init; }
    public required string Class { get; init; }
    public string Text { get; init; } = "";
    public string Description { get; init; } = "";
    public string ResourceId { get; init; } = "";
    public string Package { get; init; } = "";
    public UiBounds Bounds { get; init; }
    public bool Clickable { get; init; }
    public bool LongClickable { get; init; }
    public bool Scrollable { get; init; }
    public bool Checkable { get; init; }
    public bool Checked { get; init; }
    public bool Selected { get; init; }
    public bool Password { get; init; }

    /// <summary>Hint of a text field when the dump reports it (empty fields usually show their hint as text).</summary>
    public string Hint { get; init; } = "";

    /// <summary>Text gathered from non-interactive descendants (e.g. a list row's title).</summary>
    public string ChildText { get; init; } = "";

    public bool Editable => Class.EndsWith("EditText", StringComparison.Ordinal) || Class.EndsWith("AutoCompleteTextView", StringComparison.Ordinal);

    public string ShortClass => Class.Contains('.', StringComparison.Ordinal) ? Class[(Class.LastIndexOf('.') + 1)..] : Class;

    public string ResourceName => ResourceId.Contains('/', StringComparison.Ordinal) ? ResourceId[(ResourceId.IndexOf('/', StringComparison.Ordinal) + 1)..] : ResourceId;

    /// <summary>Best human label: text, then content description, then descendant text, then resource name.</summary>
    public string Label
    {
        get
        {
            foreach (var s in new[] { Text, Description, ChildText, ResourceName.Replace('_', ' ') })
            {
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s.Length > 60 ? s[..60] + "…" : s.Trim();
                }
            }

            return "";
        }
    }

    /// <summary>
    /// Stable identity across visits (ignores data-dependent text for list rows with resource ids). Text fields are
    /// identified by id and position, because their text changes as soon as something is typed.
    /// </summary>
    public string Key => Editable
        ? $"{ResourceName}|{ShortClass}|field@{Bounds.Left / 40},{Bounds.Top / 40}"
        : $"{ResourceName}|{ShortClass}|{(string.IsNullOrEmpty(ResourceName) ? Label : Label.Length > 24 ? Label[..24] : Label)}";

    /// <summary>True when a text field holds nothing typed yet (empty, or only showing its hint).</summary>
    public bool IsEmptyField => Editable && (Text.Length == 0 || (Hint.Length > 0 && Text == Hint)); // without a hint attribute the agent tracks what it typed itself

    public override string ToString()
    {
        var flags = new List<string>();
        if (Editable)
        {
            flags.Add(Password ? "password field" : "text field");
        }

        if (Scrollable)
        {
            flags.Add("scrollable");
        }

        if (Checkable)
        {
            flags.Add(Checked ? "checked" : "unchecked");
        }

        if (Selected)
        {
            flags.Add("selected");
        }

        var extra = flags.Count > 0 ? $" ({string.Join(", ", flags)})" : "";
        return $"[{Id}] {ShortClass} '{Label}'{extra}";
    }
}

/// <summary>A parsed uiautomator dump: interactive elements in reading order plus all visible text.</summary>
/// <param name="HasSpinner">A visible indeterminate progress indicator (loading).</param>
/// <param name="Title">The app bar title (first text in the top band of the screen), when there is one.</param>
public sealed record UiSnapshot(IReadOnlyList<UiElement> Elements, IReadOnlyList<string> AllText, string? RootPackage, bool HasSpinner = false, string? Title = null)
{
    public static readonly UiSnapshot Empty = new([], [], null);

    public UiElement? Find(int id) => Elements.FirstOrDefault(e => e.Id == id);
}

public static partial class UiHierarchyParser
{
    [GeneratedRegex(@"\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\]")]
    private static partial Regex BoundsPattern();

    private static readonly string[] IgnoredPackages = ["com.android.systemui"];

    /// <summary>Parses `uiautomator dump` XML. Malformed or empty input yields <see cref="UiSnapshot.Empty"/>.</summary>
    public static UiSnapshot Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return UiSnapshot.Empty;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return UiSnapshot.Empty;
        }

        var raw = new List<UiElement>();
        var texts = new List<string>();
        string? rootPackage = null;
        var hasSpinner = false;
        var headings = new List<(string Text, UiBounds Bounds)>();
        var screenHeight = 0;
        foreach (var node in doc.Descendants("node"))
        {
            var pkg = (string?)node.Attribute("package") ?? "";
            rootPackage ??= string.IsNullOrEmpty(pkg) ? null : pkg;
            if (IgnoredPackages.Contains(pkg))
            {
                continue;
            }

            var text = (string?)node.Attribute("text") ?? "";
            var desc = (string?)node.Attribute("content-desc") ?? "";
            var nodeBounds = ParseBounds((string?)node.Attribute("bounds"));
            screenHeight = Math.Max(screenHeight, nodeBounds.Bottom);
            if (IsHeadingCandidate(node, text))
            {
                headings.Add((text.Trim(), nodeBounds));
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                texts.Add(text.Trim());
            }
            else if (!string.IsNullOrWhiteSpace(desc))
            {
                texts.Add(desc.Trim());
            }

            bool Flag(string name) => (string?)node.Attribute(name) == "true";
            var cls = (string?)node.Attribute("class") ?? "";
            var bounds = ParseBounds((string?)node.Attribute("bounds"));
            if (cls.EndsWith("ProgressBar", StringComparison.Ordinal) && !bounds.IsEmpty && bounds.Width < 400)
            {
                hasSpinner = true; // small = spinner; wide bars are usually determinate progress, not loading
            }
            var interactive = Flag("enabled") && !bounds.IsEmpty &&
                              (Flag("clickable") || Flag("long-clickable") || Flag("checkable") || Flag("scrollable") ||
                               cls.EndsWith("EditText", StringComparison.Ordinal));
            if (!interactive)
            {
                continue;
            }

            // Lists/containers must not borrow a row's text as their own label.
            var childText = Flag("scrollable") ? "" : string.Join(" · ", node.Descendants("node")
                .Where(d => (string?)d.Attribute("clickable") != "true")
                .Select(d => (string?)d.Attribute("text") is { Length: > 0 } t ? t : (string?)d.Attribute("content-desc") ?? "")
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Take(3));

            raw.Add(new UiElement
            {
                Class = cls,
                Text = text.Trim(),
                Description = desc.Trim(),
                ResourceId = (string?)node.Attribute("resource-id") ?? "",
                Package = pkg,
                Bounds = bounds,
                Clickable = Flag("clickable"),
                LongClickable = Flag("long-clickable"),
                Scrollable = Flag("scrollable"),
                Checkable = Flag("checkable"),
                Checked = Flag("checked"),
                Selected = Flag("selected"),
                Password = Flag("password"),
                Hint = (string?)node.Attribute("hint") ?? "",
                ChildText = childText,
            });
        }

        // Drop clickable wrappers that exactly overlap a more specific clickable child (common in Compose/Material).
        var deduped = raw.Where(e => !raw.Any(o => !ReferenceEquals(o, e) && o.Bounds == e.Bounds && o.Clickable == e.Clickable &&
                                                     !string.IsNullOrEmpty(o.Label) && string.IsNullOrEmpty(e.Text) && string.IsNullOrEmpty(e.Description) &&
                                                     raw.IndexOf(o) > raw.IndexOf(e)))
            .OrderBy(e => e.Bounds.Top / 8)
            .ThenBy(e => e.Bounds.Left)
            .Select((e, i) => e with { Id = i + 1 })
            .ToList();

        return new UiSnapshot(deduped, texts, rootPackage, hasSpinner, PickTitle(headings, screenHeight));
    }

    /// <summary>
    /// Text that could name the screen: short, not an input or control, and not inside anything tappable (so
    /// "Skip", "Next" and list rows don't count).
    /// </summary>
    private static bool IsHeadingCandidate(XElement node, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40 || text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 6)
        {
            return false;
        }

        var cls = (string?)node.Attribute("class") ?? "";
        if (cls.EndsWith("EditText", StringComparison.Ordinal) || cls.EndsWith("Button", StringComparison.Ordinal) ||
            cls.EndsWith("CheckBox", StringComparison.Ordinal) || cls.EndsWith("Switch", StringComparison.Ordinal))
        {
            return false;
        }

        return node.AncestorsAndSelf("node").All(n => (string?)n.Attribute("clickable") != "true");
    }

    /// <summary>
    /// The screen's name: the first heading in the app bar band (below the status bar), else the largest heading
    /// text in the upper part of the screen (onboarding and sign-in pages have no app bar). Null when nothing
    /// stands out.
    /// </summary>
    internal static string? PickTitle(IReadOnlyList<(string Text, UiBounds Bounds)> headings, int screenHeight)
    {
        if (headings.Count == 0 || screenHeight <= 0)
        {
            return null;
        }

        var appBar = headings.FirstOrDefault(h => h.Bounds.Top > screenHeight * 0.02 && h.Bounds.Top < screenHeight * 0.14);
        if (appBar.Text is not null)
        {
            return appBar.Text;
        }

        var upper = headings.Where(h => h.Bounds.Top > screenHeight * 0.05 && h.Bounds.Top < screenHeight * 0.75).ToList();
        if (upper.Count == 0)
        {
            return null;
        }

        // Text height stands in for font size: a heading is clearly taller than the typical text on the screen.
        var heights = headings.Select(h => h.Bounds.Height).Order().ToList();
        var median = heights[heights.Count / 2];
        var largest = upper.OrderByDescending(h => h.Bounds.Height).ThenBy(h => h.Bounds.Top).First();
        return largest.Bounds.Height >= median * 1.25 || headings.Count == 1 ? largest.Text : null;
    }

    internal static UiBounds ParseBounds(string? s)
    {
        var m = BoundsPattern().Match(s ?? "");
        if (!m.Success)
        {
            return default;
        }

        int G(int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        return new UiBounds(G(1), G(2), G(3), G(4));
    }
}
