using System.IO;
using System.Runtime.InteropServices;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using HtmlAgilityPack;
using Markdig;
using Mandarin.Core;
using Microsoft.Web.WebView2.Core;

namespace Mandarin.Engines;

/// <summary>HTML as the shared intermediate document format + printing to PDF with the built-in WebView2.</summary>
public static class HtmlEngine
{
    public const string Css = """
        @page { size: A4; margin: 18mm 16mm; }
        html { -webkit-text-size-adjust: 100%; }
        body { font-family: "Segoe UI", "Segoe UI Variable Text", Arial, sans-serif; font-size: 11pt; line-height: 1.55;
               color: #1f1f1f; max-width: 820px; margin: 0 auto; padding: 0 8px; word-wrap: break-word; }
        h1, h2, h3, h4, h5, h6 { font-family: "Segoe UI Variable Display", "Segoe UI", Arial, sans-serif; line-height: 1.25;
               margin: 1.2em 0 .5em; page-break-after: avoid; }
        h1 { font-size: 1.9em; } h2 { font-size: 1.5em; } h3 { font-size: 1.25em; } h4 { font-size: 1.1em; }
        p { margin: 0 0 .8em; }
        img { max-width: 100%; height: auto; }
        table { border-collapse: collapse; margin: 1em 0; width: auto; max-width: 100%; }
        td, th { border: 1px solid #c8c8c8; padding: 4px 8px; vertical-align: top; }
        th { background: #f3f3f3; text-align: left; }
        pre, code { font-family: "Cascadia Mono", Consolas, monospace; font-size: .92em; }
        pre { background: #f6f6f6; padding: 10px 12px; border-radius: 6px; overflow-x: auto; white-space: pre-wrap; }
        code { background: #f3f3f3; padding: 1px 4px; border-radius: 4px; }
        pre code { background: none; padding: 0; }
        blockquote { border-left: 3px solid #f07a2a; margin: 1em 0; padding: .2em 1em; color: #555; }
        a { color: #c2410c; }
        hr { border: 0; border-top: 1px solid #ddd; margin: 1.5em 0; }
        .sheet-title { margin-top: 0; }
        """;

    public static string WrapDocument(string title, string bodyHtml, string? extraCss = null) =>
        $"""
        <!DOCTYPE html>
        <html lang="ru">
        <head>
        <meta charset="utf-8"/>
        <meta name="viewport" content="width=device-width, initial-scale=1"/>
        <title>{WebUtility.HtmlEncode(title)}</title>
        <style>{Css}{extraCss}</style>
        </head>
        <body>
        {bodyHtml}
        </body>
        </html>
        """;

    // ---------- Sources ----------

    public static string MarkdownToHtml(string markdown, string title)
    {
        var pipeline = new Markdig.MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        return WrapDocument(title, Markdig.Markdown.ToHtml(markdown, pipeline));
    }

    public static string TextToHtml(string text, string title)
    {
        var paragraphs = Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n");
        var sb = new StringBuilder();
        foreach (var p in paragraphs)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            sb.Append("<p>").Append(WebUtility.HtmlEncode(p.Trim()).Replace("\n", "<br/>\n")).Append("</p>\n");
        }
        return WrapDocument(title, sb.ToString());
    }

    /// <summary>
    /// Inlines local images as data URIs so the HTML is self-contained.
    /// Relative paths inside the document folder only: foreign HTML must not pull arbitrary
    /// files from the disk (C:\Users\…\passport.jpg) into the result.
    /// </summary>
    public static string InlineLocalImages(string html, string baseDir)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var imgs = doc.DocumentNode.SelectNodes("//img[@src]");
        if (imgs == null) return html;
        var root = Path.GetFullPath(baseDir).TrimEnd('\\') + "\\";
        foreach (var img in imgs)
        {
            var src = WebUtility.HtmlDecode(img.GetAttributeValue("src", ""));
            if (src.StartsWith("data:") || src.Contains(":")) continue; // data:, http:, file:, C:\…
            try
            {
                var path = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(src).Replace('/', '\\').TrimStart('\\')));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
                img.SetAttributeValue("src", $"data:{MimeFor(path)};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}");
            }
            catch { /* broken link - keep it */ }
        }
        return doc.DocumentNode.OuterHtml;
    }

    public static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/jpeg"
    };

    public static string TitleOf(string html, string fallback)
    {
        var m = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var t = m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
        return t.Length > 0 ? t : fallback;
    }

    // ---------- Targets ----------

    public static string HtmlToText(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        var sb = new StringBuilder();
        Walk(body, sb);
        var text = Regex.Replace(sb.ToString(), @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim() + Environment.NewLine;

        static void Walk(HtmlNode node, StringBuilder sb)
        {
            foreach (var child in node.ChildNodes)
            {
                switch (child.NodeType)
                {
                    case HtmlNodeType.Text:
                        var t = WebUtility.HtmlDecode(child.InnerText);
                        sb.Append(Regex.Replace(t, @"\s+", " "));
                        break;
                    case HtmlNodeType.Element:
                        var name = child.Name.ToLowerInvariant();
                        if (name is "script" or "style" or "head" or "title") continue;
                        if (name == "br") { sb.Append('\n'); continue; }
                        if (name == "li") sb.Append("\n• ");
                        if (name is "td" or "th") sb.Append('\t');
                        var block = name is "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "ul" or "ol" or "table"
                            or "tr" or "blockquote" or "pre" or "section" or "article" or "header" or "footer" or "hr";
                        if (block) sb.Append('\n');
                        if (name == "pre") sb.Append(WebUtility.HtmlDecode(child.InnerText));
                        else Walk(child, sb);
                        if (block) sb.Append(name is "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "pre" ? "\n\n" : "\n");
                        break;
                }
            }
        }
    }

    /// <summary>HTML → Markdown. Embedded images are moved into a sibling folder.</summary>
    public static string HtmlToMarkdown(string html, string outputMdPath)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var imgs = doc.DocumentNode.SelectNodes("//img[starts-with(@src,'data:')]");
        if (imgs != null && imgs.Count > 0)
        {
            // always a new folder: never write images into an existing user folder
            var folder = OutputPaths.UniqueFolder(Path.GetDirectoryName(outputMdPath)!, Path.GetFileNameWithoutExtension(outputMdPath) + L.T(" - images"));
            var folderName = Path.GetFileName(folder);
            Directory.CreateDirectory(folder);
            var i = 0;
            foreach (var img in imgs)
            {
                var src = img.GetAttributeValue("src", "");
                var m = Regex.Match(src, @"^data:image/(\w+)[^;]*;base64,(.+)$", RegexOptions.Singleline);
                if (!m.Success) continue;
                var ext = m.Groups[1].Value switch { "jpeg" => "jpg", "svg+xml" => "svg", var e => e };
                var name = $"image{++i:000}.{ext}";
                File.WriteAllBytes(Path.Combine(folder, name), Convert.FromBase64String(m.Groups[2].Value));
                img.SetAttributeValue("src", $"{Uri.EscapeDataString(folderName)}/{name}");
            }
        }
        var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        doc.DocumentNode.SelectNodes("//style|//script")?.ToList().ForEach(n => n.Remove());
        var config = new ReverseMarkdown.Config { GithubFlavored = true };
        config.Tags.Unknown = ReverseMarkdown.Config.UnknownTagsOption.Bypass;
        config.Formatting.RemoveComments = true;
        config.Links.SmartHref = true;
        var converter = new ReverseMarkdown.Converter(config);
        var md = converter.Convert(body.InnerHtml);
        return Regex.Replace(md, @"\n{3,}", "\n\n").Trim() + "\n";
    }

    /// <summary>HTML → DOCX with HtmlToOpenXml (no Office).</summary>
    public static async Task HtmlToDocxAsync(string html, string output, string title)
    {
        using var mem = new MemoryStream();
        using (var package = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(mem, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = package.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new DocumentFormat.OpenXml.Wordprocessing.Body());
            var converter = new HtmlToOpenXml.HtmlConverter(main);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            // offline: external images are not downloaded (HtmlToOpenXml fetches them from the network by default)
            foreach (var img in doc.DocumentNode.SelectNodes("//img[@src]") ?? Enumerable.Empty<HtmlNode>())
                if (!img.GetAttributeValue("src", "").StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    img.Remove();
            var body = doc.DocumentNode.SelectSingleNode("//body")?.InnerHtml ?? doc.DocumentNode.OuterHtml;
            await converter.ParseBody(body);
            package.PackageProperties.Title = title;
            package.PackageProperties.Creator = Environment.UserName;
            main.Document.Save();
        }
        await File.WriteAllBytesAsync(output, mem.ToArray());
    }

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

