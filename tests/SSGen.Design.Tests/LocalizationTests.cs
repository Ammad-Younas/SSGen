using SkiaSharp;
using SSGen.Scene;

namespace SSGen.Design.Tests;

public class LocalizationTests
{
    [Theory]
    [InlineData("اپنے کام آسانی سے منظم کریں", true)]  // Urdu
    [InlineData("نظّم مهامك بسهولة", true)]            // Arabic
    [InlineData("סדר את המשימות שלך", true)]            // Hebrew
    [InlineData("अपने काम व्यवस्थित करें", true)]       // Hindi
    [InlineData("Organiza tus tareas", false)]
    [InlineData("Упорядочьте задачи", false)]
    public void Scripts_that_join_or_reorder_are_shaped_and_get_a_font_that_has_them(string text, bool shaped)
    {
        Assert.Equal(shaped, SSGen.Scene.TextLayout.NeedsShaping(text));
        var layer = new TextLayer { Text = text, Font = "Poppins", Weight = 700 };
        var face = SSGen.Scene.TextLayout.Face(layer);
        Assert.True(Fonts.Covers(face, text), $"{face.FamilyName} lacks glyphs for {text}");
    }

    [Fact]
    public void Right_to_left_text_renders_joined_and_right_aligned()
    {
        var scene = new SceneDocument
        {
            Width = 1000,
            Height = 300,
            Background = new SolidFill("#FFFFFFFF"),
            Layers =
            {
                new TextLayer
                {
                    Text = "اپنے کام آسانی سے منظم کریں", X = 20, Y = 20, W = 960, H = 260, Size = 70, MinSize = 30,
                    Color = "#FF000000", Align = TextAlign.Left, RightToLeft = true, VerticalAlign = VerticalAlign.Middle,
                },
            },
        };
        using var assets = new FileAssetResolver();
        using var image = SceneRenderer.Render(scene, assets);
        using var bitmap = SKBitmap.FromImage(image);
        SaveForReview(image, "rtl-urdu.png");

        // "Left" means "start" for right-to-left text: the ink sits on the right half.
        var (left, right) = (0, 0);
        for (var y = 0; y < bitmap.Height; y += 2)
        {
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                if (bitmap.GetPixel(x, y).Red < 128)
                {
                    if (x < bitmap.Width / 2)
                    {
                        left++;
                    }
                    else
                    {
                        right++;
                    }
                }
            }
        }

        Assert.True(right > 0, "nothing was drawn");
        Assert.True(right > left, $"expected right-aligned text (left {left}, right {right})");

        // Shaped Arabic-script text is narrower than its isolated letters laid side by side.
        using var font = new SKFont(SSGen.Scene.TextLayout.Face((TextLayer)scene.Layers[0]), 70);
        var text = ((TextLayer)scene.Layers[0]).Text;
        Assert.True(SSGen.Scene.TextLayout.Measure(font, text, 0) < font.MeasureText(text) * 1.2f);
    }

    [Fact]
    public void Text_without_spaces_wraps_between_characters()
    {
        var layer = new TextLayer { Text = "轻松管理您的所有任务和项目", Font = "Poppins", W = 300, H = 400, Size = 60, MinSize = 40, MaxLines = 4 };
        var block = SSGen.Scene.TextLayout.Fit(layer);
        Assert.True(block.Lines.Count > 1);
        Assert.False(block.Truncated);
        Assert.Equal(layer.Text, string.Concat(block.Lines));
    }

    [Fact]
    public void Each_language_renders_into_its_own_folder_and_rows()
    {
        using var p = new TempProject();
        var shots = Enumerable.Range(0, 2).Select(i => p.AddScreen(TestImages.Screenshot(new SKColor(0x50, 0x47, 0xE5), seed: i), $"x/.S{i}Activity", $"Screen {i}")).ToList();
        var catalog = SSGen.Templates.StorePresetCatalog.BuiltIn();
        var presets = new[] { catalog.Find("play-phone")!, catalog.Find("play-icon")! };
        var icon = TestImages.Save(p.Folder, "icon.png", TestImages.Icon(new SKColor(0x50, 0x47, 0xE5)));
        SSGen.Templates.RenderRequest Request(string locale, string headline, bool rtl) => new()
        {
            Presets = presets,
            Template = SSGen.Templates.TemplateLibrary.Find("headline-top")!,
            Items = shots.Select(s => new SSGen.Templates.RenderItem(s.Id, s.FullPath(p.Project), 1080, 2400, s.Title!, headline, "")).ToList(),
            IconFile = icon,
            Locale = locale,
            RightToLeft = rtl,
        };

        SSGen.Templates.RenderService.Render(p.Project, Request("", "Organize your tasks", false));
        var urdu = SSGen.Templates.RenderService.Render(p.Project, Request("ur", "اپنے کام منظم کریں", true));
        Assert.False(urdu.HasErrors);
        Assert.DoesNotContain(urdu.Images, i => i.Preset.Id == "play-icon"); // the icon has no language

        var phone = Path.Combine(p.Project.Folder, "renders", "google_play", "phone");
        Assert.Equal(2, Directory.GetFiles(phone, "*.png").Length);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(phone, "ur"), "*.png").Length);

        var records = SSGen.Templates.RenderStore.List(p.Project);
        Assert.Equal(2, records.Count(r => r.Locale == "ur"));
        Assert.Equal(3, records.Count(r => r.Locale == "")); // 2 screenshots + icon
        var scene = SSGen.Templates.RenderStore.LoadScene(p.Project, records.First(r => r.Locale == "ur" && r.Editable));
        var headline = scene.Layers.OfType<TextLayer>().Single(l => l.Id == "headline");
        Assert.True(headline.RightToLeft);

        // Rendering the primary language again leaves the Urdu set alone.
        SSGen.Templates.RenderService.Render(p.Project, Request("", "Plan your day", false));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(phone, "ur"), "*.png").Length);
        Assert.Equal(2, SSGen.Templates.RenderStore.List(p.Project).Count(r => r.Locale == "ur"));
    }

    [Fact]
    public async Task Captions_are_translated_stored_and_languages_remembered()
    {
        using var p = new TempProject();
        var a = p.AddScreen(TestImages.Screenshot(SKColors.Teal, seed: 1), "x/.A", "A");
        var b = p.AddScreen(TestImages.Screenshot(SKColors.Teal, seed: 2), "x/.B", "B");

        Assert.Equal("en-US", SSGen.Selection.Languages.Primary(p.Project).Code);
        SSGen.Selection.Languages.SetAdditional(p.Project, ["ur", "de-DE", "en-US"]);
        Assert.Equal(["ur", "de-DE"], SSGen.Selection.Languages.Additional(p.Project).Select(l => l.Code));
        Assert.True(SSGen.Selection.Languages.Find("ur").RightToLeft);
        Assert.True(SSGen.Selection.Languages.Find("ps-AF").RightToLeft);

        var calls = 0;
        var llm = new FakeLlm(_ => ++calls == 1
            ? """{"captions":[{"headline":"Only one"}]}"""   // wrong count: must be retried
            : """Here you go: {"captions":[{"headline":"\"Aufgaben im Griff.\"","subheadline":""},{"headline":"Alles an einem Ort","subheadline":"Projekte und Listen"}]}""");
        var translated = await new SSGen.Selection.CaptionTranslator(llm).TranslateAsync(
            [new(a.Id, "Tasks under control", ""), new(b.Id, "Everything in one place", "Projects and lists")],
            SSGen.Selection.Languages.Find("en-US"), SSGen.Selection.Languages.Find("de-DE"), "Smart Task", CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal("Aufgaben im Griff", translated[0].Headline);   // quotes and full stop removed
        Assert.Equal("", translated[0].Subheadline);
        Assert.Contains("Smart Task", llm.Requests[0].Messages[0].Text, StringComparison.Ordinal);

        var store = new SSGen.Selection.CaptionStore(p.Project);
        foreach (var c in translated)
        {
            store.Save(c);
        }

        store.Save(translated[1] with { Headline = "Alles an einem Platz", Source = "manual" });
        var loaded = store.Load("de-DE");
        Assert.Equal("Alles an einem Platz", loaded[b.Id].Headline);
        Assert.Equal("manual", loaded[b.Id].Source);
        Assert.Empty(store.Load("ur"));
    }

    [Fact]
    public void Export_copies_sets_per_store_language_and_size_with_a_manifest_and_zip()
    {
        using var p = new TempProject();
        var shots = Enumerable.Range(0, 3).Select(i => p.AddScreen(TestImages.Screenshot(SKColors.Teal, seed: i), $"x/.S{i}Activity", $"Screen {i}")).ToList();
        var catalog = SSGen.Templates.StorePresetCatalog.BuiltIn();
        foreach (var locale in new[] { "", "ur" })
        {
            SSGen.Templates.RenderService.Render(p.Project, new SSGen.Templates.RenderRequest
            {
                Presets = [catalog.Find("play-phone")!],
                Template = SSGen.Templates.TemplateLibrary.Find("minimal")!,
                Items = shots.Select(s => new SSGen.Templates.RenderItem(s.Id, s.FullPath(p.Project), 1080, 2400, s.Title!, "Headline", "")).ToList(),
                Locale = locale,
                RightToLeft = locale == "ur",
            });
        }

        // One file goes missing: it's reported and skipped, and the count rule still holds (2 of 2..8).
        File.Delete(SSGen.Templates.RenderStore.List(p.Project).First(r => r.Locale == "ur").FullPath(p.Project));

        var destination = Path.Combine(p.Folder, "out");
        var result = SSGen.Templates.ExportService.Export(p.Project, catalog, new SSGen.Templates.ExportRequest
        {
            PresetIds = ["play-phone"],
            Locales = ["", "ur"],
            Destination = destination,
            PrimaryLocale = "en-US",
            Zip = true,
            AppName = "Smart Task",
            PackageName = "com.example.smarttask",
        });

        Assert.Equal(5, result.Files.Count);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(Path.Combine(result.Folder, "google_play", "en-US", "phone", "01_screen_0.png")));
        Assert.True(File.Exists(Path.Combine(result.Folder, "google_play", "ur", "phone", "02_screen_1.png")));
        Assert.StartsWith("smart_task_", Path.GetFileName(result.Folder), StringComparison.Ordinal);
        Assert.Contains(result.Issues, i => i.Message.Contains("was not exported", StringComparison.Ordinal));

        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Folder, "manifest.json")));
        var root = manifest.RootElement;
        Assert.Equal("com.example.smarttask", root.GetProperty("app").GetProperty("package").GetString());
        Assert.Equal(catalog.Verified, root.GetProperty("storeRules").GetProperty("verified").GetString());
        Assert.Equal(5, root.GetProperty("files").GetArrayLength());
        Assert.Equal(64, root.GetProperty("files")[0].GetProperty("sha256").GetString()!.Length);

        Assert.True(File.Exists(result.ZipFile));
        using var zip = System.IO.Compression.ZipFile.OpenRead(result.ZipFile!);
        Assert.Contains(zip.Entries, e => e.FullName.EndsWith("manifest.json", StringComparison.Ordinal));
        Assert.Equal(6, zip.Entries.Count(e => e.Length > 0));
    }

    internal static void SaveForReview(SKImage image, string name)
    {
        if (Environment.GetEnvironmentVariable("SSGEN_RENDER_OUT") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name), SceneRenderer.Encode(image, ImageFormat.Png));
        }
    }
}
