using System.Diagnostics;
using System.Globalization;
using SSGen.Core;
using SSGen.Core.Processes;
using SSGen.Emulator.Interop;

namespace SSGen.Emulator.Adb;

public enum AdbDeviceState
{
    Device,
    Offline,
    Unauthorized,
    Other,
}

/// <summary>One line of `adb devices -l`.</summary>
public sealed record AdbDeviceEntry(string Serial, AdbDeviceState State, string? Model, string? Product)
{
    public bool IsEmulator => Serial.StartsWith("emulator-", StringComparison.Ordinal);

    /// <summary>Console port for emulator serials ("emulator-5554" -> 5554).</summary>
    public int? EmulatorPort => IsEmulator && int.TryParse(Serial["emulator-".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null;
}

public static class AdbOutputParser
{
    public static IReadOnlyList<AdbDeviceEntry> ParseDevices(string output)
    {
        var list = new List<AdbDeviceEntry>();
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith('*'))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var state = parts[1] switch
            {
                "device" => AdbDeviceState.Device,
                "offline" => AdbDeviceState.Offline,
                "unauthorized" => AdbDeviceState.Unauthorized,
                _ => AdbDeviceState.Other,
            };
            string? Field(string key) => parts.Skip(2).FirstOrDefault(p => p.StartsWith(key + ":", StringComparison.Ordinal))?[(key.Length + 1)..];
            list.Add(new AdbDeviceEntry(parts[0], state, Field("model"), Field("product")));
        }

        return list;
    }

    /// <summary>Turns `adb install` failure output into a readable error with a fix.</summary>
    public static UserFacingException InstallError(string output)
    {
        var code = System.Text.RegularExpressions.Regex.Match(output ?? string.Empty, @"INSTALL_[A-Z_]+").Value;
        var (message, fix) = code switch
        {
            "INSTALL_FAILED_NO_MATCHING_ABIS" => (
                "The APK contains native code for a CPU type this emulator doesn't support (for example ARM-only code on an x86_64 emulator).",
                "Use a universal or x86_64 build of the app, or an emulator image that matches the APK's ABI."),
            "INSTALL_FAILED_OLDER_SDK" => (
                "The app needs a newer Android version than this emulator runs.",
                "Create a virtual device with a higher API level."),
            "INSTALL_FAILED_UPDATE_INCOMPATIBLE" => (
                "A different build of this app (signed with another key) is already installed.",
                "Uninstall the existing app from the emulator, or reset the virtual device."),
            "INSTALL_FAILED_VERSION_DOWNGRADE" => (
                "A newer version of this app is already installed.",
                "Uninstall the existing app first, or reset the virtual device."),
            "INSTALL_FAILED_INSUFFICIENT_STORAGE" => (
                "The emulator is out of storage.",
                "Reset (wipe) the virtual device, or create one with a larger data partition."),
            "INSTALL_FAILED_MISSING_SPLIT" => (
                "This APK is only part of a split app; the other parts are missing.",
                "Import all split APKs together, or build a universal APK (bundletool build-apks --mode=universal)."),
            "INSTALL_PARSE_FAILED_NOT_APK" or "INSTALL_PARSE_FAILED_NO_CERTIFICATES" or "INSTALL_PARSE_FAILED_UNEXPECTED_EXCEPTION" => (
                "The file isn't a valid, signed APK.",
                "Check that you picked the right file and that it is a signed release or debug APK."),
            "INSTALL_FAILED_TEST_ONLY" => (
                "The APK is marked test-only.",
                "Build a normal debug or release APK (testOnly is set by some IDE run configurations)."),
            _ => ("The app couldn't be installed on the device.", "See the details below. " + Tail(output)),
        };
        return new UserFacingException(code.Length > 0 ? $"{message} ({code})" : message, fix);
    }

    private static string Tail(string? s) => s is null ? string.Empty : s.Length <= 300 ? s.Trim() : "…" + s[^300..].Trim();
}

/// <summary>Thin, testable wrapper over adb.exe. Every call is scoped to a device serial when one is given.</summary>
public sealed class AdbClient
{
    private readonly IProcessRunner _runner;

    public AdbClient(string adbPath, IProcessRunner runner)
    {
        AdbPath = adbPath;
        _runner = runner;
    }

    public string AdbPath { get; }

    public Task<ProcessResult> RunAsync(string? serial, IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default) =>
        _runner.RunAsync(new ProcessSpec(AdbPath, [.. Scope(serial), .. args]) { Timeout = timeout ?? TimeSpan.FromSeconds(30) }, ct);

    public async Task<IReadOnlyList<AdbDeviceEntry>> GetDevicesAsync(CancellationToken ct = default)
    {
        var r = await RunAsync(null, ["devices", "-l"], TimeSpan.FromSeconds(30), ct);
        if (!r.Succeeded)
        {
            throw new UserFacingException("adb isn't responding.", "Restart SSGen. If it keeps happening, run \"adb kill-server\" in a terminal. " + r.Combined);
        }

        return AdbOutputParser.ParseDevices(r.StdOut);
    }

    /// <summary>Restarts the adb server (reloads the user's adb key). Other adb clients reconnect automatically.</summary>
    public async Task RestartServerAsync(CancellationToken ct = default)
    {
        await RunAsync(null, ["kill-server"], TimeSpan.FromSeconds(15), ct);
        await RunAsync(null, ["start-server"], TimeSpan.FromSeconds(30), ct);
    }

    /// <summary>Runs a shell command and returns stdout. Throws if adb fails or the command exits non-zero.</summary>
    public async Task<string> ShellAsync(string serial, string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var r = await RunAsync(serial, ["shell", command], timeout, ct);
        if (!r.Succeeded)
        {
            throw new UserFacingException(
                r.TimedOut ? $"The device didn't respond to \"{command}\" in time." : $"Device command failed: {command}",
                "Check that the device is still running. " + r.Combined.Trim());
        }

        return r.StdOut;
    }

    /// <summary>Runs a shell command and returns its exit code and output without throwing on non-zero exit.</summary>
    public Task<ProcessResult> TryShellAsync(string serial, string command, TimeSpan? timeout = null, CancellationToken ct = default) =>
        RunAsync(serial, ["shell", command], timeout, ct);

    /// <summary>`adb exec-out` with raw binary stdout (e.g. screencap -p).</summary>
    public async Task<byte[]> ExecOutAsync(string serial, string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(AdbPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in (string[])[.. Scope(serial), "exec-out", command])
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new UserFacingException("Couldn't start adb.", "Check the Android SDK path in Settings.");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        try
        {
            using var ms = new MemoryStream();
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.StandardOutput.BaseStream.CopyToAsync(ms, timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                throw new UserFacingException($"Device command failed: {command}", stderr.Trim());
            }

            return ms.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new UserFacingException($"The device didn't respond to \"{command}\" in time.", "Check that the device is still running.");
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }

    public async Task PushAsync(string serial, string localPath, string remotePath, CancellationToken ct = default)
    {
        var r = await RunAsync(serial, ["push", localPath, remotePath], TimeSpan.FromMinutes(2), ct);
        if (!r.Succeeded)
        {
            throw new UserFacingException($"Couldn't copy {Path.GetFileName(localPath)} to the device.", r.Combined.Trim());
        }
    }

    public async Task ForwardAsync(string serial, int localPort, string remote, CancellationToken ct = default)
    {
        var r = await RunAsync(serial, ["forward", $"tcp:{localPort}", remote], null, ct);
        if (!r.Succeeded)
        {
            throw new UserFacingException("Couldn't set up the connection to the device.", r.Combined.Trim());
        }
    }

    public Task RemoveForwardAsync(string serial, int localPort) =>
        RunAsync(serial, ["forward", "--remove", $"tcp:{localPort}"], TimeSpan.FromSeconds(10));

    /// <summary>Installs one APK, or several splits of one app together. Grants all runtime permissions (-g).</summary>
    public async Task InstallAsync(string serial, IReadOnlyList<string> apkPaths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(apkPaths);
        if (apkPaths.Count == 0)
        {
            throw new ArgumentException("No APKs given.", nameof(apkPaths));
        }

        string[] args = apkPaths.Count == 1
            ? ["install", "-r", "-g", "-t", apkPaths[0]]
            : ["install-multiple", "-r", "-g", "-t", .. apkPaths];
        var r = await RunAsync(serial, args, TimeSpan.FromMinutes(5), ct);
        if (!r.Succeeded || !r.Combined.Contains("Success", StringComparison.Ordinal))
        {
            throw r.TimedOut
                ? new UserFacingException("Installing the app timed out.", "The emulator may be overloaded. Try again, or restart the virtual device.")
                : AdbOutputParser.InstallError(r.Combined);
        }
    }

    /// <summary>Starts a long-running adb command (logcat, scrcpy server shell).</summary>
    public LongRunningProcess StartLongRunning(string? serial, IEnumerable<string> args, JobObject? job = null, Action<LongRunningProcess>? beforeStart = null) =>
        LongRunningProcess.Start(AdbPath, [.. Scope(serial), .. args], job, beforeStart);

    private static string[] Scope(string? serial) => serial is null ? [] : ["-s", serial];
}
