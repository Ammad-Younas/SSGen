using System.Text.RegularExpressions;

namespace SSGen.Agent.Observation;

public enum InterruptKind
{
    None,

    /// <summary>Android runtime permission request: allowed (while using the app) so features stay reachable.</summary>
    PermissionDialog,

    /// <summary>"App isn't responding": wait.</summary>
    NotResponding,

    /// <summary>"App keeps stopping": close the dialog; counts as a crash.</summary>
    CrashDialog,

    /// <summary>Interstitial or banner ad with a close/skip control.</summary>
    Ad,

    /// <summary>Rating prompt, promo or newsletter popup with a "Not now"-style dismiss button.</summary>
    Popup,

    /// <summary>Only a spinner on screen: wait for content.</summary>
    Loading,

    /// <summary>Sign-in wall (password field, or a small screen that only offers ways to sign in).</summary>
    Login,
}

/// <param name="Target">The element to tap to get past it, when there is one.</param>
public sealed record Interrupt(InterruptKind Kind, UiElement? Target, string Description)
{
    public static readonly Interrupt None = new(InterruptKind.None, null, "");
}

/// <summary>
/// Recognises screens that interrupt exploration and should be handled the same way every time, before any
/// strategy (AI or not) decides. Rules use resource ids where Android has stable ones, and English text otherwise.
/// </summary>
public static partial class ScreenClassifier
{
    // com.android.permissioncontroller ids, most to least preferred ("while using the app" > "only this time").
    private static readonly string[] AllowIds =
        ["permission_allow_foreground_only_button", "permission_allow_one_time_button", "permission_allow_button", "permission_allow_all_button"];

    private static readonly string[] AllowTexts = ["while using the app", "only this time", "allow"];

    private static readonly string[] DismissTexts =
        ["not now", "no thanks", "no, thanks", "maybe later", "remind me later", "later", "dismiss", "close", "skip"];

    [GeneratedRegex(@"\b(rate|rating|review|enjoying|newsletter|subscribe to|special offer|limited time|upgrade to|go premium|try premium|discount)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PromoText();

    [GeneratedRegex(@"\b(sign in|log in|login|sign-in|log-in)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SignInText();

    [GeneratedRegex(@"(ad_close|close_ad|skip_ad|interstitial_close|ad_skip)", RegexOptions.IgnoreCase)]
    private static partial Regex AdCloseId();

    [GeneratedRegex(@"^(close ad|skip ad|skip ads?|interstitial close button|close advertisement)$", RegexOptions.IgnoreCase)]
    private static partial Regex AdCloseLabel();

    public static Interrupt Classify(UiSnapshot ui, string? foregroundPackage, string appPackage)
    {
        ArgumentNullException.ThrowIfNull(ui);
        var package = foregroundPackage ?? ui.RootPackage ?? "";
        var texts = string.Join(" | ", ui.AllText).ToLowerInvariant();

        // 1. Runtime permission dialog (shown by the permission controller, outside the app).
        if (package.Contains("permissioncontroller", StringComparison.Ordinal) || ui.Elements.Any(e => e.ResourceName.StartsWith("permission_allow", StringComparison.Ordinal)))
        {
            var allow = AllowIds.Select(id => ui.Elements.FirstOrDefault(e => e.ResourceName == id)).FirstOrDefault(e => e is not null)
                        ?? AllowTexts.Select(t => ui.Elements.FirstOrDefault(e => Is(e, t))).FirstOrDefault(e => e is not null);
            if (allow is not null)
            {
                return new Interrupt(InterruptKind.PermissionDialog, allow, $"Permission request: allowing ('{allow.Label}') so the app's features stay reachable");
            }
        }

        // 2. System "isn't responding" / "keeps stopping" dialogs.
        if (texts.Contains("isn't responding", StringComparison.Ordinal) || texts.Contains("is not responding", StringComparison.Ordinal) ||
            ui.Elements.Any(e => e.ResourceName == "aerr_wait"))
        {
            var wait = ui.Elements.FirstOrDefault(e => e.ResourceName == "aerr_wait") ?? ui.Elements.FirstOrDefault(e => Is(e, "wait"));
            return new Interrupt(InterruptKind.NotResponding, wait, "The app isn't responding; waiting for it");
        }

        if (texts.Contains("keeps stopping", StringComparison.Ordinal) || texts.Contains("has stopped", StringComparison.Ordinal) ||
            ui.Elements.Any(e => e.ResourceName is "aerr_close" or "aerr_restart"))
        {
            var close = ui.Elements.FirstOrDefault(e => e.ResourceName == "aerr_close") ?? ui.Elements.FirstOrDefault(e => Is(e, "close app") || Is(e, "ok"));
            return new Interrupt(InterruptKind.CrashDialog, close, "The app crashed");
        }

        // The remaining checks are about the app's own screens.
        if (!string.Equals(package, appPackage, StringComparison.Ordinal) && !package.Contains("gms", StringComparison.Ordinal))
        {
            return Interrupt.None;
        }

        // 3. Ads (also served from Google Play services' package).
        var adClose = ui.Elements.FirstOrDefault(e => AdCloseId().IsMatch(e.ResourceName) || AdCloseLabel().IsMatch(e.Label.Trim()));
        if (adClose is not null)
        {
            return new Interrupt(InterruptKind.Ad, adClose, $"Closing an ad ('{adClose.Label}')");
        }

        // 4. Small promo/rating popups with an explicit "not now" option.
        if (ui.Elements.Count <= 5 && PromoText().IsMatch(texts))
        {
            var dismiss = DismissTexts.Select(t => ui.Elements.FirstOrDefault(e => Is(e, t))).FirstOrDefault(e => e is not null);
            if (dismiss is not null)
            {
                return new Interrupt(InterruptKind.Popup, dismiss, $"Dismissing a popup ('{dismiss.Label}')");
            }
        }

        // 5. Login walls.
        if (ui.Elements.Any(e => e.Password) ||
            (ui.Elements.Count <= 6 && SignInText().IsMatch(texts) && ui.Elements.Any(e => e.Editable || SignInText().IsMatch(e.Label))))
        {
            return new Interrupt(InterruptKind.Login, null, "Sign-in screen");
        }

        // 6. Loading: a spinner and (almost) nothing else.
        if (ui.HasSpinner && ui.Elements.Count <= 2)
        {
            return new Interrupt(InterruptKind.Loading, null, "Loading");
        }

        return Interrupt.None;
    }

    private static bool Is(UiElement e, string text) =>
        string.Equals(e.Label.Trim(), text, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Text.Trim(), text, StringComparison.OrdinalIgnoreCase);
}
