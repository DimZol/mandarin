using System.IO.Pipes;
using System.Text;
using Mandarin.Core;

namespace Mandarin.Platform;

/// <summary>Single app instance; a second launch hands its arguments to the first one (a named pipe or a Unix socket).</summary>
public sealed class SingleInstance : IDisposable
{
    static string PipeName => "Mandarin.FileConverter.Pipe" + Environment.UserName;
    readonly Mutex _mutex;
    readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    public event Action<string[]>? ArgumentsReceived;

    public SingleInstance()
    {
        _mutex = new Mutex(true, "Mandarin.FileConverter.SingleInstance." + Environment.UserName, out var created);
        IsFirst = created;
    }

    public void StartServer()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync();
                    var args = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    UiThread.Post(() => ArgumentsReceived?.Invoke(args));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Error("Pipe server", ex); await Task.Delay(500); }
            }
        });
    }

    public static bool SendToFirst(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write(string.Join('\n', args.Length == 0 ? new[] { "--show" } : args));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst) try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
        _mutex.Dispose();
    }
}
