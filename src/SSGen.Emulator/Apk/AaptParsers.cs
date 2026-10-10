using System.Globalization;
using System.Text.RegularExpressions;

namespace SSGen.Emulator.Apk;

/// <summary>Parses `aapt2 dump badging` output.</summary>
public static partial class AaptBadgingParser
{
    [GeneratedRegex(@"(\w[\w-]*)='([^']*)'")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"^application-icon-(\d+):'([^']*)'")]
    private static partial Regex IconLine();

    [GeneratedRegex(@"'([^']*)'")]
    private static partial Regex Quoted();

    public sealed record Badging(ApkInfo Info, string? IconPath);

    public static Badging Parse(string output)
    {
        string? package = null, versionName = null, label = null, launcher = null, iconPath = null;
        long? versionCode = null;
        int? minSdk = null, targetSdk = null;
        var permissions = new List<string>();
        var abis = new List<string>();
        var bestDensity = -1;

        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("package:", StringComparison.Ordinal))
            {
                var kv = Pairs(line);
                package = kv.GetValueOrDefault("name");
                versionName = NullIfEmpty(kv.GetValueOrDefault("versionName"));
                if (long.TryParse(kv.GetValueOrDefault("versionCode"), NumberStyles.None, CultureInfo.InvariantCulture, out var vc))
                {
                    versionCode = vc;
                }
            }
            else if (line.StartsWith("minSdkVersion:", StringComparison.Ordinal) || line.StartsWith("sdkVersion:", StringComparison.Ordinal))
            {
                minSdk = FirstInt(line);
            }
            else if (line.StartsWith("targetSdkVersion:", StringComparison.Ordinal))
            {
                targetSdk = FirstInt(line);
            }
            else if (line.StartsWith("application-label:", StringComparison.Ordinal))
            {
                label ??= NullIfEmpty(Quoted().Match(line).Groups[1].Value);
            }
            else if (line.StartsWith("application:", StringComparison.Ordinal))
            {
                label ??= NullIfEmpty(Pairs(line).GetValueOrDefault("label"));
            }
            else if (IconLine().Match(line) is { Success: true } icon)
            {
                var density = int.Parse(icon.Groups[1].Value, CultureInfo.InvariantCulture);
                // 65534/65535 are "anydpi"/"nodpi": only use them if nothing density-specific exists.
                var rank = density >= 65534 ? 0 : density;
                if (rank > bestDensity)
                {
                    bestDensity = rank;
                    iconPath = icon.Groups[2].Value;
                }
            }
            else if (line.StartsWith("launchable-activity:", StringComparison.Ordinal))
            {
                launcher ??= Pairs(line).GetValueOrDefault("name");
            }
            else if (line.StartsWith("uses-permission:", StringComparison.Ordinal))
            {
                if (Pairs(line).GetValueOrDefault("name") is { Length: > 0 } p)
                {
                    permissions.Add(p);
                }
            }
            else if (line.StartsWith("native-code:", StringComparison.Ordinal))
            {
                abis.AddRange(Quoted().Matches(line).Select(m => m.Groups[1].Value));
            }
        }

        if (package is null)
        {
            throw new FormatException("aapt2 output has no package line.");
        }

        var info = new ApkInfo
        {
            PackageName = package,
            Label = label,
            VersionCode = versionCode,
            VersionName = versionName,
            MinSdk = minSdk,
            TargetSdk = targetSdk,
            LauncherActivity = launcher,
            Permissions = permissions.Distinct().ToList(),
            NativeAbis = abis,
            ParsedBy = "aapt2",
        };
        return new Badging(info, iconPath);
    }

    private static Dictionary<string, string> Pairs(string line) =>
        KeyValue().Matches(line).GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);

    private static int? FirstInt(string line) =>
        int.TryParse(Quoted().Match(line).Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

/// <summary>
/// Parses `aapt2 dump resources` enough to resolve icons: resource id -> name and per-config values/files.
/// </summary>
public static partial class AaptResourceDumpParser
{
    public sealed record ResourceValue(string Config, string? FilePath, string? Literal);

    public sealed record ResourceEntry(uint Id, string Name, IReadOnlyList<ResourceValue> Values);

    [GeneratedRegex(@"^\s*resource 0x([0-9a-fA-F]{8}) (\S+)")]
    private static partial Regex ResourceLine();

    [GeneratedRegex(@"^\s*\(([^)]*)\) \(file\) (\S+)")]
    private static partial Regex FileLine();

    [GeneratedRegex(@"^\s*\(([^)]*)\) (.+)$")]
    private static partial Regex ValueLine();

    public static IReadOnlyDictionary<uint, ResourceEntry> Parse(string output)
    {
        var result = new Dictionary<uint, ResourceEntry>();
        uint? currentId = null;
        string? currentName = null;
        var values = new List<ResourceValue>();

        void Flush()
        {
            if (currentId is { } id && currentName is not null)
            {
                result[id] = new ResourceEntry(id, currentName, values.ToList());
            }

            values.Clear();
        }

        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (ResourceLine().Match(line) is { Success: true } r)
            {
                Flush();
                currentId = uint.Parse(r.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                currentName = r.Groups[2].Value;
            }
            else if (currentId is not null && FileLine().Match(line) is { Success: true } f)
            {
                values.Add(new ResourceValue(f.Groups[1].Value, f.Groups[2].Value, null));
            }
            else if (currentId is not null && ValueLine().Match(line) is { Success: true } v)
            {
                values.Add(new ResourceValue(v.Groups[1].Value, null, v.Groups[2].Value.Trim()));
            }
            else if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                Flush();
                currentId = null;
            }
        }

        Flush();
        return result;
    }

    /// <summary>Density rank of a config qualifier ("xxxhdpi" > "xxhdpi" > … > "" ), used to pick the sharpest raster.</summary>
    public static int DensityRank(string config) => config switch
    {
        _ when config.Contains("xxxhdpi", StringComparison.Ordinal) => 6,
        _ when config.Contains("xxhdpi", StringComparison.Ordinal) => 5,
        _ when config.Contains("xhdpi", StringComparison.Ordinal) => 4,
        _ when config.Contains("hdpi", StringComparison.Ordinal) => 3,
        _ when config.Contains("mdpi", StringComparison.Ordinal) => 2,
        _ when config.Contains("ldpi", StringComparison.Ordinal) => 1,
        _ => 0,
    };

    public static bool IsRaster(string? path) =>
        path is not null && (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Parses `aapt2 dump xmltree` of an adaptive-icon to find its background/foreground references.</summary>
public static partial class AdaptiveIconParser
{
    [GeneratedRegex(@"E: (background|foreground)\b[\s\S]*?drawable\(0x01010199\)=@0x([0-9a-fA-F]{8})")]
    private static partial Regex Layer();

    public static (uint? Background, uint? Foreground) Parse(string xmltree)
    {
        uint? bg = null, fg = null;
        foreach (Match m in Layer().Matches(xmltree ?? string.Empty))
        {
            var id = uint.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (m.Groups[1].Value == "background")
            {
                bg ??= id;
            }
            else
            {
                fg ??= id;
            }
        }

        return (bg, fg);
    }
}
