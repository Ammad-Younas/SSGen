using System.Globalization;
using Android.Emulation.Control;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using SSGen.Emulator.Devices;

namespace SSGen.Emulator.Mirror;

/// <summary>gRPC endpoint of a running emulator, read from its discovery file.</summary>
public sealed record EmulatorGrpcEndpoint(int Port, string? Token);

/// <summary>
/// Reads the discovery files the emulator writes to %LOCALAPPDATA%\Temp\avd\running\pid_*.ini
/// (keys: port.serial, grpc.port, grpc.token).
/// </summary>
public static class EmulatorDiscovery
{
    public static string RunningDirectory =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Temp", "avd", "running");

    /// <summary>The gRPC endpoint of the emulator whose console port is <paramref name="consolePort"/>, if it has one.</summary>
    public static EmulatorGrpcEndpoint? Find(int consolePort, string? directory = null)
    {
        var dir = directory ?? RunningDirectory;
        if (!Directory.Exists(dir))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "pid_*.ini"))
        {
            Dictionary<string, string> values;
            try
            {
                values = File.ReadAllLines(file)
                    .Select(l => l.Split('=', 2))
                    .Where(p => p.Length == 2)
                    .GroupBy(p => p[0].Trim())
                    .ToDictionary(g => g.Key, g => g.First()[1].Trim());
            }
            catch (IOException)
            {
                continue; // being written
            }

            if (values.GetValueOrDefault("port.serial") == consolePort.ToString(CultureInfo.InvariantCulture) &&
                int.TryParse(values.GetValueOrDefault("grpc.port"), CultureInfo.InvariantCulture, out var grpcPort))
            {
                return new EmulatorGrpcEndpoint(grpcPort, values.GetValueOrDefault("grpc.token"));
            }
        }

        return null;
    }
}

/// <summary>
/// Live preview for emulators through the emulator's own gRPC API (as Android Studio's embedded emulator does):
/// raw frames come from the host-side framebuffer, so nothing is encoded inside the guest. This is much faster
/// than scrcpy on emulators, whose H.264 encoders are software-only.
/// </summary>
public sealed class EmulatorGrpcMirror : IScreenMirror
{
    private const int StreamHeight = 1280;

    private readonly EmulatorController.EmulatorControllerClient _client;
    private readonly GrpcChannel _channel;
    private readonly Metadata _headers = [];
    private readonly int _deviceWidth;
    private readonly int _deviceHeight;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private byte[] _bgra = [];
    private volatile int _frameWidth;
    private volatile int _frameHeight;

    public EmulatorGrpcMirror(EmulatorGrpcEndpoint endpoint, int deviceWidth, int deviceHeight, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{endpoint.Port}", new GrpcChannelOptions { MaxReceiveMessageSize = 64 * 1024 * 1024 });
        _client = new EmulatorController.EmulatorControllerClient(_channel);
        if (!string.IsNullOrEmpty(endpoint.Token))
        {
            _headers.Add("authorization", "Bearer " + endpoint.Token);
        }

        _deviceWidth = deviceWidth;
        _deviceHeight = deviceHeight;
        _log = log;
    }

    public string Mode => "Emulator stream (gRPC)";

    public FrameBuffer Frames { get; } = new();

    public event EventHandler<Exception>? Failed;

    public async Task StartAsync(CancellationToken ct = default)
    {
        // Probe once so auth/connection errors surface here (and the caller can fall back) rather than later.
        await _client.getStatusAsync(new Google.Protobuf.WellKnownTypes.Empty(), _headers, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: ct);
        _loop = Task.Run(() => StreamAsync(_cts.Token), CancellationToken.None);
    }

    public Task TouchAsync(TouchAction action, int x, int y)
    {
        if (_frameWidth == 0)
        {
            return Task.CompletedTask;
        }

        // Frame pixels -> device pixels. Pressure 0 ends the touch.
        var touch = new Touch
        {
            X = x * _deviceWidth / _frameWidth,
            Y = y * _deviceHeight / _frameHeight,
            Identifier = 0,
            Pressure = action == TouchAction.Up ? 0 : 1,
        };
        return Call(() => _client.sendTouchAsync(new TouchEvent { Touches = { touch } }, _headers).ResponseAsync);
    }

    public Task KeyAsync(AndroidKey key) =>
        DomKey(key) is { } name
            ? Call(() => _client.sendKeyAsync(new KeyboardEvent { EventType = KeyboardEvent.Types.KeyEventType.Keypress, Key = name }, _headers).ResponseAsync)
            : Task.CompletedTask;

    public Task TextAsync(string text) =>
        Call(() => _client.sendKeyAsync(new KeyboardEvent { Text = text }, _headers).ResponseAsync);

    public async Task ScrollAsync(int x, int y, float notches)
    {
        // Emulate a wheel notch as a short drag.
        var dy = (int)(notches * -120);
        await TouchAsync(TouchAction.Down, x, y);
        for (var i = 1; i <= 6; i++)
        {
            await TouchAsync(TouchAction.Move, x, y + (dy * i / 6));
            await Task.Delay(10);
        }

        await TouchAsync(TouchAction.Up, x, y + dy);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        _channel.Dispose();
        _cts.Dispose();
    }

    internal static string? DomKey(AndroidKey key) => key switch
    {
        AndroidKey.Back => "GoBack",
        AndroidKey.Home => "GoHome",
        AndroidKey.AppSwitch => "AppSwitch",
        AndroidKey.Enter => "Enter",
        AndroidKey.Delete => "Backspace",
        AndroidKey.Tab => "Tab",
        AndroidKey.Escape => "Escape",
        AndroidKey.DpadUp => "ArrowUp",
        AndroidKey.DpadDown => "ArrowDown",
        AndroidKey.Menu => "ContextMenu",
        _ => null,
    };

    private async Task StreamAsync(CancellationToken ct)
    {
        var width = (uint)Math.Max(1, (long)_deviceWidth * StreamHeight / Math.Max(1, _deviceHeight));
        var format = new ImageFormat { Format = ImageFormat.Types.ImgFormat.Rgba8888, Width = width, Height = StreamHeight };
        try
        {
            using var call = _client.streamScreenshot(format, _headers, cancellationToken: ct);
            await foreach (var image in call.ResponseStream.ReadAllAsync(ct))
            {
                int w = (int)image.Format.Width, h = (int)image.Format.Height;
                var rgba = image.Image_.Span;
                if (w == 0 || h == 0 || rgba.Length < w * h * 4)
                {
                    continue;
                }

                if (_bgra.Length != w * h * 4)
                {
                    _bgra = new byte[w * h * 4];
                }

                for (var i = 0; i < _bgra.Length; i += 4)
                {
                    _bgra[i] = rgba[i + 2];
                    _bgra[i + 1] = rgba[i + 1];
                    _bgra[i + 2] = rgba[i];
                    _bgra[i + 3] = 255;
                }

                _frameWidth = w;
                _frameHeight = h;
                Frames.Publish(_bgra, w, h);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (RpcException ex)
        {
            _log.LogWarning(ex, "Emulator gRPC stream ended");
            Failed?.Invoke(this, ex);
        }
    }

    private async Task Call(Func<Task> rpc)
    {
        try
        {
            await rpc();
        }
        catch (RpcException ex)
        {
            _log.LogDebug(ex, "Emulator gRPC input failed");
        }
    }
}
