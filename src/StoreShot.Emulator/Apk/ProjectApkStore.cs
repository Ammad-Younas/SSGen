using System.Text.Json;
using StoreShot.Core.Projects;

namespace StoreShot.Emulator.Apk;

/// <summary>The APK imported into a project: a copy under apk/, its parsed info, and the extracted icon.</summary>
public sealed record ProjectApk(string ApkFile, ApkInfo Info, string? IconFile, IReadOnlyList<string> Warnings, DateTime ImportedUtc)
{
    public string ApkPath(Project project) => Path.Combine((project ?? throw new ArgumentNullException(nameof(project))).Folder, ApkFile);

    public string? IconPath(Project project) => IconFile is null ? null : Path.Combine((project ?? throw new ArgumentNullException(nameof(project))).Folder, IconFile);
}

public static class ProjectApkStore
{
    private const string MetaKey = "apk";

    /// <summary>Copies the APK into the project (replacing any previous one) and stores its metadata.</summary>
    public static ProjectApk Import(Project project, string sourceApk, ApkInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(inspection);
        var apkDir = Path.Combine(project.Folder, "apk");
        Directory.CreateDirectory(apkDir);
        foreach (var old in Directory.EnumerateFiles(apkDir, "*.apk"))
        {
            if (!string.Equals(Path.GetFullPath(old), Path.GetFullPath(sourceApk), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(old);
            }
        }

        var info = inspection.Info;
        var fileName = $"{info.PackageName}-{info.VersionCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0"}.apk";
        var target = Path.Combine(apkDir, fileName);
        if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(sourceApk), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourceApk, target, overwrite: true);
        }

        string? iconFile = null;
        if (inspection.IconPng is { } icon)
        {
            iconFile = "icon.png";
            File.WriteAllBytes(Path.Combine(project.Folder, iconFile), icon);
        }

        var stored = new ProjectApk(Path.Combine("apk", fileName), info, iconFile, inspection.Warnings, DateTime.UtcNow);
        ProjectMeta.Set(project, MetaKey, JsonSerializer.Serialize(stored));
        return stored;
    }

    public static ProjectApk? Load(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var json = ProjectMeta.Get(project, MetaKey);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProjectApk>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
