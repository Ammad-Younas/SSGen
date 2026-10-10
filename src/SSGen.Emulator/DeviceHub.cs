using Microsoft.Extensions.Logging;
using SSGen.Core;
using SSGen.Core.Processes;
using SSGen.Core.Settings;
using SSGen.Core.Setup;
using SSGen.Emulator.Adb;
using SSGen.Emulator.Apk;
using SSGen.Emulator.Avd;
using SSGen.Emulator.Devices;
using SSGen.Emulator.Mirror;

namespace SSGen.Emulator;

/// <summary>
/// Entry point to everything device-related. Resolves the Android SDK from current settings on demand, so a
/// changed SDK path in Settings takes effect without restarting.
/// </summary>
public sealed class DeviceHub : IAsyncDisposable
{
    private readonly ISettingsService _settings;
    private readonly IProcessRunner _runner;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private string? _sdkRoot;
    private LocalAvdProvider? _avdProvider;
    private AdbClient? _adb;

    public DeviceHub(ISettingsService settings, IProcessRunner runner, ILoggerFactory loggers, string? toolsDirectory = null)
    {
        _settings = settings;
        _runner = runner;
        _loggers = loggers;
        _log = loggers.CreateLogger<DeviceHub>();
        ToolsDirectory = toolsDirectory ?? Path.Combine(AppContext.BaseDirectory, "tools");
        Apks = new ApkInspector(runner);
        Avds = new AvdManager();
    }

    public string ToolsDirectory { get; }
    public string ScrcpyServerPath => Path.Combine(ToolsDirectory, "scrcpy", "scrcpy-server-v" + ScrcpyProtocol.ServerVersion);
    public string FfmpegDirectory => Path.Combine(ToolsDirectory, "ffmpeg");

    public ApkInspector Apks { get; }
    public AvdManager Avds { get; }

    /// <summary>Set to include the in-memory mock device (tests, or SSGEN_MOCK_DEVICE=1).</summary>
    public MockEmulatorProvider? MockProvider { get; set; } =
        Environment.GetEnvironmentVariable("SSGEN_MOCK_DEVICE") == "1" ? new MockEmulatorProvider() : null;

    /// <summary>The located SDK, or a user-facing error explaining how to fix it.</summary>
    public AndroidSdk RequireSdk()
    {
        var sdk = AndroidSdkLocator.Locate(_settings.Current.AndroidSdkPath).Sdk;
        if (sdk is null || !sdk.HasPlatformTools)
        {
            throw new UserFacingException("The Android SDK (with platform-tools) wasn't found.", "Open the Environment page to locate or install it.");
        }

        return sdk;
    }

    public AdbClient Adb
    {
        get
        {
            Refresh();
            return _adb!;
        }
    }

    public IReadOnlyList<IEmulatorProvider> Providers
    {
        get
        {
            Refresh();
            var list = new List<IEmulatorProvider>();
            if (_avdProvider is not null)
            {
                list.Add(_avdProvider);
            }

            list.Add(new ConnectedPhoneProvider(_adb!));
            if (MockProvider is not null)
            {
                list.Add(MockProvider);
            }

            return list;
        }
    }

    public IEmulatorProvider ProviderFor(DeviceTarget target) =>
        Providers.FirstOrDefault(p => p.Kind == (target ?? throw new ArgumentNullException(nameof(target))).Kind)
        ?? throw new UserFacingException("That device type isn't available.", "Check the Environment page.");

    public async Task<IReadOnlyList<DeviceTarget>> ListTargetsAsync(CancellationToken ct = default)
    {
        var all = new List<DeviceTarget>();
        foreach (var provider in Providers)
        {
            all.AddRange(await provider.ListTargetsAsync(ct));
        }

        return all;
    }

    /// <summary>
    /// Starts the fastest available mirror: the emulator's gRPC stream (emulators), scrcpy + FFmpeg (phones, or
    /// emulators without gRPC), and finally screenshot polling.
    /// </summary>
    public async Task<IScreenMirror> StartMirrorAsync(IDevice device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Kind == DeviceKind.Emulator && TryConsolePort(device.Serial) is { } consolePort && EmulatorDiscovery.Find(consolePort) is { } endpoint)
        {
            var (w, h) = await device.GetScreenSizeAsync(ct);
            var grpc = new EmulatorGrpcMirror(endpoint, w, h, _loggers.CreateLogger<EmulatorGrpcMirror>());
            try
            {
                await grpc.StartAsync(ct);
                return grpc;
            }
            catch (Exception ex) when (ex is Grpc.Core.RpcException or IOException or InvalidOperationException)
            {
                _log.LogWarning(ex, "Emulator gRPC mirror failed; trying scrcpy");
                await grpc.DisposeAsync();
            }
        }

        if (device is AdbDevice adbDevice && ScrcpyMirror.IsAvailable(ScrcpyServerPath, FfmpegDirectory))
        {
            var scrcpy = new ScrcpyMirror(adbDevice.Adb, device.Serial, ScrcpyServerPath, FfmpegDirectory, _loggers.CreateLogger<ScrcpyMirror>())
            {
                // SSGEN_SCRCPY_LOG=verbose logs ignored input events etc. (see ScrcpyMirror.ServerOutput).
                ServerLogLevel = Environment.GetEnvironmentVariable("SSGEN_SCRCPY_LOG") ?? "info",
            };
            try
            {
                await scrcpy.StartAsync(ct);
                return scrcpy;
            }
            catch (Exception ex) when (ex is UserFacingException or IOException or System.Net.Sockets.SocketException or InvalidOperationException or DllNotFoundException or NotSupportedException)
            {
                _log.LogWarning(ex, "scrcpy mirror failed; falling back to screenshot polling");
                await scrcpy.DisposeAsync();
            }
        }
        else if (device is AdbDevice)
        {
            _log.LogWarning("scrcpy server or FFmpeg missing in {Dir}; using screenshot polling", ToolsDirectory);
        }

        var polling = new ScreencapMirror(device, _loggers.CreateLogger<ScreencapMirror>());
        await polling.StartAsync(ct);
        return polling;
    }

    private static int? TryConsolePort(string serial) =>
        serial.StartsWith("emulator-", StringComparison.Ordinal) &&
        int.TryParse(serial["emulator-".Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null;

    public async ValueTask DisposeAsync()
    {
        LocalAvdProvider? provider;
        lock (_gate)
        {
            provider = _avdProvider;
            _avdProvider = null;
        }

        if (provider is not null)
        {
            await provider.StopAllAsync();
            provider.Dispose();
        }
    }

    private void Refresh()
    {
        var sdk = RequireSdk();
        lock (_gate)
        {
            if (_sdkRoot == sdk.Root && _adb is not null)
            {
                return;
            }

            _sdkRoot = sdk.Root;
            _adb = new AdbClient(sdk.AdbPath, _runner);
            // Keep the existing provider (and its running emulators) if the SDK moved; it still works.
            _avdProvider ??= sdk.HasEmulator ? new LocalAvdProvider(_adb, Avds, sdk.EmulatorPath, _loggers.CreateLogger<LocalAvdProvider>()) : null;
        }
    }
}
