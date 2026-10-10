using System.Diagnostics;
using System.Text;

namespace StoreShot.Core.Processes;

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Text written to stdin after start; stdin is then closed.</summary>
    public string? StandardInput { get; init; }

    /// <summary>Called for every stdout/stderr line as it arrives (lines split on \r or \n, so progress bars work).</summary>
    public Action<string>? OnLine { get; init; }

    public override string ToString() => $"{FileName} {string.Join(' ', Arguments)}";
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    /// <summary>Stdout and stderr combined; many Android tools write useful output to stderr.</summary>
    public string Combined => StdErr.Length == 0 ? StdOut : StdOut.Length == 0 ? StdErr : StdOut + "\n" + StdErr;
}

public interface IProcessRunner
{
    /// <summary>
    /// Runs a process to completion. Throws <see cref="UserFacingException"/> if it can't be started,
    /// <see cref="OperationCanceledException"/> if cancelled. Timeouts return a result with TimedOut set.
    /// The whole process tree is killed on timeout or cancellation.
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var psi = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = spec.StandardInput is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in spec.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (spec.WorkingDirectory is not null)
        {
            psi.WorkingDirectory = spec.WorkingDirectory;
        }

        if (spec.Environment is not null)
        {
            foreach (var (k, v) in spec.Environment)
            {
                psi.Environment[k] = v;
            }
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) => Collect(e.Data, stdout, stdoutDone, spec.OnLine);
        process.ErrorDataReceived += (_, e) => Collect(e.Data, stderr, stderrDone, spec.OnLine);

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new UserFacingException(
                $"Couldn't start \"{spec.FileName}\": {ex.Message}",
                "Check that the tool is installed and the path in Settings is correct.",
                ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (spec.StandardInput is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(spec.StandardInput.AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Process exited before reading stdin; its exit code tells the story.
            }
        }

        using var timeoutCts = new CancellationTokenSource(spec.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
            await Task.WhenAll(stdoutDone.Task, stderrDone.Task).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            cancellationToken.ThrowIfCancellationRequested();
            timedOut = true;
        }
        catch (TimeoutException)
        {
            // Output streams didn't close (a grandchild kept the handles open); use what we have.
        }

        lock (stdout)
        {
            lock (stderr)
            {
                return new ProcessResult(timedOut ? -1 : process.ExitCode, stdout.ToString().TrimEnd(), stderr.ToString().TrimEnd(), timedOut);
            }
        }
    }

    private static void Collect(string? line, StringBuilder sink, TaskCompletionSource done, Action<string>? onLine)
    {
        if (line is null)
        {
            done.TrySetResult();
            return;
        }

        lock (sink)
        {
            sink.AppendLine(line);
        }

        onLine?.Invoke(line);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied while exiting; nothing more we can do.
        }
    }
}
