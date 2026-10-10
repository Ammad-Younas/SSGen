using System.Text.Json.Serialization;
using SSGen.Agent.Observation;

namespace SSGen.Agent.Strategies;

[JsonConverter(typeof(JsonStringEnumConverter<ActionKind>))]
public enum ActionKind
{
    Tap,
    Type,
    Scroll,
    Back,
    Wait,
    Stop,
}

[JsonConverter(typeof(JsonStringEnumConverter<ScrollDirection>))]
public enum ScrollDirection
{
    Down,
    Up,
    Left,
    Right,
}

[JsonConverter(typeof(JsonStringEnumConverter<RiskLevel>))]
public enum RiskLevel
{
    None,
    Low,
    High,
}

/// <summary>A decided next step. <see cref="Element"/> is resolved against the current screen's elements.</summary>
public sealed record AgentAction(ActionKind Kind, UiElement? Element = null, string? Text = null, ScrollDirection? Direction = null)
{
    public static AgentAction Back() => new(ActionKind.Back);

    public static AgentAction Stop() => new(ActionKind.Stop);

    public string Describe() => Kind switch
    {
        ActionKind.Tap => $"Tap {Element}",
        ActionKind.Type => $"Type \"{Text}\" into {Element}",
        ActionKind.Scroll => $"Scroll {Direction?.ToString().ToLowerInvariant()}" + (Element is null ? "" : $" in {Element}"),
        ActionKind.Back => "Press Back",
        ActionKind.Wait => "Wait for the screen to load",
        _ => "Stop exploring",
    };

    /// <summary>Compact edge label for the screen graph.</summary>
    public string EdgeLabel => Kind switch
    {
        ActionKind.Tap => "tap " + Element?.Label,
        ActionKind.Type => "type into " + Element?.Label,
        ActionKind.Scroll => "scroll " + Direction?.ToString().ToLowerInvariant(),
        _ => Kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>A strategy's decision: the action, its reasoning, the model's own risk estimate, and a label for the screen.</summary>
public sealed record AgentDecision(AgentAction Action, string Thought, RiskLevel Risk = RiskLevel.None, string? ScreenLabel = null);
