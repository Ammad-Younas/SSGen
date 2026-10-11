using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using SSGen.Agent.Graph;
using SSGen.Agent.Runs;
using SSGen.Agent.Safety;
using SSGen.Agent.Strategies;
using SSGen.Ai;
using SSGen.Core.Data;
using SSGen.Core.Processes;
using SSGen.Core.Settings;
using SSGen.Emulator;
using Xunit.Abstractions;

namespace SSGen.Agent.Tests;

public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SSGEN_INTEGRATION") != "1")
        {
            Skip = "Set SSGEN_INTEGRATION=1 to run emulator integration tests.";
        }
        else if (SampleApk() is null)
        {
            Skip = "Sample APK missing: run tools/fetch-deps.ps1";
        }
    }

    public static string? SampleApk([CallerFilePath] string here = "")
    {
        var p = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "third_party", "samples", "privacyfriendlynotes-105.apk"));
        return File.Exists(p) ? p : null;
    }
}

/// <summary>Systematic exploration of the real sample app on the dedicated emulator. Run with SSGEN_INTEGRATION=1.</summary>
public class AgentIntegrationTests
{
    private readonly ITestOutputHelper _out;

    public AgentIntegrationTests(ITestOutputHelper output) => _out = output;

    private sealed class Writer(ITestOutputHelper output, Stopwatch clock) : IAgentObserver
    {
        public void OnLog(AgentLogEntry entry) => output.WriteLine($"[{clock.Elapsed:mm\\:ss}] #{entry.Step} {entry.Kind}: {entry.Message}");

        public void OnScreen(byte[] png, ScreenNode node) { }

        public void OnNewScreen(ScreenNode node, byte[] png) { }

        public void OnProgress(int stepNumber, CostMeter meter, ScreenGraph graph) { }

        public Task<bool> ConfirmAsync(string question) => Task.FromResult(false);

        public void OnTakeoverRequested(string reason) => output.WriteLine("TAKEOVER: " + reason);
    }

    [IntegrationFact]
    public async Task Systematic_exploration_of_the_sample_app()
    {
        using var dir = new TempDir();
        var db = new SqliteDatabase(dir.Combine("app.db"), MigrationSet.App);
        db.Migrate();
        await using var hub = new DeviceHub(new SettingsService(db), new ProcessRunner(), NullLoggerFactory.Instance);
        var sdk = hub.RequireSdk();
        var clock = Stopwatch.StartNew();

        var avd = hub.Avds.List().First(a => a.IsManagedBySSGen && a.ApiLevel == 36 && a.Width == 1080 && a.Height == 2400);
        var target = (await hub.ListTargetsAsync()).Single(t => t.Avd?.Name == avd.Name);
        var provider = hub.ProviderFor(target);
        var device = await provider.ConnectAsync(target, new Emulator.Devices.DeviceStartOptions { Headless = true }, new Progress<string>(_out.WriteLine));
        try
        {
            var apk = IntegrationFactAttribute.SampleApk()!;
            var info = (await hub.Apks.InspectAsync(apk, sdk.Aapt2Path)).Info;
            try
            {
                await device.InstallAsync([apk]);
            }
            catch (Core.UserFacingException ex)
            {
                _out.WriteLine($"Install failed: {ex.Message} | {ex.SuggestedFix}");
                throw;
            }

            await hub.Adb.ShellAsync(device.Serial, "pm clear " + info.PackageName);

            var project = dir.NewProject();
            var agent = new ExplorationAgent(device, new SystematicStrategy(), new Guardrails(safeMode: true), new CostMeter(false, null, null),
                project, new Writer(_out, clock), NullLogger.Instance);
            using var control = new AgentControl();
            var summary = await agent.RunAsync(new AgentRunOptions
            {
                AppName = info.DisplayName,
                PackageName = info.PackageName,
                LauncherActivity = info.LauncherActivity,
                MaxSteps = 80,
                MaxDuration = TimeSpan.FromMinutes(14),
            }, control, CancellationToken.None);

            _out.WriteLine($"{summary.Status}: {summary.Message} screens={summary.Screens} steps={summary.Steps} in {clock.Elapsed}");
            foreach (var n in agent.Graph.Nodes)
            {
                _out.WriteLine($"  #{n.Id} {n.DisplayName} visits={n.Visits}");
            }

            Assert.NotEqual(AgentRunStatus.Failed, summary.Status);
            Assert.True(summary.Screens >= 4, $"only {summary.Screens} screens");
        }
        finally
        {
            await provider.StopAsync(target with { RunningSerial = device.Serial });
        }
    }
}
