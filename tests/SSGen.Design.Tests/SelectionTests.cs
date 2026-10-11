using System.Runtime.CompilerServices;
using SkiaSharp;
using SSGen.Ai;
using SSGen.Core.Data;
using SSGen.Core.Projects;
using SSGen.Scene;
using SSGen.Selection;
using SSGen.Templates;

namespace SSGen.Design.Tests;

internal sealed class TempProject : IDisposable
{
    public TempProject()
    {
        Folder = Path.Combine(Path.GetTempPath(), "ssgen-design-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        var appDb = new SqliteDatabase(Path.Combine(Folder, "app.db"), MigrationSet.App);
        appDb.Migrate();
        Project = new ProjectService(appDb).Create("P", Folder);
    }

    public string Folder { get; }

    public Project Project { get; }

    public CapturedScreen AddScreen(byte[] png, string activity, string? title = null, string? text = null)
    {
        var s = ScreenRepository.Add(Project, png, "agent", activity, 1080, 2400);
        ScreenRepository.SetText(Project, s.Id, title, text);
        return ScreenRepository.List(Project).Single(x => x.Id == s.Id);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Replies from a function of the request; counts calls.</summary>
internal sealed class FakeLlm : ILlmProvider
{
    private readonly Func<LlmRequest, string> _reply;

    public FakeLlm(Func<LlmRequest, string> reply) => _reply = reply;

    public List<LlmRequest> Requests { get; } = [];

    public ProviderType Type => ProviderType.OpenAICompatible;

    public string Model => "fake";

    public bool IsLocal => true;

    public ProviderCapabilities Capabilities => new(true, false);

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(new LlmResponse(_reply(request), new LlmUsage(1000, 200)));
    }

    public async IAsyncEnumerable<string> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return (await CompleteAsync(request, ct)).Text;
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(["fake"]);

    public Task<bool?> DetectVisionAsync(CancellationToken ct = default) => Task.FromResult<bool?>(true);
}

public class SelectionTests
{
    private static readonly SKColor Teal = new(0x0F, 0x76, 0x6E);
    private static readonly string[] PresetIds = ["play-phone", "play-feature-graphic", "play-icon", "as-iphone-6.9"];

    private static byte[] Blank()
    {
        using var surface = SKSurface.Create(new SKImageInfo(1080, 2400));
        surface.Canvas.Clear(SKColors.White);
        using var img = surface.Snapshot();
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    [Fact]
    public void Metrics_tell_blank_from_rich_screens()
    {
        var blank = ScreenMetrics.Analyze(Blank());
        var rich = ScreenMetrics.Analyze(TestImages.Screenshot(Teal));
        Assert.True(blank.IsBlank);
        Assert.False(rich.IsBlank);
        Assert.True(rich.Richness > blank.Richness);
        Assert.True(rich.Colorfulness > blank.Colorfulness);
    }

    [Fact]
    public void Built_in_ranking_rejects_and_orders_a_story()
    {
        using var p = new TempProject();
        var home = p.AddScreen(TestImages.Screenshot(Teal, seed: 1), "org.app/.ui.main.MainActivity", "Notes", string.Join("\n", Enumerable.Range(1, 20).Select(i => $"Note {i}")));
        var detail = p.AddScreen(TestImages.Screenshot(new SKColor(0xB4, 0x23, 0x18), seed: 3), "org.app/.ui.notes.TextNoteActivity", "Text note");
        var settings = p.AddScreen(TestImages.Screenshot(new SKColor(0x4B, 0x55, 0x63), seed: 5), "org.app/.ui.settings.SettingsActivity", "Settings");
        var login = p.AddScreen(TestImages.Screenshot(Teal, seed: 7), "org.app/.auth.LoginActivity", "Sign in");
        var blank = p.AddScreen(Blank(), "org.app/.ui.EmptyActivity");
        var dup = p.AddScreen(TestImages.Screenshot(Teal, seed: 1), "org.app/.ui.main.MainActivity", "Notes");

        var candidates = ScreenRepository.List(p.Project).Select(s => ScreenCandidate.Load(p.Project, s)).ToList();
        var ranked = HeuristicRanker.Rank(candidates).ToDictionary(a => a.ScreenId);

        Assert.Equal(RejectReason.Login, ranked[login.Id].Reject);
        Assert.Equal(RejectReason.Blank, ranked[blank.Id].Reject);
        Assert.Equal(RejectReason.Duplicate, ranked[dup.Id].Reject ?? ranked[home.Id].Reject); // one of the twins is dropped
        Assert.Equal(StoryRole.Settings, ranked[settings.Id].Role);
        Assert.Equal("Text note", ranked[detail.Id].Headline);

        var story = StoryPicker.Pick(ranked.Values.ToList(), count: 6);
        Assert.Equal(3, story.Count);                       // only usable screens
        Assert.Equal(StoryRole.Hero, story[0].Role);
        Assert.Equal(settings.Id, story[^1].ScreenId);      // settings last
        Assert.DoesNotContain(story, a => a.ScreenId == login.Id || a.ScreenId == blank.Id);
    }

    [Theory]
    [InlineData("org.app/.ui.manageCategories.ManageCategoriesActivity", "Manage categories")]
    [InlineData("com.x/.HomeFragment", "Home")]
    [InlineData(null, "Screen")]
    public void Screen_names_from_activities(string? activity, string expected) => Assert.Equal(expected, ScreenCandidate.Humanize(activity));

    private static string Reply(int count, string headlinePrefix = "Great", int? skip = null) =>
        "{\"screens\":[" + string.Join(",", Enumerable.Range(1, count).Where(i => i != skip).Select(i =>
            $"{{\"screen\":{i},\"core\":{10 - i},\"visual\":8,\"richness\":7,\"uniqueness\":6,\"conversion\":{10 - i},\"reject\":{(i == 2 ? "\"sensitive\"" : "null")},\"settings\":false,\"label\":\"Screen {i}\",\"headline\":\"{headlinePrefix} screen number {i}\",\"subheadline\":\"Sub {i}\",\"reasoning\":\"Because.\"}}")) + "]}";

    [Fact]
    public async Task Ai_ranking_batches_downscales_caches_and_maps_scores()
    {
        using var p = new TempProject();
        for (var i = 0; i < 8; i++)
        {
            p.AddScreen(TestImages.Screenshot(new SKColor((byte)(30 * i), 0x76, 0x6E), seed: i + 10), $"org.app/.A{i}Activity", $"Screen {i}");
        }

        var candidates = ScreenRepository.List(p.Project).Select(s => ScreenCandidate.Load(p.Project, s)).ToList();
        var llm = new FakeLlm(req => Reply(req.Messages[0].Images?.Count ?? 0));
        var spent = LlmUsage.Zero;
        var ranker = new AiRanker(llm, vision: true, new VisionCache(p.Project), u => spent += u);
        var ranked = await ranker.RankAsync(candidates, new RankingContext("Notes", "a notes app", 6), CancellationToken.None);

        Assert.Equal(2, llm.Requests.Count);                           // 6 + 2
        Assert.Equal(6, llm.Requests[0].Messages[0].Images!.Count);
        Assert.All(llm.Requests[0].Messages[0].Images!, img => Assert.Equal("image/jpeg", img.MediaType));
        using (var decoded = SKBitmap.Decode(llm.Requests[0].Messages[0].Images![0].Data))
        {
            Assert.True(Math.Max(decoded.Width, decoded.Height) <= 640); // downscaled before sending
        }

        Assert.Contains("a notes app", llm.Requests[0].Messages[0].Text, StringComparison.Ordinal);
        Assert.Equal(new LlmUsage(2000, 400), spent);
        var first = ranked.Single(a => a.ScreenId == candidates[0].Id);
        Assert.Equal("ai", first.Source);
        Assert.Equal(((9 * 0.3) + (8 * 0.2) + (7 * 0.2) + (6 * 0.1) + (9 * 0.2)) * 10, first.Score, 1);
        Assert.Equal("sensitive", ranked.Single(a => a.ScreenId == candidates[1].Id).Reject);

        // Same images, model and prompt: answered from the cache, no tokens.
        var again = await new AiRanker(llm, true, new VisionCache(p.Project), u => spent += u).RankAsync(candidates, new RankingContext("Notes", "a notes app", 6), CancellationToken.None);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Equal(ranked.Select(a => a.Score), again.Select(a => a.Score));
    }

    [Fact]
    public async Task Ai_ranking_retries_when_a_screen_is_missing_and_polishes_captions()
    {
        using var p = new TempProject();
        for (var i = 0; i < 3; i++)
        {
            p.AddScreen(TestImages.Screenshot(Teal, seed: i + 20), $"org.app/.B{i}Activity");
        }

        var candidates = ScreenRepository.List(p.Project).Select(s => ScreenCandidate.Load(p.Project, s)).ToList();
        var calls = 0;
        var llm = new FakeLlm(req => req.System?.Contains("screenshot captions", StringComparison.Ordinal) == true
            ? """{"captions":[{"headline":"Your ideas, organised at last","subheadline":"One"},{"headline":"Find anything in seconds","subheadline":"Two"}]}"""
            : ++calls == 1 ? Reply(3, skip: 3) : Reply(3));
        var ranker = new AiRanker(llm, vision: false, cache: null, _ => { });
        var ranked = await ranker.RankAsync(candidates, new RankingContext("Notes"), CancellationToken.None);
        Assert.Equal(2, calls); // retried with the missing screen named
        Assert.Contains("Missing screenshots: 3", llm.Requests[1].Messages[^1].Text, StringComparison.Ordinal);
        Assert.Null(llm.Requests[0].Messages[0].Images); // text-only mode sends no images

        var picked = StoryPicker.Pick(ranked, 2);
        var polished = await ranker.PolishAsync(picked, new RankingContext("Notes"), CancellationToken.None);
        Assert.Equal("Your ideas, organised at last", polished[0].Headline);
        Assert.Equal(picked.Select(a => a.ScreenId), polished.Select(a => a.ScreenId));
    }

    [Fact]
    public void Selection_round_trips_and_reorders()
    {
        using var p = new TempProject();
        var a = p.AddScreen(TestImages.Screenshot(Teal, seed: 1), "x/.A");
        var b = p.AddScreen(TestImages.Screenshot(Teal, seed: 2), "x/.B");
        var store = new SelectionStore(p.Project);
        var all = new List<ScreenAssessment>
        {
            new(a.Id, 80, null, "A", "Head A", "", "", StoryRole.Hero, "heuristic"),
            new(b.Id, 60, null, "B", "Head B", "", "", StoryRole.Feature, "heuristic"),
        };
        store.SaveRanking(all, all);
        Assert.Equal([a.Id, b.Id], store.Load().Where(i => i.Included).Select(i => i.ScreenId));

        store.SetOrder([b.Id]);
        var items = store.Load();
        Assert.Equal(b.Id, items.Single(i => i.Included).ScreenId);
        store.Save(items.Single(i => i.ScreenId == b.Id) with { Headline = "Edited" });
        Assert.Equal("Edited", store.Load().Single(i => i.ScreenId == b.Id).Headline);
    }

    [Fact]
    public void Render_pipeline_writes_files_scenes_rows_and_validates()
    {
        using var p = new TempProject();
        var shots = Enumerable.Range(0, 3).Select(i => p.AddScreen(TestImages.Screenshot(Teal, seed: i), $"x/.S{i}Activity", $"Screen {i}")).ToList();
        var iconPath = TestImages.Save(p.Folder, "icon.png", TestImages.Icon(Teal));
        var catalog = StorePresetCatalog.BuiltIn();
        var presets = PresetIds.Select(id => catalog.Find(id)!).ToList();
        var result = RenderService.Render(p.Project, new RenderRequest
        {
            Presets = presets,
            Template = TemplateLibrary.Find("duo")!,
            Items = shots.Select(s => new RenderItem(s.Id, s.FullPath(p.Project), 1080, 2400, s.Title!, "Headline " + s.Id, "")).ToList(),
            IconFile = iconPath,
            AppName = "Notes",
            Tagline = "Private notes, lists and sketches",
            Palette = PaletteExtractor.FromImage(File.ReadAllBytes(iconPath)),
        });

        Assert.Equal(3 + 1 + 1 + 3, result.Images.Count);
        Assert.False(result.HasErrors, string.Join(" ", result.SetIssues.Concat(result.Images.SelectMany(i => i.Issues)).Select(i => i.Message)));
        Assert.All(result.Images, img => Assert.True(File.Exists(img.File)));
        var written = string.Join(", ", Directory.GetFiles(Path.Combine(p.Project.Folder, "renders"), "*.png", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(p.Project.Folder, f)));
        Assert.True(File.Exists(Path.Combine(p.Project.Folder, "renders", "google_play", "phone", "01_screen_0.png")), written);
        Assert.True(File.Exists(Path.Combine(p.Project.Folder, "renders", "app_store", "iphone_6_9_dynamic_island_large", "03_screen_2.png")), written);
        var scene = SceneDocument.FromJson(File.ReadAllText(result.Images[0].SceneFile!));
        Assert.Equal("duo", scene.Meta["template"]);

        var icon = result.Images.Single(i => i.Preset.Id == "play-icon");
        Assert.Equal(6, RenderTests.PngColorType(File.ReadAllBytes(icon.File))); // icon keeps alpha
        var fg = result.Images.Single(i => i.Preset.Id == "play-feature-graphic");
        Assert.Equal((1024, 500), (fg.Width, fg.Height));

        using var c = p.Project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM renders;";
        Assert.Equal(8L, (long)cmd.ExecuteScalar()!);

        // Rendering again replaces the previous files instead of piling up.
        RenderService.Render(p.Project, new RenderRequest { Presets = [presets[0]], Template = TemplateLibrary.Find("minimal")!, Items = [new RenderItem(shots[0].Id, shots[0].FullPath(p.Project), 1080, 2400, "Only", "Only", "")] });
        Assert.Single(Directory.GetFiles(Path.Combine(p.Project.Folder, "renders", "google_play", "phone")));
        var count = PresetValidator.ValidateCount(presets[0], 1);
        Assert.Contains(count, i => i.Message.Contains("at least 2", StringComparison.Ordinal));
    }
}

public class DesignHelpersTests
{
    [Fact]
    public void Palette_comes_from_the_icon_and_falls_back_for_grey_icons()
    {
        var red = PaletteExtractor.FromImage(TestImages.Icon(new SKColor(0xD3, 0x2F, 0x2F)));
        var primary = Colors.Parse(red.Primary);
        Assert.True(primary.Red > 150 && primary.Green < 90 && primary.Blue < 90, red.Primary);

        var grey = PaletteExtractor.FromImage(TestImages.Icon(new SKColor(0x80, 0x80, 0x80)));
        Assert.Equal(BrandPalette.Default, grey);
    }

    [Theory]
    [InlineData("#FFD600", Colors.Ink)]
    [InlineData("#0B3D91", Colors.White)]
    [InlineData("#FFFFFF", Colors.Ink)]
    [InlineData("#000000", Colors.White)]
    public void Text_colour_is_chosen_for_contrast(string background, string expected) => Assert.Equal(expected, Colors.TextOn(Colors.Parse(background)));

    [Fact]
    public void Text_shrinks_to_fit_and_truncates_only_as_a_last_resort()
    {
        var layer = new TextLayer { Text = "Keep every note in one place", W = 900, H = 260, Size = 120, MinSize = 40, MaxLines = 3 };
        var block = TextLayout.Fit(layer);
        Assert.False(block.Truncated);
        Assert.True(block.FontSize < 120);
        Assert.True(block.Height <= layer.H + (block.FontSize * 0.25));

        var tiny = new TextLayer { Text = string.Join(' ', Enumerable.Repeat("overflowing", 40)), W = 300, H = 100, Size = 60, MinSize = 30, MaxLines = 2 };
        var cut = TextLayout.Fit(tiny);
        Assert.True(cut.Truncated);
        Assert.True(cut.Lines.Count <= 2);
        Assert.EndsWith("…", cut.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Preset_catalog_is_sourced_dated_and_validates()
    {
        var catalog = StorePresetCatalog.BuiltIn();
        Assert.Equal("2026-10-11", catalog.Verified);
        Assert.All(catalog.Stores, s => Assert.StartsWith("https://", s.Source, StringComparison.Ordinal));
        var phone = catalog.Find("play-phone")!;
        Assert.Empty(PresetValidator.ValidateImage(phone, 1080, 1920, "png", false, 500_000));
        Assert.Contains(PresetValidator.ValidateImage(phone, 1080, 2400, "png", false, 1), i => i.Message.Contains("twice", StringComparison.Ordinal) || i.Message.Contains("times", StringComparison.Ordinal));
        Assert.Contains(PresetValidator.ValidateImage(phone, 1080, 1920, "png", true, 1), i => i.Message.Contains("transparency", StringComparison.Ordinal));
        Assert.Contains(PresetValidator.ValidateImage(phone, 4000, 4000, "png", false, 1), i => i.Message.Contains("3840", StringComparison.Ordinal));

        var iphone = catalog.Find("as-iphone-6.1")!;
        Assert.True(iphone.Required);
        Assert.Empty(PresetValidator.ValidateImage(iphone, 1179, 2556, "jpeg", false, 1));
        Assert.Contains(PresetValidator.ValidateImage(iphone, 1080, 1920, "png", false, 1), i => i.IsError);

        var tablet = catalog.Find("play-tablet-10")!;
        Assert.Contains(PresetValidator.ValidateImage(tablet, 1500, 2560, "png", false, 1), i => i.Message.Contains("9:16", StringComparison.Ordinal));

        var icon = catalog.Find("play-icon")!;
        Assert.Contains(PresetValidator.ValidateImage(icon, 512, 512, "png", true, 2_000_000), i => i.Message.Contains("KB", StringComparison.Ordinal));
    }

    [Fact]
    public void User_override_file_wins_and_a_broken_one_falls_back()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ssgen-presets-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "store-presets.json");
        StorePresetCatalog.ExportDefault(file);
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"verified\": \"2026-10-11\"", "\"verified\": \"2030-01-01\"", StringComparison.Ordinal));
        Assert.Equal("2030-01-01", StorePresetCatalog.Load(file).Verified);

        File.WriteAllText(file, "{ not json");
        Assert.Equal("2026-10-11", StorePresetCatalog.Load(file).Verified);
        Directory.Delete(dir, true);
    }
}
