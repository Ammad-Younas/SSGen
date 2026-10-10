using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using StoreShot.Core;
using StoreShot.Emulator.Adb;
using StoreShot.Emulator.Avd;
using StoreShot.Emulator.Interop;

namespace StoreShot.Emulator.Devices;

public enum DeviceTargetKind
{
    Avd,
    Phone,
    Mock,
}

/// <summary>Something the user can pick to run the app on: an AVD (running or not), a USB phone, or the mock.</summary>
public sealed record DeviceTarget(
    string Id,
    string DisplayName,
    DeviceTargetKind Kind,
    string Details,
    string? RunningSerial,
    AvdInfo? Avd = null,
    string? Problem = null)
{
    public bool IsRunning => RunningSerial is not null;

    /// <summary>True for AVDs StoreShot created (and may wipe or delete).</summary>
    public bool IsManaged => Avd?.IsManagedByStoreShot == true;

    public override string ToString() => DisplayName;
}

public sealed record DeviceStartOptions
{
    /// <summary>Run the emulator without its own window (the preview inside StoreShot still works).</summary>
    public bool Headless { get; init; } = true;

    /// <summary>Factory-reset the AVD's user data on start (StoreShot-managed AVDs only).</summary>
    public bool WipeData { get; init; }

    public TimeSpan BootTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Ignore the quick-boot snapshot and boot from scratch (slower, but recovers from a bad snapshot).</summary>
    public bool ColdBoot { get; init; }

    /// <summary>
    /// Emulator -gpu mode. "host" renders on the PC's GPU, which leaves the guest CPU free for the software
    /// H.264 encoder used by the live preview; "auto" picks software rendering when headless.
    /// </summary>
    public string Gpu { get; init; } = "host";
}

/// <summary>Swappable source of devices (brief: LocalAVD, ConnectedPhone, Mock).</summary>
public interface IEmulatorProvider
{
    DeviceTargetKind Kind { get; }

    Task<IReadOnlyList<DeviceTarget>> ListTargetsAsync(CancellationToken ct = default);

    /// <summary>Starts the target if needed, waits until Android has fully booted, and returns a controllable device.</summary>
    Task<IDevice> ConnectAsync(DeviceTarget target, DeviceStartOptions options, IProgress<string>? progress, CancellationToken ct = default);

    /// <summary>Stops the target if this provider can (emulators); no-op for phones.</summary>
    Task StopAsync(DeviceTarget target, CancellationToken ct = default);
}

/// <summary>Android Virtual Devices on this PC, launched with the SDK emulator inside a kill-on-close job object.</summary>
public sealed class LocalAvdProvider : IEmulatorProvider, IDisposable
{
    private readonly AdbClient _adb;
    private readonly AvdManager _avds;
    private readonly string _emulatorPath;
    private readonly ILogger _log;
    private readonly JobObject _job = new();
    private readonly Dictionary<string, LongRunningProcess> _launched = new(StringComparer.Ordinal);

    public LocalAvdProvider(AdbClient adb, AvdManager avds, string emulatorPath, ILogger log)
    {
        _adb = adb;
        _avds = avds;
        _emulatorPath = emulatorPath;
        _log = log;
    }

    public DeviceTargetKind Kind => DeviceTargetKind.Avd;

    public async Task<IReadOnlyList<DeviceTarget>> ListTargetsAsync(CancellationToken ct = default)
    {
        var running = await GetRunningEmulatorsAsync(ct);
        return _avds.List()
            .Select(a => new DeviceTarget(
                "avd:" + a.Name,
                a.Name,
                DeviceTargetKind.Avd,
                a.Summary + (a.IsManagedByStoreShot ? " · SSGen" : ""),
                running.GetValueOrDefault(a.Name),
                a,
                a.IsComplete ? null : "Its files are missing; recreate it."))
            .ToList();
    }

    public async Task<IDevice> ConnectAsync(DeviceTarget target, DeviceStartOptions options, IProgress<string>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        var avd = target.Avd ?? throw new ArgumentException("Not an AVD target.", nameof(target));
        var serial = (await GetRunningEmulatorsAsync(ct)).GetValueOrDefault(avd.Name);
        if (serial is null)
        {
            if (options.WipeData)
            {
                AvdManager.EnsureManaged(avd, "reset");
            }

            serial = await LaunchAsync(avd, options, progress, ct);
            try
            {
                await WaitForBootAsync(serial, options.BootTimeout, options.ColdBoot ? null : TimeSpan.FromSeconds(60), progress, ct);
            }
            catch (StuckOfflineException)
            {
                // A corrupt quick-boot snapshot leaves the guest hung with adb "offline". Kill without saving
                // (so the bad snapshot isn't re-saved) and cold boot once.
                _log.LogWarning("{Avd} stayed offline after a quick boot; retrying with a cold boot", avd.Name);
                progress?.Report("Quick boot failed; cold booting instead (about a minute)…");
                KillLaunched(avd.Name);
                await Task.Delay(2000, ct);
                serial = await LaunchAsync(avd, options with { ColdBoot = true }, progress, ct);
                await WaitForBootAsync(serial, options.BootTimeout, null, progress, ct);
            }
        }
        else if (options.WipeData)
        {
            throw new UserFacingException($"\"{avd.Name}\" is running, so it can't be reset now.", "Stop it first, then reset.");
        }
        else
        {
            await WaitForBootAsync(serial, options.BootTimeout, null, progress, ct);
        }

        progress?.Report("Ready.");
        return new AdbDevice(_adb, serial, avd.Name, DeviceKind.Emulator);
    }

    public async Task StopAsync(DeviceTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var name = target.Avd?.Name ?? target.DisplayName;
        var serial = target.RunningSerial ?? (await GetRunningEmulatorsAsync(ct)).GetValueOrDefault(name);
        if (serial is not null)
        {
            // Graceful shutdown lets the emulator save its quick-boot snapshot. Killing it mid-save corrupts the
            // snapshot (the next boot then hangs "offline"), so allow plenty of time before falling back to a kill.
            await _adb.RunAsync(serial, ["emu", "kill"], TimeSpan.FromSeconds(10), ct);
        }

        LongRunningProcess? process;
        lock (_launched)
        {
            _launched.Remove(name, out process);
        }

        if (process is not null)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning("Emulator {Name} didn't exit after 'emu kill'; killing it", name);
            }

            process.Dispose();
        }
    }

    /// <summary>Stops every emulator StoreShot started. Called on app exit; the job object is the backstop.</summary>
    public async Task StopAllAsync()
    {
        string[] names;
        lock (_launched)
        {
            names = [.. _launched.Keys];
        }

        foreach (var name in names)
        {
            try
            {
                await StopAsync(new DeviceTarget("avd:" + name, name, DeviceTargetKind.Avd, "", null));
            }
            catch (Exception ex) when (ex is UserFacingException or InvalidOperationException or IOException)
            {
                _log.LogWarning(ex, "Failed to stop emulator {Name}", name);
            }
        }
    }

    public void Dispose()
    {
        lock (_launched)
        {
            foreach (var p in _launched.Values)
            {
                p.Dispose();
            }

            _launched.Clear();
        }

        _job.Dispose();
    }

    /// <summary>Running emulators by AVD name ("emu avd name" over the console).</summary>
    private async Task<Dictionary<string, string>> GetRunningEmulatorsAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in await _adb.GetDevicesAsync(ct))
        {
            if (!d.IsEmulator || d.State == AdbDeviceState.Unauthorized)
            {
                continue;
            }

            var r = await _adb.RunAsync(d.Serial, ["emu", "avd", "name"], TimeSpan.FromSeconds(10), ct);
            var name = r.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && l != "OK");
            if (name is not null)
            {
                result[name] = d.Serial;
            }
        }

        return result;
    }

    private async Task<string> LaunchAsync(AvdInfo avd, DeviceStartOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        if (!avd.IsComplete)
        {
            throw new UserFacingException($"The files for \"{avd.Name}\" are missing.", "Delete it and create a new virtual device.");
        }

        var used = (await _adb.GetDevicesAsync(ct)).Select(d => d.EmulatorPort).OfType<int>().ToHashSet();
        var port = Enumerable.Range(0, 16).Select(i => 5554 + (i * 2)).FirstOrDefault(p => !used.Contains(p) && IsPortFree(p) && IsPortFree(p + 1));
        if (port == 0)
        {
            throw new UserFacingException("Too many emulators are running.", "Stop some emulators and try again.");
        }

        var args = new List<string> { "-avd", avd.Name, "-port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-no-boot-anim", "-no-audio", "-gpu", options.Gpu };
        // gRPC bridge for the fast live preview (EmulatorGrpcMirror); token auth, localhost only.
        var grpcPort = Enumerable.Range(8554, 200).First(IsPortFree);
        args.AddRange(["-grpc", grpcPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "-grpc-use-token"]);

        if (options.Headless)
        {
            args.Add("-no-window");
        }

        if (options.WipeData)
        {
            args.Add("-wipe-data");
        }

        if (options.ColdBoot)
        {
            args.Add("-no-snapshot-load");
        }

        progress?.Report(options.WipeData ? $"Resetting and starting {avd.Name}…" : $"Starting {avd.Name}…");
        _log.LogInformation("Launching emulator: {Args}", string.Join(' ', args));
        var process = LongRunningProcess.Start(_emulatorPath, args, _job);
        process.LineReceived += (_, line) => _log.LogDebug("[emulator {Name}] {Line}", avd.Name, line);
        lock (_launched)
        {
            _launched[avd.Name] = process;
        }

        var serial = $"emulator-{port}";
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                lock (_launched)
                {
                    _launched.Remove(avd.Name);
                }

                throw EmulatorExitedError(avd.Name, process.RecentOutput);
            }

            var devices = await _adb.GetDevicesAsync(ct);
            if (devices.Any(d => d.Serial == serial))
            {
                return serial;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new UserFacingException($"\"{avd.Name}\" didn't appear in adb within 2 minutes.", "Check the Environment page (hardware acceleration), then try again. Emulator output:\n" + Tail(process.RecentOutput));
            }

            await Task.Delay(1000, ct);
        }
    }

    private async Task WaitForBootAsync(string serial, TimeSpan timeout, TimeSpan? offlineLimit, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Waiting for Android to boot…");
        var deadline = DateTime.UtcNow + timeout;
        DateTime? unauthorizedSince = null;
        DateTime? offlineSince = null;
        var restartedAdb = false;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var state = (await _adb.GetDevicesAsync(ct)).FirstOrDefault(d => d.Serial == serial)?.State;
            offlineSince = state == AdbDeviceState.Offline ? offlineSince ?? DateTime.UtcNow : null;
            if (offlineLimit is { } limit && offlineSince is { } since && DateTime.UtcNow - since > limit)
            {
                throw new StuckOfflineException();
            }

            if (state == AdbDeviceState.Unauthorized)
            {
                // Google Play images require adb key authorisation. The emulator provisions the user's
                // ~/.android/adbkey.pub, but an adb server started elsewhere (e.g. elevated, or with another
                // ANDROID_USER_HOME) may hold a different key. Restarting the server once fixes that.
                unauthorizedSince ??= DateTime.UtcNow;
                if (!restartedAdb && DateTime.UtcNow - unauthorizedSince > TimeSpan.FromSeconds(15))
                {
                    _log.LogWarning("{Serial} is unauthorized; restarting the adb server to reload this user's adb key", serial);
                    progress?.Report("Re-authorising adb with the emulator…");
                    await _adb.RestartServerAsync(ct);
                    restartedAdb = true;
                    unauthorizedSince = DateTime.UtcNow;
                }
                else if (restartedAdb && DateTime.UtcNow - unauthorizedSince > TimeSpan.FromSeconds(45))
                {
                    throw new UserFacingException(
                        "adb isn't authorised to control the emulator.",
                        "Close other tools that use adb (Android Studio, other emulators), then try again. If it persists, delete %USERPROFILE%\\.android\\adbkey and adbkey.pub so adb creates a fresh key.");
                }

                await Task.Delay(2000, ct);
                continue;
            }

            unauthorizedSince = null;
            var r = await _adb.RunAsync(serial, ["shell", "getprop sys.boot_completed"], TimeSpan.FromSeconds(10), ct);
            if (r.Succeeded && r.StdOut.Trim() == "1")
            {
                // Dismiss the keyguard so the app is visible.
                await _adb.RunAsync(serial, ["shell", "wm dismiss-keyguard"], TimeSpan.FromSeconds(10), ct);
                return;
            }

            await Task.Delay(2000, ct);
        }

        throw new UserFacingException(
            $"Android didn't finish booting within {timeout.TotalMinutes:0} minutes.",
            "The first boot of a new device can be slow. Try again; if it keeps failing, reset the virtual device or check hardware acceleration on the Environment page.");
    }

    private void KillLaunched(string avdName)
    {
        LongRunningProcess? process;
        lock (_launched)
        {
            _launched.Remove(avdName, out process);
        }

        process?.Dispose(); // kills the process tree
    }

    internal static UserFacingException EmulatorExitedError(string avdName, string output)
    {
        string fix = output switch
        {
            _ when output.Contains("already running", StringComparison.OrdinalIgnoreCase) || output.Contains("multiinstance", StringComparison.OrdinalIgnoreCase)
                => "This virtual device is already open in another emulator window (maybe from Android Studio). Close it there first.",
            _ when output.Contains("x86 emulation currently requires hardware acceleration", StringComparison.OrdinalIgnoreCase) || output.Contains("WHPX", StringComparison.Ordinal) && output.Contains("not", StringComparison.OrdinalIgnoreCase)
                => "Hardware acceleration isn't available. See the Environment page for how to enable Windows Hypervisor Platform.",
            _ when output.Contains("Not enough space", StringComparison.OrdinalIgnoreCase) || output.Contains("disk space", StringComparison.OrdinalIgnoreCase)
                => "Free up disk space and try again.",
            _ when output.Contains("Cannot find AVD system path", StringComparison.OrdinalIgnoreCase) || output.Contains("system image", StringComparison.OrdinalIgnoreCase)
                => "The system image for this device is missing. Install it on the Environment page, or create a new device.",
            _ => "See the emulator output below.",
        };
        return new UserFacingException($"The emulator for \"{avdName}\" closed while starting.", fix + "\n\n" + Tail(output));
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string Tail(string s) => s.Length <= 1200 ? s.Trim() : "…" + s[^1200..].Trim();
}

/// <summary>Thrown internally when a launched emulator stays "offline" (hung quick-boot snapshot).</summary>
internal sealed class StuckOfflineException : Exception
{
    public StuckOfflineException() : base("Emulator stayed offline.") { }

    public StuckOfflineException(string message) : base(message) { }

    public StuckOfflineException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Physical phones connected over USB (or adb over Wi-Fi) with USB debugging enabled.</summary>
public sealed class ConnectedPhoneProvider : IEmulatorProvider
{
    private readonly AdbClient _adb;

    public ConnectedPhoneProvider(AdbClient adb) => _adb = adb;

    public DeviceTargetKind Kind => DeviceTargetKind.Phone;

    public async Task<IReadOnlyList<DeviceTarget>> ListTargetsAsync(CancellationToken ct = default) =>
        (await _adb.GetDevicesAsync(ct))
            .Where(d => !d.IsEmulator)
            .Select(d => new DeviceTarget(
                "phone:" + d.Serial,
                d.Model?.Replace('_', ' ') ?? d.Serial,
                DeviceTargetKind.Phone,
                $"USB · {d.Serial}",
                d.State == AdbDeviceState.Device ? d.Serial : null,
                Problem: d.State switch
                {
                    AdbDeviceState.Unauthorized => "Unlock the phone and allow USB debugging for this PC.",
                    AdbDeviceState.Offline => "The phone is offline. Reconnect the cable.",
                    _ => null,
                }))
            .ToList();

    public Task<IDevice> ConnectAsync(DeviceTarget target, DeviceStartOptions options, IProgress<string>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.RunningSerial is null)
        {
            throw new UserFacingException($"{target.DisplayName} isn't ready.", target.Problem ?? "Reconnect the phone and allow USB debugging.");
        }

        return Task.FromResult<IDevice>(new AdbDevice(_adb, target.RunningSerial, target.DisplayName, DeviceKind.Phone));
    }

    public Task StopAsync(DeviceTarget target, CancellationToken ct = default) => Task.CompletedTask;
}
