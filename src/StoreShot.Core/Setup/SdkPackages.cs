using System.Globalization;
using System.Text.RegularExpressions;
using StoreShot.Core.Processes;

namespace StoreShot.Core.Setup;

/// <summary>One row of `android sdk list` / `sdkmanager --list`. Ids are normalised to the slash form.</summary>
public sealed record SdkPackage(string Id, string Version, string Description, string? UpdateVersion = null);

public sealed record SdkPackageList(IReadOnlyList<SdkPackage> Installed, IReadOnlyList<SdkPackage> Available);

public static partial class SdkPackageListParser
{
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex ColumnGap();

    /// <summary>
    /// Parses both the current column-aligned format and the older pipe-separated sdkmanager format.
    /// Unknown lines (warnings, headers, separators) are ignored.
    /// </summary>
    public static SdkPackageList Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var installed = new List<SdkPackage>();
        var available = new List<SdkPackage>();
        List<SdkPackage>? section = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith("Installed packages", StringComparison.OrdinalIgnoreCase))
            {
                section = installed;
                continue;
            }

            if (trimmed.StartsWith("Available packages", StringComparison.OrdinalIgnoreCase))
            {
                section = available;
                continue;
            }

            if (trimmed.StartsWith("Available updates", StringComparison.OrdinalIgnoreCase))
            {
                // Updates are already reflected by "->" on installed rows.
                section = null;
                continue;
            }

            // Package rows are indented; anything flush-left ends the section (e.g. trailing messages).
            if (!char.IsWhiteSpace(line[0]))
            {
                section = null;
                continue;
            }

            if (section is null || ParseRow(trimmed) is not { } pkg)
            {
                continue;
            }

            section.Add(pkg);
        }

        return new SdkPackageList(installed, available);
    }

    private static SdkPackage? ParseRow(string row)
    {
        string[] cols = row.Contains('|', StringComparison.Ordinal)
            ? row.Split('|', StringSplitOptions.TrimEntries)
            : ColumnGap().Split(row);

        if (cols.Length < 2 || cols[0] is "Path" || cols[0].StartsWith("---", StringComparison.Ordinal))
        {
            return null;
        }

        var id = cols[0].Replace(';', '/');
        var version = cols[1];
        string? update = null;
        var descIndex = 2;
        if (cols.Length > 3 && cols[2] == "->")
        {
            update = cols[3];
            descIndex = 4;
        }

        var description = cols.Length > descIndex ? cols[descIndex] : string.Empty;
        return new SdkPackage(id, version, description, update);
    }

    /// <summary>Highest stable build-tools package id, e.g. "build-tools/37.0.0".</summary>
    public static string? LatestStableBuildTools(IEnumerable<SdkPackage> packages) =>
        packages
            .Where(p => p.Id.StartsWith("build-tools/", StringComparison.Ordinal))
            .Select(p => (p.Id, Parsed: AndroidSdk.ParseFolderVersion(p.Id["build-tools/".Length..])))
            .Where(p => p.Parsed is { IsStable: true })
            .OrderByDescending(p => p.Parsed!.Value.Version)
            .Select(p => p.Id)
            .FirstOrDefault();

    /// <summary>
    /// Recommended phone system image: highest plain API level (no ".x" minor or "-ext" variants), Google Play,
    /// matching host ABI.
    /// </summary>
    public static string? RecommendedSystemImage(IEnumerable<SdkPackage> packages, string abi = "x86_64")
    {
        return packages
            .Select(p => p.Id.Split('/'))
            .Where(parts => parts.Length == 4 && parts[0] == "system-images" && parts[2] == "google_apis_playstore" && parts[3] == abi)
            .Where(parts => Regex.IsMatch(parts[1], @"^android-\d+$", RegexOptions.CultureInvariant))
            .OrderByDescending(parts => int.Parse(parts[1]["android-".Length..], CultureInfo.InvariantCulture))
            .Select(parts => string.Join('/', parts))
            .FirstOrDefault();
    }
}

public sealed record InstallProgress(double? Percent, string Message);

/// <summary>
/// Lists and installs SDK packages. Prefers the native Android CLI (android.exe, no Java needed) and falls back
/// to the deprecated sdkmanager.bat (needs Java 17+).
/// </summary>
public sealed partial class SdkPackageManager
{
    private readonly AndroidSdk _sdk;
    private readonly IProcessRunner _runner;
    private readonly string? _javaHome;

    public SdkPackageManager(AndroidSdk sdk, IProcessRunner runner, string? javaHome)
    {
        _sdk = sdk;
        _runner = runner;
        _javaHome = javaHome;
    }

    [GeneratedRegex(@"(\d{1,3})\s?%")]
    private static partial Regex PercentPattern();

    public bool CanManagePackages => _sdk.AndroidCliPath is not null || (_sdk.SdkManagerPath is not null && _javaHome is not null);

    public async Task<SdkPackageList> ListAsync(CancellationToken ct)
    {
        var result = await _runner.RunAsync(BuildSpec(["list"], ["--list"], TimeSpan.FromMinutes(3), null), ct);
        if (!result.Succeeded)
        {
            throw new UserFacingException(
                "Couldn't fetch the list of Android SDK packages.",
                "Check your internet connection and try again. Details: " + Tail(result.Combined));
        }

        return SdkPackageListParser.Parse(result.StdOut);
    }

    /// <summary>
    /// Installs a package. The caller must have shown the Android SDK licence to the user first: this answers "y"
    /// to licence prompts on their behalf.
    /// </summary>
    public async Task InstallAsync(string packageId, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new InstallProgress(null, $"Installing {packageId}…"));
        void OnLine(string line)
        {
            var m = PercentPattern().Match(line);
            double? pct = m.Success ? Math.Clamp(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 0, 100) : null;
            if (line.Trim().Length > 0)
            {
                progress?.Report(new InstallProgress(pct, line.Trim()));
            }
        }

        var yes = string.Concat(Enumerable.Repeat("y\n", 30));
        var spec = BuildSpec(["install", packageId], ["--install", packageId.Replace('/', ';')], TimeSpan.FromMinutes(60), OnLine) with
        {
            StandardInput = yes,
        };
        var result = await _runner.RunAsync(spec, ct);
        if (!result.Succeeded)
        {
            throw new UserFacingException(
                result.TimedOut ? $"Installing {packageId} timed out." : $"Installing {packageId} failed.",
                "Check your internet connection and free disk space, then retry. Details: " + Tail(result.Combined));
        }

        progress?.Report(new InstallProgress(100, $"Installed {packageId}."));
    }

    private ProcessSpec BuildSpec(string[] cliArgs, string[] legacyArgs, TimeSpan timeout, Action<string>? onLine)
    {
        if (_sdk.AndroidCliPath is { } cli)
        {
            return new ProcessSpec(cli, [$"--sdk={_sdk.Root}", "sdk", .. cliArgs]) { Timeout = timeout, OnLine = onLine };
        }

        if (_sdk.SdkManagerPath is { } legacy && _javaHome is not null)
        {
            return new ProcessSpec(legacy, [$"--sdk_root={_sdk.Root}", .. legacyArgs])
            {
                Timeout = timeout,
                OnLine = onLine,
                Environment = new Dictionary<string, string> { ["JAVA_HOME"] = _javaHome },
            };
        }

        throw new UserFacingException(
            "The Android SDK command-line tools aren't installed, so StoreShot can't download SDK packages.",
            "In Android Studio open Settings > Languages & Frameworks > Android SDK > SDK Tools and install \"Android SDK Command-line Tools (latest)\".");
    }

    private static string Tail(string s) => s.Length <= 400 ? s : "…" + s[^400..];
}
