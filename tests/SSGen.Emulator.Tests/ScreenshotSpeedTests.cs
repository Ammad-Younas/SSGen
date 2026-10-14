using System.Diagnostics;
using SSGen.Core.Processes;
using SSGen.Emulator.Adb;
using SSGen.Emulator.Devices;
using SSGen.Emulator.Mirror;
using Xunit.Abstractions;

namespace SSGen.Emulator.Tests;

/// <summary>
/// Against an emulator that is already running (SSGEN_INTEGRATION=1): the gRPC screenshot matches what
/// screencap returns and is faster. Read-only: nothing on the device changes.
/// </summary>
public class ScreenshotSpeedTests
{
    private readonly ITestOutputHelper _out;

    public ScreenshotSpeedTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Emulator_screenshots_come_over_grpc_faster_than_screencap()
    {
        if (Environment.GetEnvironmentVariable("SSGEN_INTEGRATION") != "1")
        {
            return; // opt-in: needs a running emulator
        }

        var sdk = Environment.GetEnvironmentVariable("ANDROID_HOME") ?? @"D:\App_Development_Setup\Android_SDK";
        var adb = new AdbClient(Path.Combine(sdk, "platform-tools", "adb.exe"), new ProcessRunner());
        var serial = (await adb.GetDevicesAsync()).FirstOrDefault(d => d.Serial.StartsWith("emulator-", StringComparison.Ordinal) && d.State == AdbDeviceState.Device)?.Serial;
        if (serial is null)
        {
            _out.WriteLine("No running emulator: nothing to measure.");
            return;
        }

        using var grpc = EmulatorScreenshots.TryCreate(serial);
        Assert.NotNull(grpc);

        var sw = Stopwatch.StartNew();
        var viaAdb = await adb.ExecOutAsync(serial, "screencap -p", TimeSpan.FromSeconds(15));
        var adbMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var viaGrpc = await grpc!.CapturePngAsync();
        var grpcMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var device = await new AdbDevice(adb, serial, "test", DeviceKind.Emulator).CaptureScreenshotPngAsync();
        var deviceMs = sw.ElapsedMilliseconds;
        _out.WriteLine($"screencap {adbMs} ms, gRPC {grpcMs} ms, AdbDevice {deviceMs} ms");

        using var a = SkiaSharp.SKBitmap.Decode(viaAdb);
        using var b = SkiaSharp.SKBitmap.Decode(viaGrpc);
        using var c = SkiaSharp.SKBitmap.Decode(device);
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        Assert.Equal((a.Width, a.Height), (c.Width, c.Height));
        _out.WriteLine($"mean luma: screencap {MeanLuma(a):0.0}, gRPC {MeanLuma(b):0.0}, AdbDevice {MeanLuma(c):0.0}");
        Assert.True(Math.Abs(MeanLuma(a) - MeanLuma(b)) < 12, "gRPC image differs from screencap (black or stale?)");
        Assert.True(grpcMs < adbMs, $"gRPC {grpcMs} ms vs screencap {adbMs} ms");
    }

    private static double MeanLuma(SkiaSharp.SKBitmap bmp)
    {
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
