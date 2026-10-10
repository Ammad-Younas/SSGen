using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SSGen.Core;
using SSGen.Core.Setup;

namespace SSGen.Emulator.Avd;

/// <summary>Screen shapes SSGen offers for new virtual devices (portrait).</summary>
public sealed record DeviceProfile(string Id, string DisplayName, int Width, int Height, int Density, int RamMb, bool IsTablet)
{
    public static readonly DeviceProfile PixelPhone = new("phone", "Phone (Pixel-class, 6.3\")", 1080, 2400, 420, 2048, false);
    public static readonly DeviceProfile SmallPhone = new("small_phone", "Small phone (4.7\")", 720, 1280, 320, 2048, false);
    public static readonly DeviceProfile Tablet7 = new("tablet_7", "7\" tablet", 1200, 1920, 320, 3072, true);
    public static readonly DeviceProfile Tablet10 = new("tablet_10", "10\" tablet", 1600, 2560, 320, 3072, true);

    public static IReadOnlyList<DeviceProfile> All { get; } = [PixelPhone, SmallPhone, Tablet7, Tablet10];

    public override string ToString() => DisplayName;
}

/// <summary>An AVD on disk.</summary>
public sealed record AvdInfo(
    string Name,
    string IniPath,
    string? Directory,
    string? Target,
    string? SystemImageDir,
    string? Abi,
    int? Width,
    int? Height,
    bool IsManagedBySSGen)
{
    public bool IsComplete => Directory is not null && System.IO.Directory.Exists(Directory);

    /// <summary>API level from "android-36" style targets, or null.</summary>
    public int? ApiLevel => Target is null ? null : AndroidSdk.ParseApiLevel(Target) is var n and > 0 ? n : null;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (ApiLevel is { } api)
            {
                parts.Add($"API {api}");
            }

            if (Width is { } w && Height is { } h)
            {
                parts.Add($"{w}×{h}");
            }

            if (Abi is not null)
            {
                parts.Add(Abi);
            }

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Lists and creates AVDs by reading and writing their .ini files directly. This is the documented on-disk format
/// and needs no Java. SSGen only deletes or wipes AVDs it created itself (marked in config.ini).
/// </summary>
public sealed partial class AvdManager
{
    /// <summary>config.ini marker for AVDs created by SSGen (the only ones it may wipe or delete).</summary>
    public const string ManagedKey = "ssgen.managed";

    /// <summary>Marker written before the rename to SSGen; still honoured so existing AVDs stay managed.</summary>
    public const string LegacyManagedKey = "storeshot.managed";

    public AvdManager(string? avdHome = null) => AvdHome = avdHome ?? DefaultAvdHome();

    public string AvdHome { get; }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex ValidName();

    /// <summary>Where the emulator looks for AVDs: ANDROID_AVD_HOME, ANDROID_USER_HOME\avd, or %USERPROFILE%\.android\avd.</summary>
    public static string DefaultAvdHome()
    {
        if (Environment.GetEnvironmentVariable("ANDROID_AVD_HOME") is { Length: > 0 } avdHome)
        {
            return avdHome;
        }

        if (Environment.GetEnvironmentVariable("ANDROID_USER_HOME") is { Length: > 0 } userHome)
        {
            return Path.Combine(userHome, "avd");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "avd");
    }

    public IReadOnlyList<AvdInfo> List()
    {
        if (!Directory.Exists(AvdHome))
        {
            return [];
        }

        var list = new List<AvdInfo>();
        foreach (var ini in Directory.EnumerateFiles(AvdHome, "*.ini"))
        {
            var name = Path.GetFileNameWithoutExtension(ini);
            var top = ReadIni(ini);
            var dir = top.GetValueOrDefault("path");
            if (dir is null || !Directory.Exists(dir))
            {
                var rel = top.GetValueOrDefault("path.rel");
                var candidate = rel is null ? null : Path.Combine(Path.GetDirectoryName(AvdHome)!, rel);
                dir = candidate is not null && Directory.Exists(candidate) ? candidate : null;
            }

            var config = dir is null ? new Dictionary<string, string>() : ReadIni(Path.Combine(dir, "config.ini"));
            list.Add(new AvdInfo(
                name,
                ini,
                dir,
                top.GetValueOrDefault("target") ?? config.GetValueOrDefault("target"),
                config.GetValueOrDefault("image.sysdir.1"),
                config.GetValueOrDefault("abi.type"),
                ParseInt(config.GetValueOrDefault("hw.lcd.width")),
                ParseInt(config.GetValueOrDefault("hw.lcd.height")),
                config.GetValueOrDefault(ManagedKey) == "yes" || config.GetValueOrDefault(LegacyManagedKey) == "yes"));
        }

        return list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public AvdInfo? Find(string name) => List().FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Creates a new AVD from an installed system image. Never overwrites an existing one.</summary>
    public AvdInfo Create(string name, DeviceProfile profile, SystemImage image, string sdkRoot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(image);
        if (!ValidName().IsMatch(name))
        {
            throw new UserFacingException($"\"{name}\" isn't a valid device name.", "Use only letters, digits, dots, dashes and underscores.");
        }

        if (Find(name) is not null || Directory.Exists(Path.Combine(AvdHome, name + ".avd")))
        {
            throw new UserFacingException($"A virtual device named \"{name}\" already exists.", "Pick another name, or use the existing device.");
        }

        Directory.CreateDirectory(AvdHome);
        var dir = Path.Combine(AvdHome, name + ".avd");
        Directory.CreateDirectory(dir);

        var sysdir = Path.GetRelativePath(sdkRoot, image.Directory).Replace('/', '\\').TrimEnd('\\') + "\\";
        var playStore = image.Tag == "google_apis_playstore";
        var config = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["AvdId"] = name,
            ["avd.ini.displayname"] = name,
            ["avd.ini.encoding"] = "UTF-8",
            ["PlayStore.enabled"] = playStore ? "true" : "false",
            ["abi.type"] = image.Abi,
            ["hw.cpu.arch"] = image.Abi == "arm64-v8a" ? "arm64" : image.Abi,
            ["hw.cpu.ncore"] = "4",
            ["hw.ramSize"] = profile.RamMb.ToString(CultureInfo.InvariantCulture),
            ["vm.heapSize"] = "256",
            ["disk.dataPartition.size"] = "6G",
            ["hw.lcd.width"] = profile.Width.ToString(CultureInfo.InvariantCulture),
            ["hw.lcd.height"] = profile.Height.ToString(CultureInfo.InvariantCulture),
            ["hw.lcd.density"] = profile.Density.ToString(CultureInfo.InvariantCulture),
            ["hw.initialOrientation"] = "portrait",
            ["hw.keyboard"] = "yes",
            ["hw.mainKeys"] = "no",
            ["hw.gpu.enabled"] = "yes",
            ["hw.gpu.mode"] = "auto",
            ["hw.audioInput"] = "no",
            ["hw.camera.back"] = "emulated",
            ["hw.camera.front"] = "emulated",
            ["hw.sdCard"] = "no",
            ["showDeviceFrame"] = "no",
            ["skin.dynamic"] = "yes",
            ["fastboot.forceColdBoot"] = "no",
            ["fastboot.forceFastBoot"] = "yes",
            ["image.sysdir.1"] = sysdir,
            ["tag.id"] = image.Tag,
            ["tag.ids"] = image.Tag,
            ["target"] = image.Platform,
            [ManagedKey] = "yes",
            ["ssgen.profile"] = profile.Id,
        };
        WriteIni(Path.Combine(dir, "config.ini"), config);
        WriteIni(Path.Combine(AvdHome, name + ".ini"), new Dictionary<string, string>
        {
            ["avd.ini.encoding"] = "UTF-8",
            ["path"] = dir,
            ["path.rel"] = Path.Combine("avd", name + ".avd"),
            ["target"] = image.Platform,
        });

        return Find(name)!;
    }

    /// <summary>Deletes an AVD that SSGen created. Refuses to touch any other AVD.</summary>
    public static void Delete(AvdInfo avd)
    {
        ArgumentNullException.ThrowIfNull(avd);
        EnsureManaged(avd, "delete");
        if (avd.Directory is not null && Directory.Exists(avd.Directory))
        {
            Directory.Delete(avd.Directory, recursive: true);
        }

        File.Delete(avd.IniPath);
    }

    /// <summary>Throws unless SSGen created this AVD.</summary>
    public static void EnsureManaged(AvdInfo avd, string action)
    {
        ArgumentNullException.ThrowIfNull(avd);
        if (!avd.IsManagedBySSGen)
        {
            throw new UserFacingException(
                $"SSGen won't {action} \"{avd.Name}\" because it didn't create it.",
                "Manage your own virtual devices in Android Studio's Device Manager.");
        }
    }

    /// <summary>Picks a free name like "SSGen_Pixel_API36", adding a numeric suffix if needed.</summary>
    public string SuggestName(DeviceProfile profile, SystemImage image)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(image);
        var kind = profile.Id switch
        {
            "phone" => "Pixel",
            "small_phone" => "SmallPhone",
            "tablet_7" => "Tablet7",
            "tablet_10" => "Tablet10",
            _ => "Device",
        };
        var baseName = $"SSGen_{kind}_API{image.ApiLevel}";
        var existing = List().Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = baseName;
        for (var i = 2; existing.Contains(name); i++)
        {
            name = $"{baseName}_{i}";
        }

        return name;
    }

    internal static Dictionary<string, string> ReadIni(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return map;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && !line.StartsWith('#'))
            {
                map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        return map;
    }

    private static void WriteIni(string path, IEnumerable<KeyValuePair<string, string>> values) =>
        File.WriteAllText(path, string.Concat(values.Select(kv => $"{kv.Key}={kv.Value}\n")), new UTF8Encoding(false));

    private static int? ParseInt(string? s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
}
