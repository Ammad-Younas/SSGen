using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SSGen.Core.Projects;

namespace SSGen.Templates;

public sealed record ExportRequest
{
    /// <summary>Store sizes to export (preset ids).</summary>
    public required IReadOnlyList<string> PresetIds { get; init; }

    /// <summary>Languages to export: "" = the primary language, otherwise BCP 47 tags.</summary>
    public required IReadOnlyList<string> Locales { get; init; }

    /// <summary>Folder in which the export folder is created.</summary>
    public required string Destination { get; init; }

    /// <summary>The primary language's tag, used as its folder name.</summary>
    public string PrimaryLocale { get; init; } = "en-US";

    public bool Zip { get; init; }

    /// <summary>Copy images that break a store rule too (they're marked in the manifest).</summary>
    public bool IncludeInvalid { get; init; }

    public string AppName { get; init; } = "";
    public string PackageName { get; init; } = "";
    public string AppVersion { get; init; } = "";
}

/// <summary>One exported file and what the store rules check found.</summary>
public sealed record ExportedFile(
    string Path,
    string Store,
    string PresetId,
    string Size,
    string Locale,
    int Position,
    int Width,
    int Height,
    string Format,
    long Bytes,
    string Sha256,
    IReadOnlyList<ValidationIssue> Issues);

public sealed record ExportResult(string Folder, string? ZipFile, IReadOnlyList<ExportedFile> Files, IReadOnlyList<ValidationIssue> Issues, int Skipped)
{
    public bool HasErrors => Issues.Any(i => i.IsError) || Files.Any(f => f.Issues.Any(i => i.IsError));
}

/// <summary>
/// Copies rendered images into an upload-ready folder: &lt;App&gt;_&lt;date&gt;/&lt;Store&gt;/&lt;language&gt;/&lt;Size&gt;/NN_name.png,
/// re-checks every file and every set against the store rules, writes manifest.json (files, sizes, checksums,
/// problems, and where the store rules came from) and optionally a ZIP of the folder.
/// </summary>
public static class ExportService
{
    public static ExportResult Export(Project project, StorePresetCatalog catalog, ExportRequest request, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);

        var presetIds = request.PresetIds.ToHashSet(StringComparer.Ordinal);
        var locales = request.Locales.ToHashSet(StringComparer.Ordinal);
        var records = RenderStore.List(project)
            .Where(r => presetIds.Contains(r.PresetId) && locales.Contains(r.Locale))
            .OrderBy(r => r.PresetId, StringComparer.Ordinal).ThenBy(r => r.Locale, StringComparer.Ordinal).ThenBy(r => r.Position)
            .ToList();
        if (records.Count == 0)
        {
            throw new InvalidOperationException("There are no rendered images for the chosen sizes and languages. Render them on the Design page first.");
        }

        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
        var name = $"{RenderService.Slug(request.AppName.Length > 0 ? request.AppName : project.Name)}_{stamp}";
        var folder = Path.Combine(request.Destination, name);
        for (var n = 2; Directory.Exists(folder); n++)
        {
            folder = Path.Combine(request.Destination, $"{name}_{n}");
        }

        Directory.CreateDirectory(folder);
        var files = new List<ExportedFile>();
        var issues = new List<ValidationIssue>();
        var skipped = 0;
        var done = 0;

        foreach (var set in records.GroupBy(r => (r.PresetId, r.Locale)))
        {
            ct.ThrowIfCancellationRequested();
            var preset = catalog.Find(set.Key.PresetId);
            var language = set.Key.Locale.Length == 0 ? request.PrimaryLocale : set.Key.Locale;
            var store = preset?.Store.Name ?? "Other";
            var size = preset?.Name ?? set.Key.PresetId;
            var target = Path.Combine(folder, RenderService.Slug(store), language, RenderService.Slug(size));
            Directory.CreateDirectory(target);

            var copied = 0;
            foreach (var record in set)
            {
                progress?.Report((done++, records.Count));
                var source = record.FullPath(project);
                var fileIssues = preset is null ? [] : RenderStore.Validate(project, record, preset);
                if (!File.Exists(source) || (fileIssues.Any(i => i.IsError) && !request.IncludeInvalid))
                {
                    skipped++;
                    issues.Add(new ValidationIssue(true, $"{store} {size} ({language}): {Path.GetFileName(record.File)} was not exported: {string.Join(" ", fileIssues.Select(i => i.Message))}".TrimEnd(' ', ':')));
                    continue;
                }

                var destination = Path.Combine(target, Path.GetFileName(source));
                File.Copy(source, destination, overwrite: true);
                var info = ImageFileInfo.Read(destination);
                files.Add(new ExportedFile(
                    Path.GetRelativePath(folder, destination).Replace('\\', '/'),
                    store,
                    set.Key.PresetId,
                    size,
                    language,
                    record.Position,
                    info?.Width ?? record.Width,
                    info?.Height ?? record.Height,
                    info?.Format ?? Path.GetExtension(destination).TrimStart('.'),
                    new FileInfo(destination).Length,
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(destination))),
                    fileIssues));
                copied++;
            }

            if (preset is { Kind: PresetKind.Screenshot })
            {
                issues.AddRange(PresetValidator.ValidateCount(preset, copied).Select(i => i with { Message = $"{store} {size} ({language}): {i.Message}" }));
            }
        }

        WriteManifest(folder, catalog, request, files, issues);

        string? zip = null;
        if (request.Zip)
        {
            zip = folder + ".zip";
            if (File.Exists(zip))
            {
                File.Delete(zip);
            }

            ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Optimal, includeBaseDirectory: true);
        }

        progress?.Report((records.Count, records.Count));
        return new ExportResult(folder, zip, files, issues, skipped);
    }

    private static void WriteManifest(string folder, StorePresetCatalog catalog, ExportRequest request, List<ExportedFile> files, List<ValidationIssue> issues)
    {
        var manifest = new
        {
            generator = "SSGen",
            createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            app = new { name = request.AppName, package = request.PackageName, version = request.AppVersion },
            storeRules = new
            {
                verified = catalog.Verified,
                sources = catalog.Stores.Select(s => new { store = s.Name, url = s.Source }),
            },
            problems = issues.Select(i => new { error = i.IsError, message = i.Message }),
            files = files.Select(f => new
            {
                path = f.Path,
                store = f.Store,
                preset = f.PresetId,
                size = f.Size,
                language = f.Locale,
                position = f.Position + 1,
                width = f.Width,
                height = f.Height,
                format = f.Format,
                bytes = f.Bytes,
                sha256 = f.Sha256,
                problems = f.Issues.Select(i => new { error = i.IsError, message = i.Message }),
            }),
        };
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, ManifestJson));
    }

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };
}
