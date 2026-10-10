using System.Diagnostics;
using System.Text;
using StoreShot.Core;
using StoreShot.Emulator.Interop;

namespace StoreShot.Emulator.Adb;

/// <summary>
/// A process that runs until stopped (emulator, logcat, scrcpy server). Output lines are delivered as they arrive.
/// When a <see cref="JobObject"/> is given, Windows kills the process if SSGen exits unexpectedly.
/// </summary>
public sealed class LongRunningProcess : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _recent = new();
    private int _disposed;

    private LongRunningProcess(Process process) => _process = process;

    public event EventHandler<string>? LineReceived;

    public event EventHandler<int>? Exited;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int Id => _process.Id;

    /// <summary>The last few KB of output, for error messages.</summary>
    public string RecentOutput
    {
        get
        {
            lock (_recent)
            {
                return _recent.ToString();
            }
        }
    }

    public static LongRunningProcess Start(string fileName, IEnumerable<string> arguments, JobObject? job = null, Action<LongRunningProcess>? beforeStart = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var lrp = new LongRunningProcess(process);
        beforeStart?.Invoke(lrp);
        process.OutputDataReceived += (_, e) => lrp.OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => lrp.OnLine(e.Data);
        process.Exited += (_, _) => lrp.Exited?.Invoke(lrp, SafeExitCode(process));
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            process.Dispose();
            throw new UserFacingException($"Couldn't start \"{fileName}\": {ex.Message}", "Check the Android SDK path in Settings.", ex);
        }

        job?.Assign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return lrp;
    }

    public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        Kill();
        _process.Dispose();
    }

    private static int SafeExitCode(Process p)
    {
        try
        {
            return p.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private void OnLine(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_recent)
        {
            _recent.AppendLine(line);
            if (_recent.Length > 8192)
            {
                _recent.Remove(0, _recent.Length - 4096);
            }
        }

        LineReceived?.Invoke(this, line);
    }
}
