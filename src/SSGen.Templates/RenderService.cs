using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;
using SSGen.Core.Projects;
using SSGen.Scene;

namespace SSGen.Templates;

/// <summary>One screen to render, in set order.</summary>
public sealed record RenderItem(long ScreenId, string ScreenFile, int Width, int Height, string Label, string Headline, string Subheadline);

public sealed record RenderRequest
{
    public required IReadOnlyList<StorePreset> Presets { get; init; }
    public required TemplateDefinition Template { get; init; }
    public required IReadOnlyList<RenderItem> Items { get; init; }
    public BrandPalette Palette { get; init; } = BrandPalette.Default;
    public string? IconFile { get; init; }
    public string AppName { get; init; } = "";

    /// <summary>Feature graphic tagline.</summary>
    public string Tagline { get; init; } = "";

    public ImageFormat Format { get; init; } = ImageFormat.Png;

    /// <summary>Language of this set: "" = the project's primary language (renders/&lt;Store&gt;/&lt;Size&gt;/),
    /// otherwise a BCP 47 tag (renders/&lt;Store&gt;/&lt;Size&gt;/&lt;tag&gt;/).</summary>
    public string Locale { get; init; } = "";

    /// <summary>Right-to-left captions (Arabic, Urdu, Persian, Hebrew).</summary>
    public bool RightToLeft { get; init; }
}

/// <summary>A rendered file and what the validator found.</summary>
public sealed record RenderedImage(StorePreset Preset, long? ScreenId, int Position, string File, string? SceneFile, int Width, int Height, IReadOnlyList<ValidationIssue> Issues);

public sealed record RenderResult(IReadOnlyList<RenderedImage> Images, IReadOnlyList<ValidationIssue> SetIssues)
{
    public bool HasErrors => SetIssues.Any(i => i.IsError) || Images.Any(i => i.Issues.Any(x => x.IsError));
}

/// <summary>
/// Renders the chosen screens with a template for each selected preset into the project's renders/ folder
/// (renders/&lt;Store&gt;/&lt;Preset&gt;/NN_name.png), saves each image's editable scene document next to it,
/// records everything in the renders table, and validates files and counts against the store rules.
/// </summary>
public static partial class RenderService
{
    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlug();

    public static RenderResult Render(Project project, RenderRequest request, IProgress<(int Done, int Total, string Message)>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(request);
        var images = new List<RenderedImage>();
        var setIssues = new List<ValidationIssue>();
        using var assets = new FileAssetResolver(project.Folder);
        var total = request.Presets.Sum(p => p.Kind == PresetKind.Screenshot ? request.Items.Count : 1);
        var done = 0;

        foreach (var preset in request.Presets)
        {
            ct.ThrowIfCancellationRequested();
            // The store icon doesn't depend on the language: only the primary set has it.
            if (preset.Kind == PresetKind.Icon && request.Locale.Length > 0)
            {
                continue;
            }

            var folder = Path.Combine(project.Folder, "renders", Slug(preset.Store.Name), Slug(preset.Name));
            if (request.Locale.Length > 0)
            {
                folder = Path.Combine(folder, request.Locale);
            }

            ClearPreset(project, preset, folder, request.Locale);
            Directory.CreateDirectory(folder);

            switch (preset.Kind)
            {
                case PresetKind.Screenshot:
                    for (var i = 0; i < request.Items.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var item = request.Items[i];
                        progress?.Report((done, total, $"{preset.Store.Name} {preset.Name}: {i + 1} of {request.Items.Count}"));
                        var inputs = new TemplateInputs
                        {
                            Headline = item.Headline,
                            Subheadline = item.Subheadline,
                            Screenshot = item.ScreenFile,
                            ScreenshotWidth = item.Width,
                            ScreenshotHeight = item.Height,
                            Screenshot2 = request.Items[(i + 1) % request.Items.Count].ScreenFile,
                            Icon = request.IconFile,
                            AppName = request.AppName,
                            Palette = request.Palette,
                            Index = i,
                            Count = request.Items.Count,
                            RightToLeft = request.RightToLeft,
                        };
                        var name = $"{i + 1:00}_{Slug(item.Label)}";
                        images.Add(RenderOne(project, preset, request, inputs, folder, name, item.ScreenId, i, assets));
                        done++;
                    }

                    setIssues.AddRange(PresetValidator.ValidateCount(preset, request.Items.Count));
                    break;

                case PresetKind.FeatureGraphic when TemplateLibrary.BuiltIn.FirstOrDefault(t => t.IsFeatureGraphic) is { } fg:
                    progress?.Report((done, total, $"{preset.Store.Name} {preset.Name}"));
                    var fgInputs = new TemplateInputs
                    {
                        Screenshot = request.Items.Count > 0 ? request.Items[0].ScreenFile : "",
                        Icon = request.IconFile,
                        AppName = request.AppName,
                        Subheadline = request.Tagline,
                        Palette = request.Palette,
                        RightToLeft = request.RightToLeft,
                    };
                    images.Add(RenderOne(project, preset, request with { Template = fg }, fgInputs, folder, "feature_graphic", null, 0, assets));
                    done++;
                    break;

                case PresetKind.Icon when IconPath(project, request) is { } iconPath:
                    progress?.Report((done, total, $"{preset.Store.Name} {preset.Name}"));
                    images.Add(RenderIcon(project, preset, iconPath, folder));
                    done++;
                    break;
            }
        }

        progress?.Report((total, total, "Done"));
        return new RenderResult(images, setIssues);
    }

    private static RenderedImage RenderOne(Project project, StorePreset preset, RenderRequest request, TemplateInputs inputs, string folder, string name, long? screenId, int position, IAssetResolver assets)
    {
        var scene = TemplateEngine.Build(request.Template, inputs, preset.Width, preset.Height, preset.Frame, preset.Id);
        using var image = SceneRenderer.Render(scene, assets);
        var format = preset.EffectiveRules.Formats is { } formats && !formats.Contains(request.Format == ImageFormat.Png ? "png" : "jpeg") ? ImageFormat.Png : request.Format;
        var bytes = SceneRenderer.Encode(image, format);
        var file = Path.Combine(folder, name + (format == ImageFormat.Png ? ".png" : ".jpg"));
        File.WriteAllBytes(file, bytes);

        var scenesDir = Path.Combine(project.Folder, "renders", "scenes");
        Directory.CreateDirectory(scenesDir);
        var sceneFile = Path.Combine(scenesDir, $"{ScenePrefix(preset, request.Locale)}{name}.json");
        SceneAssets.MakeRelative(scene, project.Folder);
        File.WriteAllText(sceneFile, scene.ToJson());

        var issues = PresetValidator.ValidateImage(preset, image.Width, image.Height, format == ImageFormat.Png ? "png" : "jpeg", hasAlpha: false, bytes.Length);
        Record(project, screenId, preset.Id, request.Template.Id, position, file, sceneFile, image.Width, image.Height, request.Locale);
        return new RenderedImage(preset, screenId, position, file, sceneFile, image.Width, image.Height, issues);
    }

    private static string? IconPath(Project project, RenderRequest request)
    {
        if (string.IsNullOrEmpty(request.IconFile))
        {
            return null;
        }

        var path = Path.IsPathRooted(request.IconFile) ? request.IconFile : Path.Combine(project.Folder, request.IconFile);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The store icon: the app's icon scaled to the exact size, keeping transparency (32-bit PNG).</summary>
    private static RenderedImage RenderIcon(Project project, StorePreset preset, string iconPath, string folder)
    {
        using var source = SKBitmap.Decode(iconPath);
        using var surface = SKSurface.Create(new SKImageInfo(preset.Width, preset.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        if (source is not null)
        {
            using var img = SKImage.FromBitmap(source);
            surface.Canvas.DrawImage(img, SKRect.Create(preset.Width, preset.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        using var snap = surface.Snapshot();
        var bytes = SceneRenderer.Encode(snap, ImageFormat.Png);
        var file = Path.Combine(folder, "icon_512.png");
        File.WriteAllBytes(file, bytes);
        var issues = new List<ValidationIssue>(PresetValidator.ValidateImage(preset, preset.Width, preset.Height, "png", hasAlpha: true, bytes.Length));
        if (source is not null && Math.Min(source.Width, source.Height) < preset.Width)
        {
            issues.Add(new(false, $"The APK's icon is only {source.Width}×{source.Height} px, so the {preset.Width} px store icon is upscaled and may look soft."));
        }

        Record(project, null, preset.Id, "icon", 0, file, "", preset.Width, preset.Height, "");
        return new RenderedImage(preset, null, 0, file, null, preset.Width, preset.Height, issues);
    }

    /// <summary>"play-phone_" for the primary language, "play-phone@ur_" for another.</summary>
    private static string ScenePrefix(StorePreset preset, string locale) => locale.Length == 0 ? preset.Id + "_" : $"{preset.Id}@{locale}_";

    /// <summary>Removes a preset's previous renders in one language (files and rows) before rendering it again.</summary>
    private static void ClearPreset(Project project, StorePreset preset, string folder, string locale)
    {
        if (Directory.Exists(folder))
        {
            foreach (var f in Directory.EnumerateFiles(folder))
            {
                File.Delete(f);
            }
        }

        var scenes = Path.Combine(project.Folder, "renders", "scenes");
        if (Directory.Exists(scenes))
        {
            foreach (var f in Directory.EnumerateFiles(scenes, ScenePrefix(preset, locale) + "*.json"))
            {
                File.Delete(f);
            }
        }

        using var c = project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM renders WHERE preset_id = $p AND locale = $l;";
        cmd.Parameters.AddWithValue("$p", preset.Id);
        cmd.Parameters.AddWithValue("$l", locale);
        cmd.ExecuteNonQuery();
    }

    private static void Record(Project project, long? screenId, string presetId, string templateId, int position, string file, string sceneFile, int width, int height, string locale)
    {
        using var c = project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO renders(screen_id, preset_id, template_id, position, file, scene_file, width, height, created_utc, locale)
            VALUES ($s, $p, $t, $pos, $f, $sf, $w, $h, $c, $l);
            """;
        cmd.Parameters.AddWithValue("$s", (object?)screenId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", presetId);
        cmd.Parameters.AddWithValue("$t", templateId);
        cmd.Parameters.AddWithValue("$pos", position);
        cmd.Parameters.AddWithValue("$f", Path.GetRelativePath(project.Folder, file));
        cmd.Parameters.AddWithValue("$sf", sceneFile.Length == 0 ? "" : Path.GetRelativePath(project.Folder, sceneFile));
        cmd.Parameters.AddWithValue("$w", width);
        cmd.Parameters.AddWithValue("$h", height);
        cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$l", locale);
        cmd.ExecuteNonQuery();
    }

    internal static string Slug(string s)
    {
        var slug = NonSlug().Replace(s.ToLowerInvariant(), "_").Trim('_');
        return slug.Length == 0 ? "screen" : slug.Length > 40 ? slug[..40].TrimEnd('_') : slug;
    }
}
