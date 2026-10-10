using StoreShot.Core.Processes;
using StoreShot.Core.Settings;
using StoreShot.Core.Setup;

namespace StoreShot.Core.Tests;

public class AndroidSdkTests
{
    [Fact]
    public void Picks_highest_stable_build_tools_that_has_aapt2()
    {
        using var dir = new TempDir();
        dir.Touch("build-tools", "35.0.0", "aapt2.exe");
        dir.Touch("build-tools", "36.1.0", "aapt2.exe");
        dir.Touch("build-tools", "37.0.0-rc1", "aapt2.exe");
        Directory.CreateDirectory(dir.Combine("build-tools", "38.0.0")); // incomplete: no aapt2

        var sdk = new AndroidSdk(dir.Path);

        Assert.Equal(dir.Combine("build-tools", "36.1.0", "aapt2.exe"), sdk.Aapt2Path);
    }

    [Fact]
    public void Lists_only_complete_system_images()
    {
        using var dir = new TempDir();
        dir.Touch("system-images", "android-36", "google_apis_playstore", "x86_64", "system.img");
        Directory.CreateDirectory(dir.Combine("system-images", "android-37.0", "google_apis", "x86_64"));

        var image = Assert.Single(new AndroidSdk(dir.Path).GetSystemImages());

        Assert.Equal("system-images/android-36/google_apis_playstore/x86_64", image.PackageId);
        Assert.Equal(36, image.ApiLevel);
        Assert.True(image.IsPhoneImage);
    }

    [Fact]
    public void Finds_cmdline_tools_latest_then_highest_version()
    {
        using var dir = new TempDir();
        dir.Touch("cmdline-tools", "9.0", "bin", "sdkmanager.bat");
        dir.Touch("cmdline-tools", "12.0", "bin", "sdkmanager.bat");
        var sdk = new AndroidSdk(dir.Path);
        Assert.Equal(dir.Combine("cmdline-tools", "12.0", "bin"), sdk.CmdlineToolsBin);
        Assert.Null(sdk.AndroidCliPath);

        dir.Touch("cmdline-tools", "latest", "bin", "android.exe");
        Assert.Equal(dir.Combine("cmdline-tools", "latest", "bin", "android.exe"), sdk.AndroidCliPath);
    }

    [Theory]
    [InlineData("android-36", 36)]
    [InlineData("android-36.1", 36)]
    [InlineData("android-36-ext18", 36)]
    [InlineData("android-Baklava", 0)]
    public void ParseApiLevel(string platform, int expected) => Assert.Equal(expected, AndroidSdk.ParseApiLevel(platform));

    [Fact]
    public void Locator_respects_priority_and_skips_non_sdk_folders()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("home", "platform-tools"));
        Directory.CreateDirectory(dir.Combine("root", "emulator"));
        Directory.CreateDirectory(dir.Combine("empty"));
        var env = new Dictionary<string, string?>
        {
            ["ANDROID_HOME"] = dir.Combine("empty"),
            ["ANDROID_SDK_ROOT"] = dir.Combine("root"),
        };

        var auto = AndroidSdkLocator.Locate(null, k => env.GetValueOrDefault(k));
        Assert.Equal(dir.Combine("root"), auto.Sdk!.Root);
        Assert.Equal("ANDROID_SDK_ROOT", auto.Source);

        var configured = AndroidSdkLocator.Locate(dir.Combine("home"), k => env.GetValueOrDefault(k));
        Assert.Equal("Settings", configured.Source);

        var none = AndroidSdkLocator.Locate(null, _ => null);
        Assert.Null(none.Sdk);
    }
}

public class SdkPackageListParserTests
{
    // Trimmed from real `android sdk list` output (Android CLI, cmdline-tools 23).
    private const string NewFormat = """
        Installed packages:
          build-tools/36.1.0                                                    36.1.0                                  Android SDK Build-Tools 36.1
          emulator                                                              36.6.11              ->        37.2.12  Android Emulator
          system-images/android-36/google_apis_playstore/x86_64                 7.0.0                                   Google Play Intel x86_64 Atom System Image
        Available packages:
          build-tools/37.0.0                                                    37.0.0                                  Android SDK Build-Tools 37
          build-tools/37.0.0-rc2                                                37.0.0-rc.2                             Android SDK Build-Tools 37-rc2
          system-images/android-36-ext18/google_apis_playstore/x86_64           1.0.0                                   Google Play Intel x86_64 Atom System Image
          system-images/android-36.1/google_apis_playstore/x86_64               4.0.0                                   Google Play Intel x86_64 Atom System Image
          system-images/android-36/google_apis_playstore/x86_64                 7.0.0                                   Google Play Intel x86_64 Atom System Image
          system-images/android-35/google_apis_playstore/x86_64                 2.0.0                                   Google Play Intel x86_64 Atom System Image
          system-images/android-36/google_apis_playstore/arm64-v8a              7.0.0                                   Google Play ARM 64 v8a System Image
        To learn more, see the docs.
          not/a-package                                                         1.0.0                                   Should be ignored (outside a section)
        """;

    // Older sdkmanager format.
    private const string LegacyFormat = """
        Installed packages:
          Path                 | Version | Description                    | Location
          -------              | ------- | -------                        | -------
          platform-tools       | 35.0.1  | Android SDK Platform-Tools     | platform-tools
        Available Packages:
          Path                 | Version | Description
          -------              | ------- | -------
          build-tools;34.0.0   | 34.0.0  | Android SDK Build-Tools 34
        """;

    [Fact]
    public void Parses_current_column_format()
    {
        var list = SdkPackageListParser.Parse(NewFormat);

        Assert.Equal(3, list.Installed.Count);
        var emulator = list.Installed.Single(p => p.Id == "emulator");
        Assert.Equal("36.6.11", emulator.Version);
        Assert.Equal("37.2.12", emulator.UpdateVersion);
        Assert.Equal("Android Emulator", emulator.Description);
        Assert.Equal(7, list.Available.Count);
        Assert.DoesNotContain(list.Available, p => p.Id == "not/a-package");
    }

    [Fact]
    public void Parses_legacy_pipe_format_and_normalises_ids()
    {
        var list = SdkPackageListParser.Parse(LegacyFormat);

        Assert.Equal("platform-tools", Assert.Single(list.Installed).Id);
        Assert.Equal("build-tools/34.0.0", Assert.Single(list.Available).Id);
    }

    [Fact]
    public void Picks_latest_stable_build_tools()
    {
        var list = SdkPackageListParser.Parse(NewFormat);
        Assert.Equal("build-tools/37.0.0", SdkPackageListParser.LatestStableBuildTools(list.Available));
    }

    [Fact]
    public void Recommends_highest_plain_api_play_image_for_abi()
    {
        var list = SdkPackageListParser.Parse(NewFormat);

        Assert.Equal("system-images/android-36/google_apis_playstore/x86_64", SdkPackageListParser.RecommendedSystemImage(list.Available));
        Assert.Equal("system-images/android-36/google_apis_playstore/arm64-v8a", SdkPackageListParser.RecommendedSystemImage(list.Available, "arm64-v8a"));
    }
}

public class ToolOutputParserTests
{
    [Theory]
    [InlineData("java version \"21.0.11\" 2026-04-21 LTS\nJava(TM) SE Runtime Environment", 21)]
    [InlineData("openjdk version \"17.0.2\" 2022-01-18", 17)]
    [InlineData("java version \"1.8.0_301\"\nJava(TM) SE Runtime", 8)]
    [InlineData("openjdk version \"25\" 2025-09-16", 25)]
    public void Java_major_version(string output, int major) =>
        Assert.Equal(major, JavaLocator.ParseMajorVersion(output)!.Value.Major);

    [Fact]
    public void Java_garbage_is_null() => Assert.Null(JavaLocator.ParseMajorVersion("'java' is not recognized"));

    [Fact]
    public void Adb_version() => Assert.Equal("37.0.0-14910828", PlatformToolsCheck.ParseVersion(
        "Android Debug Bridge version 1.0.41\nVersion 37.0.0-14910828\nInstalled as D:\\sdk\\platform-tools\\adb.exe"));

    [Fact]
    public void Emulator_version() => Assert.Equal("36.6.11.0", EmulatorCheck.ParseVersion(
        "INFO    | Android emulator version 36.6.11.0 (build_id 15507667) (CL:N/A)"));

    [Fact]
    public void Accel_check_ok()
    {
        var (ok, msg) = AccelerationCheck.ParseAccelCheck(0, "accel:\n0\nWHPX(10.0.26300) is installed and usable.\naccel");
        Assert.True(ok);
        Assert.Equal("WHPX(10.0.26300) is installed and usable.", msg);
    }

    [Fact]
    public void Accel_check_failure_keeps_message()
    {
        var (ok, msg) = AccelerationCheck.ParseAccelCheck(1, "accel:\n1\nWHPX is not installed or not enabled.\naccel");
        Assert.False(ok);
        Assert.Contains("WHPX", msg, StringComparison.Ordinal);
    }
}

public class SetupServiceTests
{
    private sealed class ThrowingCheck : ISetupCheck
    {
        public string Id => "boom";
        public string Title => "Boom";
        public string Purpose => "Throws";
        public int Order => 1;

        public Task<CheckResult> RunAsync(SetupContext context, CancellationToken ct) => throw new InvalidOperationException("kaput");
    }

    private sealed class UserFacingCheck : ISetupCheck
    {
        public string Id => "uf";
        public string Title => "UF";
        public string Purpose => "Throws user-facing";
        public int Order => 0;

        public Task<CheckResult> RunAsync(SetupContext context, CancellationToken ct) => throw new UserFacingException("Readable", "Do this");
    }

    [Fact]
    public async Task Exceptions_become_fail_results_and_checks_are_ordered()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(TestDb.CreateApp(dir));
        var service = new SetupService([new ThrowingCheck(), new UserFacingCheck()], settings, new ProcessRunner(), new AppPaths(dir.Combine("data")));
        var ctx = new SetupContext(settings.Current, new SdkLocation(null, null, []), null, new ProcessRunner(), new AppPaths(dir.Combine("data")));

        Assert.Equal(["uf", "boom"], service.Checks.Select(c => c.Id));

        var uf = await service.RunCheckAsync(service.Checks[0], ctx, CancellationToken.None);
        Assert.Equal(CheckStatus.Fail, uf.Status);
        Assert.Equal("Do this", uf.FixInstructions);

        var boom = await service.RunCheckAsync(service.Checks[1], ctx, CancellationToken.None);
        Assert.Equal(CheckStatus.Fail, boom.Status);
        Assert.Contains("kaput", boom.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sdk_dependent_checks_fail_cleanly_without_sdk()
    {
        using var dir = new TempDir();
        var ctx = new SetupContext(new AppSettings(), new SdkLocation(null, null, ["x"]), null, new ProcessRunner(), new AppPaths(dir.Path));
        ISetupCheck[] checks = [new AndroidSdkCheck(), new PlatformToolsCheck(), new EmulatorCheck(), new BuildToolsCheck(), new SystemImageCheck(), new AccelerationCheck()];

        foreach (var check in checks)
        {
            var result = await check.RunAsync(ctx, CancellationToken.None);
            Assert.Equal(CheckStatus.Fail, result.Status);
        }
    }
}

public class ProcessRunnerTests
{
    [Fact]
    public async Task Captures_output_and_exit_code()
    {
        var lines = new List<string>();
        var r = await new ProcessRunner().RunAsync(new ProcessSpec("cmd.exe", ["/c", "echo hello& echo oops 1>&2& exit /b 3"]) { OnLine = lines.Add });

        Assert.Equal(3, r.ExitCode);
        Assert.Equal("hello", r.StdOut);
        Assert.Equal("oops", r.StdErr.Trim());
        Assert.Contains("hello", lines);
    }

    [Fact]
    public async Task Times_out_and_kills()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await new ProcessRunner().RunAsync(new ProcessSpec("cmd.exe", ["/c", "ping -n 30 127.0.0.1 >nul"]) { Timeout = TimeSpan.FromMilliseconds(500) });

        Assert.True(r.TimedOut);
        Assert.False(r.Succeeded);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancellation_throws()
    {
        using var cts = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessRunner().RunAsync(new ProcessSpec("cmd.exe", ["/c", "ping -n 30 127.0.0.1 >nul"]), cts.Token));
    }

    [Fact]
    public async Task Missing_executable_is_user_facing() =>
        await Assert.ThrowsAsync<UserFacingException>(() =>
            new ProcessRunner().RunAsync(new ProcessSpec(@"C:\definitely\not\here.exe", [])));

    [Fact]
    public async Task Writes_stdin()
    {
        var r = await new ProcessRunner().RunAsync(new ProcessSpec("cmd.exe", ["/c", "set /p X=& echo got %X%"]) { StandardInput = "y\n" });
        Assert.Contains("got", r.StdOut, StringComparison.Ordinal);
    }
}
