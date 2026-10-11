using System.Text.RegularExpressions;

namespace SSGen.Selection;

/// <summary>Why a screen shouldn't be in the store set.</summary>
public static class RejectReason
{
    public const string Blank = "blank";
    public const string Loading = "loading";
    public const string Crash = "crash";
    public const string Permission = "permission";
    public const string Login = "login";
    public const string Keyboard = "keyboard";
    public const string Sensitive = "sensitive";
    public const string Duplicate = "duplicate";
    public const string Error = "error";

    public static string Describe(string? reason) => reason switch
    {
        Blank => "Blank or nearly empty",
        Loading => "Still loading",
        Crash => "Crash or error dialog",
        Permission => "Permission dialog",
        Login => "Sign-in screen",
        Keyboard => "Keyboard covers the screen",
        Sensitive => "Shows personal or sensitive data",
        Duplicate => "Nearly the same as a better screen",
        Error => "Error or empty state",
        null or "" => "",
        var other => other,
    };
}

/// <summary>Where a screen belongs in the story: the hero first, settings last.</summary>
public enum StoryRole
{
    Feature,
    Hero,
    Secondary,
    Settings,
}

/// <summary>The verdict on one screen: score 0..100, a reject reason or none, and suggested captions.</summary>
public sealed record ScreenAssessment(
    long ScreenId,
    double Score,
    string? Reject,
    string Label,
    string Headline,
    string Subheadline,
    string Reasoning,
    StoryRole Role,
    string Source)
{
    public bool Usable => Reject is null;
}

public interface IScreenRanker
{
    string Name { get; }

    Task<IReadOnlyList<ScreenAssessment>> RankAsync(IReadOnlyList<ScreenCandidate> candidates, RankingContext context, CancellationToken ct);
}

/// <summary>What rankers know about the app and the user's wishes.</summary>
public sealed record RankingContext(string AppName, string? Hints = null, int TargetCount = 6);

/// <summary>
/// Ranking without AI: image measurements (how much is on screen, colour) and names (activity, app bar title).
/// Rejects blank, loading, crash, permission, sign-in and keyboard-covered screens and near-duplicates; puts
/// onboarding and settings-like screens last; suggests the screen's title as its headline.
/// </summary>
public sealed partial class HeuristicRanker : IScreenRanker
{
    [GeneratedRegex(@"splash|tutorial|onboard|intro|welcome|walkthrough|launcher", RegexOptions.IgnoreCase)]
    private static partial Regex Onboarding();

    [GeneratedRegex(@"login|log ?in|sign ?in|sign ?up|signup|register|auth|password|otp", RegexOptions.IgnoreCase)]
    private static partial Regex Login();

    [GeneratedRegex(@"setting|preference|about|help|licen[cs]e|privacy|terms|feedback|faq", RegexOptions.IgnoreCase)]
    private static partial Regex SettingsLike();

    [GeneratedRegex(@"permission|grant", RegexOptions.IgnoreCase)]
    private static partial Regex Permission();

    [GeneratedRegex(@"crash|error|stopped|not responding|something went wrong|no internet|offline", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorText();

    [GeneratedRegex(@"main|home|dashboard|feed|overview", RegexOptions.IgnoreCase)]
    private static partial Regex HomeLike();

    public string Name => "Built-in ranking (no AI)";

    public Task<IReadOnlyList<ScreenAssessment>> RankAsync(IReadOnlyList<ScreenCandidate> candidates, RankingContext context, CancellationToken ct) =>
        Task.FromResult(Rank(candidates));

    public static IReadOnlyList<ScreenAssessment> Rank(IReadOnlyList<ScreenCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var activities = candidates.GroupBy(c => c.Screen.Activity ?? "").ToDictionary(g => g.Key, g => g.Count());
        var results = new List<ScreenAssessment>();
        foreach (var c in candidates)
        {
            var names = $"{c.Screen.Activity} {c.Screen.Title}";
            var text = c.Screen.UiText ?? "";
            var m = c.Metrics;
            string? reject = m.IsBlank ? RejectReason.Blank
                : m.BottomKeyboard ? RejectReason.Keyboard
                : Permission().IsMatch(names) ? RejectReason.Permission
                : Login().IsMatch(names) ? RejectReason.Login
                : ErrorText().IsMatch(c.Screen.Title ?? "") ? RejectReason.Error
                : m.Richness < 0.04 ? RejectReason.Blank
                : null;

            var role = Onboarding().IsMatch(names) ? StoryRole.Secondary
                : SettingsLike().IsMatch(names) ? StoryRole.Settings
                : StoryRole.Feature;

            // Score: content richness and colour, a bonus for screens of rarely-seen activities, the home screen, and
            // screens with plenty of text; penalties for onboarding and settings.
            var uniqueness = 1.0 / activities[c.Screen.Activity ?? ""];
            var textAmount = Math.Clamp(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length / 20.0, 0, 1);
            var score = (45 * m.Richness) + (15 * m.Colorfulness) + (15 * uniqueness) + (15 * textAmount)
                        + (HomeLike().IsMatch(names) ? 10 : 0)
                        - (role == StoryRole.Secondary ? 25 : 0)
                        - (role == StoryRole.Settings ? 20 : 0);
            score = Math.Round(Math.Clamp(score, 0, 100), 1);

            var label = c.DisplayName;
            results.Add(new ScreenAssessment(c.Id, score, reject, label, Headline(label), "", Explain(c, reject, role), role, "heuristic"));
        }

        return MarkDuplicates(candidates, results);
    }

    /// <summary>Near-identical screens keep only the best-scoring one.</summary>
    internal static List<ScreenAssessment> MarkDuplicates(IReadOnlyList<ScreenCandidate> candidates, List<ScreenAssessment> results)
    {
        var byId = candidates.ToDictionary(c => c.Id);
        var ordered = results.OrderByDescending(r => r.Score).ToList();
        var kept = new List<ScreenCandidate>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var r = ordered[i];
            if (r.Reject is not null)
            {
                continue;
            }

            var c = byId[r.ScreenId];
            // Duplicate = the same screen: same activity and near-identical pixels (two list screens can share a layout).
            if (kept.Any(k => string.Equals(k.Screen.Activity, c.Screen.Activity, StringComparison.Ordinal) && ScreenMetrics.Hamming(k.Metrics.DHash, c.Metrics.DHash) <= 3))
            {
                ordered[i] = r with { Reject = RejectReason.Duplicate, Reasoning = "Nearly the same as a higher-scored screen." };
            }
            else
            {
                kept.Add(c);
            }
        }

        // Keep the original order of the input.
        var final = ordered.ToDictionary(r => r.ScreenId);
        return results.Select(r => final[r.ScreenId]).ToList();
    }

    /// <summary>A short headline from a screen name: "Manage categories" → "Manage categories".</summary>
    internal static string Headline(string label) => label.Length <= 40 ? label : label[..40].TrimEnd() + "…";

    private static string Explain(ScreenCandidate c, string? reject, StoryRole role)
    {
        if (reject is not null)
        {
            return RejectReason.Describe(reject) + ".";
        }

        var parts = new List<string>
        {
            c.Metrics.Richness switch { > 0.6 => "lots of content", > 0.3 => "some content", _ => "little content" },
        };
        if (role == StoryRole.Settings)
        {
            parts.Add("settings-like, shown last");
        }
        else if (role == StoryRole.Secondary)
        {
            parts.Add("onboarding, low priority");
        }

        return char.ToUpperInvariant(parts[0][0]) + string.Join("; ", parts)[1..] + ".";
    }
}

/// <summary>Picks the set and orders it as a story: hero first, then features, then secondary, settings last.</summary>
public static class StoryPicker
{
    /// <summary>Store limits: Google Play phones 2 to 8, App Store 1 to 10. Default 5 to 8.</summary>
    public static IReadOnlyList<ScreenAssessment> Pick(IReadOnlyList<ScreenAssessment> assessments, int count, int max = 8)
    {
        ArgumentNullException.ThrowIfNull(assessments);
        var n = Math.Clamp(count, 1, max);
        var usable = assessments.Where(a => a.Usable).OrderByDescending(a => a.Score).Take(n).ToList();
        if (usable.Count == 0)
        {
            return [];
        }

        // The best screen that isn't onboarding/settings becomes the hero.
        var hero = usable.FirstOrDefault(a => a.Role is StoryRole.Feature or StoryRole.Hero) ?? usable[0];
        return [hero with { Role = StoryRole.Hero }, .. usable.Where(a => a != hero).OrderBy(a => a.Role).ThenByDescending(a => a.Score)];
    }
}
