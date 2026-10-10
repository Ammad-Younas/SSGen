using System.Text.Json.Nodes;
using SSGen.Agent.Observation;
using SSGen.Agent.Safety;
using SSGen.Agent.Strategies;
using SSGen.Ai;

namespace SSGen.Agent.Tests;

public class GuardrailsTests
{
    private static UiElement El(string text, string id = "", string desc = "") => new()
    {
        Id = 1, Class = "android.widget.Button", Text = text, ResourceId = id, Description = desc, Clickable = true,
        Bounds = new UiBounds(0, 0, 100, 100),
    };

    private static GuardResult Check(Guardrails g, UiElement el, UiSnapshot? screen = null, RiskLevel risk = RiskLevel.None, ActionKind kind = ActionKind.Tap, string? text = null) =>
        g.Check(new AgentDecision(new AgentAction(kind, el, text), "", risk), screen ?? new UiSnapshot([el], [el.Text], "com.app"));

    [Theory]
    [InlineData("Buy premium")]
    [InlineData("Delete account")]
    [InlineData("PAY NOW")]
    [InlineData("Place order")]
    [InlineData("Sign out")]
    [InlineData("Start free trial")]
    [InlineData("Supprimer")]
    [InlineData("Comprar ahora")]
    public void Blocks_dangerous_labels(string label) =>
        Assert.Equal(GuardVerdict.Blocked, Check(new Guardrails(safeMode: true), El(label)).Verdict);

    [Theory]
    [InlineData("Display settings")]
    [InlineData("Postcode")]
    [InlineData("Payments history")] // plural/compound words don't match "payment"
    [InlineData("Workouts")]
    [InlineData("Notes")]
    public void Allows_harmless_labels(string label) =>
        Assert.Equal(GuardVerdict.Allowed, Check(new Guardrails(safeMode: true), El(label)).Verdict);

    [Fact]
    public void Blocks_by_resource_id_and_description()
    {
        var g = new Guardrails(safeMode: true);
        Assert.Equal(GuardVerdict.Blocked, Check(g, El("", id: "com.app:id/btn_deleteAccount")).Verdict);
        Assert.Equal(GuardVerdict.Blocked, Check(g, El("", desc: "Send message")).Verdict);
    }

    [Fact]
    public void Blocks_ok_on_a_dangerous_dialog_but_not_elsewhere()
    {
        var g = new Guardrails(safeMode: true);
        var ok = El("OK");
        Assert.Equal(GuardVerdict.Blocked, Check(g, ok, new UiSnapshot([ok], ["Delete this note permanently?", "OK", "Cancel"], "com.app")).Verdict);
        Assert.Equal(GuardVerdict.Allowed, Check(g, ok, new UiSnapshot([ok], ["Welcome to Notes", "OK"], "com.app")).Verdict);
    }

    [Fact]
    public void Card_numbers_high_risk_extra_phrases_and_safe_mode_off()
    {
        var g = new Guardrails(safeMode: true, ["archive"]);
        Assert.Equal(GuardVerdict.Blocked, Check(g, El("Card"), kind: ActionKind.Type, text: "4111 1111 1111 1111").Verdict);
        Assert.Equal(GuardVerdict.Blocked, Check(g, El("Continue"), risk: RiskLevel.High).Verdict);
        Assert.Equal(GuardVerdict.Blocked, Check(g, El("Archive")).Verdict);

        var off = Check(new Guardrails(safeMode: false), El("Buy premium"));
        Assert.Equal(GuardVerdict.NeedsConfirmation, off.Verdict);
        Assert.Contains("buy", off.Reason, StringComparison.Ordinal);
    }
}

public class StructuredOutputTests
{
    private sealed record Reply(string Name, int Count);

    private static readonly JsonObject Schema = new() { ["type"] = "object" };

    private static LlmRequest Req => new() { Messages = [LlmMessage.User("go")] };

    [Theory]
    [InlineData("{\"name\":\"a\",\"count\":1}")]
    [InlineData("Sure! Here you go:\n```json\n{\"name\":\"a\",\"count\":1,}\n```\nAnything else?")]
    [InlineData("The answer is {\"name\": \"a\", \"count\": 1}, done. {\"other\": 2}")]
    [InlineData("{\"Name\":\"a\",\"Count\":1} // comment")]
    public async Task Tolerant_parsing(string text)
    {
        var r = await StructuredOutput.AskAsync<Reply>(new ScriptedLlm(text), Req, Schema, "{}");
        Assert.Equal(new Reply("a", 1), r.Value);
        Assert.Equal(1, r.Attempts);
    }

    [Fact]
    public async Task Retries_with_feedback_then_succeeds()
    {
        var llm = new ScriptedLlm("I think we should tap it.", "{\"name\":\"\",\"count\":1}", "{\"name\":\"ok\",\"count\":2}");
        var r = await StructuredOutput.AskAsync<Reply>(llm, Req, Schema, "{}", v => v.Name.Length == 0 ? "name must not be empty." : null);

        Assert.Equal(3, r.Attempts);
        Assert.Equal(new LlmUsage(300, 60), r.Usage);
        var last = llm.Requests[^1].Messages;
        Assert.Contains("name must not be empty", last[^1].Text, StringComparison.Ordinal);
        Assert.Equal(LlmRole.Assistant, last[^2].Role);
        Assert.Contains("JSON Schema", llm.Requests[0].System, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gives_up_with_invalid_output_error_and_still_reports_the_tokens_spent()
    {
        var spent = LlmUsage.Zero;
        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            StructuredOutput.AskAsync<Reply>(new ScriptedLlm(_ => "no json here"), Req, Schema, "{}", onUsage: u => spent += u));
        Assert.Equal(LlmErrorKind.InvalidOutput, ex.Kind);
        Assert.Contains("did not contain a JSON object", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no json here", ex.SuggestedFix, StringComparison.Ordinal);
        Assert.Equal(new LlmUsage(300, 60), spent); // 3 failed attempts still count toward the cost cap
    }

    [Fact]
    public void Systematic_never_calls_an_empty_screen_explored()
    {
        var graph = new Graph.ScreenGraph();
        var (node, _) = graph.Observe(new ScreenFingerprint(".Main", 1, new HashSet<string>(), new HashSet<string>()));
        node.IsRoot = true;
        AgentContext Ctx() => new("App", "com.app", null, 1, 10, graph, node, UiSnapshot.Empty, [], []);

        Assert.Equal(ActionKind.Wait, SystematicStrategy.Decide(Ctx()).Action.Kind);
        graph.Observe(node.Fingerprint);
        Assert.Equal(ActionKind.Back, SystematicStrategy.Decide(Ctx()).Action.Kind);
        graph.Observe(node.Fingerprint);
        graph.Observe(node.Fingerprint);
        Assert.Equal(ActionKind.Stop, SystematicStrategy.Decide(Ctx()).Action.Kind); // bounded on the root screen
    }

    [Fact]
    public async Task Ai_strategy_parses_validates_and_falls_back()
    {
        var ui = new UiSnapshot([new UiElement { Id = 1, Class = "android.widget.Button", Text = "Workouts", Clickable = true, Bounds = new UiBounds(0, 0, 10, 10) }], ["Workouts"], "com.app");
        var graph = new Graph.ScreenGraph();
        var (node, _) = graph.Observe(new ScreenFingerprint(".Main", 1, new HashSet<string>(), new HashSet<string>()));
        var ctx = new AgentContext("App", "com.app", "fitness app", 1, 10, graph, node, ui, [], []);

        // Invalid element id first, then a valid tap: the strategy retries and returns the tap.
        var llm = new ScriptedLlm(
            """{"thought":"x","screen_label":"Home","action":"tap","element_id":9,"text":null,"direction":null,"risk":"none"}""",
            """{"thought":"Open workouts","screen_label":"Home","action":"tap","element_id":1,"text":null,"direction":null,"risk":"low"}""");
        LlmUsage total = LlmUsage.Zero;
        var strategy = new AiStrategy(llm, vision: false, u => total += u);
        var decision = await strategy.DecideAsync(ctx, CancellationToken.None);

        Assert.Equal(ActionKind.Tap, decision.Action.Kind);
        Assert.Equal("Workouts", decision.Action.Element!.Label);
        Assert.Equal("Home", decision.ScreenLabel);
        Assert.Equal(RiskLevel.Low, decision.Risk);
        Assert.Equal(200, total.InputTokens);
        Assert.Null(llm.Requests[0].Messages[0].Images); // no vision -> no screenshot
        Assert.Contains("fitness app", llm.Requests[0].Messages[0].Text, StringComparison.Ordinal);

        // Garbage forever -> falls back to the systematic choice instead of failing the run.
        var fallback = await new AiStrategy(new ScriptedLlm(_ => "???"), false, _ => { }).DecideAsync(ctx, CancellationToken.None);
        Assert.Equal(ActionKind.Tap, fallback.Action.Kind);
        Assert.StartsWith("(AI unusable", fallback.Thought, StringComparison.Ordinal);
    }
}
