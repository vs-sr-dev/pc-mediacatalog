using System.Diagnostics;
using System.Text;

namespace MediaCatalog.Core.Tools;

public record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>Runs an external console tool and captures its output, with a timeout.</summary>
public static class ProcessRunner
{
    /// <param name="onStdOutLine">
    /// Called for each line the tool writes to standard output as it writes it, so a long
    /// job can say how far along it is instead of going quiet for ten minutes. Fires on a
    /// background thread.
    /// </param>
    public static async Task<ProcessResult> RunAsync(
        string exePath,
        string arguments,
        CancellationToken ct = default,
        int timeoutMs = 120_000,
        byte[]? captureStdoutBinary = null,
        Action<string>? onStdOutLine = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            stdout.AppendLine(e.Data);
            if (onStdOutLine == null) return;
            try { onStdOutLine(e.Data); }
            catch { /* a progress report must never take the job down with it */ }
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            var timedOut = !ct.IsCancellationRequested;
            if (!timedOut) throw; // genuine user cancellation
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), TimedOut: false);
    }

    /// <summary>
    /// Variant that captures raw stdout bytes (for piping ffmpeg rawvideo output).
    /// stderr is still captured as text.
    /// </summary>
    public static async Task<(int exitCode, byte[] stdout, string stderr)> RunBinaryAsync(
        string exePath, string arguments, CancellationToken ct = default, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginErrorReadLine();

        using var ms = new MemoryStream();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(ms, timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (ct.IsCancellationRequested) throw;
            return (-1, ms.ToArray(), stderr.ToString());
        }

        return (process.ExitCode, ms.ToArray(), stderr.ToString());
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
    }
}
