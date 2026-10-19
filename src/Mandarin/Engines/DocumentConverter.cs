using System.IO;
using System.Net;
using System.Text;
using System.Windows.Documents;
using Mandarin.Core;

namespace Mandarin.Engines;

/// <summary>Documents, EPUB, PDF and presentations: picks the best conversion path.</summary>
public static class DocumentConverter
{
    // formats Word and LibreOffice open themselves (HTML is deliberately excluded: Word downloads external resources)
    static readonly HashSet<string> WordInputs = new() { "docx", "doc", "docm", "odt", "rtf" };

    static Action<string> Detail(JobContext ctx) => engine => ctx.Detail(L.T("Converting with ") + engine);

    // ---------- Document → HTML (shared intermediate format) ----------

    public static async Task<string> ToHtmlAsync(string input, JobContext ctx)
    {
        var ext = FileKinds.Ext(input);
        var title = FileKinds.BaseName(input);
        switch (ext)
        {
            case "md" or "markdown":
                var md = SheetEngine.ReadTextAutoEncoding(input);
                return HtmlEngine.InlineLocalImages(HtmlEngine.MarkdownToHtml(md, title), Path.GetDirectoryName(input)!);
            case "txt":
                return HtmlEngine.TextToHtml(SheetEngine.ReadTextAutoEncoding(input), title);
            case "html" or "htm":
                return HtmlEngine.InlineLocalImages(SheetEngine.ReadTextAutoEncoding(input), Path.GetDirectoryName(input)!);
            case "docx" or "docm":
                return await Task.Run(() => DocxReader.ToHtml(input), ctx.Token);
            case "doc" or "odt" or "rtf" when OfficeEngine.Native(OfficeApp.Word):
                return await ViaNativeDocxAsync(input, ctx);
            case "odt":
                return await Task.Run(() => DocxReader.OdtToHtml(input), ctx.Token);
            case "rtf":
                return await RtfToHtmlAsync(input);
            case "doc":
                throw new UserFacingException(OfficeEngine.NeedEngineHint(L.T("Legacy DOC documents")));
            case "epub":
                return await Task.Run(() => EpubEngine.ToHtml(input), ctx.Token);
            case "pdf":
                var blocks = await PdfEngine.ExtractBlocksAsync(input, ctx);
                return PdfEngine.BlocksToHtml(blocks, title);
            default:
                throw new UserFacingException(L.F("The {0} format is not supported", ext.ToUpperInvariant()));
        }
    }

    /// <summary>DOC/ODT/RTF → DOCX with the selected engine (Office or LibreOffice) → our clean HTML.</summary>
    static async Task<string> ViaNativeDocxAsync(string input, JobContext ctx)
    {
        var temp = OutputPaths.TempDir();
        try
        {
            var docx = Path.Combine(temp, "doc.docx");
            await OfficeEngine.TrySaveAsAsync(OfficeApp.Word, input, docx, "docx", Detail(ctx), ctx.Token);
            return DocxReader.ToHtml(docx);
        }
        finally { OutputPaths.TryDelete(temp); }
    }

    /// <summary>RTF without Office: load into a WPF FlowDocument and walk its blocks.</summary>
    static Task<string> RtfToHtmlAsync(string input)
    {
        var tcs = new TaskCompletionSource<string>();
        var t = new Thread(() =>
        {
            try
            {
                var doc = new FlowDocument();
                var range = new TextRange(doc.ContentStart, doc.ContentEnd);
                using (var fs = File.OpenRead(input)) range.Load(fs, System.Windows.DataFormats.Rtf);
                var sb = new StringBuilder();
                foreach (var b in doc.Blocks) RenderBlock(b, sb);
                tcs.SetResult(HtmlEngine.WrapDocument(FileKinds.BaseName(input), sb.ToString()));
            }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;

        static void RenderBlock(Block b, StringBuilder sb)
        {
            switch (b)
            {
                case Paragraph p:
                    var inner = Inlines(p.Inlines);
                    var size = p.FontSize;
                    var bold = p.FontWeight.ToOpenTypeWeight() >= 600;
                    if (size >= 20 || (bold && inner.Length < 80 && size >= 15)) sb.Append($"<h{(size >= 24 ? 1 : 2)}>{inner}</h{(size >= 24 ? 1 : 2)}>\n");
                    else sb.Append($"<p>{inner}</p>\n");
                    break;
                case List l:
                    var tag = l.MarkerStyle is System.Windows.TextMarkerStyle.Decimal or System.Windows.TextMarkerStyle.LowerLatin
                        or System.Windows.TextMarkerStyle.UpperLatin or System.Windows.TextMarkerStyle.LowerRoman or System.Windows.TextMarkerStyle.UpperRoman ? "ol" : "ul";
                    sb.Append($"<{tag}>\n");
                    foreach (var li in l.ListItems)
                    {
                        sb.Append("<li>");
                        var inner2 = new StringBuilder();
                        foreach (var child in li.Blocks)
                            if (child is Paragraph lp) inner2.Append(Inlines(lp.Inlines)); else RenderBlock(child, inner2);
                        sb.Append(inner2).Append("</li>\n");
                    }
                    sb.Append($"</{tag}>\n");
                    break;
                case Table t:
                    sb.Append("<table>\n");
                    foreach (var g in t.RowGroups)
                        foreach (var r in g.Rows)
                        {
                            sb.Append("<tr>");
                            foreach (var c in r.Cells)
                            {
                                var cell = new StringBuilder();
                                foreach (var cb in c.Blocks) if (cb is Paragraph cp) cell.Append(Inlines(cp.Inlines)).Append("<br/>");
                                sb.Append("<td>").Append(cell.ToString().TrimEnd("<br/>".ToCharArray())).Append("</td>");
                            }
                            sb.Append("</tr>\n");
                        }
                    sb.Append("</table>\n");
                    break;
                case Section s:
                    foreach (var child in s.Blocks) RenderBlock(child, sb);
                    break;
            }
        }

        static string Inlines(InlineCollection inlines)
        {
            var sb = new StringBuilder();
            foreach (var i in inlines)
            {
                switch (i)
                {
                    case Run r:
                        var text = WebUtility.HtmlEncode(r.Text);
                        if (r.FontWeight.ToOpenTypeWeight() >= 600) text = $"<strong>{text}</strong>";
                        if (r.FontStyle == System.Windows.FontStyles.Italic) text = $"<em>{text}</em>";
                        if (r.TextDecorations?.Count > 0) text = $"<u>{text}</u>";
                        sb.Append(text);
                        break;
                    case LineBreak: sb.Append("<br/>"); break;
                    case Hyperlink h:
                        sb.Append($"<a href=\"{WebUtility.HtmlEncode(h.NavigateUri?.ToString() ?? "")}\">{Inlines(h.Inlines)}</a>");
                        break;
                    case Span s: sb.Append(Inlines(s.Inlines)); break;
                }
            }
            return sb.ToString();
        }
    }

    // ---------- Conversion ----------

    public static async Task ConvertAsync(string input, string target, JobContext ctx)
    {
        var src = FileKinds.Ext(input);
        var title = FileKinds.BaseName(input);

        switch (target)
        {
            case "pdf":
            {
                var output = OutputPaths.File(input, "pdf");
                await ToPdfAsync(input, output, ctx);
                ctx.AddOutput(output);
                return;
            }
            case "png" or "jpg":
            {
                var temp = OutputPaths.TempDir();
                try
                {
                    var pdf = Path.Combine(temp, OutputPaths.Sanitize(title) + ".pdf");
                    if (src == "pdf") File.Copy(input, pdf);
                    else await ToPdfAsync(input, pdf, ctx);
                    // images must land next to the original, not in the temporary folder
                    await PdfPagesNextToOriginalAsync(input, pdf, target, ctx);
                }
                finally { OutputPaths.TryDelete(temp); }
                return;
            }
            case "docx":
            {
                var output = OutputPaths.File(input, "docx");
                // DOC/ODT/RTF use the native engine; everything else (and the "Built-in only" mode) goes through our HTML
                var native = WordInputs.Contains(src) && await OfficeEngine.TrySaveAsAsync(OfficeApp.Word, input, output, "docx", Detail(ctx), ctx.Token);
                if (!native)
                {
                    var html = await ToHtmlAsync(input, ctx);
                    await HtmlEngine.HtmlToDocxAsync(html, output, HtmlEngine.TitleOf(html, title));
                }
                ctx.AddOutput(output);
                return;
            }
            case "odt":
            {
                var output = OutputPaths.File(input, "odt");
                await ToOdtAsync(input, output, ctx);
                ctx.AddOutput(output);
                return;
            }
            case "rtf":
            {
                var output = OutputPaths.File(input, "rtf");
                await ViaNativeFromAnyAsync(input, output, "rtf", ctx);
                ctx.AddOutput(output);
                return;
            }
            case "html":
            {
                var output = OutputPaths.File(input, "html");
                var html = await ToHtmlAsync(input, ctx);
                await File.WriteAllTextAsync(output, html, new UTF8Encoding(false), ctx.Token);
                ctx.AddOutput(output);
                return;
            }
            case "md":
            {
                var output = OutputPaths.File(input, "md");
                string md;
                if (src == "pdf")
                {
                    var blocks = await PdfEngine.ExtractBlocksAsync(input, ctx);
                    var images = new List<(string name, byte[] data)>();
                    var folder = OutputPaths.UniqueFolder(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + L.T(" - images"));
                    var folderName = Path.GetFileName(folder);
                    md = PdfEngine.BlocksToMarkdown(blocks, folderName, images);
                    if (images.Count > 0)
                    {
                        Directory.CreateDirectory(folder);
                        foreach (var (name, data) in images) await File.WriteAllBytesAsync(Path.Combine(folder, name), data, ctx.Token);
                    }
                }
                else md = HtmlEngine.HtmlToMarkdown(await ToHtmlAsync(input, ctx), output);
                await File.WriteAllTextAsync(output, md, new UTF8Encoding(false), ctx.Token);
                ctx.AddOutput(output);
                return;
            }
            case "txt":
            {
                var output = OutputPaths.File(input, "txt");
                string text = src == "pdf"
                    ? PdfEngine.BlocksToText(await PdfEngine.ExtractBlocksAsync(input, ctx, includeImages: false))
                    : HtmlEngine.HtmlToText(await ToHtmlAsync(input, ctx));
                await File.WriteAllTextAsync(output, text, new UTF8Encoding(false), ctx.Token);
                ctx.AddOutput(output);
                return;
            }
            case "epub":
            {
                var output = OutputPaths.File(input, "epub");
                if (src == "pdf" && AppSettings.Current.PdfToEpub == PdfEpubMode.KeepPageLayout)
                    await EpubEngine.FromPdfPagesAsync(input, output, title, ctx);
                else
                {
                    var html = await ToHtmlAsync(input, ctx);
                    await Task.Run(() => EpubEngine.FromHtml(html, HtmlEngine.TitleOf(html, title), Environment.UserName, output), ctx.Token);
                }
                ctx.AddOutput(output);
                return;
            }
            case "pptx" when src == "pdf":
            {
                var output = OutputPaths.File(input, "pptx");
                await PdfToPptxAsync(input, output, ctx);
                ctx.AddOutput(output);
                return;
            }
            case "xlsx" when src == "pdf":
            {
                var output = OutputPaths.File(input, "xlsx");
                var grid = await Task.Run(() => PdfEngine.ExtractGrid(input, ctx.Token), ctx.Token);
                var sheets = grid.Select(g =>
                {
                    var s = new Sheet { Name = g.name };
                    foreach (var row in g.rows) s.Rows.Add(row.Select(c => (object?)ParseCell(c)).ToList());
                    return s;
                }).ToList();
                await Task.Run(() => SheetEngine.WriteXlsx(sheets, output), ctx.Token);
                ctx.AddOutput(output);
                return;
            }
            default:
                throw new UserFacingException(L.F("Converting {0} → {1} is not supported", src.ToUpperInvariant(), target.ToUpperInvariant()));
        }
    }

    static object? ParseCell(string s)
    {
        var t = s.Replace(" ", "").Replace(" ", "");
        if (t.Length > 0 && t.Length < 18 && System.Text.RegularExpressions.Regex.IsMatch(t, @"^-?\d+([.,]\d+)?$"))
            if (double.TryParse(t.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                return d;
        return s;
    }

    static async Task PdfPagesNextToOriginalAsync(string original, string pdf, string ext, JobContext ctx)
    {
        var doc = await WinRt.OpenPdfAsync(pdf);
        var count = doc.PageCount;
        var dpi = Math.Min(AppSettings.Current.PdfImageDpi, 200);
        var folder = count > 1 ? OutputPaths.Folder(original, L.T(" - pages")) : null;
        if (folder != null) Directory.CreateDirectory(folder);
        for (uint i = 0; i < count; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            ctx.Report(0.5 + 0.5 * i / count);
            var png = await WinRt.RenderPageAsync(doc, i, dpi);
            var output = folder == null ? OutputPaths.File(original, ext) : Path.Combine(folder, $"{FileKinds.BaseName(original)} — {i + 1:000}.{ext}");
            using var mi = new ImageMagick.MagickImage(png);
            mi.Quality = (uint)AppSettings.Current.JpegQuality;
            mi.Write(output, ext == "png" ? ImageMagick.MagickFormat.Png : ImageMagick.MagickFormat.Jpeg);
            if (folder == null) ctx.AddOutput(output);
        }
        if (folder != null) ctx.AddOutput(folder);
    }

    public static async Task ToPdfAsync(string input, string output, JobContext ctx)
    {
        var src = FileKinds.Ext(input);
        if (FileKinds.Classify(input) == FileCategory.Slides)
        {
            await SlidesToPdfAsync(input, output, ctx);
            return;
        }
        if (FileKinds.Classify(input) == FileCategory.Spreadsheet)
        {
            if (src != "csv" && src != "tsv" && await OfficeEngine.TrySaveAsAsync(OfficeApp.Excel, input, output, "pdf", Detail(ctx), ctx.Token)) return;
            var sheets = SheetEngine.Read(input);
            await HtmlEngine.HtmlToPdfAsync(SheetEngine.ToHtml(sheets, FileKinds.BaseName(input)), output, true, ctx.Token);
            return;
        }
        // Word/ODT/RTF documents use the native engine (exact layout), everything else goes through HTML and WebView2
        if (WordInputs.Contains(src) && await OfficeEngine.TrySaveAsAsync(OfficeApp.Word, input, output, "pdf", Detail(ctx), ctx.Token)) return;
        if (src == "doc") throw new UserFacingException(OfficeEngine.NeedEngineHint(L.T("Legacy DOC documents")));
        var html = await ToHtmlAsync(input, ctx);
        ctx.Detail(L.T("Creating PDF…"));
        await HtmlEngine.HtmlToPdfAsync(html, output, false, ctx.Token);
    }

    static Task ToOdtAsync(string input, string output, JobContext ctx) => ViaNativeFromAnyAsync(input, output, "odt", ctx);

    /// <summary>
    /// Any document → ODT/RTF with the selected engine. Whatever Word/LibreOffice cannot open themselves
    /// (Markdown, HTML, EPUB, PDF) is turned into DOCX by our own code first.
    /// </summary>
    static async Task ViaNativeFromAnyAsync(string input, string output, string target, JobContext ctx)
    {
        if (!OfficeEngine.Native(OfficeApp.Word))
            throw new UserFacingException(OfficeEngine.NeedEngineHint(L.F("Saving to {0}", target.ToUpperInvariant())));
        var src = FileKinds.Ext(input);
        if (WordInputs.Contains(src) || src == "txt")
        {
            await OfficeEngine.TrySaveAsAsync(OfficeApp.Word, input, output, target, Detail(ctx), ctx.Token);
            return;
        }
        var temp = OutputPaths.TempDir();
        try
        {
            var docx = Path.Combine(temp, OutputPaths.Sanitize(FileKinds.BaseName(input)) + ".docx");
            var html = await ToHtmlAsync(input, ctx);
            await HtmlEngine.HtmlToDocxAsync(html, docx, FileKinds.BaseName(input));
            await OfficeEngine.TrySaveAsAsync(OfficeApp.Word, docx, output, target, Detail(ctx), ctx.Token);
        }
        finally { OutputPaths.TryDelete(temp); }
    }
    // ---------- PDF → PPTX ----------

    static async Task PdfToPptxAsync(string input, string output, JobContext ctx)
    {
        var doc = await WinRt.OpenPdfAsync(input);
        var slides = new List<(byte[], bool)>();
        double aspect = 16.0 / 9;
        for (uint i = 0; i < doc.PageCount; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            ctx.Report(0.9 * i / doc.PageCount);
            var (w, h) = WinRt.PageSizePt(doc, i);
            if (i == 0) aspect = w / h;
            var png = await WinRt.RenderPageAsync(doc, i, 0, (uint)Math.Round(1920 * Math.Max(1, w / h / aspect)));
            using var mi = new ImageMagick.MagickImage(png);
            mi.Quality = 90;
            slides.Add((mi.ToByteArray(ImageMagick.MagickFormat.Jpeg), false));
        }
        await Task.Run(() => PptxWriter.Write(output, slides, aspect, FileKinds.BaseName(input)), ctx.Token);
    }

    // ---------- Presentations ----------

    public static async Task SlidesConvertAsync(string input, string target, JobContext ctx)
    {
        switch (target)
        {
            case "pdf":
            {
                var output = OutputPaths.File(input, "pdf");
                await SlidesToPdfAsync(input, output, ctx);
                ctx.AddOutput(output);
                return;
            }
            case "pptx":
            {
                var output = OutputPaths.File(input, "pptx");
                if (!await OfficeEngine.TrySaveAsAsync(OfficeApp.PowerPoint, input, output, "pptx", Detail(ctx), ctx.Token))
                    throw new UserFacingException(OfficeEngine.NeedEngineHint(L.T("Presentations")));
                ctx.AddOutput(output);
                return;
            }
            case "png" or "jpg":
            {
                if (OfficeEngine.For(OfficeApp.PowerPoint) == Backend.MicrosoftOffice)
                {
                    var folder = OutputPaths.Folder(input, L.T(" - slides"));
                    Directory.CreateDirectory(folder);
                    ctx.Detail(L.T("Exporting slides with Microsoft PowerPoint…"));
                    var files = await OfficeEngine.PowerPointExportSlidesAsync(input, folder, target, 1920, ctx.Token);
                    if (files.Count == 1)
                    {
                        var single = OutputPaths.File(input, target);
                        File.Move(files[0], single);
                        Directory.Delete(folder);
                        ctx.AddOutput(single);
                    }
                    else ctx.AddOutput(folder);
                    return;
                }
                // LibreOffice: presentation → PDF → page images
                var temp = OutputPaths.TempDir();
                try
                {
                    var pdf = Path.Combine(temp, "slides.pdf");
                    await SlidesToPdfAsync(input, pdf, ctx);
                    await PdfPagesNextToOriginalAsync(input, pdf, target, ctx);
                }
                finally { OutputPaths.TryDelete(temp); }
                return;
            }            default:
                throw new UserFacingException(L.F("A presentation cannot be saved as {0}", target.ToUpperInvariant()));
        }
    }

    static async Task SlidesToPdfAsync(string input, string output, JobContext ctx)
    {
        if (!await OfficeEngine.TrySaveAsAsync(OfficeApp.PowerPoint, input, output, "pdf", Detail(ctx), ctx.Token))
            throw new UserFacingException(OfficeEngine.NeedEngineHint(L.T("Presentations")));
    }
}