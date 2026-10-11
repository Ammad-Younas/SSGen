using System.Text.RegularExpressions;
using SSGen.Agent.Observation;

namespace SSGen.Agent.Strategies;

/// <summary>
/// Plausible sample input for text fields, so explored screens show realistic content (a saved note, a filled
/// form) instead of empty states. Clearly fictional: example.com addresses and 555 numbers.
/// </summary>
public static partial class DemoText
{
    /// <summary>Short titles. None may contain a guardrail word ("buy", "send", "post"…): tapping the saved item later would be blocked.</summary>
    private static readonly string[] General =
    [
        "Weekly groceries", "Plan weekend trip", "Morning run 5 km", "Team meeting notes", "Book club picks",
        "Gift ideas for Sam", "Garden to-do list", "Recipe: lemon pasta",
    ];

    internal const string LongSample = "Milk, eggs and coffee. Pick up flowers on Saturday.";

    public static IReadOnlyList<string> AllSamples => [.. General, LongSample, "alex@example.com", "5550100", "example.com", "plan", "12", "alexmorgan"];

    [GeneratedRegex("e-?mail", RegexOptions.IgnoreCase)]
    private static partial Regex Email();

    [GeneratedRegex(@"phone|mobile|\btel\b", RegexOptions.IgnoreCase)]
    private static partial Regex Phone();

    [GeneratedRegex(@"\b(first|last|full|your)?\s*name\b|username|nickname", RegexOptions.IgnoreCase)]
    private static partial Regex Name();

    [GeneratedRegex(@"search|find|filter|query", RegexOptions.IgnoreCase)]
    private static partial Regex Search();

    [GeneratedRegex(@"amount|price|qty|quantity|number|weight|age|count|minutes|km|kg", RegexOptions.IgnoreCase)]
    private static partial Regex Number();

    [GeneratedRegex(@"title|subject|heading|\bname\b|etname", RegexOptions.IgnoreCase)]
    private static partial Regex Title();

    [GeneratedRegex(@"url|website|link", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"note|description|content|body|details|comment|message|text", RegexOptions.IgnoreCase)]
    private static partial Regex LongText();

    /// <summary>Sample text for a field, chosen from its id, hint and label; <paramref name="seed"/> varies the general choice.</summary>
    public static string For(UiElement field, int seed = 0)
    {
        ArgumentNullException.ThrowIfNull(field);
        var what = $"{field.ResourceName} {field.Hint} {field.Label} {field.Description}";
        return true switch
        {
            _ when Email().IsMatch(what) => "alex@example.com",
            _ when Phone().IsMatch(what) => "5550100",
            _ when Url().IsMatch(what) => "example.com",
            _ when Search().IsMatch(what) => "plan",
            _ when Number().IsMatch(what) => "12",
            _ when Name().IsMatch(what) && field.ResourceName.Contains("user", StringComparison.OrdinalIgnoreCase) => "alexmorgan",
            _ when Title().IsMatch(field.ResourceName) || Title().IsMatch(field.Hint) => General[Math.Abs(seed) % General.Length],
            _ when LongText().IsMatch(what) => LongSample,
            _ when Name().IsMatch(what) => General[Math.Abs(seed) % General.Length],
            _ => General[Math.Abs(seed) % General.Length],
        };
    }
}
