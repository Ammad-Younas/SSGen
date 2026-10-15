using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using SSGen.Agent.Graph;
using SSGen.Agent.Observation;
using SSGen.Agent.Runs;
using SSGen.Agent.Safety;
using SSGen.Agent.Strategies;
using SSGen.Ai;
using SSGen.Core.Projects;
using SSGen.Emulator.Devices;
using static SSGen.Emulator.Devices.MockDevice;

namespace SSGen.Agent.Tests;

/// <summary>M4: interruptions, sign-in walls, takeover, crashes, resume and limits, on the mock device.</summary>
public class AgentV2Tests
{
    private const string User = "alex@test.dev";
    private const string Pass = "s3cret!pw";

    private sealed class Recorder : IAgentObserver
    {
        public List<AgentLogEntry> Log { get; } = [];
        public List<string> Takeovers { get; } = [];
        public Action<string>? DuringTakeover { get; set; }

        public string AllText => string.Join("\n", Log.Select(l => l.Message));

        public void OnLog(AgentLogEntry entry) => Log.Add(entry);

        public void OnScreen(byte[] png, ScreenNode node) { }

        public void OnNewScreen(ScreenNode node, byte[] png) { }

        public void OnProgress(int stepNumber, CostMeter meter, ScreenGraph graph) { }

        public Task<bool> ConfirmAsync(string question) => Task.FromResult(false);

        public void OnTakeoverRequested(string reason)
        {
            Takeovers.Add(reason);
            DuringTakeover?.Invoke(reason);
        }
    }

    private static AgentRunOptions Options(LoginOptions? login = null, int maxSteps = 60) => new()
    {
        AppName = "Mock",
        PackageName = AppPackage,
        MaxSteps = maxSteps,
        Login = login ?? new LoginOptions(),
        SettlePoll = TimeSpan.FromMilliseconds(1),
        SettleTimeout = TimeSpan.FromMilliseconds(20),
        WarmupTimeout = TimeSpan.FromMilliseconds(20),
        Patience = TimeSpan.FromMilliseconds(10),
    };

    private static ExplorationAgent Agent(MockDevice device, Project project, Recorder recorder, IAgentStrategy? strategy = null) =>
        new(device, strategy ?? new SystematicStrategy(), new Guardrails(safeMode: true), new CostMeter(false, null, null), project, recorder, NullLogger.Instance);

    private static MockScreen Screen(string id, string title, params MockButton[] buttons) =>
        new(id, title, "." + char.ToUpperInvariant(id[0]) + id[1..] + "Activity", buttons, SKColors.SteelBlue);

    private static MockScreen LoginScreen(bool guest = false) =>
        Screen("login", "Welcome",
            [Btn("Email", 0, null, MockElementKind.TextField, "com.example.mock:id/email"),
             Btn("Password", 1, null, MockElementKind.PasswordField, "com.example.mock:id/password"),
             Btn("Sign in", 2, "home") with { Submits = true },
             .. guest ? [Btn("Continue as guest", 3, "home")] : Array.Empty<MockButton>()])
        with { RequiredInput = new Dictionary<string, string> { ["Email"] = User, ["Password"] = Pass } };

    private static MockScreen[] App(MockScreen first, params MockScreen[] more) =>
    [
        first,
        Screen("home", "Home", Btn("Notes", 0, "notes"), Btn("Stats", 1, "stats")),
        Screen("notes", "Notes", Btn("Open note", 0, "note")),
        Screen("note", "Note", Btn("Pin", 0, null)),
        Screen("stats", "Stats", Btn("Week", 0, null)),
        .. more,
    ];

    // ---------------------------------------------------------------- sign-in walls

    [Fact]
    public async Task Test_account_gets_past_the_login_wall_and_never_leaks()
    {
        using var dir = new TempDir();
        var project = dir.NewProject();
        var device = new MockDevice(App(LoginScreen()));
        var recorder = new Recorder();
        var summary = await Agent(device, project, recorder).RunAsync(Options(new LoginOptions(User, Pass)), new AgentControl(), CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, summary.Status);
        Assert.Contains("demo on", device.Actions);   // clean status bar while exploring...
        Assert.False(device.StatusBarDemo);           // ...and the real one restored afterwards
        Assert.Equal(User, device.TypedText("login", "Email"));
        Assert.Equal(Pass, device.TypedText("login", "Password"));
        Assert.Contains("home:Notes", device.TappedButtons);
        Assert.Contains("stats:Week", device.TappedButtons);
        Assert.Empty(recorder.Takeovers);

        // The password appears nowhere: not in the live log, not in the project's saved run log.
        Assert.DoesNotContain(Pass, recorder.AllText, StringComparison.Ordinal);
        using var c = project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT group_concat(message, '\n') FROM agent_steps;";
        Assert.DoesNotContain(Pass, (string)cmd.ExecuteScalar()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_test_account_then_asks_the_user_who_logs_in()
    {
        using var dir = new TempDir();
        var device = new MockDevice(App(LoginScreen()));
        var recorder = new Recorder();
        using var control = new AgentControl();
        recorder.DuringTakeover = _ =>
        {
            // The user logs in on the device, then presses Resume.
            device.TapAsync(540, 1000).Wait();
            device.PressKeyAsync(AndroidKey.MoveEnd).Wait();
            for (var i = 0; i < 40; i++)
            {
                device.PressKeyAsync(AndroidKey.Delete).Wait();
            }

            device.InputTextAsync(User).Wait();
            device.TapAsync(540, 1260).Wait();
            for (var i = 0; i < 40; i++)
            {
                device.PressKeyAsync(AndroidKey.Delete).Wait();
            }

            device.InputTextAsync(Pass).Wait();
            device.TapAsync(540, 1520).Wait();
            control.Resume();
        };

        var summary = await Agent(device, dir.NewProject(), recorder).RunAsync(Options(new LoginOptions(User, "wrong")), control, CancellationToken.None);

        Assert.Single(recorder.Takeovers);
        Assert.Contains("didn't work", recorder.Takeovers[0], StringComparison.Ordinal);
        Assert.Contains(recorder.Log, l => l.Message == "Continuing after manual control.");
        Assert.Contains("stats:Week", device.TappedButtons);
        Assert.Equal(AgentRunStatus.Completed, summary.Status);
    }

    [Fact]
    public async Task Guest_route_is_taken_when_there_is_no_test_account()
    {
        using var dir = new TempDir();
        var device = new MockDevice(App(LoginScreen(guest: true)));
        var recorder = new Recorder();
        await Agent(device, dir.NewProject(), recorder).RunAsync(Options(new LoginOptions(GuestHint: "Tap 'Continue as guest'")), new AgentControl(), CancellationToken.None);

        Assert.Contains("login:Continue as guest", device.TappedButtons);
        Assert.DoesNotContain("login:Sign in", device.TappedButtons);
        Assert.Contains("home:Notes", device.TappedButtons);
        Assert.Empty(recorder.Takeovers);
    }

    [Fact]
    public async Task Login_wall_without_any_way_in_finishes_with_advice_when_not_asking()
    {
        using var dir = new TempDir();
        var recorder = new Recorder();
        var summary = await Agent(new MockDevice(App(LoginScreen())), dir.NewProject(), recorder)
            .RunAsync(Options(new LoginOptions(AskUser: false)), new AgentControl(), CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, summary.Status);
        Assert.Contains("requires signing in", summary.Message, StringComparison.Ordinal);
        Assert.Empty(recorder.Takeovers);
    }

    // ---------------------------------------------------------------- interruptions

    [Fact]
    public async Task Permission_dialogs_are_allowed_and_not_saved_as_screens()
    {
        using var dir = new TempDir();
        var project = dir.NewProject();
        var permission = Screen("perm", "Allow Mock to take pictures?",
                Btn("While using the app", 0, "camera", id: "com.android.permissioncontroller:id/permission_allow_foreground_only_button"),
                Btn("Don't allow", 1, "!back", id: "com.android.permissioncontroller:id/permission_deny_button"))
            with { Package = "com.google.android.permissioncontroller" };
        var device = new MockDevice([Screen("home", "Home", Btn("Take photo", 0, "perm")), permission, Screen("camera", "Camera", Btn("Shutter", 0, null))]);
        var summary = await Agent(device, project, new Recorder()).RunAsync(Options(), new AgentControl(), CancellationToken.None);

        Assert.Contains("perm:While using the app", device.TappedButtons);
        Assert.DoesNotContain("perm:Don't allow", device.TappedButtons);
        Assert.Contains("camera:Shutter", device.TappedButtons);
        Assert.Equal(2, summary.Screens); // home and camera; the system dialog isn't an app screen
    }

    [Fact]
    public async Task Loading_screens_are_waited_out_and_popups_dismissed()
    {
        using var dir = new TempDir();
        var device = new MockDevice(
        [
            Screen("home", "Home", Btn("Feed", 0, "loading"), Btn("Promo", 1, "promo")),
            Screen("loading", "", new MockButton("", new SKRectI(490, 1150, 590, 1250), null, MockElementKind.Spinner)) with { AutoAdvanceAfterDumps = 2, AutoAdvanceTo = "feed" },
            Screen("feed", "Feed", Btn("Like", 0, null), Btn("Comment", 1, null)),
            Screen("promo", "Enjoying Mock? Rate this app!", Btn("Rate now", 0, null), Btn("Not now", 1, "!back")),
        ]);
        var recorder = new Recorder();
        var summary = await Agent(device, dir.NewProject(), recorder).RunAsync(Options(), new AgentControl(), CancellationToken.None);

        Assert.Contains("feed:Like", device.TappedButtons);
        Assert.Contains("promo:Not now", device.TappedButtons);
        Assert.DoesNotContain("promo:Rate now", device.TappedButtons);
        Assert.Contains(recorder.Log, l => l.Message.StartsWith("Waiting for the screen to finish loading", StringComparison.Ordinal));
        Assert.DoesNotContain("feed:Comment", device.TappedButtons); // blocked by the guardrails ("comment")
        Assert.Equal(2, summary.Screens); // home and feed; loading and the popup aren't kept
    }

    [Fact]
    public async Task Crashes_are_survived_then_capped()
    {
        using var dir = new TempDir();
        MockScreen[] app = [Screen("home", "Home", Btn("Broken", 0, "!crash"), Btn("Fine", 1, "fine")), Screen("fine", "Fine", Btn("Ok", 0, null))];

        var recorder = new Recorder();
        var survived = await Agent(new MockDevice(app), dir.NewProject("A"), recorder).RunAsync(Options(), new AgentControl(), CancellationToken.None);
        Assert.Equal(AgentRunStatus.Completed, survived.Status);
        Assert.Contains(recorder.Log, l => l.Message.StartsWith("The app closed unexpectedly after: Tap", StringComparison.Ordinal));

        var capped = await Agent(new MockDevice(app), dir.NewProject("B"), new Recorder()).RunAsync(Options() with { MaxCrashes = 1 }, new AgentControl(), CancellationToken.None);
        Assert.Equal(AgentRunStatus.Failed, capped.Status);
        Assert.Contains("crashed 1 times", capped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_fields_get_sample_text_and_the_keyboard_is_closed()
    {
        using var dir = new TempDir();
        var device = new MockDevice([Screen("home", "Home", Btn("New note", 0, "edit")), Screen("edit", "Edit", Btn("Note title", 0, null, MockElementKind.TextField, "com.example.mock:id/title"), Btn("Save", 1, null))]);
        await Agent(device, dir.NewProject(), new Recorder()).RunAsync(Options(), new AgentControl(), CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(device.TypedText("edit", "Note title")));
        Assert.False(device.KeyboardShown, string.Join(" | ", device.Actions));
        Assert.Single(device.Actions, a => a.StartsWith("text ", StringComparison.Ordinal)); // typed once, not again after the text changed
    }

    [Fact]
    public async Task Payment_screens_are_handed_to_the_user_and_never_filled_in()
    {
        using var dir = new TempDir();
        var device = new MockDevice(
        [
            Screen("home", "Home", Btn("Upgrade", 0, "pay"), Btn("Notes", 1, "notes")),
            Screen("pay", "Checkout", Btn("Card number", 0, null, MockElementKind.TextField, "com.example.mock:id/card"), Btn("Pay now", 1, null)),
            Screen("notes", "Notes", Btn("First note", 0, null)),
        ]);
        var recorder = new Recorder();
        using var control = new AgentControl();
        recorder.DuringTakeover = _ => Task.Run(async () =>
        {
            await Task.Delay(50); // the user looks, does nothing, and presses Resume
            control.Resume();
        });

        var summary = await Agent(device, dir.NewProject(), recorder).RunAsync(Options(), control, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, summary.Status);
        Assert.Single(recorder.Takeovers, t => t.Contains("payment", StringComparison.OrdinalIgnoreCase)); // once per screen
        Assert.True(string.IsNullOrEmpty(device.TypedText("pay", "Card number")), "the card field must never be filled in");
        Assert.Contains(recorder.Log, l => l.Message == "Leaving the payment screen without touching it.");
        Assert.Contains(recorder.Log, l => l.Message.StartsWith("New screen", StringComparison.Ordinal) && l.Message.Contains("Notes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Discovered_screens_reach_Design_only_when_moved_and_can_be_cleared()
    {
        using var dir = new TempDir();
        var project = dir.NewProject();
        var manual = ScreenRepository.Add(project, [1, 2, 3], "manual", ".Main", 10, 10);
        Assert.True(manual.InDesign); // a manual capture goes straight to Design

        await Agent(new MockDevice(), project, new Recorder()).RunAsync(Options(), new AgentControl(), CancellationToken.None);
        var store = new AgentRunStore(project);
        var run = store.LatestRun()!;
        var discovered = store.ScreenIds(run.Id);
        Assert.Equal(5, discovered.Count);
        Assert.All(ScreenRepository.List(project).Where(s => s.Source == "agent"), s => Assert.False(s.InDesign));

        // "Continue to Design" for the first two only, then clear the exploration.
        ScreenRepository.SetInDesign(project, discovered.Take(2), true);
        Assert.Equal(3, store.ClearHistory()); // the three not moved are deleted
        Assert.Null(store.LatestRun());
        Assert.Equal(3, ScreenRepository.List(project).Count); // manual + the two moved
        Assert.All(ScreenRepository.List(project), s => Assert.True(s.InDesign));

        // "Clear design" takes everything off the page but keeps the files.
        ScreenRepository.SetInDesign(project, null, false);
        Assert.All(ScreenRepository.List(project), s => Assert.False(s.InDesign));
        Assert.All(ScreenRepository.List(project), s => Assert.True(File.Exists(s.FullPath(project))));
    }

    // ---------------------------------------------------------------- control, resume, limits

    [Fact]
    public async Task User_takeover_pauses_and_continues_without_a_false_link()
    {
        using var dir = new TempDir();
        var device = new MockDevice();
        var recorder = new Recorder();
        using var control = new AgentControl();
        control.TakeOver("You took over.");
        var run = Agent(device, dir.NewProject(), recorder).RunAsync(Options(), control, CancellationToken.None);
        // Wait until the agent says it is paused (startup time varies), then check it stays waiting.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !recorder.Log.ToArray().Any(l => l.Message.StartsWith("Paused while you have control", StringComparison.Ordinal)))
        {
            await Task.Delay(50);
        }

        await Task.Delay(300);
        Assert.False(run.IsCompleted); // waiting for the user
        control.Resume();
        var summary = await run;

        Assert.Equal(AgentRunStatus.Completed, summary.Status);
        Assert.Contains(recorder.Log, l => l.Message.StartsWith("Paused while you have control", StringComparison.Ordinal));
        Assert.Contains(recorder.Log, l => l.Message == "Continuing after manual control.");
    }

    [Fact]
    public async Task A_stopped_run_resumes_where_it_left_off()
    {
        using var dir = new TempDir();
        var project = dir.NewProject();
        var first = Agent(new MockDevice(), project, new Recorder());
        var partial = await first.RunAsync(Options(maxSteps: 4), new AgentControl(), CancellationToken.None);
        Assert.Equal(AgentRunStatus.LimitReached, partial.Status);
        var foundFirst = partial.Screens;
        Assert.InRange(foundFirst, 1, 4);

        var store = new AgentRunStore(project);
        var latest = store.LatestRun()!;
        Assert.True(latest.CanResume);

        var device = new MockDevice();
        var resumed = await Agent(device, project, new Recorder()).RunAsync(Options() with { ResumeRunId = latest.Id }, new AgentControl(), CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, resumed.Status);
        Assert.Equal(5, resumed.Screens);
        Assert.Equal(5, ScreenRepository.List(project).Count(s => s.Source == "agent")); // no duplicate captures
        Assert.Equal(5, store.Screens(store.LatestRun()!.Id).Count);
        Assert.False(store.LatestRun()!.CanResume);
    }

    [Fact]
    public async Task Interrupted_runs_are_marked_on_the_next_start()
    {
        using var dir = new TempDir();
        var project = dir.NewProject();
        var store = new AgentRunStore(project);
        store.StartRun("Systematic (no AI)"); // never finished: SSGen "crashed"
        var next = store.StartRun("Systematic (no AI)");
        using var c = project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT status FROM agent_runs WHERE id < $id;";
        cmd.Parameters.AddWithValue("$id", next);
        Assert.Equal("interrupted", (string)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task No_progress_limit_ends_a_run_that_finds_nothing_new()
    {
        using var dir = new TempDir();
        var llm = new ScriptedLlm(_ => """{"thought":"hmm","screen_label":"Home","action":"wait","element_id":null,"text":null,"direction":null,"risk":"none"}""");
        var summary = await Agent(new MockDevice(), dir.NewProject(), new Recorder(), new AiStrategy(llm, false, _ => { }))
            .RunAsync(Options() with { NoProgressLimit = 4 }, new AgentControl(), CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, summary.Status);
        Assert.Contains("in the last 4 steps", summary.Message, StringComparison.Ordinal);
    }
}

public class ClassifierAndHelpersTests
{
    private static UiElement El(string text, string id = "", bool password = false, string cls = "android.widget.Button", bool editable = false) => new()
    {
        Class = editable || password ? "android.widget.EditText" : cls, Text = text, ResourceId = id, Clickable = true, Password = password,
        Bounds = new UiBounds(0, 0, 100, 100),
    };

    private static UiSnapshot Ui(string package, bool spinner, params UiElement[] els) =>
        new(els.Select((e, i) => e with { Id = i + 1 }).ToList(), els.Select(e => e.Text).ToList(), package, spinner);

    [Fact]
    public void Classifies_interruptions()
    {
        const string app = "com.app";
        var perm = ScreenClassifier.Classify(Ui("com.google.android.permissioncontroller", false,
            El("Allow", "com.android.permissioncontroller:id/permission_allow_button"),
            El("While using the app", "com.android.permissioncontroller:id/permission_allow_foreground_only_button"),
            El("Don't allow", "com.android.permissioncontroller:id/permission_deny_button")), null, app);
        Assert.Equal(InterruptKind.PermissionDialog, perm.Kind);
        Assert.Equal("While using the app", perm.Target!.Text);

        var anr = ScreenClassifier.Classify(Ui("android", false, El("Close app", "android:id/aerr_close"), El("Wait", "android:id/aerr_wait")) with { AllText = ["Mock isn't responding"] }, null, app);
        Assert.Equal(InterruptKind.NotResponding, anr.Kind);
        Assert.Equal("Wait", anr.Target!.Text);

        var crash = ScreenClassifier.Classify(Ui("android", false, El("Close app", "android:id/aerr_close")) with { AllText = ["Mock keeps stopping"] }, null, app);
        Assert.Equal(InterruptKind.CrashDialog, crash.Kind);

        Assert.Equal(InterruptKind.Ad, ScreenClassifier.Classify(Ui(app, false, El("", "com.app:id/ad_close_button"), El("Install now")), null, app).Kind);
        Assert.Equal(InterruptKind.Popup, ScreenClassifier.Classify(Ui(app, false, El("Rate us"), El("Not now")) with { AllText = ["Enjoying the app? Rate us", "Not now"] }, null, app).Kind);
        Assert.Equal(InterruptKind.Login, ScreenClassifier.Classify(Ui(app, false, El("Email", editable: true), El("", password: true), El("Sign in")), null, app).Kind);
        Assert.Equal(InterruptKind.Loading, ScreenClassifier.Classify(Ui(app, true), null, app).Kind);

        // Ordinary screens, including a "Sign in" button inside a busy screen, aren't interruptions.
        Assert.Equal(InterruptKind.None, ScreenClassifier.Classify(Ui(app, false, El("Home"), El("Search"), El("Profile"), El("Settings"), El("Feed"), El("Sign in"), El("Help")), null, app).Kind);
        Assert.Equal(InterruptKind.None, ScreenClassifier.Classify(Ui(app, true, El("Home"), El("Search"), El("Profile")), null, app).Kind);
    }

    [Theory]
    [InlineData("email_input", "Email", "alex@example.com")]
    [InlineData("phone", "Mobile number", "5550100")]
    [InlineData("search_src_text", "Search notes", "plan")]
    [InlineData("amount", "Amount", "12")]
    [InlineData("etContent", "Note", "Milk, eggs and coffee. Pick up flowers on Saturday.")]
    [InlineData("etName", "Note 1", "Weekly groceries")]
    public void Demo_text_fits_the_field(string id, string hint, string expected) =>
        Assert.Equal(expected, DemoText.For(new UiElement { Class = "android.widget.EditText", ResourceId = "com.app:id/" + id, Text = hint }));

    [Fact]
    public void A_screen_s_own_items_come_before_shared_ones()
    {
        static UiElement B(int id, string label, int top) => new()
        {
            Id = id, Class = "android.widget.Button", Text = label, Clickable = true, Bounds = new UiBounds(0, top, 100, top + 80),
        };

        var graph = new ScreenGraph();
        var (main, _) = graph.Observe(new ScreenFingerprint(".Main", 1, new HashSet<string> { "m1", "m2", "m3" }, new HashSet<string>()));
        var menuButton = B(1, "Open menu", 0);
        main.Known.Add(menuButton.Key);
        var (popup, _) = graph.Observe(new ScreenFingerprint(".Main", 1, new HashSet<string> { "p1", "p2", "p3" }, new HashSet<string>()));
        var ui = new UiSnapshot([menuButton, B(2, "New sketch", 500)], [], "com.app");
        popup.Known.UnionWith(ui.Elements.Select(e => e.Key));

        var decision = SystematicStrategy.Decide(new AgentContext("App", "com.app", null, 1, 10, graph, popup, ui, [], []));
        Assert.Equal("New sketch", decision.Action.Element!.Label); // not the menu button the main screen also has
    }

    [Fact]
    public void Sample_text_never_trips_the_guardrails()
    {
        var guard = new Guardrails(safeMode: true);
        foreach (var sample in DemoText.AllSamples)
        {
            var el = new UiElement { Class = "android.widget.TextView", Text = sample, Clickable = true, Bounds = new UiBounds(0, 0, 10, 10) };
            Assert.Equal(GuardVerdict.Allowed, guard.Check(new AgentDecision(new AgentAction(ActionKind.Tap, el), ""), new UiSnapshot([el], [sample], "com.app")).Verdict);
        }
    }

    [Fact]
    public void A_popup_over_a_screen_is_a_different_screen()
    {
        var screen = new HashSet<string> { "a", "b", "c", "d", "e" };
        var withMenu = new HashSet<string> { "x", "y", "z" };
        var baseFp = new ScreenFingerprint(".Main", 0xABCDEF, screen, new HashSet<string>());
        var popupFp = new ScreenFingerprint(".Main", 0xABCDEF, withMenu, new HashSet<string>()); // same pixels (tiny menu)
        Assert.False(baseFp.Matches(popupFp));
        Assert.True(baseFp.Matches(baseFp with { DHash = 0xABCDEE }));
    }

    [Fact]
    public async Task Test_account_never_reaches_the_ai()
    {
        var ui = new UiSnapshot([new UiElement { Id = 1, Class = "android.widget.EditText", Text = "alex@test.dev", Clickable = true, Bounds = new UiBounds(0, 0, 10, 10) }], ["Signed in as alex@test.dev"], "com.app");
        var graph = new ScreenGraph();
        var (node, _) = graph.Observe(new ScreenFingerprint(".Main", 1, new HashSet<string>(), new HashSet<string>()));
        var llm = new ScriptedLlm("""{"thought":"x","screen_label":"Profile","action":"back","element_id":null,"text":null,"direction":null,"risk":"none"}""");
        var ctx = new AgentContext("App", "com.app", null, 1, 10, graph, node, ui, [], []) { Secrets = ["alex@test.dev"], TargetScreens = 6, StepsWithoutNewScreen = 12 };
        await new AiStrategy(llm, vision: false, _ => { }).DecideAsync(ctx, CancellationToken.None);

        var prompt = llm.Requests[0].Messages[0].Text;
        Assert.DoesNotContain("alex@test.dev", prompt, StringComparison.Ordinal);
        Assert.Contains("[hidden]", prompt, StringComparison.Ordinal);
        Assert.Contains("about 6 store screenshots", prompt, StringComparison.Ordinal);
        Assert.Contains("No new screen in the last 12 steps", prompt, StringComparison.Ordinal);
    }
}
