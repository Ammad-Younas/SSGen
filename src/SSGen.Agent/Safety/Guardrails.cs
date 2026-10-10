using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SSGen.Agent.Observation;
using SSGen.Agent.Strategies;

namespace SSGen.Agent.Safety;

public enum GuardVerdict
{
    Allowed,

    /// <summary>Safe mode: never performed.</summary>
    Blocked,

    /// <summary>Safe mode off: only performed if the user confirms.</summary>
    NeedsConfirmation,
}

public sealed record GuardResult(GuardVerdict Verdict, string? Reason = null)
{
    public static readonly GuardResult Allow = new(GuardVerdict.Allowed);
}

/// <summary>
/// Deterministic safety layer applied to every action before it runs, independent of the model:
/// keyword blocklist (several languages) on the target's text, description and resource id; confirmation buttons
/// on screens that mention a dangerous operation; payment-card-like typed text; and the model's own "high" risk flag.
/// </summary>
public sealed partial class Guardrails
{
    /// <summary>Built-in phrases (lower-case). Matched on word boundaries.</summary>
    public static readonly IReadOnlyList<string> DefaultBlocklist =
    [
        // money
        "buy", "purchase", "pay", "pay now", "payment", "checkout", "check out", "place order", "confirm order", "order now",
        "subscribe", "start free trial", "start trial", "add to cart", "donate", "transfer", "withdraw", "top up", "redeem",
        // destructive
        "delete", "delete account", "remove account", "close account", "deactivate", "erase", "wipe", "factory reset",
        "reset all", "uninstall", "clear data", "clear all",
        // outbound communication / public posting
        "send", "post", "publish", "share", "tweet", "reply", "comment", "invite", "call", "dial", "report",
        // session
        "sign out", "log out", "logout",
        // German, Spanish, French, Portuguese, Italian
        "kaufen", "bezahlen", "löschen", "senden", "abonnieren",
        "comprar", "pagar", "eliminar", "borrar", "enviar", "suscribirse",
        "acheter", "payer", "supprimer", "envoyer", "s'abonner",
        "excluir", "assinar", "acquista", "paga", "elimina", "invia",
    ];

    /// <summary>Generic "go ahead" buttons that are dangerous when the screen is about one of the above.</summary>
    private static readonly string[] ConfirmWords = ["ok", "yes", "confirm", "continue", "proceed", "agree", "i understand", "done"];

    private readonly List<Regex> _patterns;

    public Guardrails(bool safeMode, IEnumerable<string>? extraPhrases = null)
    {
        SafeMode = safeMode;
        Phrases = DefaultBlocklist.Concat((extraPhrases ?? []).Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 1))
            .Distinct().ToList();
        _patterns = Phrases.Select(p => new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(p)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
    }

    public bool SafeMode { get; }

    public IReadOnlyList<string> Phrases { get; }

    public static IEnumerable<string> ParseExtra(string? lines) =>
        (lines ?? "").Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"\b(?:\d[ -]?){13,19}\b")]
    private static partial Regex CardNumber();

    public GuardResult Check(AgentDecision decision, UiSnapshot screen)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(screen);
        var action = decision.Action;
        if (action.Kind is ActionKind.Back or ActionKind.Wait or ActionKind.Stop or ActionKind.Scroll)
        {
            return GuardResult.Allow;
        }

        string? reason = null;
        if (action.Element is { } el)
        {
            var target = Normalize($"{el.Text} {el.Description} {el.ChildText} {Humanize(el.ResourceName)}");
            if (MatchPhrase(target) is { } phrase)
            {
                reason = $"'{el.Label}' matches the blocked phrase \"{phrase}\"";
            }
            else if (ConfirmWords.Contains(Normalize(el.Label)) && MatchPhrase(Normalize(string.Join(" ", screen.AllText))) is { } context)
            {
                reason = $"'{el.Label}' would confirm an action on a screen about \"{context}\"";
            }
        }

        if (reason is null && action.Kind == ActionKind.Type && action.Text is { } text && CardNumber().IsMatch(text))
        {
            reason = "the text looks like a payment card number";
        }

        if (reason is null && decision.Risk == RiskLevel.High)
        {
            reason = "the AI flagged this action as high risk";
        }

        if (reason is null)
        {
            return GuardResult.Allow;
        }

        return new GuardResult(SafeMode ? GuardVerdict.Blocked : GuardVerdict.NeedsConfirmation, reason);
    }

    private string? MatchPhrase(string text)
    {
        for (var i = 0; i < _patterns.Count; i++)
        {
            if (_patterns[i].IsMatch(text))
            {
                return Phrases[i];
            }
        }

        return null;
    }

    /// <summary>"btn_deleteAccount" -> "btn delete account".</summary>
    internal static string Humanize(string id)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < id.Length; i++)
        {
            var c = id[i];
            if (c is '_' or '-' or '.')
            {
                sb.Append(' ');
            }
            else
            {
                if (char.IsUpper(c) && i > 0 && char.IsLower(id[i - 1]))
                {
                    sb.Append(' ');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    private static string Normalize(string s) => s.Trim().ToLower(CultureInfo.InvariantCulture);
}
