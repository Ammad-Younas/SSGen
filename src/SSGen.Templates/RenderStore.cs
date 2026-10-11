using System.Globalization;
using Microsoft.Data.Sqlite;
using SkiaSharp;
using SSGen.Core.Projects;
using SSGen.Scene;

namespace SSGen.Templates;

/// <summary>A rendered image (renders table): the file, its editable scene document and when it was edited.</summary>
public sealed record RenderRecord(
    long Id,
    long? ScreenId,
    string PresetId,
    string TemplateId,
    int Position,
    string File,
    string SceneFile,
    int Width,
    int Height,
    DateTime CreatedUtc,
    DateTime? EditedUtc)
{
    public string FullPath(Project project) => Path.Combine(project?.Folder ?? throw new ArgumentNullException(nameof(project)), File);

    public string? SceneFullPath(Project project) => SceneFile.Length == 0 ? null : Path.Combine(project?.Folder ?? throw new ArgumentNullException(nameof(project)), SceneFile);

    /// <summary>Screenshots and the feature graphic have a scene; the store icon is a plain copy of the app icon.</summary>
    public bool Editable => SceneFile.Length > 0;

    public bool IsEdited => EditedUtc is not null;

    /// <summary>"03_sketch_note" → "Sketch note".</summary>
    public string DisplayName
    {
        get
        {
            var name = Path.GetFileNameWithoutExtension(File);
            var underscore = name.IndexOf('_', StringComparison.Ordinal);
            if (underscore > 0 && name[..underscore].All(char.IsDigit))
            {
                name = name[(underscore + 1)..];
            }

            name = name.Replace('_', ' ').Trim();
            return name.Length == 0 ? "Image" : char.ToUpperInvariant(name[0]) + name[1..];
        }
    }
}

/// <summary>What a saved image file actually is (read back from disk for validation).</summary>
public sealed record ImageFileInfo(int Width, int Height, string Format, bool HasAlpha, long Bytes)
{
    public static ImageFileInfo? Read(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return null;
        }

        using var codec = SKCodec.Create(path);
        if (codec is null)
        {
            return null;
        }

        var format = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => "png",
            SKEncodedImageFormat.Jpeg => "jpeg",
            var f => f.ToString().ToLowerInvariant(),
        };
        return new ImageFileInfo(codec.Info.Width, codec.Info.Height, format, codec.Info.AlphaType != SKAlphaType.Opaque, new FileInfo(path).Length);
    }
}

/// <summary>Reads and updates the renders table, and saves scenes changed in the editor (scene JSON + image).</summary>
public static class RenderStore
{
    public static IReadOnlyList<RenderRecord> List(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        using var c = project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, screen_id, preset_id, template_id, position, file, scene_file, width, height, created_utc, edited_utc
            FROM renders ORDER BY preset_id, position, id;
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<RenderRecord>();
        while (r.Read())
        {
            list.Add(Read(r));
        }

        return list;
    }

    public static RenderRecord? Get(Project project, long id) => List(project).FirstOrDefault(r => r.Id == id);

    /// <summary>How many of these presets' images were changed in the editor (re-rendering would replace them).</summary>
    public static int CountEdited(Project project, IEnumerable<string> presetIds)
    {
        var ids = presetIds.ToHashSet(StringComparer.Ordinal);
        return List(project).Count(r => r.IsEdited && ids.Contains(r.PresetId));
    }

    public static SceneDocument LoadScene(Project project, RenderRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var path = record.SceneFullPath(project) ?? throw new InvalidOperationException("This image has no editable design.");
        return SceneDocument.FromJson(System.IO.File.ReadAllText(path));
    }

    /// <summary>
    /// Saves an edited scene: writes the JSON, renders the image again (same file and format), marks the row as
    /// edited and validates the file against the preset (when the preset is known).
    /// </summary>
    public static (RenderRecord Record, IReadOnlyList<ValidationIssue> Issues) SaveScene(Project project, RenderRecord record, SceneDocument scene, StorePreset? preset)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(scene);
        var scenePath = record.SceneFullPath(project) ?? throw new InvalidOperationException("This image has no editable design.");
        SceneAssets.MakeRelative(scene, project.Folder);

        var file = record.FullPath(project);
        var format = Path.GetExtension(file).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Jpeg : ImageFormat.Png;
        byte[] bytes;
        using (var assets = new FileAssetResolver(project.Folder))
        using (var image = SceneRenderer.Render(scene, assets))
        {
            bytes = SceneRenderer.Encode(image, format);
        }

        WriteAtomically(file, bytes);
        WriteAtomically(scenePath, System.Text.Encoding.UTF8.GetBytes(scene.ToJson()));

        var edited = DateTime.UtcNow;
        using (var c = project.Database.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE renders SET edited_utc = $e, width = $w, height = $h WHERE id = $id;";
            cmd.Parameters.AddWithValue("$e", edited.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$w", scene.Width);
            cmd.Parameters.AddWithValue("$h", scene.Height);
            cmd.Parameters.AddWithValue("$id", record.Id);
            cmd.ExecuteNonQuery();
        }

        var issues = preset is null
            ? []
            : PresetValidator.ValidateImage(preset, scene.Width, scene.Height, format == ImageFormat.Png ? "png" : "jpeg", hasAlpha: false, bytes.Length);
        return (record with { EditedUtc = edited, Width = scene.Width, Height = scene.Height }, issues);
    }

    /// <summary>Checks the file on disk against its preset.</summary>
    public static IReadOnlyList<ValidationIssue> Validate(Project project, RenderRecord record, StorePreset preset)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(preset);
        return ImageFileInfo.Read(record.FullPath(project)) is { } info
            ? PresetValidator.ValidateImage(preset, info.Width, info.Height, info.Format, info.HasAlpha, info.Bytes)
            : [new ValidationIssue(true, $"{Path.GetFileName(record.File)} is missing. Render the set again on the Design page.")];
    }

    /// <summary>Write to a temporary file, then swap, so a crash or a reader never sees half a file.</summary>
    private static void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        System.IO.File.WriteAllBytes(temp, bytes);
        System.IO.File.Move(temp, path, overwrite: true);
    }

    private static RenderRecord Read(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.IsDBNull(1) ? null : r.GetInt64(1),
        r.GetString(2),
        r.GetString(3),
        r.GetInt32(4),
        r.GetString(5),
        r.GetString(6),
        r.GetInt32(7),
        r.GetInt32(8),
        Utc(r.GetString(9)),
        r.IsDBNull(10) ? null : Utc(r.GetString(10)));

    private static DateTime Utc(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}

/// <summary>Scene image sources inside the project are stored relative to it, so a moved project keeps working.</summary>
public static class SceneAssets
{
    public static void MakeRelative(SceneDocument scene, string projectFolder)
    {
        ArgumentNullException.ThrowIfNull(scene);
        foreach (var layer in scene.Layers)
        {
            switch (layer)
            {
                case ImageLayer image:
                    image.Source = Relative(image.Source, projectFolder);
                    break;
                case DeviceLayer device:
                    device.Source = Relative(device.Source, projectFolder);
                    break;
            }
        }

        if (scene.Meta.TryGetValue("screenshot", out var shot))
        {
            scene.Meta["screenshot"] = Relative(shot, projectFolder);
        }
    }

    private static string Relative(string source, string folder)
    {
        if (string.IsNullOrEmpty(source) || !Path.IsPathRooted(source))
        {
            return source;
        }

        var relative = Path.GetRelativePath(folder, source);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? source : relative;
    }
}
