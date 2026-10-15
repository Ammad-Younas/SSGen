using SkiaSharp;
using SSGen.Scene;
using SSGen.Scene.Editing;
using SSGen.Templates;

namespace SSGen.Design.Tests;

public class EditorTests
{
    private static readonly SKColor Teal = new(0x00, 0x96, 0x88);

    private static SceneDocument Doc() => new()
    {
        Width = 1000,
        Height = 2000,
        Background = new SolidFill("#FF112233"),
        Layers =
        {
            new ShapeLayer { Id = "box", Name = "Box", X = 100, Y = 100, W = 400, H = 200 },
            new TextLayer { Id = "headline", Name = "Headline", Text = "Hello", X = 50, Y = 50, W = 900, H = 300 },
        },
    };

    // ------------------------------------------------------------------ undo and redo

    [Fact]
    public void Every_kind_of_change_undoes_and_redoes()
    {
        var editor = new SceneEditor(Doc());
        var original = editor.Document.ToJson();

        editor.Modify("box", l => l.X = 300, "Move");
        editor.History.Break();
        editor.SetBackground(new SolidFill("#FFFF0000"));
        var added = editor.Add(new ShapeLayer { Id = "box", Name = "Second" }); // id clash gets a fresh id
        editor.Reorder("headline", -1);
        editor.Remove("box");
        editor.Duplicate("headline");
        var final = editor.Document.ToJson();

        Assert.NotEqual("box", added.Id);
        Assert.Equal(["headline", "headline", added.Id], editor.Document.Layers.Select(l => l.Id == "headline" || l.Name == "Headline copy" ? "headline" : l.Id));
        Assert.True(editor.IsDirty);

        while (editor.Undo())
        {
        }

        Assert.Equal(original, editor.Document.ToJson());
        Assert.False(editor.IsDirty);

        while (editor.Redo())
        {
        }

        Assert.Equal(final, editor.Document.ToJson());
    }

    [Fact]
    public void Typing_merges_into_one_step_but_separate_fields_do_not()
    {
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var editor = new SceneEditor(Doc());
        var history = new EditHistory(() => now);
        var doc = editor.Document;

        // Simulate typing "Hi there" one character at a time into the same box.
        var text = "";
        foreach (var ch in "Hi there")
        {
            var before = LayerJsonOf(doc, "headline");
            text += ch;
            ((TextLayer)doc.Layers[1]).Text = text;
            history.Record(new ChangeLayerCommand("Edit text", "headline", before, LayerJsonOf(doc, "headline"), "headline:text"));
            now = now.AddMilliseconds(200);
        }

        var beforeSize = LayerJsonOf(doc, "headline");
        ((TextLayer)doc.Layers[1]).Size = 90;
        history.Record(new ChangeLayerCommand("Font size", "headline", beforeSize, LayerJsonOf(doc, "headline"), "headline:size"));

        history.Undo(doc);
        Assert.Equal("Hi there", ((TextLayer)doc.Layers[1]).Text);
        history.Undo(doc);
        Assert.Equal("Hello", ((TextLayer)doc.Layers[1]).Text);
        Assert.False(history.CanUndo);

        // A pause longer than the merge window starts a new step.
        history.Redo(doc);
        history.Redo(doc);
        now = now.AddSeconds(5);
        var b2 = LayerJsonOf(doc, "headline");
        ((TextLayer)doc.Layers[1]).Size = 120;
        history.Record(new ChangeLayerCommand("Font size", "headline", b2, LayerJsonOf(doc, "headline"), "headline:size"));
        history.Undo(doc);
        Assert.Equal(90, ((TextLayer)doc.Layers[1]).Size);
    }

    [Fact]
    public void Dirty_state_follows_the_saved_point()
    {
        var editor = new SceneEditor(Doc());
        editor.Modify("box", l => l.X = 1, "Move");
        editor.History.MarkSaved();
        Assert.False(editor.IsDirty);
        editor.Undo();
        Assert.True(editor.IsDirty);
        editor.Redo();
        Assert.False(editor.IsDirty);

        // A change in the merge window after a save is a new step, so undo returns to the saved state.
        editor.Modify("box", l => l.X = 2, "Move", "box:x");
        Assert.True(editor.IsDirty);
        editor.Undo();
        Assert.False(editor.IsDirty);

        // Branching away from the saved state makes it unreachable.
        editor.Undo();
        editor.Modify("box", l => l.Y = 9, "Move");
        editor.Undo();
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void A_drag_is_one_step_and_escape_cancels_it()
    {
        var editor = new SceneEditor(Doc());
        var previews = 0;
        editor.Previewed += (_, _) => previews++;
        editor.BeginGesture("box");
        for (var i = 1; i <= 10; i++)
        {
            editor.UpdateGesture(l => l.X = 100 + (i * 10));
        }

        editor.EndGesture("Move");
        Assert.Equal(10, previews);
        Assert.Equal(200, editor.Find("box")!.X);
        editor.Undo();
        Assert.Equal(100, editor.Find("box")!.X);
        Assert.False(editor.History.CanUndo);

        editor.BeginGesture("box");
        editor.UpdateGesture(l => l.W = 50);
        editor.CancelGesture();
        Assert.Equal(400, editor.Find("box")!.W);
        Assert.False(editor.History.CanUndo);
    }

    // ------------------------------------------------------------------ geometry

    [Fact]
    public void Hit_testing_respects_rotation_order_visibility_and_locks()
    {
        var doc = Doc();
        Assert.Equal("headline", SceneGeometry.HitTest(doc, new Pt(150, 150))!.Id); // topmost wins
        doc.Layers[1].Visible = false;
        Assert.Equal("box", SceneGeometry.HitTest(doc, new Pt(150, 150))!.Id);
        doc.Layers[0].Locked = true;
        Assert.Null(SceneGeometry.HitTest(doc, new Pt(150, 150)));

        // A 400×200 box rotated 90° is 200 wide and 400 tall around its centre (300, 200).
        var box = new ShapeLayer { X = 100, Y = 100, W = 400, H = 200, Rotation = 90 };
        Assert.True(SceneGeometry.Contains(box, new Pt(300, 380)));
        Assert.False(SceneGeometry.Contains(box, new Pt(480, 200)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(-120)]
    public void Resizing_from_a_corner_keeps_the_opposite_corner_in_place(double rotation)
    {
        var layer = new ShapeLayer { X = 100, Y = 100, W = 400, H = 200, Rotation = rotation };
        var anchor = SceneGeometry.CornerPoint(layer, Corner.TopLeft);
        var target = SceneGeometry.ToCanvas(layer, new Pt(300, 200)); // local point beyond the bottom-right corner
        SceneGeometry.ResizeFromCorner(layer, Corner.BottomRight, target, keepAspect: false);

        Assert.Equal(500, layer.W, 6);
        Assert.Equal(300, layer.H, 6);
        var after = SceneGeometry.CornerPoint(layer, Corner.TopLeft);
        Assert.Equal(anchor.X, after.X, 6);
        Assert.Equal(anchor.Y, after.Y, 6);
    }

    [Fact]
    public void Resizing_can_keep_proportions_and_never_collapses()
    {
        var layer = new ShapeLayer { X = 0, Y = 0, W = 400, H = 200 };
        SceneGeometry.ResizeFromCorner(layer, Corner.BottomRight, new Pt(800, 250), keepAspect: true);
        Assert.Equal(2.0, layer.W / layer.H, 6);
        Assert.Equal(800, layer.W, 6);

        SceneGeometry.ResizeFromCorner(layer, Corner.BottomRight, new Pt(-500, -500), keepAspect: false);
        Assert.Equal(8, layer.W);
        Assert.Equal(8, layer.H);
    }

    [Fact]
    public void Rotation_follows_the_pointer_and_snaps()
    {
        var layer = new ShapeLayer { X = 0, Y = 0, W = 100, H = 100 };
        Assert.Equal(0, SceneGeometry.RotationTowards(layer, new Pt(50, -100), snap: false), 6);   // straight up
        Assert.Equal(90, SceneGeometry.RotationTowards(layer, new Pt(300, 50), snap: false), 6);   // right
        Assert.Equal(180, SceneGeometry.RotationTowards(layer, new Pt(50, 300), snap: false), 6);  // down
        Assert.Equal(15, SceneGeometry.RotationTowards(layer, new Pt(50 + 20, -100), snap: true), 6);
        Assert.Equal(-170, SceneGeometry.Normalize(190));
    }

    // ------------------------------------------------------------------ style transfer

    [Fact]
    public void Applying_a_style_keeps_each_images_content_and_panorama_place()
    {
        var source = Doc();
        source.Background = new LinearGradientFill(["#FF000000", "#FFFFFFFF"], 0, Span: 4, Offset: 0);
        var headline = (TextLayer)source.Layers[1];
        headline.Color = "#FFFFD600";
        headline.Font = "DM Serif Display";
        headline.Y = 400;
        source.Layers.Insert(0, new ShapeLayer { Id = "shape-1", X = 300 });
        source.Layers.Add(new TextLayer { Id = "badge", Text = "NEW", X = 10 });
        source.Layers.RemoveAll(l => l.Id == "box");

        var target = Doc();
        target.Background = new LinearGradientFill(["#FF0000FF"], 0, Span: 4, Offset: 2);
        ((TextLayer)target.Layers[1]).Text = "Second screen";
        target.Layers.Insert(0, new ShapeLayer { Id = "shape-1", X = 300 - 2000 });

        var result = StyleTransfer.Apply(source, target);

        var bg = Assert.IsType<LinearGradientFill>(result.Background);
        Assert.Equal(["#FF000000", "#FFFFFFFF"], bg.Colors);
        Assert.Equal(2, bg.Offset);
        var h = (TextLayer)result.Layers.Single(l => l.Id == "headline");
        Assert.Equal(("Second screen", "#FFFFD600", "DM Serif Display", 400.0), (h.Text, h.Color, h.Font, h.Y));
        Assert.Equal(300 - 2000, result.Layers.Single(l => l.Id == "shape-1").X); // still in its place in the strip
        Assert.Equal("NEW", ((TextLayer)result.Layers.Single(l => l.Id == "badge")).Text);
        Assert.DoesNotContain(result.Layers, l => l.Id == "box");
        Assert.Equal("Hello", ((TextLayer)source.Layers.Single(l => l.Id == "headline")).Text); // source untouched
    }

    // ------------------------------------------------------------------ new scene features

    [Fact]
    public void Picture_adjustments_and_outlines_render()
    {
        static SKColor Centre(SceneDocument d)
        {
            using var assets = new FileAssetResolver();
            using var img = SceneRenderer.Render(d, assets);
            using var bmp = SKBitmap.FromImage(img);
            return bmp.GetPixel(50, 50);
        }

        var folder = Path.Combine(Path.GetTempPath(), "ssgen-editor-tests");
        Directory.CreateDirectory(folder);
        var file = TestImages.Save(folder, "grey.png", Solid(new SKColor(128, 128, 128)));
        var doc = new SceneDocument { Width = 100, Height = 100, Layers = { new ImageLayer { Source = file, W = 100, H = 100 } } };
        var plain = Centre(doc);

        ((ImageLayer)doc.Layers[0]).Adjust = new ImageAdjust(Brightness: 0.5);
        Assert.True(Centre(doc).Red > plain.Red + 40);

        File.WriteAllBytes(file, Solid(new SKColor(200, 40, 40)));
        doc = new SceneDocument { Width = 100, Height = 100, Layers = { new ImageLayer { Source = file, W = 100, H = 100, Adjust = new ImageAdjust(Saturation: -1) } } };
        var grey = Centre(doc);
        Assert.True(Math.Abs(grey.Red - grey.Green) < 4 && Math.Abs(grey.Green - grey.Blue) < 4);

        var outlined = new SceneDocument
        {
            Width = 100,
            Height = 100,
            Background = new SolidFill("#FFFFFFFF"),
            Layers = { new ShapeLayer { W = 100, H = 100, Fill = new SolidFill("#FFFFFFFF"), StrokeColor = "#FF000000", StrokeWidth = 10 } },
        };
        using var assets = new FileAssetResolver();
        using var image = SceneRenderer.Render(outlined, assets);
        using var bitmap = SKBitmap.FromImage(image);
        Assert.Equal(SKColors.Black, bitmap.GetPixel(3, 50));
        Assert.Equal(SKColors.White, bitmap.GetPixel(50, 50));
    }

    [Fact]
    public void Older_scene_files_without_new_fields_still_load()
    {
        const string json = """{ "version": 1, "width": 10, "height": 10, "layers": [ { "type": "image", "source": "a.png", "w": 10, "h": 10 } ] }""";
        var doc = SceneDocument.FromJson(json);
        Assert.Null(((ImageLayer)doc.Layers[0]).Adjust);
    }

    // ------------------------------------------------------------------ saving edited renders

    [Fact]
    public void Saving_an_edit_renders_the_image_again_and_marks_it_edited()
    {
        using var p = new TempProject();
        var shots = Enumerable.Range(0, 2).Select(i => p.AddScreen(TestImages.Screenshot(Teal, seed: i), $"x/.S{i}Activity", $"Screen {i}")).ToList();
        var preset = StorePresetCatalog.BuiltIn().Find("play-phone")!;
        RenderService.Render(p.Project, new RenderRequest
        {
            Presets = [preset],
            Template = TemplateLibrary.Find("headline-top")!,
            Items = shots.Select(s => new RenderItem(s.Id, s.FullPath(p.Project), 1080, 2400, s.Title!, "Headline", "")).ToList(),
        });

        var records = RenderStore.List(p.Project);
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.False(r.IsEdited));
        Assert.Equal("Screen 0", records[0].DisplayName);

        // Sources are stored relative to the project, so the project can move.
        var scene = RenderStore.LoadScene(p.Project, records[0]);
        var device = scene.Layers.OfType<DeviceLayer>().Single();
        Assert.False(Path.IsPathRooted(device.Source), device.Source);

        var before = File.ReadAllBytes(records[0].FullPath(p.Project));
        scene.Background = new SolidFill("#FFFF0000");
        var (saved, issues) = RenderStore.SaveScene(p.Project, records[0], scene, preset);

        Assert.True(saved.IsEdited);
        Assert.DoesNotContain(issues, i => i.IsError);
        var after = File.ReadAllBytes(records[0].FullPath(p.Project));
        Assert.NotEqual(before, after);
        Assert.Equal(2, RenderTests.PngColorType(after));
        Assert.Equal("#FFFF0000", ((SolidFill)RenderStore.LoadScene(p.Project, saved).Background).Color);
        Assert.Equal(1, RenderStore.CountEdited(p.Project, ["play-phone"]));
        Assert.Equal(0, RenderStore.CountEdited(p.Project, ["appstore-iphone-69"]));
        Assert.DoesNotContain(RenderStore.Validate(p.Project, saved, preset), i => i.IsError);
        Assert.Empty(Directory.GetFiles(p.Project.Folder, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_screen_can_have_its_own_template_and_can_be_deleted()
    {
        using var p = new TempProject();
        var shots = Enumerable.Range(0, 2).Select(i => p.AddScreen(TestImages.Screenshot(Teal, seed: i), $"x/.S{i}Activity", $"Screen {i}")).ToList();
        var store = new SSGen.Selection.SelectionStore(p.Project);
        store.Save(new SSGen.Selection.SelectionItem(shots[0].Id, true, 0, 50, null, "A", "A", "", "", "manual"));
        store.SetTemplate(shots[1].Id, "tilted");

        // Ranking again rewrites the row but keeps the screen's own design.
        store.Save(new SSGen.Selection.SelectionItem(shots[1].Id, true, 1, 40, null, "B", "B", "", "", "heuristic"));
        Assert.Equal("tilted", store.Load().Single(i => i.ScreenId == shots[1].Id).TemplateId);
        Assert.Null(store.Load().Single(i => i.ScreenId == shots[0].Id).TemplateId);

        var preset = StorePresetCatalog.BuiltIn().Find("play-phone")!;
        RenderService.Render(p.Project, new RenderRequest
        {
            Presets = [preset],
            Template = TemplateLibrary.Find("headline-top")!,
            Items =
            [
                new RenderItem(shots[0].Id, shots[0].FullPath(p.Project), 1080, 2400, "A", "A", ""),
                new RenderItem(shots[1].Id, shots[1].FullPath(p.Project), 1080, 2400, "B", "B", "") { Template = TemplateLibrary.Find("tilted") },
            ],
        });
        Assert.Equal(["headline-top", "tilted"], RenderStore.List(p.Project).OrderBy(r => r.Position).Select(r => r.TemplateId));

        var file = shots[0].FullPath(p.Project);
        SSGen.Core.Projects.ScreenRepository.Delete(p.Project, shots[0].Id);
        Assert.False(File.Exists(file));
        Assert.DoesNotContain(SSGen.Core.Projects.ScreenRepository.List(p.Project), s => s.Id == shots[0].Id);
        Assert.DoesNotContain(store.Load(), i => i.ScreenId == shots[0].Id);           // selection row went with it
        Assert.Null(RenderStore.List(p.Project).Single(r => r.Position == 0).ScreenId); // the render forgets it
    }

    [Fact]
    public void Edits_can_be_reset_and_everything_can_be_cleared()
    {
        using var p = new TempProject();
        var shot = p.AddScreen(TestImages.Screenshot(Teal, seed: 3), "x/.SActivity", "Screen");
        var preset = StorePresetCatalog.BuiltIn().Find("play-phone")!;
        RenderService.Render(p.Project, new RenderRequest
        {
            Presets = [preset],
            Template = TemplateLibrary.Find("headline-top")!,
            Items = [new RenderItem(shot.Id, shot.FullPath(p.Project), 1080, 2400, "Screen", "Headline", "")],
        });
        var record = RenderStore.List(p.Project).Single();
        var original = File.ReadAllBytes(record.FullPath(p.Project));
        var originalScene = File.ReadAllText(record.SceneFullPath(p.Project)!);

        // Two saves: the original kept is the template's, not the first edit.
        var scene = RenderStore.LoadScene(p.Project, record);
        scene.Background = new SolidFill("#FFFF0000");
        var (saved, _) = RenderStore.SaveScene(p.Project, record, scene, preset);
        scene.Background = new SolidFill("#FF00FF00");
        RenderStore.SaveScene(p.Project, saved, scene, preset);
        Assert.NotEqual(original, File.ReadAllBytes(record.FullPath(p.Project)));

        Assert.Equal((1, 0), RenderStore.ResetEdits(p.Project));
        Assert.Equal(original, File.ReadAllBytes(record.FullPath(p.Project)));
        Assert.Equal(originalScene, File.ReadAllText(record.SceneFullPath(p.Project)!));
        Assert.False(RenderStore.List(p.Project).Single().IsEdited);
        Assert.Equal((0, 0), RenderStore.ResetEdits(p.Project)); // nothing left to reset

        var store = new SSGen.Selection.SelectionStore(p.Project);
        store.Save(new SSGen.Selection.SelectionItem(shot.Id, true, 0, 50, null, "S", "Headline", "", "", "manual"));
        new SSGen.Selection.CaptionStore(p.Project).Save(new SSGen.Selection.LocalizedCaption(shot.Id, "ur", "سرخی", "", "manual"));
        store.ClearAll();
        Assert.Empty(store.Load());
        Assert.Empty(new SSGen.Selection.CaptionStore(p.Project).Load("ur"));
        Assert.Single(SSGen.Core.Projects.ScreenRepository.List(p.Project)); // screenshots stay

        Assert.Equal(1, RenderStore.DeleteAll(p.Project));
        Assert.Empty(RenderStore.List(p.Project));
        Assert.False(Directory.Exists(p.Project.RendersDir));
    }

    private static byte[] Solid(SKColor color)
    {
        using var bmp = new SKBitmap(100, 100);
        bmp.Erase(color);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static string LayerJsonOf(SceneDocument doc, string id) =>
        System.Text.Json.JsonSerializer.Serialize(doc.Layers.Single(l => l.Id == id), SceneJson.Options);
}
