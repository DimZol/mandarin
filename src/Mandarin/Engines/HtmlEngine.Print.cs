using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Mandarin.Core;
using Microsoft.Web.WebView2.Core;

namespace Mandarin.Engines;

/// <summary>Printing HTML to PDF with the built-in WebView2 (Windows 10/11).</summary>
public static partial class HtmlEngine
{
    // ---------- Printing to PDF (WebView2) ----------

    static readonly SemaphoreSlim PrintGate = new(1, 1);
    static CoreWebView2Environment? _env;
    static Window? _host;
    static CoreWebView2Controller? _controller;
    static string? _currentDocument;

    public static async Task HtmlToPdfAsync(string html, string output, bool landscape = false, CancellationToken ct = default)
    {
        var tempDir = OutputPaths.TempDir();
        var htmlFile = Path.Combine(tempDir, "document.html");
        await File.WriteAllTextAsync(htmlFile, html, new UTF8Encoding(false), ct);
        try
        {
            await PrintGate.WaitAsync(ct);
            try
            {
                var op = Application.Current.Dispatcher.InvokeAsync(() => PrintOnUiAsync(htmlFile, output, landscape));
                await await op.Task;
            }
            finally
            {
                PrintGate.Release();
            }
        }
        finally
        {
            OutputPaths.TryDelete(tempDir);
        }
    }

    static async Task PrintOnUiAsync(string htmlFile, string output, bool landscape)
    {
        if (_controller == null)
        {
            _host = new Window
            {
                Width = 1000, Height = 1400, Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Title = "Mandarin print host"
            };
            _host.Show();
            var hwnd = new WindowInteropHelper(_host).Handle;
            // no background network traffic: DNS hints, pings, component update checks
            const string args = "--dns-prefetch-disable --no-pings --disable-background-networking --disable-component-update";
            try
            {
                try
                {
                    _env ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppSettings.Folder, "WebView2"), new CoreWebView2EnvironmentOptions(args));
                    _controller = await _env.CreateCoreWebView2ControllerAsync(hwnd);
                }
                catch (COMException ex) when (ex.HResult == unchecked((int)0x8007139F))
                {
                    // another WebView2 process holds the data folder with different options - use our own
                    Log.Error("WebView2: the data folder is busy, using a fallback one", ex);
                    _env = await CoreWebView2Environment.CreateAsync(null,
                        Path.Combine(AppSettings.Folder, "WebView2-" + Environment.ProcessId), new CoreWebView2EnvironmentOptions(args));
                    _controller = await _env.CreateCoreWebView2ControllerAsync(hwnd);
                }
            }
            catch (WebView2RuntimeNotFoundException)
            {
                Shutdown();
                throw new UserFacingException(L.T("Microsoft Edge WebView2 Runtime was not found - it is required to create PDFs from documents."));
            }
            catch
            {
                Shutdown();
                throw;
            }
            var core = _controller.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsScriptEnabled = false;
            // offline: external resources are never loaded, files go nowhere
            core.AddWebResourceRequestedFilter("http://*", CoreWebView2WebResourceContext.All);
            core.AddWebResourceRequestedFilter("https://*", CoreWebView2WebResourceContext.All);
            // local files: only the document being printed (images are already inlined as data URIs)
            core.AddWebResourceRequestedFilter("file://*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (_currentDocument != null && string.Equals(e.Request.Uri, _currentDocument, StringComparison.OrdinalIgnoreCase)) return;
                e.Response = _env.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
        }

        var wv = _controller.CoreWebView2;
        var tcs = new TaskCompletionSource<bool>();
        void Done(object? s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult(e.IsSuccess);
        wv.NavigationCompleted += Done;
        _currentDocument = new Uri(htmlFile).AbsoluteUri;
        wv.Navigate(_currentDocument);
        var ok = await tcs.Task;
        wv.NavigationCompleted -= Done;
        if (!ok) throw new UserFacingException(L.T("Could not prepare the document for printing"));

        var settings = _env!.CreatePrintSettings();
        settings.Orientation = landscape ? CoreWebView2PrintOrientation.Landscape : CoreWebView2PrintOrientation.Portrait;
        settings.ShouldPrintBackgrounds = true;
        settings.ShouldPrintHeaderAndFooter = false;
        settings.PageWidth = 8.27; settings.PageHeight = 11.69; // A4
        settings.MarginTop = settings.MarginBottom = 0.6;
        settings.MarginLeft = settings.MarginRight = 0.6;
        settings.ScaleFactor = 1.0;
        var printed = await wv.PrintToPdfAsync(output, settings);
        wv.Navigate("about:blank");
        ScheduleRelease();
        if (!printed) throw new UserFacingException(L.T("Could not save the PDF"));
    }

    static System.Windows.Threading.DispatcherTimer? _idle;

    /// <summary>WebView2 processes use memory - close them after a minute of idling.</summary>
    static void ScheduleRelease()
    {
        _idle ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _idle.Stop();
        _idle.Tick -= OnIdle;
        _idle.Tick += OnIdle;
        _idle.Start();
    }

    static async void OnIdle(object? sender, EventArgs e)
    {
        _idle?.Stop();
        if (PrintGate.CurrentCount == 0) { ScheduleRelease(); return; } // printing in progress
        await PrintGate.WaitAsync();
        try { Shutdown(); }
        finally { PrintGate.Release(); }
    }

    public static void Shutdown()
    {
        try
        {
            _controller?.Close();
            _host?.Close();
        }
        catch { /* already closed */ }
        _controller = null;
        _host = null;
        _env = null;
    }
}
