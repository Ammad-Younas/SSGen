using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SSGen.Core;
using SSGen.Emulator.Adb;

namespace SSGen.Emulator.Devices;

/// <summary>An emulator or USB phone controlled through adb.</summary>
public sealed partial class AdbDevice : IDevice
{
    private const string UiDumpPath = "/data/local/tmp/ssgen-ui.xml";
    private readonly AdbClient _adb;

    public AdbDevice(AdbClient adb, string serial, string displayName, DeviceKind kind)
    {
        _adb = adb;
        Serial = serial;
        DisplayName = displayName;
        Kind = kind;
    }

    public string Serial { get; }
    public string DisplayName { get; }
    public DeviceKind Kind { get; }
    public AdbClient Adb => _adb;

    [GeneratedRegex(@"(?:Override|Physical) size:\s*(\d+)x(\d+)")]
    private static partial Regex WmSize();

    [GeneratedRegex(@"(?:topResumedActivity|mResumedActivity|ResumedActivity)[^{]*\{[^ ]+ [^ ]+ ([\w.$]+/[\w.$]+)")]
    private static partial Regex ResumedActivity();

    public async Task<string> GetAbiAsync(CancellationToken ct = default) =>
        (await _adb.ShellAsync(Serial, "getprop ro.product.cpu.abi", null, ct)).Trim();

    public async Task<int> GetApiLevelAsync(CancellationToken ct = default) =>
        int.TryParse((await _adb.ShellAsync(Serial, "getprop ro.build.version.sdk", null, ct)).Trim(), CultureInfo.InvariantCulture, out var v) ? v : 0;

    public async Task<(int Width, int Height)> GetScreenSizeAsync(CancellationToken ct = default)
    {
        var output = await _adb.ShellAsync(Serial, "wm size", null, ct);
        // Prefer "Override size" (what apps see) over "Physical size".
        var matches = WmSize().Matches(output);
        var m = matches.FirstOrDefault(x => x.Value.StartsWith("Override", StringComparison.Ordinal)) ?? matches.FirstOrDefault()
            ?? throw new UserFacingException("Couldn't read the device's screen size.", output.Trim());
        return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    public Task<byte[]> CaptureScreenshotPngAsync(CancellationToken ct = default) =>
        _adb.ExecOutAsync(Serial, "screencap -p", TimeSpan.FromSeconds(15), ct);

    public async Task<string> DumpUiHierarchyAsync(CancellationToken ct = default)
    {
        // uiautomator fails transiently while the UI animates ("could not get idle state"); retry briefly.
        for (var attempt = 0; ; attempt++)
        {
            var r = await _adb.TryShellAsync(Serial, $"uiautomator dump {UiDumpPath} >/dev/null && cat {UiDumpPath}", TimeSpan.FromSeconds(30), ct);
            var start = r.StdOut.IndexOf("<?xml", StringComparison.Ordinal);
            if (r.Succeeded && start >= 0)
            {
                return r.StdOut[start..];
            }

            if (attempt >= 2)
            {
                return string.Empty;
            }

            await Task.Delay(500, ct);
        }
    }

    public async Task<string?> GetForegroundActivityAsync(CancellationToken ct = default)
    {
        var r = await _adb.TryShellAsync(Serial, "dumpsys activity activities | grep -E 'topResumedActivity|mResumedActivity|ResumedActivity'", TimeSpan.FromSeconds(15), ct);
        return ParseResumedActivity(r.StdOut);
    }

    internal static string? ParseResumedActivity(string dumpsys) =>
        ResumedActivity().Match(dumpsys ?? string.Empty) is { Success: true } m ? m.Groups[1].Value : null;

    public Task TapAsync(int x, int y, CancellationToken ct = default) =>
        _adb.ShellAsync(Serial, Invariant($"input tap {x} {y}"), null, ct);

    public Task SwipeAsync(int x1, int y1, int x2, int y2, int durationMs = 300, CancellationToken ct = default) =>
        _adb.ShellAsync(Serial, Invariant($"input swipe {x1} {y1} {x2} {y2} {durationMs}"), null, ct);

    public Task InputTextAsync(string text, CancellationToken ct = default) =>
        _adb.ShellAsync(Serial, "input text " + EscapeInputText(text), null, ct);

    public Task PressKeyAsync(AndroidKey key, CancellationToken ct = default) =>
        _adb.ShellAsync(Serial, Invariant($"input keyevent {(int)key}"), null, ct);

    public Task InstallAsync(IReadOnlyList<string> apkPaths, CancellationToken ct = default) =>
        _adb.InstallAsync(Serial, apkPaths, ct);

    public async Task<bool> IsInstalledAsync(string packageName, CancellationToken ct = default)
    {
        var r = await _adb.TryShellAsync(Serial, "pm path " + ShellQuote(packageName), null, ct);
        return r.Succeeded && r.StdOut.Contains("package:", StringComparison.Ordinal);
    }

    public async Task LaunchAsync(string packageName, string? activity, CancellationToken ct = default)
    {
        var command = activity is null
            ? $"monkey -p {ShellQuote(packageName)} -c android.intent.category.LAUNCHER 1"
            : $"am start -W -n {ShellQuote(packageName + "/" + activity)}";
        var r = await _adb.TryShellAsync(Serial, command, TimeSpan.FromSeconds(60), ct);
        if (!r.Succeeded || r.Combined.Contains("Error", StringComparison.Ordinal) || r.Combined.Contains("No activities found", StringComparison.Ordinal))
        {
            throw new UserFacingException($"Couldn't open {packageName}.", "Check that the app is installed and has a launcher activity. " + r.Combined.Trim());
        }
    }

    public Task ForceStopAsync(string packageName, CancellationToken ct = default) =>
        _adb.ShellAsync(Serial, "am force-stop " + ShellQuote(packageName), null, ct);

    public async Task<int?> GetPidAsync(string packageName, CancellationToken ct = default)
    {
        var r = await _adb.TryShellAsync(Serial, "pidof " + ShellQuote(packageName), null, ct);
        var first = r.StdOut.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return int.TryParse(first, CultureInfo.InvariantCulture, out var pid) ? pid : null;
    }

    /// <summary>
    /// Escapes text for `input text`: spaces become %s (input's own encoding) and the result is single-quoted for
    /// the device shell. Only ASCII is reliable with `input text`; other characters are dropped.
    /// </summary>
    public static string EscapeInputText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (c == ' ')
            {
                sb.Append("%s");
            }
            else if (c is >= (char)0x21 and <= (char)0x7E)
            {
                sb.Append(c);
            }
        }

        return ShellQuote(sb.ToString());
    }

    /// <summary>POSIX single-quote quoting for the device shell.</summary>
    public static string ShellQuote(string s) => "'" + (s ?? string.Empty).Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
