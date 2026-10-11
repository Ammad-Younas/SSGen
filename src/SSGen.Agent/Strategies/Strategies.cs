using SSGen.Agent.Graph;
using SSGen.Agent.Observation;

namespace SSGen.Agent.Strategies;

/// <summary>Everything a strategy may look at to choose the next action.</summary>
public sealed record AgentContext(
    string AppName,
    string PackageName,
    string? Hints,
    int Step,
    int MaxSteps,
    ScreenGraph Graph,
    ScreenNode Node,
    UiSnapshot Ui,
    byte[] Screenshot,
    IReadOnlyList<string> RecentActions)
{
    /// <summary>Steps since a new screen was last found (the AI is nudged to try elsewhere when this grows).</summary>
    public int StepsWithoutNewScreen { get; init; }

    /// <summary>How many store screenshots the user wants (guides what to look for), if set.</summary>
    public int? TargetScreens { get; init; }

    /// <summary>Values that must never reach an AI prompt (the test account's user name and password).</summary>
    public IReadOnlyCollection<string> Secrets { get; init; } = [];
}

public interface IAgentStrategy
{
    /// <summary>Shown in the UI ("Systematic (no AI)", "AI: Claude …").</summary>
    string Name { get; }

    Task<AgentDecision> DecideAsync(AgentContext context, CancellationToken ct);
}

/// <summary>
/// Systematic exploration without AI: on each screen try every untried element (navigation bars and tabs first,
/// then content in reading order) and scroll once; then travel along known taps to a screen that still has untried
/// elements; otherwise go back. On the launch screen (where Back would leave the app) it stops instead. Onboarding
/// is skipped first, and each empty text field gets sample text once (after the buttons), so screens show content.
/// Works in manual mode and is the fallback when an AI decision is unusable.
/// </summary>
public sealed class SystematicStrategy : IAgentStrategy
{
    public string Name => "Systematic (no AI)";

    public Task<AgentDecision> DecideAsync(AgentContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(Decide(context));
    }

    public static AgentDecision Decide(AgentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var node = context.Node;
        var candidate = Candidates(context.Ui).FirstOrDefault(e => !node.Tried.Contains(e.Key));
        if (candidate is not null)
        {
            return new AgentDecision(new AgentAction(ActionKind.Tap, candidate), $"Trying '{candidate.Label}', not explored yet on this screen.");
        }

        // Each field once (tracked by its stable key). Emptiness can't be relied on: without a hint attribute in the
        // dump, an empty field showing its hint looks like it has text.
        var field = context.Ui.Elements.FirstOrDefault(e => e.Editable && !e.Password && !node.Tried.Contains(e.Key));
        if (field is not null)
        {
            var text = DemoText.For(field, context.Graph.Nodes.Count + node.Tried.Count);
            return new AgentDecision(new AgentAction(ActionKind.Type, field, text), $"Filling '{field.Label}' with sample text so the screen shows content.");
        }

        if (!node.ScrolledDown && context.Ui.Elements.FirstOrDefault(e => e.Scrollable) is { } list)
        {
            return new AgentDecision(new AgentAction(ActionKind.Scroll, list, Direction: ScrollDirection.Down), "Everything visible is explored; scrolling to reveal more.");
        }

        // Nothing tappable at all (still loading, a popup the UI tree doesn't expose, a game): never call that
        // "explored". Wait once, then back out of it.
        if (node.Known.Count == 0 && !node.HasScrollable)
        {
            if (node.IsRoot && node.Visits > 3)
            {
                return new AgentDecision(AgentAction.Stop(), "The app's screens expose no tappable elements (games and some web views); systematic exploration can't continue.");
            }

            return node.Visits <= 1
                ? new AgentDecision(new AgentAction(ActionKind.Wait), "Nothing tappable yet; waiting for the screen to load.")
                : new AgentDecision(AgentAction.Back(), "Nothing tappable on this screen; backing out.");
        }

        var onScreen = Candidates(context.Ui).ToDictionary(e => e.Key, StringComparer.Ordinal);
        if (context.Graph.FirstHopToUnfinished(node, onScreen.Keys.ToHashSet(StringComparer.Ordinal)) is { ElementKey: { } key } hop)
        {
            node.Revisits[key] = node.Revisits.GetValueOrDefault(key) + 1;
            var target = context.Graph.Get(hop.To)?.DisplayName ?? "another screen";
            return new AgentDecision(new AgentAction(ActionKind.Tap, onScreen[key]), $"Heading to {target}, which still has unexplored parts.");
        }

        if (node.IsRoot)
        {
            return new AgentDecision(AgentAction.Stop(), "Every reachable element has been tried.");
        }

        return new AgentDecision(AgentAction.Back(), "Nothing new here; going back.");
    }

    private static readonly string[] OnboardingSkip = ["skip", "skip intro", "skip tour", "get started", "got it"];

    /// <summary>
    /// Tappable elements in priority order: a way past onboarding first ("Skip", "Get started"), then navigation,
    /// then reading order. Text fields are handled separately.
    /// </summary>
    internal static IEnumerable<UiElement> Candidates(UiSnapshot ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        var screenBottom = ui.Elements.Count == 0 ? 0 : ui.Elements.Max(e => e.Bounds.Bottom);
        bool IsNav(UiElement e) =>
            e.ResourceName.Contains("nav", StringComparison.OrdinalIgnoreCase) ||
            e.ResourceName.Contains("tab", StringComparison.OrdinalIgnoreCase) ||
            e.ShortClass.Contains("Tab", StringComparison.Ordinal) ||
            (screenBottom > 0 && e.Bounds.Top > screenBottom * 0.85 && e.Bounds.Height < screenBottom * 0.12);

        return ui.Elements
            .Where(e => (e.Clickable || e.Checkable) && !e.Editable && !e.Scrollable && !string.IsNullOrWhiteSpace(e.Label))
            .OrderByDescending(e => OnboardingSkip.Contains(e.Label.Trim().ToLowerInvariant()))
            .ThenByDescending(IsNav)
            .ThenBy(e => e.Id);
    }
}
