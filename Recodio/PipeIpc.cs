using System.IO.Pipes;
using System.Text;

namespace Recodio;

public static class PipeIpc
{
    private const string PipeName = "Recodio_IPC";

    public static void StartServer(Action<string[]> onFilesReceived, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(ct);
                    var files = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (files.Length > 0) onFilesReceived(files);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    try { await Task.Delay(500, ct); } catch { /* cancelled */ }
                }
            }
        }, ct);
    }

    public static bool TrySendFiles(string[] files, int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            foreach (var f in files) writer.WriteLine(f);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
