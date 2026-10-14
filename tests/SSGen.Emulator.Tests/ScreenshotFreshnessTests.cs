using SSGen.Core.Processes;
using SSGen.Emulator.Adb;
using SSGen.Emulator.Mirror;
using Xunit.Abstractions;

namespace SSGen.Emulator.Tests;

/// <summary>
/// Against a running emulator (SSGEN_INTEGRATION=1): after the screen changes, a gRPC screenshot must show the
/// new screen, like screencap does (a stale frame makes every screen look the same to the agent).
/// </summary>
public class ScreenshotFreshnessTests
{
    private readonly ITestOutputHelper _out;

    public ScreenshotFreshnessTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Grpc_screenshot_follows_screen_changes()
    {
        if (Environment.GetEnvironmentVariable("SSGEN_INTEGRATION") != "1")
        {
            return;
        }

        var sdk = Environment.GetEnvironmentVariable("ANDROID_HOME") ?? @"D:\App_Development_Setup\Android_SDK";
        var adb = new AdbClient(Path.Combine(sdk, "platform-tools", "adb.exe"), new ProcessRunner());
        var serial = (await adb.GetDevicesAsync()).FirstOrDefault(d => d.Serial.StartsWith("emulator-", StringComparison.Ordinal) && d.State == AdbDeviceState.Device)?.Serial;
        if (serial is null)
        {
            return;
        }

        // Like the app: the live preview streams frames over gRPC at the same time.
        var endpoint = EmulatorDiscovery.Find(int.Parse(serial["emulator-".Length..], System.Globalization.CultureInfo.InvariantCulture))!;
        var size = await new SSGen.Emulator.Devices.AdbDevice(adb, serial, "t", SSGen.Emulator.Devices.DeviceKind.Emulator).GetScreenSizeAsync();
        await using var mirror = new EmulatorGrpcMirror(endpoint, size.Width, size.Height, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await mirror.StartAsync();
        using var grpc = EmulatorScreenshots.TryCreate(serial)!;
        foreach (var (step, command) in new[] { ("home", "input keyevent KEYCODE_HOME"), ("settings", "am start -a android.settings.SETTINGS"), ("home again", "input keyevent KEYCODE_HOME") })
        {
            await adb.ShellAsync(serial, command, null);
            await Task.Delay(2500);
            var viaAdb = Luma(await adb.ExecOutAsync(serial, "screencap -p", TimeSpan.FromSeconds(15)));
            var viaGrpc = Luma(await grpc.CapturePngAsync());
            _out.WriteLine($"{step}: screencap {viaAdb:0.0}, gRPC {viaGrpc:0.0}");
            Assert.True(Math.Abs(viaAdb - viaGrpc) < 12, $"{step}: gRPC {viaGrpc:0.0} vs screencap {viaAdb:0.0}");
        }
    }

    private static double Luma(byte[] png)
    {
        using var bmp = SkiaSharp.SKBitmap.Decode(png);
        double sum = 0;
        var n = 0;
        for (var y = 0; y < bmp.Height; y += 16)
        {
            for (var x = 0; x < bmp.Width; x += 16)
            {
                var px = bmp.GetPixel(x, y);
                sum += (0.2126 * px.Red) + (0.7152 * px.Green) + (0.0722 * px.Blue);
                n++;
            }
        }

        return sum / n;
    }
}
