using System.Text.RegularExpressions;
using SSGen.Agent.Observation;

namespace SSGen.Agent.Strategies;

/// <summary>
/// Plausible sample input for text fields, so explored screens show realistic content (a saved note, a filled
/// form) instead of empty states. Clearly fictional: example.com addresses and 555 numbers.
/// </summary>
public static partial class DemoText
{
    private static readonly string[] General =
    [
        "Weekly groceries", "Plan weekend trip", "Morning run 5 km", "Team meeting notes", "Book club picks",
        "Gift ideas for Sam", "Garden to-do list", "Recipe: lemon pasta",
    ];

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
            _ when Name().IsMatch(what) && !LongText().IsMatch(what) && field.ResourceName.Contains("user", StringComparison.OrdinalIgnoreCase) => "alexmorgan",
            _ when Name().IsMatch(what) && !LongText().IsMatch(what) => General[Math.Abs(seed) % General.Length],
            _ when LongText().IsMatch(what) => "Buy milk, eggs and coffee. Call the bakery about Saturday.",
            _ => General[Math.Abs(seed) % General.Length],
        };
    }
}
