using System.ComponentModel;
using System.Diagnostics;

namespace Recodio;

// Shared plumbing for running a CLI tool (yt-dlp, spotDL) as a child process: stream
// stdout/stderr line-by-line, kill the process (and its children) if cancelled, and surface
// a nonzero exit code as an exception. Kept out of the individual downloader wrappers so the
// cancellation-kill semantics can't drift between them.
public static class ProcessRunner
{
    public static async Task WaitForExitAsync(Process proc, CancellationToken ct)
    {
        await using (ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { /* already exited */ }
        }).ConfigureAwait(false))
        {
            await proc.WaitForExitAsync(ct);
        }

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
    }

    // Starts psi with redirected stdout/stderr, streams each non-blank line to onLine as it
    // arrives, waits for exit (killing on cancellation), and throws if the exit code is nonzero.
    //
    // throwOnPositiveExit controls only the POSITIVE-exit-code case. A negative exit code always
    // throws - that's the abrupt-kill/bot-detection signal both downloaders retry on. A positive
    // exit code from yt-dlp, though, just means "--ignore-errors kept going but at least one item
    // in the batch failed" - the run still finished, so a caller tracking its own per-item
    // failCount (from ERROR: lines) can pass false here to get that count back instead of an
    // exception, and show an honest "N ok, M con error" summary rather than a hard failure.
    public static async Task RunAsync(
        ProcessStartInfo psi, Action<string> onLine, Func<int, string> nonZeroExitMessage, CancellationToken ct,
        bool throwOnPositiveExit = true)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        void HandleLine(string? data)
        {
            if (data == null) return;
            var trimmed = data.TrimEnd();
            if (trimmed.Length > 0) onLine(trimmed);
        }

        proc.OutputDataReceived += (_, e) => HandleLine(e.Data);
        proc.ErrorDataReceived += (_, e) => HandleLine(e.Data);

        try
        {
            proc.Start();
        }
        catch (Win32Exception ex)
        {
            // The bare "no se puede encontrar el archivo especificado" Win32Exception is
            // meaningless to a user - name the tool and point at the fix instead.
            var toolName = Path.GetFileNameWithoutExtension(psi.FileName);
            throw new InvalidOperationException(
                $"No se encontro \"{toolName}\" ({psi.FileName}). Instalalo o revisa que este en el PATH del sistema.",
                ex);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await WaitForExitAsync(proc, ct);

        // Give async stdout/stderr handlers a brief moment to flush final lines.
        try { await Task.Delay(50, CancellationToken.None); } catch { /* ignore */ }

        if (proc.ExitCode != 0)
        {
            var message = nonZeroExitMessage(proc.ExitCode);
            if (proc.ExitCode < 0 || throwOnPositiveExit)
                throw new InvalidOperationException(message);
        }
    }
}
