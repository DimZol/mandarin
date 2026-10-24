using System.Diagnostics;
using System.Text;
using Mandarin.Core;

namespace Mandarin.Engines;

/// <summary>
/// Printing HTML to PDF without WebView2: a Chromium-based browser in headless mode (Chromium, Chrome, Edge, Brave,
/// Vivaldi) with the network switched off, otherwise LibreOffice.
/// </summary>
public static partial class HtmlEngine
{
    static readonly SemaphoreSlim PrintGate = new(1, 1);

    /// <summary>A Chromium-based browser that can print to PDF, if one is installed.</summary>
    public static string? ChromiumPath
    {
        get
        {
            var candidates = new List<string>();
            if (OsInfo.IsWindows)
            {
                foreach (var root in new[]
                         {
                             Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                             Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                         })
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
                    candidates.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
                    candidates.Add(Path.Combine(root, "Chromium", "Application", "chrome.exe"));
                    candidates.Add(Path.Combine(root, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
                    candidates.Add(Path.Combine(root, "Vivaldi", "Application", "vivaldi.exe"));
                }
            }
            else
            {
                foreach (var dir in new[] { "/usr/bin", "/usr/local/bin", "/snap/bin", "/opt/google/chrome", "/opt/microsoft/msedge", "/opt/brave.com/brave" })
                    foreach (var name in new[] { "chromium", "chromium-browser", "google-chrome", "google-chrome-stable", "chrome", "microsoft-edge", "microsoft-edge-stable", "msedge", "brave-browser", "brave", "vivaldi" })
                        candidates.Add(Path.Combine(dir, name));
            }
            return candidates.FirstOrDefault(File.Exists);
        }
    }

    public static async Task HtmlToPdfAsync(string html, string output, bool landscape = false, CancellationToken ct = default)
    {
        var tempDir = OutputPaths.TempDir();
        try
        {
            // the page size and orientation come from the document itself
            var page = landscape ? "@page { size: A4 landscape; margin: 15mm; }" : "@page { size: A4; margin: 15mm; }";
            html = html.Contains("</head>", StringComparison.OrdinalIgnoreCase)
                ? html.Replace("</head>", $"<style>{page}</style></head>")
                : $"<style>{page}</style>{html}";
            var htmlFile = Path.Combine(tempDir, "document.html");
            await File.WriteAllTextAsync(htmlFile, html, new UTF8Encoding(false), ct);
            await PrintGate.WaitAsync(ct);
            try
            {
                if (ChromiumPath is { } browser && await ChromiumPrintAsync(browser, htmlFile, Path.Combine(tempDir, "out.pdf"), tempDir, ct))
                {
                    File.Move(Path.Combine(tempDir, "out.pdf"), output);
                    return;
                }
                if (OfficeEngine.LibreInstalled)
                {
                    await OfficeEngine.LibreSaveAsAsync(htmlFile, output, "pdf:writer_web_pdf_Export", ct);
                    return;
                }
            }
            finally
            {
                PrintGate.Release();
            }
            throw new UserFacingException(L.T("Creating a PDF from a document needs LibreOffice or a Chromium-based browser (Chromium, Chrome, Edge)."));
        }
        finally
        {
            OutputPaths.TryDelete(tempDir);
        }
    }

    static async Task<bool> ChromiumPrintAsync(string browser, string htmlFile, string pdf, string tempDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[]
                 {
                     "--headless", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-sync",
                     "--disable-background-networking", "--disable-component-update", "--no-pings", "--dns-prefetch-disable",
                     // offline: every host name resolves to nothing, so no request leaves the computer
                     "--host-resolver-rules=MAP * ~NOTFOUND", "--proxy-server=127.0.0.1:9", "--disable-javascript",
                     $"--user-data-dir={Path.Combine(tempDir, "profile")}", "--no-pdf-header-footer", "--print-to-pdf-no-header",
                     $"--print-to-pdf={pdf}", new Uri(htmlFile).AbsoluteUri
                 })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            try
            {
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { /* already exited */ }
                ct.ThrowIfCancellationRequested();
                return false;
            }
            if (File.Exists(pdf) && new FileInfo(pdf).Length > 0) return true;
            Log.Error("Chromium print: " + (await stderr + "\n" + await stdout).Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Chromium print", ex);
        }
        return false;
    }

    public static void Shutdown() { /* no resident processes */ }
}
