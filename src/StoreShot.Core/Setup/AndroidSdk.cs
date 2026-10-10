using System.Globalization;

namespace StoreShot.Core.Setup;

/// <summary>An installed system image, e.g. system-images/android-36/google_apis_playstore/x86_64.</summary>
public sealed record SystemImage(string Platform, string Tag, string Abi, string Directory)
{
    /// <summary>Package id in the slash form used by the Android CLI.</summary>
    public string PackageId => $"system-images/{Platform}/{Tag}/{Abi}";

    /// <summary>Major API level parsed from the platform folder ("android-36.1" -> 36), or 0 for preview codenames.</summary>
    public int ApiLevel => AndroidSdk.ParseApiLevel(Platform);

    public bool HasGoogleApis => Tag.StartsWith("google_apis", StringComparison.Ordinal);

    /// <summary>True for images that suit phone/tablet screenshots (not Wear, TV, Automotive, or 16 KB page-size test images).</summary>
    public bool IsPhoneImage => Tag is "google_apis" or "google_apis_playstore" or "default";
}

/// <summary>Resolves tool paths inside an Android SDK root. Pure file-system inspection; never runs anything.</summary>
public sealed class AndroidSdk
{
    public AndroidSdk(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }

    public string AdbPath => Path.Combine(Root, "platform-tools", "adb.exe");
    public string EmulatorPath => Path.Combine(Root, "emulator", "emulator.exe");

    public bool HasPlatformTools => File.Exists(AdbPath);
    public bool HasEmulator => File.Exists(EmulatorPath);

    /// <summary>Folder of the highest stable build-tools version that contains aapt2, or null.</summary>
    public string? LatestBuildToolsDir => EnumerateVersionDirs(Path.Combine(Root, "build-tools"))
        .Where(d => File.Exists(Path.Combine(d.Path, "aapt2.exe")))
        .OrderByDescending(d => d.IsStable)
        .ThenByDescending(d => d.Version)
        .Select(d => d.Path)
        .FirstOrDefault();

    public string? Aapt2Path => LatestBuildToolsDir is { } dir ? Path.Combine(dir, "aapt2.exe") : null;

    /// <summary>bin folder of cmdline-tools: "latest" if present, otherwise the highest versioned folder.</summary>
    public string? CmdlineToolsBin
    {
        get
        {
            var root = Path.Combine(Root, "cmdline-tools");
            var latest = Path.Combine(root, "latest", "bin");
            if (Directory.Exists(latest))
            {
                return latest;
            }

            return EnumerateVersionDirs(root)
                .OrderByDescending(d => d.Version)
                .Select(d => Path.Combine(d.Path, "bin"))
                .FirstOrDefault(Directory.Exists);
        }
    }

    /// <summary>The newer native Android CLI (android.exe). Does not need Java.</summary>
    public string? AndroidCliPath => CmdlineToolsBin is { } bin && File.Exists(Path.Combine(bin, "android.exe")) ? Path.Combine(bin, "android.exe") : null;

    /// <summary>Legacy sdkmanager.bat. Deprecated by Google in favour of android.exe and needs Java 17+.</summary>
    public string? SdkManagerPath => CmdlineToolsBin is { } bin && File.Exists(Path.Combine(bin, "sdkmanager.bat")) ? Path.Combine(bin, "sdkmanager.bat") : null;

    public string? AvdManagerPath => CmdlineToolsBin is { } bin && File.Exists(Path.Combine(bin, "avdmanager.bat")) ? Path.Combine(bin, "avdmanager.bat") : null;

    public IReadOnlyList<SystemImage> GetSystemImages()
    {
        var root = Path.Combine(Root, "system-images");
        if (!Directory.Exists(root))
        {
            return [];
        }

        var images = new List<SystemImage>();
        foreach (var platformDir in Directory.EnumerateDirectories(root))
        {
            foreach (var tagDir in Directory.EnumerateDirectories(platformDir))
            {
                foreach (var abiDir in Directory.EnumerateDirectories(tagDir))
                {
                    // A complete image has system.img; partially downloaded ones don't.
                    if (File.Exists(Path.Combine(abiDir, "system.img")))
                    {
                        images.Add(new SystemImage(Path.GetFileName(platformDir), Path.GetFileName(tagDir), Path.GetFileName(abiDir), abiDir));
                    }
                }
            }
        }

        return images;
    }

    /// <summary>"android-36" -> 36, "android-36.1" -> 36, "android-36-ext18" -> 36, "android-Baklava" -> 0.</summary>
    public static int ParseApiLevel(string platform)
    {
        var s = platform.StartsWith("android-", StringComparison.Ordinal) ? platform["android-".Length..] : platform;
        var end = 0;
        while (end < s.Length && char.IsAsciiDigit(s[end]))
        {
            end++;
        }

        return end == 0 ? 0 : int.Parse(s.AsSpan(0, end), CultureInfo.InvariantCulture);
    }

    /// <summary>Lenient version parse for folder names like "36.1.0" or "37.0.0-rc1".</summary>
    public static (Version Version, bool IsStable)? ParseFolderVersion(string name)
    {
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        var core = dash >= 0 ? name[..dash] : name;
        return Version.TryParse(core.Contains('.', StringComparison.Ordinal) ? core : core + ".0", out var v) ? (v, dash < 0) : null;
    }

    private static IEnumerable<(string Path, Version Version, bool IsStable)> EnumerateVersionDirs(string parent)
    {
        if (!Directory.Exists(parent))
        {
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(parent))
        {
            if (ParseFolderVersion(Path.GetFileName(dir)) is { } parsed)
            {
                yield return (dir, parsed.Version, parsed.IsStable);
            }
        }
    }
}

public sealed record SdkLocation(AndroidSdk? Sdk, string? Source, IReadOnlyList<string> Tried);

public static class AndroidSdkLocator
{
    private static readonly string[] SdkMarkerDirs = ["platform-tools", "emulator", "cmdline-tools", "build-tools"];

    /// <summary>
    /// Finds the SDK in priority order: explicit setting, ANDROID_HOME, ANDROID_SDK_ROOT, %LOCALAPPDATA%\Android\Sdk.
    /// A folder counts as an SDK if it contains platform-tools, emulator, cmdline-tools or build-tools.
    /// </summary>
    public static SdkLocation Locate(string? configuredPath, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var candidates = new List<(string Path, string Source)>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add((configuredPath, "Settings"));
        }

        foreach (var name in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            if (getEnv(name) is { Length: > 0 } value)
            {
                candidates.Add((value, name));
            }
        }

        if (getEnv("LOCALAPPDATA") is { Length: > 0 } local)
        {
            candidates.Add((Path.Combine(local, "Android", "Sdk"), "Android Studio default location"));
        }

        var tried = new List<string>();
        foreach (var (path, source) in candidates)
        {
            tried.Add($"{path} ({source})");
            if (LooksLikeSdk(path))
            {
                return new SdkLocation(new AndroidSdk(path), source, tried);
            }
        }

        return new SdkLocation(null, null, tried);
    }

    public static bool LooksLikeSdk(string path) =>
        Directory.Exists(path) &&
        SdkMarkerDirs.Any(d => Directory.Exists(Path.Combine(path, d)));
}
