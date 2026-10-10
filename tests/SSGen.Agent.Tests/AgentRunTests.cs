using Microsoft.Extensions.Logging.Abstractions;
using SSGen.Agent.Graph;
using SSGen.Agent.Runs;
using SSGen.Agent.Safety;
using SSGen.Agent.Strategies;
using SSGen.Ai;
using SSGen.Core.Projects;
using SSGen.Emulator.Devices;

namespace SSGen.Agent.Tests;

public class AgentRunTests
{
    private sealed class Recorder : IAgentObserver
    {
        public List<AgentLogEntry> Log { get; } = [];
        public List<ScreenNode> NewScreens { get; } = [];
        public bool ConfirmAnswer { get; set; }
        public List<string> Questions { get; } = [];

        public void OnLog(AgentLogEntry entry) => Log.Add(entry);

        public void OnScreen(byte[] png, ScreenNode node) { }

        public void OnNewScreen(ScreenNode node, byte[] png) => NewScreens.Add(node);

        public void OnProgress(int stepNumber, CostMeter meter, ScreenGraph graph) { }

        public Task<bool> ConfirmAsync(string question)
        {
            Questions.Add(question);
            return Task.FromResult(ConfirmAnswer);
        }
    }

    private static AgentRunOptions Options(int maxSteps = 60) => new()
    {
        AppName = "Mock",
        PackageName = "com.example.mock",
        MaxSteps = maxSteps,
        SettlePoll = TimeSpan.FromMilliseconds(1),
        SettleTimeout = TimeSpan.FromMilliseconds(50),
    };

    private static string[] Tapped(MockDevice device) => device.Actions.Where(a => a.StartsWith("tap", StringComparison.Ordinal)).ToArray();

    [Fact]
    public async Task Systematic_run_explores_every_screen_and_never_taps_dangerous_buttons()
    {
        using var dir = new TempDir();
        var project = dir.NewProject();
        var device = new MockDevice();
        var recorder = new Recorder();
        var agent = new ExplorationAgent(device, new SystematicStrategy(), new Guardrails(safeMode: true), new CostMeter(false, null, null), project, recorder, NullLogger.Instance);

        using var control = new AgentControl();
        var summary = await agent.RunAsync(Options(), control, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, summary.Status);
        Assert.Equal(5, summary.Screens); // home, workouts, session, progress, settings
        Assert.Equal(5, ScreenRepository.List(project).Count(s => s.Source == "agent"));
        Assert.True(agent.Graph.Edges.Count >= 4);

        // Guardrails: the dangerous Settings buttons were never hit, while everything else was.
        Assert.DoesNotContain("settings:Delete account", device.TappedButtons);
        Assert.DoesNotContain("settings:Buy premium", device.TappedButtons);
        Assert.Contains("home:Settings", device.TappedButtons);
        Assert.Contains("workouts:Start workout", device.TappedButtons);

        Assert.Equal(2, recorder.Log.Count(l => l.Kind == AgentLogKind.Blocked));
        Assert.Contains(recorder.Log, l => l.Kind == AgentLogKind.Blocked && l.Message.Contains("Delete account", StringComparison.Ordinal));
        Assert.Contains(recorder.Log, l => l.Kind == AgentLogKind.Blocked && l.Message.Contains("Buy premium", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ai_run_blocks_dangerous_choices_tracks_cost_and_labels_screens()
    {
        using var dir = new TempDir();
        var project = dir.NewProject();
        var device = new MockDevice();
        var recorder = new Recorder();

        // A reckless model: always wants the first element, i.e. "Delete account" on Settings; otherwise explores.
        var llm = new ScriptedLlm(req =>
        {
            var text = req.Messages[0].Text;
            if (text.Contains("'Delete account' (untried)", StringComparison.Ordinal))
            {
                return """{"thought":"Delete it","screen_label":"Settings","action":"tap","element_id":1,"text":null,"direction":null,"risk":"none"}""";
            }

            if (text.Contains("'Buy premium' (untried)", StringComparison.Ordinal))
            {
                return """{"thought":"Upgrade","screen_label":"Settings","action":"tap","element_id":2,"text":null,"direction":null,"risk":"high"}""";
            }

            var line = text.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith('[') && l.EndsWith("(untried)", StringComparison.Ordinal));
            if (line is null)
            {
                var onHome = text.Contains("activity MainActivity", StringComparison.Ordinal);
                return onHome
                    ? """{"thought":"Done","screen_label":"Home","action":"stop","element_id":null,"text":null,"direction":null,"risk":"none"}"""
                    : """{"thought":"Back","screen_label":"Screen","action":"back","element_id":null,"text":null,"direction":null,"risk":"none"}""";
            }

            var id = line[1..line.IndexOf(']', StringComparison.Ordinal)];
            return $$"""{"thought":"Explore","screen_label":"Screen","action":"tap","element_id":{{id}},"text":null,"direction":null,"risk":"none"}""";
        });

        var meter = new CostMeter(isPaid: true, new Ai.Profiles.ModelPrice("mock-model", 1m, 5m), cap: 10m);
        var strategy = new AiStrategy(llm, vision: true, meter.Add);
        var agent = new ExplorationAgent(device, strategy, new Guardrails(safeMode: true), meter, project, recorder, NullLogger.Instance);
        using var control = new AgentControl();
        var summary = await agent.RunAsync(Options(), control, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, summary.Status);
        Assert.Equal(5, summary.Screens);
        Assert.Equal(2, recorder.Log.Count(l => l.Kind == AgentLogKind.Blocked));
        Assert.DoesNotContain("settings:Delete account", device.TappedButtons);
        Assert.DoesNotContain("settings:Buy premium", device.TappedButtons);
        Assert.True(meter.Usage.InputTokens > 0);
        Assert.NotNull(summary.CostUsd);
        Assert.NotNull(llm.Requests[0].Messages[0].Images); // vision -> screenshot attached (JPEG)
        Assert.Equal("image/jpeg", llm.Requests[0].Messages[0].Images![0].MediaType);
        Assert.Contains(agent.Graph.Nodes, n => n.Label == "Settings");
    }

    [Fact]
    public async Task Safe_mode_off_asks_and_respects_the_answer()
    {
        using var dir = new TempDir();
        var device = new MockDevice();
        var recorder = new Recorder { ConfirmAnswer = false };
        var agent = new ExplorationAgent(device, new SystematicStrategy(), new Guardrails(safeMode: false), new CostMeter(false, null, null), dir.NewProject(), recorder, NullLogger.Instance);
        using var control = new AgentControl();
        await agent.RunAsync(Options(), control, CancellationToken.None);

        Assert.Equal(2, recorder.Questions.Count);
        Assert.Contains(recorder.Log, l => l.Message.StartsWith("Declined by you", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Step_and_cost_limits_stop_the_run()
    {
        using var dir = new TempDir();
        var project = dir.NewProject();
        var stepLimited = await new ExplorationAgent(new MockDevice(), new SystematicStrategy(), new Guardrails(true), new CostMeter(false, null, null), project, new Recorder(), NullLogger.Instance)
            .RunAsync(Options(maxSteps: 3), new AgentControl(), CancellationToken.None);
        Assert.Equal(AgentRunStatus.LimitReached, stepLimited.Status);
        Assert.Equal(3, stepLimited.Steps);

        var meter = new CostMeter(true, new Ai.Profiles.ModelPrice("m", 1000m, 1000m), cap: 0.05m);
        var llm = new ScriptedLlm(_ => """{"thought":"t","screen_label":"s","action":"back","element_id":null,"text":null,"direction":null,"risk":"none"}""");
        var costLimited = await new ExplorationAgent(new MockDevice(), new AiStrategy(llm, false, meter.Add), new Guardrails(true), meter, project, new Recorder(), NullLogger.Instance)
            .RunAsync(Options(), new AgentControl(), CancellationToken.None);
        Assert.Equal(AgentRunStatus.LimitReached, costLimited.Status);
        Assert.Contains("cost cap", costLimited.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_stops_cleanly()
    {
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        var recorder = new Recorder();
        var agent = new ExplorationAgent(new MockDevice(), new SystematicStrategy(), new Guardrails(true), new CostMeter(false, null, null), dir.NewProject(), recorder, NullLogger.Instance);
        await cts.CancelAsync();
        var summary = await agent.RunAsync(Options(), new AgentControl(), cts.Token);
        Assert.Equal(AgentRunStatus.Stopped, summary.Status);
    }
}
