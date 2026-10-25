using System.IO.Pipes;
using System.Text;
using Mandarin.Core;

namespace Mandarin.Platform;

/// <summary>Single app instance; a second launch hands its arguments to the first one (a named pipe or a Unix socket).</summary>
public sealed class SingleInstance : IDisposable
{
    static string PipeName => "Mandarin.FileConverter.Pipe" + Environment.UserName;
    readonly Mutex? _mutex;
    readonly FileStream? _lock;
    readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    public event Action<string[]>? ArgumentsReceived;

    public SingleInstance()
    {
        if (OsInfo.IsWindows)
        {
            _mutex = new Mutex(true, "Mandarin.FileConverter.SingleInstance." + Environment.UserName, out var created);
            IsFirst = created;
            return;
        }
        // a named mutex on Linux lives only within one session (setsid), and file managers start every program in a
        // session of its own - so each "Open with" became a new copy. A locked file (flock) is seen by every process.
        try
        {
            _lock = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            IsFirst = true;
        }
        catch (IOException) { IsFirst = false; }
        catch (UnauthorizedAccessException) { IsFirst = false; }
    }

    static string LockPath =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run && Directory.Exists(run) ? run : Path.GetTempPath(),
            "mandarin-" + Environment.UserName + ".lock");

    public void StartServer()
    {
        // the first copy owns the socket: one left by a crashed copy would keep the server from starting
        if (!OsInfo.IsWindows)
            try { File.Delete(Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + PipeName)); } catch { /* not there */ }
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
        // the first copy may still be starting: give it time to open the pipe
        var until = Environment.TickCount64 + 10000;
        while (true)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(2000);
                using var writer = new StreamWriter(client, Encoding.UTF8);
                writer.Write(string.Join('\n', args.Length == 0 ? new[] { "--show" } : args));
                return true;
            }
            catch
            {
                if (Environment.TickCount64 > until) return false;
                Thread.Sleep(250);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_mutex != null)
        {
            if (IsFirst) try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
            _mutex.Dispose();
        }
        _lock?.Dispose();
    }
}
