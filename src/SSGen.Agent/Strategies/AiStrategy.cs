using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SkiaSharp;
using SSGen.Ai;

namespace SSGen.Agent.Strategies;

/// <summary>
/// Asks the configured model for the next action. The prompt carries the numbered interactive elements (with which
/// ones were already tried), the discovered screens, recent actions and the user's hints; the screenshot is added
/// (downscaled) only when the model supports vision. Output is schema-validated JSON; anything unusable falls back
/// to <see cref="SystematicStrategy"/> for that step.
/// </summary>
public sealed class AiStrategy : IAgentStrategy
{
    private const int MaxImageSide = 1024;

    private readonly ILlmProvider _provider;
    private readonly bool _vision;
    private readonly Action<LlmUsage> _onUsage;

    public AiStrategy(ILlmProvider provider, bool vision, Action<LlmUsage> onUsage)
    {
        _provider = provider;
        _vision = vision;
        _onUsage = onUsage;
    }

    public string Name => $"AI ({_provider.Model}{(_vision ? "" : ", no vision")})";

    public string? LastFallbackReason { get; private set; }

    public async Task<AgentDecision> DecideAsync(AgentContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        LastFallbackReason = null;
        var request = new LlmRequest
        {
            System = SystemPrompt,
            Messages = [BuildUserMessage(context)],
            MaxTokens = 1024,
            Temperature = 0.2,
        };

        try
        {
            var result = await StructuredOutput.AskAsync<AiReply>(_provider, request, Schema, Example, r => Validate(r, context), maxAttempts: 3, _onUsage, ct);
            return ToDecision(result.Value, context);
        }
        catch (LlmException ex) when (ex.Kind is LlmErrorKind.InvalidOutput or LlmErrorKind.Refused)
        {
            LastFallbackReason = ex.Message;
            var fallback = SystematicStrategy.Decide(context);
            return fallback with { Thought = $"(AI unusable: {ex.Message} {ex.SuggestedFix}) " + fallback.Thought };
        }
    }

    internal const string SystemPrompt = """
        You are exploring an Android app on an emulator to discover every distinct screen, so the best ones can be
        used as app-store screenshots. Each turn you see the current screen and choose ONE action.

        Goals, in order:
        1. Reach screens not seen yet: main sections first (bottom navigation, tabs, drawer), then go one or two
           levels deep into each section. Prefer elements marked "untried".
        2. Get past onboarding, splash and intro screens (Skip / Next / Continue / Get started).
        3. When a screen has nothing new left, scroll once if more content may be below, otherwise press back.
        4. Say "stop" only when the app looks fully explored.

        Rules:
        - Use only element ids from the list. Never invent ids or coordinates.
        - Never buy, pay, subscribe, delete, send, post, share, call, or sign out. Set "risk" to "high" for any
          action that could cost money, destroy data, or contact anyone; such actions are blocked anyway.
        - Stay inside the app under test. If something else is in front (browser, store, another app), press back.
        - For text fields, type short realistic demo text (e.g. a search term, a note title) so screens show content.
          Do not enter personal data. Sign-in screens are handled for you; don't try to log in or sign up.
        - Keep "thought" to one short sentence. Give the current screen a 1 to 4 word "screen_label".
        """;

    internal static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["thought"] = new JsonObject { ["type"] = "string" },
            ["screen_label"] = new JsonObject { ["type"] = "string" },
            ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("tap", "type", "scroll", "back", "wait", "stop") },
            ["element_id"] = new JsonObject { ["type"] = new JsonArray("integer", "null") },
            ["text"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
            ["direction"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["enum"] = new JsonArray("down", "up", "left", "right", null) },
            ["risk"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("none", "low", "high") },
        },
        ["required"] = new JsonArray("thought", "screen_label", "action", "element_id", "text", "direction", "risk"),
        ["additionalProperties"] = false,
    };

    internal const string Example = """
        {"thought":"The Workouts tab is untried, so open it.","screen_label":"Home dashboard","action":"tap","element_id":4,"text":null,"direction":null,"risk":"none"}
        """;

    internal static string? Validate(AiReply r, AgentContext context)
    {
        switch (r.Action)
        {
            case ActionKind.Tap or ActionKind.Type when r.ElementId is null:
                return $"\"{r.Action.ToString().ToLowerInvariant()}\" needs an element_id.";
            case ActionKind.Tap or ActionKind.Type or ActionKind.Scroll when r.ElementId is { } id && context.Ui.Find(id) is null:
                return $"element_id {id} is not in the list. Valid ids: 1..{context.Ui.Elements.Count}.";
            case ActionKind.Type when string.IsNullOrWhiteSpace(r.Text):
                return "\"type\" needs text.";
            case ActionKind.Scroll when r.Direction is null:
                return "\"scroll\" needs a direction.";
            default:
                return null;
        }
    }

    internal static AgentDecision ToDecision(AiReply r, AgentContext context)
    {
        var element = r.ElementId is { } id ? context.Ui.Find(id) : null;
        var action = new AgentAction(r.Action, element, r.Text, r.Direction);
        return new AgentDecision(action, r.Thought ?? "", r.Risk, string.IsNullOrWhiteSpace(r.ScreenLabel) ? null : r.ScreenLabel.Trim());
    }

    private LlmMessage BuildUserMessage(AgentContext c)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"App: {c.AppName} ({c.PackageName}). Step {c.Step} of at most {c.MaxSteps}.");
        if (!string.IsNullOrWhiteSpace(c.Hints))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Hints from the user: {c.Hints}");
        }

        if (c.TargetScreens is { } target)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"The user wants about {target} store screenshots: make sure each main feature gets visited, ideally with real-looking content.");
        }

        if (c.StepsWithoutNewScreen >= 10)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"No new screen in the last {c.StepsWithoutNewScreen} steps: try a different part of the app, or stop if everything is explored.");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"Screens discovered so far ({c.Graph.Nodes.Count}): {string.Join(", ", c.Graph.Nodes.Select(n => n.DisplayName + (n.IsFinished ? " (fully explored)" : "")))}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Current screen: {c.Node.DisplayName} (activity {c.Node.ShortActivity ?? "unknown"}, visit #{c.Node.Visits}).");
        if (c.Node.IsRoot)
        {
            sb.AppendLine("This is the app's start screen: pressing back here would leave the app, so use stop instead when everything is explored.");
        }
        if (c.RecentActions.Count > 0)
        {
            sb.AppendLine("Recent actions: " + string.Join(" → ", c.RecentActions));
        }

        sb.AppendLine();
        if (c.Ui.Elements.Count == 0)
        {
            sb.AppendLine("No interactive elements were reported (game, web view or still loading). Use wait, scroll or back.");
        }
        else
        {
            sb.AppendLine("Interactive elements:");
            foreach (var e in c.Ui.Elements.Take(80))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{e}{(c.Node.Tried.Contains(e.Key) ? " (tried)" : " (untried)")}");
            }
        }

        if (!_vision)
        {
            var text = string.Join(" | ", c.Ui.AllText.Distinct().Take(40));
            sb.AppendLine();
            sb.AppendLine("Visible text (no screenshot available): " + text);
        }

        // The test account never goes to a model, even if a field on screen shows it.
        var prompt = sb.ToString();
        foreach (var secret in c.Secrets.Where(s => s.Length > 0))
        {
            prompt = prompt.Replace(secret, "[hidden]", StringComparison.Ordinal);
        }

        return _vision
            ? LlmMessage.User(prompt, new LlmImage(Downscale(c.Screenshot), "image/jpeg"))
            : LlmMessage.User(prompt);
    }

    /// <summary>JPEG at most 1024 px on the long side: enough detail for UI decisions, far fewer tokens and bytes.</summary>
    internal static byte[] Downscale(byte[] png)
    {
        using var bitmap = SKBitmap.Decode(png);
        if (bitmap is null)
        {
            return png;
        }

        var scale = Math.Min(1f, (float)MaxImageSide / Math.Max(bitmap.Width, bitmap.Height));
        using var resized = scale < 1f
            ? bitmap.Resize(new SKImageInfo((int)(bitmap.Width * scale), (int)(bitmap.Height * scale)), new SKSamplingOptions(SKCubicResampler.Mitchell))
            : bitmap.Copy();
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }
}

/// <summary>The model's JSON reply (snake_case on the wire).</summary>
internal sealed record AiReply(
    [property: JsonPropertyName("thought")] string? Thought,
    [property: JsonPropertyName("screen_label")] string? ScreenLabel,
    [property: JsonPropertyName("action")] ActionKind Action,
    [property: JsonPropertyName("element_id")] int? ElementId,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("direction")] ScrollDirection? Direction,
    [property: JsonPropertyName("risk")] RiskLevel Risk);
