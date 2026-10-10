using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using StoreShot.Core;
using StoreShot.Emulator.Adb;
using StoreShot.Emulator.Devices;

namespace StoreShot.Emulator.Mirror;

/// <summary>Live view of a device screen plus input injection. Coordinates are in frame pixels.</summary>
public interface IScreenMirror : IAsyncDisposable
{
    /// <summary>Short description for the UI, e.g. "H.264 stream" or "Screenshot polling".</summary>
    string Mode { get; }

    FrameBuffer Frames { get; }

    /// <summary>Raised (on a background thread) if the mirror stops unexpectedly.</summary>
    event EventHandler<Exception>? Failed;

    Task StartAsync(CancellationToken ct = default);

    Task TouchAsync(TouchAction action, int x, int y);

    Task KeyAsync(AndroidKey key);

    Task TextAsync(string text);

    Task ScrollAsync(int x, int y, float notches);
}

/// <summary>
/// Streams the screen with the bundled scrcpy server (H.264 over an adb forward), decoded by FFmpeg.
/// Touch/keys go over scrcpy's control socket, which is much faster than `adb shell input`.
/// </summary>
public sealed class ScrcpyMirror : IScreenMirror
{
    private const string RemoteJar = "/data/local/tmp/storeshot-scrcpy-server.jar";

    private readonly AdbClient _adb;
    private readonly string _serial;
    private readonly string _serverJar;
    private readonly string _ffmpegDir;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _controlLock = new(1, 1);
    private LongRunningProcess? _server;
    private TcpClient? _video;
    private TcpClient? _control;
    private Task? _readLoop;
    private int _port;
    private long _packets;
    private long _decodeTicks;
    private volatile int _frameWidth;
    private volatile int _frameHeight;

    public ScrcpyMirror(AdbClient adb, string serial, string serverJar, string ffmpegDir, ILogger log)
    {
        _adb = adb;
        _serial = serial;
        _serverJar = serverJar;
        _ffmpegDir = ffmpegDir;
        _log = log;
    }

    public string Mode => "H.264 stream (scrcpy)";

    /// <summary>scrcpy server log level ("info" normally; "verbose" for diagnosing input issues).</summary>
    public string ServerLogLevel { get; init; } = "info";

    /// <summary>Video packets received from the device (diagnostics: compare with frames decoded).</summary>
    public long PacketsReceived => Interlocked.Read(ref _packets);

    /// <summary>Total time spent decoding + converting, in milliseconds (diagnostics).</summary>
    public double DecodeMilliseconds => Interlocked.Read(ref _decodeTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Recent output of the scrcpy server process, for diagnostics.</summary>
    public string ServerOutput => _server?.RecentOutput ?? string.Empty;

    public FrameBuffer Frames { get; } = new();

    public event EventHandler<Exception>? Failed;

    public static bool IsAvailable(string serverJar, string ffmpegDir) => File.Exists(serverJar) && H264Decoder.IsAvailable(ffmpegDir);

    public async Task StartAsync(CancellationToken ct = default)
    {
        // Load FFmpeg first so a missing/broken decoder fails fast, before touching the device.
        var decoder = new H264Decoder(_ffmpegDir);
        try
        {
            await _adb.PushAsync(_serial, _serverJar, RemoteJar, ct);
            var scid = Random.Shared.Next(1, int.MaxValue);
            _port = FreeTcpPort();
            await _adb.ForwardAsync(_serial, _port, $"localabstract:scrcpy_{scid:x8}", ct);

            var command = $"CLASSPATH={RemoteJar} app_process / com.genymobile.scrcpy.Server {ScrcpyProtocol.ServerVersion} " +
                          string.Join(' ', ScrcpyProtocol.ServerOptions(scid, maxSize: 1280, maxFps: 30, bitRate: 6_000_000, logLevel: ServerLogLevel));
            _server = _adb.StartLongRunning(_serial, ["shell", command]);
            _server.LineReceived += (_, line) => _log.LogDebug("[scrcpy] {Line}", line);

            _video = await ConnectWithDummyByteAsync(ct);
            _control = new TcpClient { NoDelay = true };
            await _control.ConnectAsync(IPAddress.Loopback, _port, ct);

            var stream = _video.GetStream();
            await ReadExactlyAsync(stream, new byte[ScrcpyProtocol.DeviceNameLength], ct);
            var codec = new byte[4];
            await ReadExactlyAsync(stream, codec, ct);
            var codecId = BinaryPrimitives.ReadUInt32BigEndian(codec);
            if (codecId != ScrcpyProtocol.CodecH264)
            {
                throw new UserFacingException("The device couldn't start H.264 screen streaming.", "The preview will use screenshots instead. " + Tail(_server.RecentOutput));
            }

            var owned = decoder;
            decoder = null; // owned by the read loop now (copy first: the lambda must not capture the nulled variable)
            _readLoop = Task.Run(() => ReadLoopAsync(stream, owned, _cts.Token), CancellationToken.None);
        }
        finally
        {
            decoder?.Dispose();
        }
    }

    public async Task TouchAsync(TouchAction action, int x, int y)
    {
        if (_frameWidth == 0)
        {
            return;
        }

        await SendAsync(ScrcpyProtocol.Touch(action, x, y, _frameWidth, _frameHeight, action == TouchAction.Up ? 0f : 1f));
    }

    public async Task KeyAsync(AndroidKey key)
    {
        await SendAsync(ScrcpyProtocol.Key(0, (int)key));
        await SendAsync(ScrcpyProtocol.Key(1, (int)key));
    }

    public Task TextAsync(string text) => SendAsync(ScrcpyProtocol.Text(text));

    public Task ScrollAsync(int x, int y, float notches) =>
        _frameWidth == 0 ? Task.CompletedTask : SendAsync(ScrcpyProtocol.Scroll(x, y, _frameWidth, _frameHeight, 0, notches));

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _video?.Dispose();
        _control?.Dispose();
        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or SocketException)
            {
            }
        }

        _server?.Dispose();
        if (_port != 0)
        {
            await _adb.RemoveForwardAsync(_serial, _port);
        }

        _cts.Dispose();
        _controlLock.Dispose();
    }

    private async Task SendAsync(byte[] message)
    {
        if (_control is not { Connected: true } control)
        {
            return;
        }

        await _controlLock.WaitAsync();
        try
        {
            await control.GetStream().WriteAsync(message);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _log.LogDebug(ex, "Control socket write failed");
        }
        finally
        {
            _controlLock.Release();
        }
    }

    /// <summary>
    /// With an adb forward, connect() succeeds even before the server listens; the server's dummy byte tells us
    /// the connection is real. Retry until it arrives or the server exits.
    /// </summary>
    private async Task<TcpClient> ConnectWithDummyByteAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, _port, ct);
                var one = new byte[1];
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                if (await client.GetStream().ReadAsync(one, readTimeout.Token) == 1)
                {
                    return client;
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
            }

            client.Dispose();
            if (_server is { HasExited: true } || DateTime.UtcNow > deadline)
            {
                throw new UserFacingException("The screen streaming server didn't start on the device.", Tail(_server?.RecentOutput ?? string.Empty));
            }

            await Task.Delay(150, ct);
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, H264Decoder decoder, CancellationToken ct)
    {
        var header = new byte[ScrcpyProtocol.HeaderLength];
        var payload = new byte[1 << 20];
        byte[]? pendingConfig = null;
        try
        {
            using (decoder)
            {
                while (!ct.IsCancellationRequested)
                {
                    await ReadExactlyAsync(stream, header, ct);
                    var h = ScrcpyProtocol.ParseHeader(header);
                    if (h.IsSession)
                    {
                        _frameWidth = h.Width;
                        _frameHeight = h.Height;
                        continue;
                    }

                    if (h.PacketSize <= 0 || h.PacketSize > 64 << 20)
                    {
                        throw new InvalidDataException($"Bad packet size {h.PacketSize}.");
                    }

                    if (payload.Length < h.PacketSize)
                    {
                        payload = new byte[h.PacketSize * 2];
                    }

                    await ReadExactlyAsync(stream, payload.AsMemory(0, h.PacketSize), ct);
                    if (h.IsConfig)
                    {
                        // SPS/PPS: merge into the next frame packet, as the scrcpy client does.
                        pendingConfig = payload.AsSpan(0, h.PacketSize).ToArray();
                        continue;
                    }

                    Interlocked.Increment(ref _packets);
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (pendingConfig is not null)
                    {
                        var merged = new byte[pendingConfig.Length + h.PacketSize];
                        pendingConfig.CopyTo(merged, 0);
                        payload.AsSpan(0, h.PacketSize).CopyTo(merged.AsSpan(pendingConfig.Length));
                        pendingConfig = null;
                        decoder.Decode(merged, h.Pts, Publish);
                    }
                    else
                    {
                        decoder.Decode(payload.AsSpan(0, h.PacketSize), h.Pts, Publish);
                    }

                    Interlocked.Add(ref _decodeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
                }
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
        {
        }
#pragma warning disable CA1031 // Background loop: any failure is reported via Failed so the UI can fall back.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.LogWarning(ex, "scrcpy stream ended");
            Failed?.Invoke(this, ex);
        }
    }

    private void Publish(ReadOnlySpan<byte> bgra, int w, int h)
    {
        // The decoded size is authoritative for input mapping (session packets can lag behind rotation).
        _frameWidth = w;
        _frameHeight = h;
        Frames.Publish(bgra, w, h);
    }

    private static async Task ReadExactlyAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
    {
        await s.ReadExactlyAsync(buffer, ct);
    }

    private static int FreeTcpPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static string Tail(string s) => s.Length <= 600 ? s.Trim() : "…" + s[^600..].Trim();
}

/// <summary>
/// Fallback mirror: polls `screencap` (a few frames per second) and injects input with `adb shell input`.
/// Works everywhere, including when FFmpeg or the scrcpy server is unavailable.
/// </summary>
public sealed class ScreencapMirror : IScreenMirror
{
    private readonly IDevice _device;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private (int X, int Y, DateTime At)? _down;

    public ScreencapMirror(IDevice device, ILogger log)
    {
        _device = device;
        _log = log;
    }

    public string Mode => "Screenshot polling (slower)";

    public FrameBuffer Frames { get; } = new();

    public event EventHandler<Exception>? Failed;

    public Task StartAsync(CancellationToken ct = default)
    {
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task TouchAsync(TouchAction action, int x, int y)
    {
        switch (action)
        {
            case TouchAction.Down:
                _down = (x, y, DateTime.UtcNow);
                break;
            case TouchAction.Up when _down is { } d:
                _down = null;
                var moved = Math.Abs(x - d.X) + Math.Abs(y - d.Y);
                var ms = (int)Math.Clamp((DateTime.UtcNow - d.At).TotalMilliseconds, 100, 2000);
                await Safe(() => moved < 20 ? _device.TapAsync(x, y) : _device.SwipeAsync(d.X, d.Y, x, y, ms));
                break;
        }
    }

    public Task KeyAsync(AndroidKey key) => Safe(() => _device.PressKeyAsync(key));

    public Task TextAsync(string text) => Safe(() => _device.InputTextAsync(text));

    public Task ScrollAsync(int x, int y, float notches) =>
        Safe(() => _device.SwipeAsync(x, y, x, y + (int)(notches * 250), 250));

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var png = await _device.CaptureScreenshotPngAsync(ct);
                PublishPng(png);
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (UserFacingException ex)
            {
                if (++failures >= 5)
                {
                    _log.LogWarning(ex, "Screenshot polling stopped after repeated failures");
                    Failed?.Invoke(this, ex);
                    return;
                }

                await Task.Delay(500, ct);
            }
        }
    }

    private void PublishPng(byte[] png)
    {
        using var decoded = SKBitmap.Decode(png);
        if (decoded is null)
        {
            return;
        }

        using var bgra = decoded.ColorType == SKColorType.Bgra8888 ? decoded.Copy() : decoded.Copy(SKColorType.Bgra8888);
        var bytes = new byte[bgra.ByteCount];
        Marshal.Copy(bgra.GetPixels(), bytes, 0, bytes.Length);
        Frames.Publish(bytes, bgra.Width, bgra.Height);
    }

    private async Task Safe(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (UserFacingException ex)
        {
            _log.LogDebug(ex, "Input failed");
        }
    }
}
