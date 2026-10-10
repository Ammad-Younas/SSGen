using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SSGen.Core.Data;
using SSGen.Core.Processes;
using SSGen.Core.Settings;
using SSGen.Emulator.Avd;
using SSGen.Emulator.Devices;
using SSGen.Emulator.Logcat;
using SSGen.Emulator.Mirror;
using Xunit.Abstractions;

namespace SSGen.Emulator.Tests;

/// <summary>
/// End-to-end against a real emulator: create/boot the dedicated AVD, install + launch the sample APK, stream with
/// scrcpy, inject input, read logcat, shut down. Run with SSGEN_INTEGRATION=1 (takes a few minutes).
/// </summary>
public class EmulatorIntegrationTests
{
    private const string NewAvdName = "SSGen_Pixel_API36";
    private readonly ITestOutputHelper _out;

    public EmulatorIntegrationTests(ITestOutputHelper output) => _out = output;

    private static async Task WaitForFrameAsync(IScreenMirror mirror)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (mirror.Frames.Version == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(mirror.Frames.Version > 0, $"no frames from {mirror.Mode}");
    }

    [IntegrationFact]
    public async Task Full_device_pipeline()
    {
        using var dir = new TempDir();
        var db = new SqliteDatabase(dir.Combine("app.db"), MigrationSet.App);
        db.Migrate();
        await using var hub = new DeviceHub(new SettingsService(db), new ProcessRunner(), NullLoggerFactory.Instance);
        var sdk = hub.RequireSdk();
        var sw = Stopwatch.StartNew();

        // Reuse any SSGen-managed 1080x2400 API 36 phone (e.g. the owner's StoreShot_Pixel_API36); create one if none.
        var avd = hub.Avds.List().FirstOrDefault(a => a.IsManagedBySSGen && a.ApiLevel == 36 && a.Width == 1080 && a.Height == 2400);
        if (avd is null)
        {
            var image = sdk.GetSystemImages().First(i => i.Platform == "android-36" && i.Tag == "google_apis_playstore" && i.Abi == "x86_64");
            avd = hub.Avds.Create(NewAvdName, DeviceProfile.PixelPhone, image, sdk.Root);
            _out.WriteLine($"Created {NewAvdName}");
        }

        var target = (await hub.ListTargetsAsync()).Single(t => t.Avd?.Name == avd.Name);
        var provider = hub.ProviderFor(target);
        var progress = new Progress<string>(m => _out.WriteLine($"[{sw.Elapsed:mm\\:ss}] {m}"));
        var device = await provider.ConnectAsync(target, new DeviceStartOptions { Headless = true, Gpu = Environment.GetEnvironmentVariable("SSGEN_EMU_GPU") ?? "host" }, progress);
        _out.WriteLine($"Booted {device.Serial} in {sw.Elapsed}");

        try
        {
            Assert.Equal("x86_64", await device.GetAbiAsync());
            Assert.Equal(36, await device.GetApiLevelAsync());
            Assert.Equal((1080, 2400), await device.GetScreenSizeAsync());

            var inspection = await hub.Apks.InspectAsync(Repo.SampleApk()!, sdk.Aapt2Path);
            await device.InstallAsync([Repo.SampleApk()!]);
            Assert.True(await device.IsInstalledAsync(inspection.Info.PackageName));

            using var logcat = LogcatSession.Start(hub.Adb, device.Serial, tail: 50);
            var logLines = 0;
            logcat.EntriesReceived += (_, batch) => Interlocked.Add(ref logLines, batch.Count);

            // Reset app data so the first-run tutorial always shows (the input check below relies on it).
            await hub.Adb.ShellAsync(device.Serial, "pm clear " + inspection.Info.PackageName);
            await device.LaunchAsync(inspection.Info.PackageName, inspection.Info.LauncherActivity);
            await Task.Delay(3000);
            var activity = await device.GetForegroundActivityAsync();
            _out.WriteLine($"Foreground: {activity}");
            Assert.StartsWith(inspection.Info.PackageName, activity, StringComparison.Ordinal);
            Assert.True(await device.GetPidAsync(inspection.Info.PackageName) > 0);

            var ui = await device.DumpUiHierarchyAsync();
            Assert.Contains("<hierarchy", ui, StringComparison.Ordinal);

            var png = await device.CaptureScreenshotPngAsync();
            using (var shot = SkiaSharp.SKBitmap.Decode(png))
            {
                Assert.Equal((1080, 2400), (shot.Width, shot.Height));
            }

            var (devW, devH) = await device.GetScreenSizeAsync();

            // 1) scrcpy path (used for USB phones): frames decode and a tap through its control socket works.
            await using (var scrcpy = new ScrcpyMirror(hub.Adb, device.Serial, hub.ScrcpyServerPath, hub.FfmpegDirectory, NullLogger.Instance))
            {
                await scrcpy.StartAsync();
                await WaitForFrameAsync(scrcpy);
                int sw2 = scrcpy.Frames.Width, sh2 = scrcpy.Frames.Height;
                _out.WriteLine($"scrcpy frame size {sw2}x{sh2}");

                var button = System.Text.RegularExpressions.Regex.Match(ui,
                    "text=\"(Skip|SKIP|Next|NEXT|Weiter|Überspringen)\"[^>]*bounds=\"\\[(\\d+),(\\d+)\\]\\[(\\d+),(\\d+)\\]\"");
                Assert.True(button.Success, "no Skip/Next button in tutorial UI dump");
                int Num(int g) => int.Parse(button.Groups[g].Value, System.Globalization.CultureInfo.InvariantCulture);
                var (cx, cy) = ((Num(2) + Num(4)) / 2, (Num(3) + Num(5)) / 2);
                var (fx, fy) = (cx * sw2 / devW, cy * sh2 / devH);
                await scrcpy.TouchAsync(TouchAction.Down, fx, fy);
                await scrcpy.TouchAsync(TouchAction.Up, fx, fy);
                await Task.Delay(1500);
                var uiAfter = await device.DumpUiHierarchyAsync();
                _out.WriteLine($"scrcpy tap '{button.Groups[1].Value}' at device ({cx},{cy}) = frame ({fx},{fy}); UI changed: {uiAfter != ui}");
                Assert.NotEqual(ui, uiAfter);
            }

            // 2) The hub's choice for emulators: the gRPC stream. Must sustain >= 15 fps while the screen moves.
            await using var mirror = await hub.StartMirrorAsync(device);
            _out.WriteLine($"Mirror: {mirror.Mode}");
            Assert.IsType<EmulatorGrpcMirror>(mirror);
            await WaitForFrameAsync(mirror);
            int w = mirror.Frames.Width, h = mirror.Frames.Height;
            _out.WriteLine($"gRPC frame size {w}x{h}");

            await hub.Adb.ShellAsync(device.Serial, "am start -W -a android.settings.SETTINGS");
            await Task.Delay(1500);
            var startVersion = mirror.Frames.Version;
            var motion = Stopwatch.StartNew();
            var down = true;
            while (motion.Elapsed < TimeSpan.FromSeconds(5))
            {
                var (y0, y1) = down ? (h * 3 / 4, h / 4) : (h / 4, h * 3 / 4);
                await mirror.TouchAsync(TouchAction.Down, w / 2, y0);
                for (var i = 1; i <= 30; i++)
                {
                    await mirror.TouchAsync(TouchAction.Move, w / 2, y0 + ((y1 - y0) * i / 30));
                    await Task.Delay(16);
                }

                await mirror.TouchAsync(TouchAction.Up, w / 2, y1);
                down = !down;
            }

            var frames = mirror.Frames.Version - startVersion;
            var avgFps = frames / motion.Elapsed.TotalSeconds;
            _out.WriteLine($"During motion: {frames} frames in {motion.Elapsed.TotalSeconds:0.0}s = {avgFps:0.0} fps avg");
            Assert.True(avgFps >= 15, $"average {avgFps:0.0} fps < 15");

            // gRPC key input: Home leaves Settings.
            await mirror.KeyAsync(AndroidKey.Home);
            await Task.Delay(1500);
            var afterHome = await device.GetForegroundActivityAsync();
            _out.WriteLine($"After gRPC Home: {afterHome}");
            Assert.DoesNotContain("settings", afterHome ?? "", StringComparison.OrdinalIgnoreCase);

            await Task.Delay(500);
            _out.WriteLine($"Logcat lines: {logLines}");
            Assert.True(logLines > 0);
        }
        finally
        {
            await provider.StopAsync(target with { RunningSerial = device.Serial });
            var stillThere = (await hub.Adb.GetDevicesAsync()).Any(d => d.Serial == device.Serial);
            _out.WriteLine($"Stopped (still listed: {stillThere}) after {sw.Elapsed}");
        }
    }
}
