using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mandarin.Core;
using Microsoft.Win32;

namespace Mandarin.Interop;

/// <summary>File thumbnails and icons from the Windows shell (as in Explorer).</summary>
public static class ShellThumbs
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4, SIIGBF_THUMBNAILONLY = 0x8;

    static readonly Dictionary<string, ImageSource?> Cache = new();

    public static ImageSource? Get(string path, int size = 96, bool iconOnly = false)
    {
        var key = $"{path}|{size}|{iconOnly}";
        lock (Cache) if (Cache.TryGetValue(key, out var c)) return c;
        ImageSource? result = null;
        try
        {
            SHCreateItemFromParsingName(Path.GetFullPath(path), IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            var hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_BIGGERSIZEOK | (iconOnly ? SIIGBF_ICONONLY : 0), out var hbmp);
            if (hr == 0 && hbmp != IntPtr.Zero)
            {
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    result = src;
                }
                finally { Native.DeleteObject(hbmp); }
            }
            Marshal.ReleaseComObject(factory);
        }
        catch
        {
            // no thumbnail - the category icon is shown instead
        }
        lock (Cache)
        {
            if (Cache.Count > 400) Cache.Clear();
            Cache[key] = result;
        }
        return result;
    }
}

/// <summary>The Windows Share sheet (Nearby sharing, mail, apps) - the AirDrop counterpart.</summary>
public static class ShareUi
{
    public static void Share(IReadOnlyList<string> paths)
    {
        var host = new Window
        {
            Width = 1, Height = 1, WindowStyle = WindowStyle.None, AllowsTransparency = true, Opacity = 0.01,
            ShowInTaskbar = false, Topmost = true, Title = L.T("Mandarin - share")
        };
        var (x, y) = Native.CursorPos();
        var (_, scale) = Native.MonitorAt(x, y);
        host.Left = x / scale; host.Top = y / scale;
        host.Show();
        var hwnd = new WindowInteropHelper(host).Handle;
        try
        {
            var dtm = Windows.ApplicationModel.DataTransfer.DataTransferManagerInterop.GetForWindow(hwnd);
            dtm.DataRequested += async (s, e) =>
            {
                var deferral = e.Request.GetDeferral();
                try
                {
                    var items = new List<Windows.Storage.IStorageItem>();
                    foreach (var p in paths)
                    {
                        if (Directory.Exists(p)) items.Add(await Windows.Storage.StorageFolder.GetFolderFromPathAsync(p));
                        else items.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(p));
                    }
                    e.Request.Data.Properties.Title = paths.Count == 1 ? Path.GetFileName(paths[0]) : L.F("Files: {0}", paths.Count);
                    e.Request.Data.Properties.Description = L.T("Sent from Mandarin");
                    e.Request.Data.SetStorageItems(items);
                }
                catch (Exception ex)
                {
                    e.Request.FailWithDisplayText(L.T("Could not prepare the files: ") + ex.Message);
                }
                finally { deferral.Complete(); }
            };
            dtm.TargetApplicationChosen += (_, _) => host.Dispatcher.BeginInvoke(host.Close);
            Windows.ApplicationModel.DataTransfer.DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
            // the host window is closed a bit later, once the dialog is open
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            timer.Tick += (_, _) => { timer.Stop(); host.Close(); };
            timer.Start();
        }
        catch (Exception ex)
        {
            host.Close();
            Log.Error("Share UI", ex);
            throw new UserFacingException(L.T("The Share window is unavailable: ") + ex.Message);
        }
    }
}

/// <summary>Single app instance; a second launch hands its arguments to the first one.</summary>
public sealed class SingleInstance : IDisposable
{
    const string PipeName = "Mandarin.FileConverter.Pipe";
    readonly Mutex _mutex;
    public bool IsFirst { get; }
    readonly CancellationTokenSource _cts = new();

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
                    await using var server = new NamedPipeServerStream(PipeName + Environment.UserName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync();
                    var args = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    Application.Current?.Dispatcher.BeginInvoke(() => ArgumentsReceived?.Invoke(args));
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
            using var client = new NamedPipeClientStream(".", PipeName + Environment.UserName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
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
        if (IsFirst) try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}

/// <summary>Start with Windows and the "Send to" entry in the Explorer context menu.</summary>
public static class Integration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mandarin.exe");

    public static void SetAutostart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable) key.SetValue("Mandarin", $"\"{ExePath}\" --background");
            else key.DeleteValue("Mandarin", false);
        }
        catch (Exception ex) { Log.Error("Autostart", ex); }
    }

    static string SendToLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Mandarin - convert.lnk");

    public static bool SendToInstalled => File.Exists(SendToLink);

    public static void SetSendTo(bool enable)
    {
        try
        {
            if (!enable) { if (File.Exists(SendToLink)) File.Delete(SendToLink); return; }
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic lnk = shell.CreateShortcut(SendToLink);
            lnk.TargetPath = ExePath;
            lnk.Arguments = "--wheel";
            lnk.IconLocation = ExePath + ",0";
            lnk.Description = L.T("Open the Mandarin conversion wheel");
            lnk.Save();
            Marshal.FinalReleaseComObject(lnk);
            Marshal.FinalReleaseComObject(shell);
        }
        catch (Exception ex) { Log.Error("SendTo", ex); }
    }

    public static void RevealInExplorer(string path)
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer, $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex) { Log.Error("Reveal", ex); }
    }

    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Open", ex); }
    }
}
