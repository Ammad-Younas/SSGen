using System.Globalization;
using System.IO.Compression;
using SkiaSharp;
using SSGen.Core;
using SSGen.Core.Processes;

namespace SSGen.Emulator.Apk;

/// <summary>
/// Reads metadata and the launcher icon from an APK. Uses aapt2 when available and falls back to a managed
/// manifest parser (no label or icon resolution through resources) when it isn't.
/// </summary>
public sealed class ApkInspector
{
    private const int IconSize = 512;

    private readonly IProcessRunner _runner;

    public ApkInspector(IProcessRunner runner) => _runner = runner;

    /// <param name="aapt2Path">Path to aapt2.exe, or null to use only the managed parser.</param>
    public async Task<ApkInspection> InspectAsync(string apkPath, string? aapt2Path, CancellationToken ct = default)
    {
        if (!File.Exists(apkPath))
        {
            throw new UserFacingException($"\"{apkPath}\" doesn't exist.", "Pick the APK file again.");
        }

        if (apkPath.EndsWith(".aab", StringComparison.OrdinalIgnoreCase))
        {
            throw AabNotSupported();
        }

        var warnings = new List<string>();
        using var zip = OpenZip(apkPath);
        if (zip.GetEntry("base/manifest/AndroidManifest.xml") is not null)
        {
            throw AabNotSupported();
        }

        var manifestEntry = zip.GetEntry("AndroidManifest.xml") ?? throw new UserFacingException(
            "This file isn't an Android app (no AndroidManifest.xml inside).", "Pick an .apk file built for Android.");
        var abis = zip.Entries
            .Select(e => e.FullName.Split('/'))
            .Where(p => p.Length == 3 && p[0] == "lib" && p[2].EndsWith(".so", StringComparison.Ordinal))
            .Select(p => p[1])
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        ApkInfo? info = null;
        byte[]? icon = null;
        if (aapt2Path is not null && File.Exists(aapt2Path))
        {
            var r = await _runner.RunAsync(new ProcessSpec(aapt2Path, ["dump", "badging", apkPath]) { Timeout = TimeSpan.FromMinutes(1) }, ct);
            if (r.Succeeded)
            {
                var badging = AaptBadgingParser.Parse(r.StdOut);
                info = badging.Info with { NativeAbis = abis };
                icon = await ResolveIconWithAaptAsync(zip, apkPath, aapt2Path, badging.IconPath, warnings, ct);
            }
            else
            {
                warnings.Add("aapt2 couldn't read this APK, so a simpler parser was used. " + r.Combined.Trim());
            }
        }
        else
        {
            warnings.Add("aapt2 (Android build-tools) isn't installed, so the app name and icon may be missing.");
        }

        if (info is null)
        {
            info = ReadManagedManifest(manifestEntry, abis);
            icon ??= GuessIconFromFiles(zip);
        }
        else
        {
            // aapt2's badging doesn't report isSplitRequired; read it from the manifest directly.
            info = info with { RequiresSplits = SafeRead(() => ReadManagedManifest(manifestEntry, abis).RequiresSplits) };
        }

        if (info.RequiresSplits)
        {
            warnings.Add("This APK is one part of a split app. Installing it alone will fail; import all of its split APKs together.");
        }

        if (info.LauncherActivity is null)
        {
            warnings.Add("The app has no launcher activity, so SSGen can't open it automatically.");
        }

        if (icon is null)
        {
            warnings.Add("The app icon couldn't be extracted. You can add it manually later.");
        }

        return new ApkInspection(info, icon, warnings);
    }

    private static UserFacingException AabNotSupported() => new(
        "Android App Bundles (.aab) can't be installed directly.",
        "Build an APK instead (Android Studio: Build > Build APK), or convert the bundle with bundletool: build-apks --mode=universal.");

    private static ZipArchive OpenZip(string path)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            throw new UserFacingException("This file isn't a valid APK (it's not a readable archive).", "Check that the download or build finished, then pick the file again.", ex);
        }
    }

    private static ApkInfo ReadManagedManifest(ZipArchiveEntry entry, IReadOnlyList<string> abis)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        try
        {
            return ManifestReader.Read(ms.ToArray(), abis);
        }
        catch (FormatException ex)
        {
            throw new UserFacingException("The app's manifest couldn't be read.", "The APK may be damaged or obfuscated in an unusual way. " + ex.Message, ex);
        }
    }

    private static bool SafeRead(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (UserFacingException)
        {
            return false;
        }
    }

    private async Task<byte[]?> ResolveIconWithAaptAsync(
        ZipArchive zip, string apkPath, string aapt2, string? iconPath, List<string> warnings, CancellationToken ct)
    {
        if (iconPath is null)
        {
            return GuessIconFromFiles(zip);
        }

        if (AaptResourceDumpParser.IsRaster(iconPath))
        {
            return NormalizeIcon(ReadEntry(zip, iconPath));
        }

        // XML icon (adaptive icon or vector). Look for raster alternatives of the same resource first.
        var dump = await _runner.RunAsync(new ProcessSpec(aapt2, ["dump", "resources", apkPath]) { Timeout = TimeSpan.FromMinutes(2) }, ct);
        if (!dump.Succeeded)
        {
            return GuessIconFromFiles(zip);
        }

        var resources = AaptResourceDumpParser.Parse(dump.StdOut);
        var owner = resources.Values.FirstOrDefault(r => r.Values.Any(v => v.FilePath == iconPath));
        if (owner is not null && BestRaster(owner) is { } legacy)
        {
            return NormalizeIcon(ReadEntry(zip, legacy));
        }

        var tree = await _runner.RunAsync(new ProcessSpec(aapt2, ["dump", "xmltree", "--file", iconPath, apkPath]) { Timeout = TimeSpan.FromSeconds(30) }, ct);
        if (tree.Succeeded)
        {
            var (bgId, fgId) = AdaptiveIconParser.Parse(tree.StdOut);
            var fg = fgId is { } f && resources.TryGetValue(f, out var fgEntry) && BestRaster(fgEntry) is { } fgPath ? ReadEntry(zip, fgPath) : null;
            if (fg is not null)
            {
                SKColor? bgColor = null;
                byte[]? bgImage = null;
                if (bgId is { } b && resources.TryGetValue(b, out var bgEntry))
                {
                    bgImage = BestRaster(bgEntry) is { } bgPath ? ReadEntry(zip, bgPath) : null;
                    bgColor = bgEntry.Values.Select(v => v.Literal).OfType<string>().Select(ParseColor).FirstOrDefault(c => c is not null);
                }

                return ComposeAdaptive(bgColor, bgImage, fg);
            }
        }

        warnings.Add("The icon is a vector drawable; a fallback image from the APK was used if one was found.");
        return GuessIconFromFiles(zip);
    }

    private static string? BestRaster(AaptResourceDumpParser.ResourceEntry entry) =>
        entry.Values
            .Where(v => AaptResourceDumpParser.IsRaster(v.FilePath))
            .OrderByDescending(v => AaptResourceDumpParser.DensityRank(v.Config))
            .Select(v => v.FilePath)
            .FirstOrDefault();

    /// <summary>Last resort: the largest launcher-looking raster in the APK.</summary>
    private static byte[]? GuessIconFromFiles(ZipArchive zip)
    {
        var candidate = zip.Entries
            .Where(e => AaptResourceDumpParser.IsRaster(e.FullName) && e.FullName.StartsWith("res/", StringComparison.Ordinal))
            .Where(e => e.FullName.Contains("ic_launcher", StringComparison.OrdinalIgnoreCase)
                        && !e.FullName.Contains("foreground", StringComparison.OrdinalIgnoreCase)
                        && !e.FullName.Contains("background", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Length)
            .FirstOrDefault();
        return candidate is null ? null : NormalizeIcon(ReadEntry(zip, candidate.FullName));
    }

    private static byte[]? ReadEntry(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null)
        {
            return null;
        }

        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static SKColor? ParseColor(string literal) =>
        literal.StartsWith('#') && SKColor.TryParse(literal, out var c) ? c : null;

    /// <summary>Decodes any raster (PNG/WebP/JPEG) and re-encodes it as a PNG no larger than 512 px.</summary>
    private static byte[]? NormalizeIcon(byte[]? data)
    {
        if (data is null)
        {
            return null;
        }

        using var bitmap = SKBitmap.Decode(data);
        if (bitmap is null)
        {
            return null;
        }

        var scale = Math.Min(1f, (float)IconSize / Math.Max(bitmap.Width, bitmap.Height));
        using var surface = SKSurface.Create(new SKImageInfo((int)(bitmap.Width * scale), (int)(bitmap.Height * scale)));
        using var paint = new SKPaint { IsAntialias = true };
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(SKImage.FromBitmap(bitmap), new SKRect(0, 0, bitmap.Width * scale, bitmap.Height * scale), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        return Encode(surface);
    }

    /// <summary>
    /// Renders an adaptive icon: layers are 108dp with the visible area the centre 72dp, masked to a rounded square.
    /// </summary>
    internal static byte[]? ComposeAdaptive(SKColor? background, byte[]? backgroundImage, byte[] foreground)
    {
        using var fg = SKBitmap.Decode(foreground);
        if (fg is null)
        {
            return null;
        }

        using var bg = backgroundImage is null ? null : SKBitmap.Decode(backgroundImage);
        using var surface = SKSurface.Create(new SKImageInfo(IconSize, IconSize));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // The full 108dp layer is drawn at 1.5x so its centre 72dp fills the output.
        var full = IconSize * 1.5f;
        var offset = (IconSize - full) / 2;
        var layerRect = new SKRect(offset, offset, offset + full, offset + full);
        var radius = IconSize * 0.22f;
        canvas.ClipRoundRect(new SKRoundRect(new SKRect(0, 0, IconSize, IconSize), radius, radius), antialias: true);

        canvas.Clear(background ?? SKColors.White);
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        if (bg is not null)
        {
            canvas.DrawImage(SKImage.FromBitmap(bg), layerRect, sampling);
        }

        canvas.DrawImage(SKImage.FromBitmap(fg), layerRect, sampling);
        return Encode(surface);
    }

    private static byte[] Encode(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Human summary line, e.g. "Notes 2.2.2 (105) · Android 5.0+ (API 21) · targets API 34".</summary>
    public static string Describe(ApkInfo info)
    {
        var parts = new List<string> { info.DisplayName + (info.VersionName is { } v ? " " + v : "") + (info.VersionCode is { } c ? $" ({c.ToString(CultureInfo.InvariantCulture)})" : "") };
        if (info.MinSdk is { } min)
        {
            parts.Add($"min API {min}");
        }

        if (info.TargetSdk is { } target)
        {
            parts.Add($"targets API {target}");
        }

        parts.Add(info.NativeAbis.Count == 0 ? "no native code" : "native: " + string.Join(", ", info.NativeAbis));
        return string.Join(" · ", parts);
    }
}
