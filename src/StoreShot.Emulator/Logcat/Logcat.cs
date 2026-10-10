using System.Globalization;
using System.Text.RegularExpressions;
using StoreShot.Emulator.Adb;

namespace StoreShot.Emulator.Logcat;

public enum LogLevel
{
    Verbose,
    Debug,
    Info,
    Warn,
    Error,
    Fatal,
}

public sealed record LogcatEntry(string Time, int Pid, int Tid, LogLevel Level, string Tag, string Message)
{
    public string LevelLetter => Level switch
    {
        LogLevel.Verbose => "V",
        LogLevel.Debug => "D",
        LogLevel.Info => "I",
        LogLevel.Warn => "W",
        LogLevel.Error => "E",
        _ => "F",
    };
}

/// <summary>Parses `logcat -v threadtime` lines: "10-10 14:44:01.418  1234  1250 I Tag: message".</summary>
public static partial class LogcatParser
{
    [GeneratedRegex(@"^\d\d-\d\d (\d\d:\d\d:\d\d\.\d{3})\s+(\d+)\s+(\d+)\s+([VDIWEFA])\s+(.*?)\s*: ?(.*)$")]
    private static partial Regex Line();

    public static LogcatEntry? Parse(string line)
    {
        var m = Line().Match(line ?? string.Empty);
        if (!m.Success)
        {
            return null;
        }

        var level = m.Groups[4].Value switch
        {
            "V" => LogLevel.Verbose,
            "D" => LogLevel.Debug,
            "I" => LogLevel.Info,
            "W" => LogLevel.Warn,
            "E" => LogLevel.Error,
            _ => LogLevel.Fatal,
        };
        return new LogcatEntry(
            m.Groups[1].Value,
            int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
            level,
            m.Groups[5].Value,
            m.Groups[6].Value);
    }
}

/// <summary>Streams logcat from a device. Entries arrive in batches to keep UI updates cheap.</summary>
public sealed class LogcatSession : IDisposable
{
    private readonly LongRunningProcess _process;
    private readonly List<LogcatEntry> _pending = [];
    private readonly Timer _flush;

    private LogcatSession(LongRunningProcess process)
    {
        _process = process;
        _flush = new Timer(_ => Flush(), null, 150, 150);
    }

    /// <summary>Raised on a background thread with entries received since the last batch.</summary>
    public event EventHandler<IReadOnlyList<LogcatEntry>>? EntriesReceived;

    /// <summary>Starts streaming, beginning with the most recent <paramref name="tail"/> lines.</summary>
    public static LogcatSession Start(AdbClient adb, string serial, int tail = 300)
    {
        ArgumentNullException.ThrowIfNull(adb);
        LogcatSession? session = null;
        var process = adb.StartLongRunning(serial, ["logcat", "-v", "threadtime", "-T", tail.ToString(CultureInfo.InvariantCulture)], beforeStart: p =>
            p.LineReceived += (_, line) => session?.OnLine(line));
        session = new LogcatSession(process);
        return session;
    }

    /// <summary>Clears the device's log buffer.</summary>
    public static Task ClearAsync(AdbClient adb, string serial) =>
        (adb ?? throw new ArgumentNullException(nameof(adb))).RunAsync(serial, ["logcat", "-c"], TimeSpan.FromSeconds(10));

    public void Dispose()
    {
        _flush.Dispose();
        _process.Dispose();
    }

    private void OnLine(string line)
    {
        if (LogcatParser.Parse(line) is { } entry)
        {
            lock (_pending)
            {
                _pending.Add(entry);
            }
        }
    }

    private void Flush()
    {
        LogcatEntry[] batch;
        lock (_pending)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            batch = [.. _pending];
            _pending.Clear();
        }

        EntriesReceived?.Invoke(this, batch);
    }
}
