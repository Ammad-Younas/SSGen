using StoreShot.Core;
using StoreShot.Emulator.Adb;
using StoreShot.Emulator.Apk;
using StoreShot.Emulator.Devices;
using StoreShot.Emulator.Logcat;
using StoreShot.Emulator.Mirror;

namespace StoreShot.Emulator.Tests;

public class ScrcpyProtocolTests
{
    [Fact]
    public void Touch_matches_v5_wire_layout()
    {
        var b = ScrcpyProtocol.Touch(TouchAction.Down, 100, 200, 1080, 2400);

        Assert.Equal(32, b.Length);
        Assert.Equal(2, b[0]);                                           // type
        Assert.Equal(0, b[1]);                                           // ACTION_DOWN
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE }, b[2..10]); // pointer id -2
        Assert.Equal(new byte[] { 0, 0, 0, 100 }, b[10..14]);          // x
        Assert.Equal(new byte[] { 0, 0, 0, 200 }, b[14..18]);          // y
        Assert.Equal(new byte[] { 0x04, 0x38, 0x09, 0x60 }, b[18..22]); // 1080, 2400
        Assert.Equal(new byte[] { 0xFF, 0xFF }, b[22..24]);            // pressure 1.0
    }

    [Fact]
    public void Key_and_back_messages()
    {
        var k = ScrcpyProtocol.Key(1, (int)AndroidKey.Home);
        Assert.Equal(new byte[] { 0, 1, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0 }, k);
        Assert.Equal(new byte[] { 4, 0 }, ScrcpyProtocol.BackOrScreenOn(0));
    }

    [Fact]
    public void Text_is_length_prefixed_and_truncated_on_char_boundary()
    {
        var t = ScrcpyProtocol.Text("hi");
        Assert.Equal(new byte[] { 1, 0, 0, 0, 2, (byte)'h', (byte)'i' }, t);

        var longText = new string('é', 200); // 400 UTF-8 bytes
        var truncated = ScrcpyProtocol.Text(longText);
        var len = (truncated[1] << 24) | (truncated[2] << 16) | (truncated[3] << 8) | truncated[4];
        Assert.Equal(300, len);
        Assert.Equal(new string('é', 150), System.Text.Encoding.UTF8.GetString(truncated, 5, len));
    }

    [Fact]
    public void Scroll_encodes_fixed_point()
    {
        var s = ScrcpyProtocol.Scroll(10, 20, 100, 200, 0, 16);
        Assert.Equal(21, s.Length);
        Assert.Equal(3, s[0]);
        Assert.Equal(new byte[] { 0x7F, 0xFF }, s[15..17]); // vertical = +16 notches -> 1.0
    }

    [Fact]
    public void Parses_session_and_media_headers()
    {
        byte[] session = [0x80, 0, 0, 0, 0, 0, 0x02, 0xD0, 0, 0, 0x05, 0x00];
        var h1 = ScrcpyProtocol.ParseHeader(session);
        Assert.True(h1.IsSession);
        Assert.Equal((720, 1280), (h1.Width, h1.Height));

        byte[] config = [0x40, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 25];
        var h2 = ScrcpyProtocol.ParseHeader(config);
        Assert.False(h2.IsSession);
        Assert.True(h2.IsConfig);
        Assert.Equal(25, h2.PacketSize);

        byte[] key = [0x20, 0, 0, 0, 0, 0, 0x03, 0xE8, 0, 0, 0x10, 0];
        var h3 = ScrcpyProtocol.ParseHeader(key);
        Assert.True(h3.IsKeyFrame);
        Assert.Equal(1000, h3.Pts);
        Assert.Equal(4096, h3.PacketSize);
    }

    [Fact]
    public void Server_options_use_hex_scid()
    {
        Assert.Contains("scid=0000beef", ScrcpyProtocol.ServerOptions(0xBEEF, 1280, 30, 1));
    }
}

public class AdbParserTests
{
    [Fact]
    public void Parses_devices_list()
    {
        var list = AdbOutputParser.ParseDevices("""
            List of devices attached
            * daemon started successfully
            emulator-5554          device product:sdk_gphone64_x86_64 model:sdk_gphone64_x86_64 device:emu64xa transport_id:1
            R58M123ABC             unauthorized usb:1-1 transport_id:2

            """);

        Assert.Equal(2, list.Count);
        Assert.True(list[0].IsEmulator);
        Assert.Equal(5554, list[0].EmulatorPort);
        Assert.Equal("sdk_gphone64_x86_64", list[0].Model);
        Assert.Equal(AdbDeviceState.Unauthorized, list[1].State);
        Assert.Null(list[1].EmulatorPort);
    }

    [Theory]
    [InlineData("Failure [INSTALL_FAILED_NO_MATCHING_ABIS: Failed to extract native libraries, res=-113]", "CPU type")]
    [InlineData("adb: failed to install x.apk: Failure [INSTALL_FAILED_OLDER_SDK: Requires newer sdk version #34]", "newer Android")]
    [InlineData("Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE: Existing package signatures do not match]", "signed with another key")]
    [InlineData("something odd", "couldn't be installed")]
    public void Install_errors_are_readable(string output, string expected)
    {
        var ex = AdbOutputParser.InstallError(output);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(ex.SuggestedFix));
    }

    [Theory]
    [InlineData("hello world", "'hello%sworld'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("a&b;c", "'a&b;c'")]
    public void Escapes_input_text(string text, string expected) => Assert.Equal(expected, AdbDevice.EscapeInputText(text));

    [Fact]
    public void Parses_resumed_activity()
    {
        const string dumpsys = "  topResumedActivity=ActivityRecord{1a2b3c u0 org.secuso.privacyfriendlynotes/.ui.main.MainActivity t12}";
        Assert.Equal("org.secuso.privacyfriendlynotes/.ui.main.MainActivity", AdbDevice.ParseResumedActivity(dumpsys));
    }
}

public class LogcatParserTests
{
    [Fact]
    public void Parses_threadtime()
    {
        var e = LogcatParser.Parse("10-10 14:44:01.418  1234  1250 E AndroidRuntime: FATAL EXCEPTION: main")!;
        Assert.Equal("14:44:01.418", e.Time);
        Assert.Equal(1234, e.Pid);
        Assert.Equal(1250, e.Tid);
        Assert.Equal(StoreShot.Emulator.Logcat.LogLevel.Error, e.Level);
        Assert.Equal("AndroidRuntime", e.Tag);
        Assert.Equal("FATAL EXCEPTION: main", e.Message);
    }

    [Fact]
    public void Ignores_separators() => Assert.Null(LogcatParser.Parse("--------- beginning of main"));
}

public class AaptParserTests
{
    private const string Badging = """
        package: name='org.secuso.privacyfriendlynotes' versionCode='105' versionName='2.2.2' platformBuildVersionName='14'
        minSdkVersion:'21'
        targetSdkVersion:'34'
        uses-permission: name='android.permission.CAMERA'
        uses-permission: name='android.permission.CAMERA'
        application-label:'Notes'
        application-icon-160:'res/a.png'
        application-icon-480:'res/BW.xml'
        application-icon-65534:'res/any.xml'
        application: label='Notes' icon='res/BW.xml'
        launchable-activity: name='org.secuso.privacyfriendlynotes.ui.SplashActivity'  label='' icon=''
        native-code: 'arm64-v8a' 'x86_64'
        """;

    [Fact]
    public void Parses_badging()
    {
        var b = AaptBadgingParser.Parse(Badging);
        Assert.Equal("org.secuso.privacyfriendlynotes", b.Info.PackageName);
        Assert.Equal(105, b.Info.VersionCode);
        Assert.Equal("2.2.2", b.Info.VersionName);
        Assert.Equal(21, b.Info.MinSdk);
        Assert.Equal(34, b.Info.TargetSdk);
        Assert.Equal("Notes", b.Info.Label);
        Assert.Equal("org.secuso.privacyfriendlynotes.ui.SplashActivity", b.Info.LauncherActivity);
        Assert.Equal(["android.permission.CAMERA"], b.Info.Permissions);
        Assert.Equal(["arm64-v8a", "x86_64"], b.Info.NativeAbis);
        Assert.Equal("res/BW.xml", b.IconPath); // highest real density wins over anydpi
    }

    [Fact]
    public void Missing_package_line_throws() => Assert.Throws<FormatException>(() => AaptBadgingParser.Parse("garbage"));

    [Fact]
    public void Parses_resource_dump_and_adaptive_icon()
    {
        var res = AaptResourceDumpParser.Parse("""
            Package name=x id=7f
              type color id=06 entryCount=1
                resource 0x7f060076 color/ic_launcher_background
                  () #ff3680bb
              type mipmap id=0f entryCount=2
                resource 0x7f0f0001 mipmap/ic_launcher
                  (mdpi) (file) res/9w.png type=PNG
                  (xxxhdpi) (file) res/o-.png type=PNG
                  (anydpi-v26) (file) res/BW.xml type=XML
            """);
        Assert.Equal("#ff3680bb", res[0x7f060076].Values.Single().Literal);
        var icon = res[0x7f0f0001];
        Assert.Equal(3, icon.Values.Count);
        Assert.Equal("res/o-.png", icon.Values.OrderByDescending(v => AaptResourceDumpParser.DensityRank(v.Config)).First().FilePath);

        var (bg, fg) = AdaptiveIconParser.Parse("""
            N: android=http://schemas.android.com/apk/res/android (line=2)
              E: adaptive-icon (line=2)
                  E: background (line=3)
                    A: http://schemas.android.com/apk/res/android:drawable(0x01010199)=@0x7f060076
                  E: foreground (line=4)
                    A: http://schemas.android.com/apk/res/android:drawable(0x01010199)=@0x7f0f0002
            """);
        Assert.Equal(0x7f060076u, bg);
        Assert.Equal(0x7f0f0002u, fg);
    }

    [Fact]
    public void Abi_compatibility()
    {
        var info = new ApkInfo { PackageName = "x", ParsedBy = "t", NativeAbis = ["armeabi-v7a", "arm64-v8a"] };
        Assert.False(info.SupportsAbi("x86_64"));
        Assert.True(info.SupportsAbi("arm64-v8a"));
        Assert.True((info with { NativeAbis = [] }).SupportsAbi("x86_64"));
        Assert.True((info with { NativeAbis = ["x86"] }).SupportsAbi("x86_64"));
    }
}

public class ApkInspectorTests
{
    [SampleApkFact]
    public async Task Aapt2_and_managed_parsers_agree_on_sample_apk()
    {
        var apk = Repo.SampleApk()!;
        var inspector = new ApkInspector(new StoreShot.Core.Processes.ProcessRunner());

        var managed = await inspector.InspectAsync(apk, aapt2Path: null);
        Assert.Equal("org.secuso.privacyfriendlynotes", managed.Info.PackageName);
        Assert.Equal(105, managed.Info.VersionCode);
        Assert.Equal("2.2.2", managed.Info.VersionName);
        Assert.Equal(21, managed.Info.MinSdk);
        Assert.Equal(34, managed.Info.TargetSdk);
        Assert.Equal("org.secuso.privacyfriendlynotes.ui.SplashActivity", managed.Info.LauncherActivity);
        Assert.Contains("android.permission.CAMERA", managed.Info.Permissions);
        Assert.Equal("managed", managed.Info.ParsedBy);

        if (Repo.Aapt2() is not { } aapt2)
        {
            return;
        }

        var full = await inspector.InspectAsync(apk, aapt2);
        Assert.Equal("aapt2", full.Info.ParsedBy);
        Assert.Equal("Notes", full.Info.Label);
        Assert.Equal(managed.Info.LauncherActivity, full.Info.LauncherActivity);
        Assert.Equal(managed.Info.Permissions.Order(), full.Info.Permissions.Order());
        Assert.NotNull(full.IconPng);
        using var icon = SkiaSharp.SKBitmap.Decode(full.IconPng);
        Assert.True(icon.Width >= 96);
    }

    [Fact]
    public async Task Rejects_non_apk_and_aab()
    {
        using var dir = new TempDir();
        var inspector = new ApkInspector(new StoreShot.Core.Processes.ProcessRunner());
        var text = dir.Combine("x.apk");
        await File.WriteAllTextAsync(text, "not a zip");
        await Assert.ThrowsAsync<UserFacingException>(() => inspector.InspectAsync(text, null));

        var aab = dir.Combine("app.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(aab, System.IO.Compression.ZipArchiveMode.Create))
        {
            zip.CreateEntry("base/manifest/AndroidManifest.xml");
        }

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => inspector.InspectAsync(aab, null));
        Assert.Contains("App Bundles", ex.Message, StringComparison.Ordinal);
    }
}
