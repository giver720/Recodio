using System.IO.Pipes;
using System.Text;

namespace Recodio;

public static class PipeIpc
{
    private const string PipeName = "Recodio_IPC";
    public const string ActivateCommand = "__ACTIVATE__";

    /// <summary>
    /// Listens for file paths (one per line) or a single ActivateCommand line.
    /// Accepts multiple concurrent clients (MaxAllowedServerInstances).
    /// </summary>
    public static void StartServer(
        Action<string[]> onFilesReceived,
        Action? onActivate,
        CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    // Handle client without blocking the accept loop for the next connection.
                    var client = server;
                    server = null;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await using (client.ConfigureAwait(false))
                            {
                                using var reader = new StreamReader(client, Encoding.UTF8);
                                var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                                Dispatch(text, onFilesReceived, onActivate);
                            }
                        }
                        catch (OperationCanceledException) { /* shutdown */ }
                        catch
                        {
                            /* client dropped */
                        }
                    }, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    try { await Task.Delay(300, ct).ConfigureAwait(false); }
                    catch { /* cancelled */ }
                }
                finally
                {
                    if (server != null)
                    {
                        try { await server.DisposeAsync().ConfigureAwait(false); }
                        catch { /* ignore */ }
                    }
                }
            }
        }, ct);
    }

    private static void Dispatch(string text, Action<string[]> onFiles, Action? onActivate)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var lines = text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return;

        if (lines.Length == 1
            && string.Equals(lines[0], ActivateCommand, StringComparison.Ordinal))
        {
            onActivate?.Invoke();
            return;
        }

        // Filter out accidental activate token mixed with files
        var files = lines
            .Where(l => !string.Equals(l, ActivateCommand, StringComparison.Ordinal))
            .ToArray();
        if (files.Length > 0)
            onFiles(files);
    }

    public static bool TrySendFiles(string[] files, int timeoutMs = 2500)
    {
        if (files.Length == 0) return TrySendActivate(timeoutMs);
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            foreach (var f in files)
                writer.WriteLine(f);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySendActivate(int timeoutMs = 2500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(ActivateCommand);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
