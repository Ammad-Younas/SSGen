using System.Globalization;
using SSGen.Ai;
using SSGen.Core.Settings;

namespace SSGen.App.Services;

/// <summary>
/// The checks before any AI work that may cost money or send screenshots away: consent before a paid (cloud)
/// model first sees screenshots (declining means text only), a spending cap asked on first paid use, and a
/// warning when the model's price is unknown (the cap can't be enforced). Local models skip all of it.
/// </summary>
public sealed class AiRunGate
{
    private readonly AiService _ai;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;

    public AiRunGate(AiService ai, ISettingsService settings, IDialogService dialogs)
    {
        _ai = ai;
        _settings = settings;
        _dialogs = dialogs;
    }

    /// <summary>The cost meter and whether to send screenshots, or null if the user cancelled.</summary>
    /// <param name="purpose">What the screenshots are for, in the consent question ("to decide where to tap").</param>
    /// <param name="warn">Receives a warning when the model won't see screenshots.</param>
    /// <param name="textOnly">The task sends no screenshots (translation): no consent question or vision warning.</param>
    public (CostMeter Meter, bool Vision)? Prepare(ResolvedProvider resolved, string purpose, Action<string>? warn = null, bool textOnly = false)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        var vision = resolved.Vision && !textOnly;
        if (!textOnly && vision && resolved.IsPaid && !_settings.Current.CloudScreenshotConsent)
        {
            var allow = _dialogs.Confirm("Send screenshots to the cloud?",
                $"\"{resolved.Profile.Name}\" is a cloud service. {purpose}, SSGen sends it downscaled screenshots of the app under test plus the screen's text.\n\n" +
                "Yes = allow (remembered; change it in Settings).\nNo = continue with text only (no screenshots).");
            if (allow)
            {
                _settings.Save(_settings.Current with { CloudScreenshotConsent = true });
            }
            else
            {
                vision = false;
            }
        }

        if (!vision && !textOnly)
        {
            warn?.Invoke("The model won't see screenshots (no vision support, or not allowed). It works from the screens' text only, so results will be weaker.");
        }

        if (!resolved.IsPaid)
        {
            return (new CostMeter(false, null, null), vision);
        }

        var price = _ai.Profiles.GetPrice(resolved.Provider.Model);
        var cap = _settings.Current.CostCapUsd;
        if (cap is null)
        {
            var answer = _dialogs.Prompt("Spending cap",
                "This is the first run with a paid provider. Set a spending cap per run in US dollars; work stops when the estimated cost reaches it. You can change it in Settings.",
                "",
                v => decimal.TryParse(v.TrimStart('$'), NumberStyles.Number, CultureInfo.CurrentCulture, out var d) && d > 0 ? null : "Enter an amount greater than 0, e.g. 1.50");
            if (answer is null)
            {
                return null;
            }

            cap = decimal.Parse(answer.TrimStart('$'), NumberStyles.Number, CultureInfo.CurrentCulture);
            _settings.Save(_settings.Current with { CostCapUsd = cap });
        }

        if (price is null && !_dialogs.Confirm("Price unknown",
                $"There's no price for \"{resolved.Provider.Model}\" in Settings > AI cost control, so SSGen can count tokens but can't estimate cost or enforce the ${cap:0.00} cap.\n\nContinue?"))
        {
            return null;
        }

        return (new CostMeter(true, price, cap), vision);
    }
}
