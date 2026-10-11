using Grpc.Core;
using Grpc.Net.Client;
using Android.Emulation.Control;

namespace SSGen.Emulator.Mirror;

/// <summary>
/// Full-resolution PNG screenshots of an emulator through its gRPC controller. The host encodes the frame, so
/// nothing runs inside the guest: much faster than "adb screencap -p" (about 0.4 to 0.7 s per capture).
/// </summary>
public sealed class EmulatorScreenshots : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly EmulatorController.EmulatorControllerClient _client;
    private readonly Metadata _headers = [];

    private EmulatorScreenshots(EmulatorGrpcEndpoint endpoint)
    {
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{endpoint.Port}", new GrpcChannelOptions { MaxReceiveMessageSize = 64 * 1024 * 1024 });
        _client = new EmulatorController.EmulatorControllerClient(_channel);
        if (!string.IsNullOrEmpty(endpoint.Token))
        {
            _headers.Add("authorization", "Bearer " + endpoint.Token);
        }
    }

    /// <summary>For "emulator-5554": the running emulator's gRPC endpoint, or null (not an emulator, no gRPC).</summary>
    public static EmulatorScreenshots? TryCreate(string serial)
    {
        if (serial is null || !serial.StartsWith("emulator-", StringComparison.Ordinal) ||
            !int.TryParse(serial["emulator-".Length..], System.Globalization.CultureInfo.InvariantCulture, out var consolePort))
        {
            return null;
        }

        return EmulatorDiscovery.Find(consolePort) is { } endpoint ? new EmulatorScreenshots(endpoint) : null;
    }

    public async Task<byte[]> CapturePngAsync(CancellationToken ct = default)
    {
        var image = await _client.getScreenshotAsync(new ImageFormat { Format = ImageFormat.Types.ImgFormat.Png }, _headers,
            deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: ct);
        var bytes = image.Image_.ToByteArray();
        return bytes.Length > 8 ? bytes : throw new InvalidOperationException("The emulator returned an empty screenshot (display off?).");
    }

    public void Dispose() => _channel.Dispose();
}
