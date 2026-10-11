using SkiaSharp;
using SSGen.Scene;
using SSGen.Templates;

namespace SSGen.Design.Tests;

public sealed class RenderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ssgen-design-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Inputs for a set: real screenshots/icon from SSGEN_SAMPLE_SHOTS / SSGEN_SAMPLE_ICON when set, else synthetic ones.</summary>
    private (List<string> Shots, string Icon) Samples()
    {
        var shots = Environment.GetEnvironmentVariable("SSGEN_SAMPLE_SHOTS") is { Length: > 0 } folder && Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.png").Order().Take(6).ToList()
            : [TestImages.Save(_dir, "s1.png", TestImages.Screenshot(new SKColor(0x0F, 0x76, 0x6E))), TestImages.Save(_dir, "s2.png", TestImages.Screenshot(new SKColor(0x0F, 0x76, 0x6E), seed: 2))];
        var icon = Environment.GetEnvironmentVariable("SSGEN_SAMPLE_ICON") is { Length: > 0 } i && File.Exists(i) ? i : TestImages.Save(_dir, "icon.png", TestImages.Icon(new SKColor(0x0F, 0x76, 0x6E)));
        return (shots, icon);
    }

    [Fact]
    public void There_are_at_least_ten_screenshot_templates_and_a_feature_graphic()
    {
        Assert.True(TemplateLibrary.Screenshots.Count() >= 10, string.Join(", ", TemplateLibrary.BuiltIn.Select(t => t.Id)));
        Assert.Contains(TemplateLibrary.BuiltIn, t => t.IsFeatureGraphic);
        Assert.Equal(TemplateLibrary.BuiltIn.Count, TemplateLibrary.BuiltIn.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void Every_template_renders_every_preset_at_the_exact_size_without_alpha()
    {
        var (shots, icon) = Samples();
        var palette = PaletteExtractor.FromImage(File.ReadAllBytes(icon));
        var catalog = StorePresetCatalog.BuiltIn();
        var cases = catalog.AllPresets.Where(p => p.Kind != PresetKind.Icon)
            .SelectMany(preset => (preset.Kind == PresetKind.FeatureGraphic ? TemplateLibrary.BuiltIn.Where(t => t.IsFeatureGraphic) : TemplateLibrary.Screenshots)
                .Select(template => (preset, template)))
            .ToList();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Independent renders (each has its own surface and assets), so run them in parallel.
        Parallel.ForEach(cases, item =>
        {
            var (preset, template) = item;
            using var assets = new FileAssetResolver();
            var inputs = new TemplateInputs
            {
                Headline = "Keep every note in one place",
                Subheadline = "Quick lists, sketches and voice memos",
                Screenshot = shots[0],
                Screenshot2 = shots[^1],
                Icon = icon,
                AppName = "Notes",
                Palette = palette,
            };
            var scene = TemplateEngine.Build(template, inputs, preset.Width, preset.Height, preset.Frame, preset.Id);
            using var image = SceneRenderer.Render(scene, assets);
            var png = SceneRenderer.Encode(image, ImageFormat.Png);
            if ((image.Width, image.Height) != (preset.Width, preset.Height))
            {
                failures.Add($"{preset.Id}/{template.Id}: size {image.Width}x{image.Height}");
            }

            if (PngColorType(png) != 2)
            {
                failures.Add($"{preset.Id}/{template.Id}: PNG colour type {PngColorType(png)} (expected RGB without alpha)");
            }

            foreach (var issue in PresetValidator.ValidateImage(preset, image.Width, image.Height, "png", PngColorType(png) == 6, png.Length).Where(i => i.IsError))
            {
                failures.Add($"{preset.Id}/{template.Id}: {issue.Message}");
            }
        });

        Assert.Empty(failures);
    }
    [Fact]
    public void Scene_documents_round_trip_through_json()
    {
        var scene = TemplateEngine.Build(TemplateLibrary.Find("headline-top")!, new TemplateInputs { Screenshot = "a.png", Headline = "Hi" }, 1080, 1920);
        var json = scene.ToJson();
        var back = SceneDocument.FromJson(json);
        Assert.Equal(json, back.ToJson());
        Assert.Equal("Hi", back.Find<TextLayer>("headline")!.Text);
        Assert.IsType<LinearGradientFill>(back.Background);
        Assert.Contains("\"type\": \"device\"", json, StringComparison.Ordinal);
    }

    /// <summary>Writes a contact sheet of all templates when SSGEN_RENDER_OUT is set (for looking at the designs).</summary>
    [Fact]
    public void Contact_sheet()
    {
        if (Environment.GetEnvironmentVariable("SSGEN_RENDER_OUT") is not { Length: > 0 } outDir)
        {
            return;
        }

        var (shots, icon) = Samples();
        var palette = PaletteExtractor.FromImage(File.ReadAllBytes(icon));
        using var assets = new FileAssetResolver();
        var templates = TemplateLibrary.Screenshots.ToList();
        const int cellW = 360, cellH = 640, cols = 6;
        var rows = (templates.Count + cols - 1) / cols;
        using var sheet = SKSurface.Create(new SKImageInfo(cellW * cols, (cellH * rows) + 200));
        sheet.Canvas.Clear(new SKColor(40, 40, 44));
        for (var i = 0; i < templates.Count; i++)
        {
            var scene = TemplateEngine.Build(templates[i], new TemplateInputs
            {
                Headline = i % 2 == 0 ? "Keep every note in one place" : "Find anything in seconds",
                Subheadline = "Quick lists, sketches and voice memos",
                Screenshot = shots[i % shots.Count],
                Screenshot2 = shots[(i + 1) % shots.Count],
                Palette = palette,
                Index = i % 3,
                Count = 3,
            }, 1080, 1920, DeviceFrame.PhonePunchHole);
            using var img = SceneRenderer.Render(scene, assets, scale: cellW / 1080.0);
            sheet.Canvas.DrawImage(img, (i % cols) * cellW, (i / cols) * cellH, new SKSamplingOptions(SKFilterMode.Linear), null);
        }

        var fg = TemplateEngine.Build(TemplateLibrary.Find("feature-graphic")!, new TemplateInputs { Screenshot = shots[0], Icon = icon, AppName = "Notes", Subheadline = "Private notes, lists and sketches", Palette = palette }, 1024, 500);
        using (var fgImg = SceneRenderer.Render(fg, assets, scale: 0.39))
        {
            sheet.Canvas.DrawImage(fgImg, 0, cellH * rows, new SKSamplingOptions(SKFilterMode.Linear), null);
        }

        using var snap = sheet.Snapshot();
        TestImages.Save(outDir, "contact-sheet.png", snap.Encode(SKEncodedImageFormat.Png, 100).ToArray());
    }

    /// <summary>PNG IHDR colour type: 2 = RGB, 6 = RGBA.</summary>
    internal static int PngColorType(byte[] png) => png[25];
}
