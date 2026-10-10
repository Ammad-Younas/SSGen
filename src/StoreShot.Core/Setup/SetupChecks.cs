using System.Text.RegularExpressions;
using StoreShot.Core.Processes;

namespace StoreShot.Core.Setup;

internal static class Fixes
{
    public const string SdkManagerHint =
        "Install \"Android SDK Command-line Tools (latest)\" from Android Studio's SDK Manager (Settings > Languages & Frameworks > Android SDK > SDK Tools).";

    /// <summary>A fix that installs a package, or null if the context can't install packages.</summary>
    public static SetupFix? Install(SetupContext ctx, string label, Func<CancellationToken, Task<string?>> resolvePackageId)
    {
        if (ctx.Packages is not { CanManagePackages: true } packages)
        {
            return null;
        }

        return new SetupFix(label, async (progress, ct) =>
        {
            progress.Report(new InstallProgress(null, "Looking up the latest package version…"));
            var id = await resolvePackageId(ct) ?? throw new UserFacingException(
                "No matching package was found in Google's SDK repository.",
                "Install it manually from Android Studio's SDK Manager.");
            await packages.InstallAsync(id, progress, ct);
        }, RequiresSdkLicense: true);
    }

    public static SetupFix? Install(SetupContext ctx, string label, string packageId) =>
        Install(ctx, label, _ => Task.FromResult<string?>(packageId));
}

public sealed class DotNetRuntimeCheck : ISetupCheck
{
    public string Id => "dotnet";
    public string Title => ".NET runtime";
    public string Purpose => "Runs StoreShot Studio itself.";
    public int Order => 0;

    public Task<CheckResult> RunAsync(SetupContext context, CancellationToken ct) =>
        Task.FromResult(new CheckResult(CheckStatus.Pass, $".NET {Environment.Version}", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription));
}

public sealed class AndroidSdkCheck : ISetupCheck
{
    public string Id => "sdk";
    public string Title => "Android SDK";
    public string Purpose => "Provides the emulator, adb and APK tools.";
    public int Order => 10;

    public Task<CheckResult> RunAsync(SetupContext context, CancellationToken ct)
    {
        var loc = context.SdkLocation;
        if (loc.Sdk is { } sdk)
        {
            return Task.FromResult(new CheckResult(CheckStatus.Pass, sdk.Root, $"Found via {loc.Source}."));
        }

        return Task.FromResult(new CheckResult(
            CheckStatus.Fail,
            "No Android SDK found.",
            "Looked in:\n" + string.Join("\n", loc.Tried.Select(t => "• " + t)),
            "Install Android Studio (it installs the SDK to %LOCALAPPDATA%\\Android\\Sdk), or set the SDK folder in Settings if it's somewhere else."));
    }
}

public sealed partial class PlatformToolsCheck : ISetupCheck
{
    public string Id => "platform-tools";
    public string Title => "Platform tools (adb)";
    public string Purpose => "Talks to the emulator: installs the APK, sends taps, captures screens.";
    public int Order => 20;

    [GeneratedRegex(@"^Version\s+(\S+)", RegexOptions.Multiline)]
    private static partial Regex VersionLine();

    public static string? ParseVersion(string output) => VersionLine().Match(output) is { Success: true } m ? m.Groups[1].Value : null;

    public async Task<CheckResult> RunAsync(SetupContext context, CancellationToken ct)
    {
        if (context.Sdk is not { } sdk)
        {
            return new CheckResult(CheckStatus.Fail, "Needs the Android SDK first.");
        }

        if (!sdk.HasPlatformTools)
        {
            return new CheckResult(CheckStatus.Fail, "adb.exe not found.", sdk.AdbPath, Fixes.SdkManagerHint,
                Fixes.Install(context, "Install platform-tools", "platform-tools"));
        }

        var r = await context.Runner.RunAsync(new ProcessSpec(sdk.AdbPath, ["version"]) { Timeout = TimeSpan.FromSeconds(20) }, ct);
        return r.Succeeded
            ? new CheckResult(CheckStatus.Pass, $"adb {ParseVersion(r.StdOut) ?? "(unknown version)"}", sdk.AdbPath)
            : new CheckResult(CheckStatus.Fail, "adb is installed but doesn't run.", r.Combined, "Reinstall platform-tools.",
                Fixes.Install(context, "Reinstall platform-tools", "platform-tools"));
    }
}

public sealed partial class EmulatorCheck : ISetupCheck
{
    public string Id => "emulator";
    public string Title => "Android Emulator";
    public string Purpose => "Runs the app you import, so it can be explored and captured.";
    public int Order => 30;

    [GeneratedRegex(@"emulator version\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();

    public static string? ParseVersion(string output) => VersionPattern().Match(output) is { Success: true } m ? m.Groups[1].Value : null;

    public async Task<CheckResult> RunAsync(SetupContext context, CancellationToken ct)
    {
        if (context.Sdk is not { } sdk)
        {
            return new CheckResult(CheckStatus.Fail, "Needs the Android SDK first.");
        }

        if (!sdk.HasEmulator)
        {
            return new CheckResult(CheckStatus.Fail, "emulator.exe not found.", sdk.EmulatorPath, Fixes.SdkManagerHint,
                Fixes.Install(context, "Install the emulator", "emulator"));
        }

        var r = await context.Runner.RunAsync(new ProcessSpec(sdk.EmulatorPath, ["-version"]) { Timeout = TimeSpan.FromSeconds(30) }, ct);
        var version = ParseVersion(r.Combined);
        return version is not null
            ? new CheckResult(CheckStatus.Pass, $"Emulator {version}", sdk.EmulatorPath)
            : new CheckResult(CheckStatus.Fail, "The emulator is installed but doesn't run.", r.Combined, "Reinstall the emulator.",
                Fixes.Install(context, "Reinstall the emulator", "emulator"));
    }
}

public sealed class BuildToolsCheck : ISetupCheck
{
    public string Id => "build-tools";
    public string Title => "Build tools (aapt2)";
    public string Purpose => "Reads the APK's name, icon, version and permissions.";
    public int Order => 40;

    public Task<CheckResult> RunAsync(SetupContext context, CancellationToken ct)
    {
        if (context.Sdk is not { } sdk)
        {
            return Task.FromResult(new CheckResult(CheckStatus.Fail, "Needs the Android SDK first."));
        }

        if (sdk.Aapt2Path is { } aapt2)
        {
            return Task.FromResult(new CheckResult(CheckStatus.Pass, $"build-tools {Path.GetFileName(sdk.LatestBuildToolsDir)}", aapt2));
        }

        // Not fatal: M2 ships a managed fallback APK parser, it just extracts less.
        return Task.FromResult(new CheckResult(
            CheckStatus.Warning,
            "aapt2 not found. APK details will be limited.",
            FixInstructions: Fixes.SdkManagerHint,
            Fix: Fixes.Install(context, "Install latest build-tools", async c =>
                SdkPackageListParser.LatestStableBuildTools((await context.GetPackageListAsync(c)).Available))));
    }
}

public sealed class CmdlineToolsCheck : ISetupCheck
{
    public string Id => "cmdline-tools";
    public string Title => "SDK command-line tools";
    public string Purpose => "Downloads missing SDK parts and creates virtual devices.";
    public int Order => 50;

    public Task<CheckResult> RunAsync(SetupContext context, CancellationToken ct)
    {
        if (context.Sdk is not { } sdk)
        {
            return Task.FromResult(new CheckResult(CheckStatus.Fail, "Needs the Android SDK first."));
        }

        if (sdk.AndroidCliPath is { } cli)
        {
            return Task.FromResult(new CheckResult(CheckStatus.Pass, "Android CLI available", cli));
        }

        if (sdk.SdkManagerPath is { } legacy)
        {
            return Task.FromResult(context.Java is null
                ? new CheckResult(CheckStatus.Warning, "Only the legacy sdkmanager is installed, and it needs Java.", legacy,
                    "Install Java 17+ (see the Java check), or update \"Android SDK Command-line Tools (latest)\" in Android Studio.")
                : new CheckResult(CheckStatus.Pass, "sdkmanager (legacy) available", legacy));
        }

        return Task.FromResult(new CheckResult(CheckStatus.Warning, "Not installed. StoreShot can't download SDK parts for you.",
            FixInstructions: Fixes.SdkManagerHint));
    }
}

public sealed class SystemImageCheck : ISetupCheck
{
    public string Id => "system-image";
    public string Title => "Emulator system image";
    public string Purpose => "The Android version the emulator boots.";
    public int Order => 60;

    public Task<CheckResult> RunAsync(SetupContext context, CancellationToken ct)
    {
        if (context.Sdk is not { } sdk)
        {
            return Task.FromResult(new CheckResult(CheckStatus.Fail, "Needs the Android SDK first."));
        }

        var abi = SetupContext.HostAbi;
        var images = sdk.GetSystemImages();
        var usable = images
            .Where(i => i.Abi == abi && i.IsPhoneImage && i.ApiLevel >= 30)
            .OrderByDescending(i => i.ApiLevel)
            .ThenByDescending(i => i.Tag == "google_apis_playstore")
            .ToList();
        var listing = images.Count == 0 ? "None installed." : "Installed:\n" + string.Join("\n", images.Select(i => "• " + i.PackageId));

        if (usable.Count > 0)
        {
            return Task.FromResult(new CheckResult(CheckStatus.Pass, $"API {usable[0].ApiLevel} ({usable[0].Tag}, {abi})", listing));
        }

        return Task.FromResult(new CheckResult(
            CheckStatus.Fail,
            $"No phone system image for {abi} (API 30+) installed.",
            listing,
            "Install a \"Google Play Intel x86_64 Atom System Image\" from Android Studio's SDK Manager (SDK Platforms tab, Show Package Details). It's a large download (1–2 GB).",
            Fixes.Install(context, "Install recommended image", async c =>
                SdkPackageListParser.RecommendedSystemImage((await context.GetPackageListAsync(c)).Available, abi))));
    }
}

public sealed class AccelerationCheck : ISetupCheck
{
    public string Id => "acceleration";
    public string Title => "Hardware acceleration";
    public string Purpose => "Without it the emulator is too slow to use.";
    public int Order => 70;

    /// <summary>Interprets `emulator -accel-check`. Output looks like "accel:\n0\nWHPX(10.0.26300) is installed and usable.\naccel".</summary>
    public static (bool Ok, string Message) ParseAccelCheck(int exitCode, string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l is not "accel:" and not "accel" && !int.TryParse(l, out _))
            .ToList();
        var message = lines.Count > 0 ? string.Join(" ", lines) : "No details reported.";
        return (exitCode == 0, message);
    }

    public async Task<CheckResult> RunAsync(SetupContext context, CancellationToken ct)
    {
        if (context.Sdk is not { HasEmulator: true } sdk)
        {
            return new CheckResult(CheckStatus.Fail, "Needs the emulator first.");
        }

        var r = await context.Runner.RunAsync(new ProcessSpec(sdk.EmulatorPath, ["-accel-check"]) { Timeout = TimeSpan.FromSeconds(60) }, ct);
        var (ok, message) = ParseAccelCheck(r.TimedOut ? -1 : r.ExitCode, r.Combined);
        return ok
            ? new CheckResult(CheckStatus.Pass, message)
            : new CheckResult(CheckStatus.Fail, "Hardware acceleration isn't available.", message,
                "1. Enable virtualization (Intel VT-x / AMD-V / SVM) in your PC's BIOS/UEFI.\n" +
                "2. Open \"Turn Windows features on or off\", tick \"Windows Hypervisor Platform\", and restart.\n" +
                "   Or run in an admin PowerShell: Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform");
    }
}

public sealed class JavaCheck : ISetupCheck
{
    public const int MinimumMajor = 17;

    public string Id => "java";
    public string Title => "Java (JDK 17+)";
    public string Purpose => "Only needed by older SDK tools (sdkmanager, avdmanager, apkanalyzer).";
    public int Order => 80;

    public Task<CheckResult> RunAsync(SetupContext context, CancellationToken ct)
    {
        const string install = "Install a JDK 17 or newer (for example Eclipse Temurin), or point Settings at Android Studio's bundled JDK (…\\Android Studio\\jbr).";
        var java = context.Java;
        if (java is null)
        {
            return Task.FromResult(new CheckResult(CheckStatus.Warning, "Not found. Most features work without it.", FixInstructions: install));
        }

        return Task.FromResult(java.MajorVersion >= MinimumMajor
            ? new CheckResult(CheckStatus.Pass, $"Java {java.VersionText}", $"{java.JavaExe} (via {java.Source})")
            : new CheckResult(CheckStatus.Warning, $"Java {java.VersionText} is too old.", java.JavaExe, install));
    }
}

public sealed class DiskSpaceCheck : ISetupCheck
{
    private const long Gb = 1024L * 1024 * 1024;

    public string Id => "disk";
    public string Title => "Free disk space";
    public string Purpose => "Virtual devices take about 8 GB; projects store screenshots and renders.";
    public int Order => 90;

    public Task<CheckResult> RunAsync(SetupContext context, CancellationToken ct)
    {
        // AVDs live under %USERPROFILE%\.android\avd by default, so check that drive too.
        var paths = new[]
        {
            context.Sdk?.Root,
            context.Paths.DataRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        var drives = paths
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.GetPathRoot(Path.GetFullPath(p!))!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(r => new DriveInfo(r))
            .Where(d => d.IsReady)
            .ToList();

        var lines = drives.Select(d => $"{d.Name} {d.AvailableFreeSpace / (double)Gb:0.#} GB free").ToList();
        var min = drives.Count == 0 ? long.MaxValue : drives.Min(d => d.AvailableFreeSpace);
        var details = string.Join("\n", lines);
        return Task.FromResult(min switch
        {
            < 2 * Gb => new CheckResult(CheckStatus.Fail, "Less than 2 GB free.", details, "Free up disk space before creating a virtual device."),
            < 10 * Gb => new CheckResult(CheckStatus.Warning, "Less than 10 GB free.", details, "A virtual device needs about 8 GB. Free up space if creation fails."),
            _ => new CheckResult(CheckStatus.Pass, string.Join(", ", lines), details),
        });
    }
}
