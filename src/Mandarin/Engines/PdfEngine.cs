using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ImageMagick;
using Mandarin.Core;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using SharpDocument = PdfSharp.Pdf.PdfDocument;

namespace Mandarin.Engines;

public enum BlockKind { Heading, Paragraph, ListItem, Image, PageBreak, Table }

/// <summary>Logical document block: shared model for PDF → DOCX/MD/EPUB/TXT.</summary>
public sealed record DocBlock(BlockKind Kind, string Text, int Level = 0, byte[]? Image = null, string? ImageMime = null,
    bool Ordered = false, List<List<string>>? Rows = null);

public static class PdfEngine
{
    // ---------- Page rendering ----------

    public static async Task ToImagesAsync(string input, string ext, JobContext ctx)
    {
        var doc = await WinRt.OpenPdfAsync(input);
        var count = doc.PageCount;
        var dpi = AppSettings.Current.PdfImageDpi;
        var folder = count > 1 ? OutputPaths.Folder(input, L.T(" - pages")) : null;
        if (folder != null) Directory.CreateDirectory(folder);
        for (uint i = 0; i < count; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            ctx.Report((double)i / count);
            var png = await WinRt.RenderPageAsync(doc, i, dpi);
            var output = folder == null
                ? OutputPaths.File(input, ext)
                : Path.Combine(folder, $"{FileKinds.BaseName(input)} — {i + 1:000}.{ext}");
            await Task.Run(() => SaveRendered(png, output, ext, dpi));
            if (folder == null) ctx.AddOutput(output);
        }
        if (folder != null) ctx.AddOutput(folder);
    }

    static void SaveRendered(byte[] png, string output, string ext, int dpi)
    {
        if (ext == "png")
        {
            using var img = new MagickImage(png);
            img.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
            img.Write(output, MagickFormat.Png);
            return;
        }
        using var jpg = new MagickImage(png);
        jpg.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
        jpg.Quality = (uint)AppSettings.Current.JpegQuality;
        jpg.Write(output, MagickFormat.Jpeg);
    }

    // ---------- Structure extraction ----------

    static readonly Regex BulletRx = new(@"^\s*([•●▪◦‣∙·○■□➢➤►\-–—*])\s+", RegexOptions.Compiled);
    static readonly Regex NumberRx = new(@"^\s*(\d{1,3}|[a-z\u0430-\u044F])[.)]\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    sealed record RawLine(string Text, double Size, bool Bold, double Top, double CenterY, double Left, double Right, int Page);

    /// <summary>Page element in reading order: text block, image or table.</summary>
    sealed class PageItem
    {
        public List<RawLine> Lines = new();
        public double Top;
        public double Left, Right;
        public byte[]? Image;
        public string? Mime;
        public List<List<string>>? Rows;
    }

    /// <summary>Parses a PDF into paragraphs, headings, lists, tables and images. Scans go through Windows OCR.</summary>
    public static async Task<List<DocBlock>> ExtractBlocksAsync(string input, JobContext? ctx, bool includeImages = true)
    {
        var blocks = new List<DocBlock>();
        var pageGroups = new List<List<PageItem>>();
        var scannedPages = new List<int>();
        int pageCount;

        using (var doc = OpenPig(input))
        {
            pageCount = doc.NumberOfPages;
            foreach (var page in doc.GetPages())
            {
                ctx?.Token.ThrowIfCancellationRequested();
                ctx?.Report(0.6 * page.Number / pageCount);
                var items = new List<PageItem>();
                var letters = page.Letters;
                if (letters.Count(l => !string.IsNullOrWhiteSpace(l.Value)) < 5)
                {
                    scannedPages.Add(page.Number);
                    pageGroups.Add(items);
                    continue;
                }
                var words = NearestNeighbourWordExtractor.Instance.GetWords(letters).ToList();
                var textBlocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
                var ordered = UnsupervisedReadingOrderDetector.Instance.Get(textBlocks).ToList();
                foreach (var tb in ordered)
                {
                    var lines = tb.TextLines.Select(tl =>
                    {
                        var ls = tl.Words.SelectMany(w => w.Letters).Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
                        var size = ls.Count == 0 ? 0 : ls.Average(l => l.PointSize);
                        var bold = ls.Count > 0 && ls.Count(l => IsBold(l)) > ls.Count * 0.6;
                        var bb = tl.BoundingBox;
                        return new RawLine(tl.Text, Math.Round(size, 1), bold, bb.Top, bb.Centroid.Y, bb.Left, bb.Right, page.Number);
                    }).Where(l => l.Text.Trim().Length > 0).ToList();
                    if (lines.Count > 0)
                        items.Add(new PageItem { Lines = lines, Top = tb.BoundingBox.Top, Left = tb.BoundingBox.Left, Right = tb.BoundingBox.Right });
                }
                items = DetectTables(items);

                if (includeImages)
                    foreach (var image in page.GetImages())
                    {
                        if (image.BoundingBox.Width < 40 || image.BoundingBox.Height < 40) continue;
                        var (bytes, mime) = ImageBytes(image);
                        if (bytes == null) continue;
                        // insert before the first block that lies below the image
                        var idx = items.FindIndex(it => it.Top < image.BoundingBox.Top - 2);
                        var entry = new PageItem { Top = image.BoundingBox.Top, Image = bytes, Mime = mime };
                        if (idx < 0) items.Add(entry); else items.Insert(idx, entry);
                    }
                pageGroups.Add(items);
            }
        }

        // body text size: the most common size weighted by character count
        var allLines = pageGroups.SelectMany(g => g).SelectMany(g => g.Lines).ToList();
        var body = allLines.Count == 0 ? 11 : allLines.GroupBy(l => Math.Round(l.Size))
            .OrderByDescending(g => g.Sum(l => l.Text.Length)).First().Key;
        if (body <= 0) body = 11;

        for (var p = 0; p < pageGroups.Count; p++)
        {
            if (p > 0) blocks.Add(new DocBlock(BlockKind.PageBreak, ""));
            if (scannedPages.Contains(p + 1) && AppSettings.Current.OcrScannedPdf)
            {
                ctx?.Detail(L.F("Recognizing text, page {0}…", p + 1));
                blocks.AddRange(await OcrPageAsync(input, (uint)p));
                continue;
            }
            // left page margin, measured from body-text paragraphs
            var textItems = pageGroups[p].Where(i => i.Image == null && i.Rows == null).ToList();
            var margin = textItems.Count == 0 ? 0 : textItems.SelectMany(i => i.Lines).Min(l => l.Left);
            var typicalWidth = textItems.Count == 0 ? 0 : textItems.SelectMany(i => i.Lines).Max(l => l.Right - l.Left);
            foreach (var item in pageGroups[p])
            {
                if (item.Image != null) { blocks.Add(new DocBlock(BlockKind.Image, "", Image: item.Image, ImageMime: item.Mime)); continue; }
                if (item.Rows != null) { blocks.Add(new DocBlock(BlockKind.Table, "", Rows: item.Rows)); continue; }
                blocks.AddRange(ClassifyBlock(item.Lines, body, margin, typicalWidth));
            }
        }
        ctx?.Report(0.7);
        return blocks;
    }

    /// <summary>
    /// Tables: horizontally adjacent blocks whose lines sit on the same baselines
    /// are merged into a grid (PDF table cells fall apart into separate column blocks when parsed).
    /// </summary>
    static List<PageItem> DetectTables(List<PageItem> items)
    {
        const double tol = 2.5;
        var cand = items.Where(i => i.Lines.Count >= 2 && i.Lines.All(l => l.Text.Length <= 60)).ToList();
        if (cand.Count < 2) return items;
        bool SharedRows(PageItem a, PageItem b) =>
            (a.Right < b.Left || b.Right < a.Left) &&
            a.Lines.Count(la => b.Lines.Any(lb => Math.Abs(la.CenterY - lb.CenterY) < tol)) >= 2;

        // grouping (simple union-find)
        var parent = cand.ToDictionary(c => c, c => c);
        PageItem Find(PageItem x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (var i = 0; i < cand.Count; i++)
            for (var j = i + 1; j < cand.Count; j++)
                if (SharedRows(cand[i], cand[j])) parent[Find(cand[i])] = Find(cand[j]);

        var groups = cand.GroupBy(Find).Where(g => g.Count() >= 2).Select(g => g.OrderBy(b => b.Left).ToList()).ToList();
        if (groups.Count == 0) return items;

        var result = new List<PageItem>();
        var consumed = new HashSet<PageItem>(groups.SelectMany(g => g));
        foreach (var it in items)
        {
            if (!consumed.Contains(it)) { result.Add(it); continue; }
            var group = groups.FirstOrDefault(g => g.Contains(it));
            if (group == null || result.Any(r => r.Rows != null && ReferenceEquals(r.Lines, group[0].Lines))) continue;
            // table rows: vertical clusters (top to bottom)
            var ys = new List<double>();
            foreach (var y in group.SelectMany(b => b.Lines).Select(l => l.CenterY).OrderByDescending(y => y))
                if (ys.Count == 0 || Math.Abs(ys[^1] - y) >= tol) ys.Add(y);
            var rows = ys.Select(y => group.Select(col =>
                string.Join(" ", col.Lines.Where(l => Math.Abs(l.CenterY - y) < tol).Select(l => l.Text.Trim()))).ToList()).ToList();
            result.Add(new PageItem { Lines = group[0].Lines, Top = group.Max(g => g.Top), Rows = rows });
        }
        return result;
    }

    static bool IsBold(Letter l)
    {
        var name = l.FontName ?? "";
        return name.Contains("Bold", StringComparison.OrdinalIgnoreCase) || name.Contains("Black", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Heavy", StringComparison.OrdinalIgnoreCase) || name.Contains("Semibold", StringComparison.OrdinalIgnoreCase);
    }

    static IEnumerable<DocBlock> ClassifyBlock(List<RawLine> lines, double body, double margin, double typicalWidth)
    {
        var text = string.Join(" ", lines.Select(l => l.Text.Trim()));

        // list without text markers (bullets drawn as graphics): short indented lines
        if (lines.Count >= 2 && typicalWidth > 0 && !BulletRx.IsMatch(lines[0].Text) && !NumberRx.IsMatch(lines[0].Text) &&
            lines.All(l => l.Left > margin + 8 && l.Right - l.Left < typicalWidth * 0.75 && !l.Text.TrimEnd().EndsWith('.')) &&
            Math.Abs(lines.Average(l => l.Size) - body) < 1.5)
        {
            foreach (var l in lines)
                yield return new DocBlock(BlockKind.ListItem, Clean(l.Text), Ordered: false);
            yield break;
        }
        var size = lines.Average(l => l.Size);
        var ratio = size / body;
        var allBold = lines.All(l => l.Bold);

        if (lines.Count <= 3 && text.Length < 160 && !text.EndsWith('.') && (ratio >= 1.15 || (allBold && text.Length < 90)))
        {
            var level = ratio >= 1.8 ? 1 : ratio >= 1.4 ? 2 : ratio >= 1.15 ? 3 : 4;
            yield return new DocBlock(BlockKind.Heading, Clean(text), level);
            yield break;
        }

        // lists: every line with a marker starts a new item
        if (BulletRx.IsMatch(lines[0].Text) || NumberRx.IsMatch(lines[0].Text))
        {
            var current = new StringBuilder();
            var ordered = NumberRx.IsMatch(lines[0].Text) && !BulletRx.IsMatch(lines[0].Text);
            foreach (var l in lines)
            {
                var t = l.Text.Trim();
                var isStart = BulletRx.IsMatch(t) || NumberRx.IsMatch(t);
                if (isStart && current.Length > 0)
                {
                    yield return new DocBlock(BlockKind.ListItem, Clean(current.ToString()), Ordered: ordered);
                    current.Clear();
                }
                if (isStart) t = ordered ? NumberRx.Replace(t, "", 1) : BulletRx.Replace(t, "", 1);
                AppendLine(current, t);
            }
            if (current.Length > 0) yield return new DocBlock(BlockKind.ListItem, Clean(current.ToString()), Ordered: ordered);
            yield break;
        }

        var sb = new StringBuilder();
        foreach (var l in lines) AppendLine(sb, l.Text.Trim());
        yield return new DocBlock(BlockKind.Paragraph, Clean(sb.ToString()));
    }

    static void AppendLine(StringBuilder sb, string line)
    {
        if (sb.Length == 0) { sb.Append(line); return; }
        // word hyphenated at the end of a line
        if (sb[^1] == '-' && line.Length > 0 && char.IsLower(line[0])) { sb.Length--; sb.Append(line); }
        else sb.Append(' ').Append(line);
    }

    static string Clean(string s) => Regex.Replace(s.Replace(' ', ' '), @"\s{2,}", " ").Trim();

    static (byte[]?, string?) ImageBytes(IPdfImage image)
    {
        try
        {
            if (image.TryGetPng(out var png)) return (png, "image/png");
            var raw = image.RawMemory.ToArray();
            if (raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8)
            {
                // CMYK JPEGs are converted to RGB, otherwise browsers and Word show them as negatives
                using var mi = new MagickImage(raw);
                if (mi.ColorSpace == ColorSpace.CMYK)
                {
                    mi.ColorSpace = ColorSpace.sRGB;
                    return (mi.ToByteArray(MagickFormat.Jpeg), "image/jpeg");
                }
                return (raw, "image/jpeg");
            }
        }
        catch
        {
            // skip the image: text matters more
        }
        return (null, null);
    }

    static async Task<List<DocBlock>> OcrPageAsync(string input, uint index)
    {
        var doc = await WinRt.OpenPdfAsync(input);
        var png = await WinRt.RenderPageAsync(doc, index, 220);
        var lines = await WinRt.RecognizeAsync(png);
        return LinesToBlocks(lines);
    }

    /// <summary>OCR lines → paragraphs split at vertical gaps.</summary>
    public static List<DocBlock> LinesToBlocks(IReadOnlyList<WinRt.OcrLine> lines)
    {
        var result = new List<DocBlock>();
        if (lines.Count == 0) return result;
        var sorted = lines.OrderBy(l => l.Y).ToList();
        var medianH = sorted.Select(l => l.H).OrderBy(h => h).ElementAt(sorted.Count / 2);
        var sb = new StringBuilder();
        double prevBottom = sorted[0].Y;
        foreach (var l in sorted)
        {
            if (sb.Length > 0 && l.Y - prevBottom > medianH * 0.9)
            {
                result.Add(new DocBlock(BlockKind.Paragraph, Clean(sb.ToString())));
                sb.Clear();
            }
            AppendLine(sb, l.Text);
            prevBottom = l.Y + l.H;
        }
        if (sb.Length > 0) result.Add(new DocBlock(BlockKind.Paragraph, Clean(sb.ToString())));
        return result;
    }

    static PigDocument OpenPig(string input)
    {
        try
        {
            return PigDocument.Open(input);
        }
        catch (Exception ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                                   ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException(L.T("The PDF is password protected"));
        }
    }

    // ---------- Blocks → HTML / Markdown / text ----------

    public static string BlocksToHtml(IReadOnlyList<DocBlock> blocks, string title, bool pageBreaks = false)
    {
        var sb = new StringBuilder();
        string? openList = null;
        void CloseList() { if (openList != null) { sb.Append($"</{openList}>\n"); openList = null; } }
        foreach (var b in blocks)
        {
            if (b.Kind != BlockKind.ListItem) CloseList();
            switch (b.Kind)
            {
                case BlockKind.Heading:
                    var lvl = Math.Clamp(b.Level, 1, 6);
                    sb.Append($"<h{lvl}>{WebUtility.HtmlEncode(b.Text)}</h{lvl}>\n");
                    break;
                case BlockKind.Paragraph:
                    sb.Append($"<p>{WebUtility.HtmlEncode(b.Text)}</p>\n");
                    break;
                case BlockKind.ListItem:
                    var tag = b.Ordered ? "ol" : "ul";
                    if (openList != tag) { CloseList(); sb.Append($"<{tag}>\n"); openList = tag; }
                    sb.Append($"<li>{WebUtility.HtmlEncode(b.Text)}</li>\n");
                    break;
                case BlockKind.Image when b.Image != null:
                    sb.Append($"<p><img src=\"data:{b.ImageMime};base64,{Convert.ToBase64String(b.Image)}\" alt=\"\"/></p>\n");
                    break;
                case BlockKind.PageBreak when pageBreaks:
                    sb.Append("<div style=\"page-break-after: always\"></div>\n");
                    break;
                case BlockKind.Table when b.Rows != null:
                    sb.Append("<table>\n");
                    foreach (var row in b.Rows)
                        sb.Append("<tr>").Append(string.Concat(row.Select(c => $"<td>{WebUtility.HtmlEncode(c)}</td>"))).Append("</tr>\n");
                    sb.Append("</table>\n");
                    break;
            }
        }
        CloseList();
        return HtmlEngine.WrapDocument(title, sb.ToString());
    }

    public static string BlocksToMarkdown(IReadOnlyList<DocBlock> blocks, string? imageFolderName = null, List<(string name, byte[] data)>? images = null)
    {
        var sb = new StringBuilder();
        var n = 0; var imgIndex = 0;
        var prevWasList = false;
        foreach (var b in blocks)
        {
            if (b.Kind != BlockKind.ListItem)
            {
                n = 0;
                if (prevWasList && b.Kind != BlockKind.PageBreak) sb.Append('\n');
            }
            prevWasList = b.Kind == BlockKind.ListItem || (prevWasList && b.Kind == BlockKind.PageBreak);
            switch (b.Kind)
            {
                case BlockKind.Heading:
                    sb.Append(new string('#', Math.Clamp(b.Level, 1, 6))).Append(' ').Append(b.Text).Append("\n\n");
                    break;
                case BlockKind.Paragraph:
                    sb.Append(EscapeMd(b.Text)).Append("\n\n");
                    break;
                case BlockKind.ListItem:
                    sb.Append(b.Ordered ? $"{++n}. " : "- ").Append(EscapeMd(b.Text)).Append('\n');
                    break;
                case BlockKind.Image when b.Image != null && images != null && imageFolderName != null:
                    var name = $"image{++imgIndex:000}.{(b.ImageMime == "image/png" ? "png" : "jpg")}";
                    images.Add((name, b.Image));
                    sb.Append($"![]({Uri.EscapeDataString(imageFolderName)}/{Uri.EscapeDataString(name)})\n\n");
                    break;
                case BlockKind.Table when b.Rows is { Count: > 0 }:
                    var cols = b.Rows.Max(r => r.Count);
                    foreach (var (row, i) in b.Rows.Select((r, i) => (r, i)))
                    {
                        sb.Append("| ").Append(string.Join(" | ", Enumerable.Range(0, cols).Select(c => c < row.Count ? row[c].Replace("|", "\\|") : ""))).Append(" |\n");
                        if (i == 0) sb.Append("|").Append(string.Concat(Enumerable.Repeat(" --- |", cols))).Append('\n');
                    }
                    sb.Append('\n');
                    break;
            }
        }
        return sb.ToString().Trim() + "\n";
    }

    static string EscapeMd(string s) => Regex.Replace(s, @"^(#|>|\d+\.)", @"\$1");

    public static string BlocksToText(IReadOnlyList<DocBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (var b in blocks)
        {
            switch (b.Kind)
            {
                case BlockKind.Heading: sb.Append(b.Text).Append("\n\n"); break;
                case BlockKind.Paragraph: sb.Append(b.Text).Append("\n\n"); break;
                case BlockKind.ListItem: sb.Append("• ").Append(b.Text).Append('\n'); break;
                case BlockKind.PageBreak: sb.Append('\n'); break;
            }
        }
        return sb.ToString().Trim() + Environment.NewLine;
    }

    // ---------- Tables (PDF → XLSX) ----------

    /// <summary>Rows and columns from word coordinates: one sheet per page.</summary>
    public static List<(string name, List<List<string>> rows)> ExtractGrid(string input, CancellationToken ct)
    {
        var sheets = new List<(string, List<List<string>>)>();
        using var doc = OpenPig(input);
        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters)
                .Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            var rows = new List<List<string>>();
            if (words.Count > 0)
            {
                // rows: vertical grouping
                var lineGroups = new List<List<Word>>();
                foreach (var w in words.OrderByDescending(w => w.BoundingBox.Centroid.Y))
                {
                    var h = Math.Max(2, w.BoundingBox.Height);
                    var g = lineGroups.LastOrDefault();
                    if (g != null && Math.Abs(g[0].BoundingBox.Centroid.Y - w.BoundingBox.Centroid.Y) < h * 0.55) g.Add(w);
                    else lineGroups.Add(new List<Word> { w });
                }
                // cells: words separated by a noticeable gap
                var segRows = new List<List<(double x, string text)>>();
                foreach (var g in lineGroups)
                {
                    var ordered = g.OrderBy(w => w.BoundingBox.Left).ToList();
                    var segs = new List<(double x, StringBuilder sb, double right)>();
                    foreach (var w in ordered)
                    {
                        var charW = w.BoundingBox.Width / Math.Max(1, w.Text.Length);
                        if (segs.Count > 0 && w.BoundingBox.Left - segs[^1].right < Math.Max(charW * 1.6, 4))
                        {
                            segs[^1].sb.Append(' ').Append(w.Text);
                            segs[^1] = (segs[^1].x, segs[^1].sb, w.BoundingBox.Right);
                        }
                        else segs.Add((w.BoundingBox.Left, new StringBuilder(w.Text), w.BoundingBox.Right));
                    }
                    segRows.Add(segs.Select(s => (s.x, s.sb.ToString())).ToList());
                }
                // columns: clusters of left edges
                var anchors = new List<double>();
                foreach (var x in segRows.SelectMany(r => r.Select(s => s.x)).OrderBy(x => x))
                    if (anchors.Count == 0 || x - anchors[^1] > 12) anchors.Add(x);
                foreach (var r in segRows)
                {
                    var row = new List<string>(new string[anchors.Count].Select(_ => ""));
                    foreach (var (x, text) in r)
                    {
                        var col = anchors.FindLastIndex(a => a <= x + 0.5);
                        if (col < 0) col = 0;
                        row[col] = row[col].Length == 0 ? text : row[col] + " " + text;
                    }
                    while (row.Count > 0 && row[^1].Length == 0) row.RemoveAt(row.Count - 1);
                    rows.Add(row);
                }
            }
            sheets.Add(($"Page {page.Number}", rows));
        }
        return sheets;
    }

    // ---------- PDFsharp operations ----------

    static SharpDocument OpenSharp(string path, PdfDocumentOpenMode mode)
    {
        try
        {
            return PdfReader.Open(path, mode);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException(L.T("The PDF is password protected"));
        }
    }

    public static void ImagesToPdf(IReadOnlyList<string> images, string output, JobContext? ctx)
    {
        using var pdf = new SharpDocument();
        pdf.Info.Creator = "Mandarin";
        for (var i = 0; i < images.Count; i++)
        {
            ctx?.Token.ThrowIfCancellationRequested();
            ctx?.Report((double)i / images.Count);
            using var img = ImageEngine.Load(images[i]);
            var hasAlpha = img.HasAlpha;
            if (!hasAlpha) img.Quality = 92;
            var bytes = img.ToByteArray(hasAlpha ? MagickFormat.Png : MagickFormat.Jpeg);
            using var ms = new MemoryStream(bytes);
            var ximg = XImage.FromStream(ms);
            const double longSide = 842; // A4
            double w, h;
            if (img.Width >= img.Height) { w = longSide; h = longSide * img.Height / img.Width; }
            else { h = longSide; w = longSide * img.Width / img.Height; }
            var page = pdf.AddPage();
            page.Width = XUnit.FromPoint(w);
            page.Height = XUnit.FromPoint(h);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawImage(ximg, 0, 0, w, h);
        }
        pdf.Save(output);
    }

    public static void Merge(IReadOnlyList<string> inputs, string output, JobContext? ctx)
    {
        using var result = new SharpDocument();
        for (var i = 0; i < inputs.Count; i++)
        {
            ctx?.Token.ThrowIfCancellationRequested();
            ctx?.Report((double)i / inputs.Count);
            using var src = OpenSharp(inputs[i], PdfDocumentOpenMode.Import);
            foreach (var page in src.Pages) result.AddPage(page);
        }
        result.Save(output);
    }

    public static string Split(string input, JobContext? ctx)
    {
        using var src = OpenSharp(input, PdfDocumentOpenMode.Import);
        var folder = OutputPaths.Folder(input, L.T(" - pages"));
        Directory.CreateDirectory(folder);
        for (var i = 0; i < src.PageCount; i++)
        {
            ctx?.Token.ThrowIfCancellationRequested();
            ctx?.Report((double)i / src.PageCount);
            using var one = new SharpDocument();
            one.Version = src.Version;
            one.Info.Title = $"{src.Info.Title} — {i + 1}";
            one.AddPage(src.Pages[i]);
            one.Save(Path.Combine(folder, $"{FileKinds.BaseName(input)} — {i + 1:000}.pdf"));
        }
        return folder;
    }

    public static int PageCount(string input)
    {
        using var src = OpenSharp(input, PdfDocumentOpenMode.Import);
        return src.PageCount;
    }

    /// <summary>New PDF from the source pages: order, rotation (multiples of 90), duplicates.</summary>
    public static void Organize(string input, IReadOnlyList<(int sourceIndex, int rotation)> pages, string output)
    {
        using var src = OpenSharp(input, PdfDocumentOpenMode.Import);
        using var result = new SharpDocument();
        result.Info.Title = src.Info.Title;
        result.Info.Author = src.Info.Author;
        foreach (var (index, rotation) in pages)
        {
            var added = result.AddPage(src.Pages[index]);
            added.Rotate = ((src.Pages[index].Rotate + rotation) % 360 + 360) % 360;
        }
        result.Save(output);
    }

    public sealed record PdfMeta(string Title, string Author, string Subject, string Keywords, string Creator);

    public static PdfMeta ReadMeta(string input)
    {
        using var doc = OpenSharp(input, PdfDocumentOpenMode.Import);
        var i = doc.Info;
        return new PdfMeta(i.Title ?? "", i.Author ?? "", i.Subject ?? "", i.Keywords ?? "", i.Creator ?? "");
    }

    public static void WriteMeta(string input, string output, PdfMeta? meta)
    {
        File.Copy(input, output);
        try
        {
            using var doc = OpenSharp(output, PdfDocumentOpenMode.Modify);
            var info = doc.Info;
            if (meta == null)
            {
                foreach (var key in info.Elements.Keys.ToList()) info.Elements.Remove(key);
                doc.Internals.Catalog.Elements.Remove("/Metadata"); // XMP
            }
            else
            {
                info.Title = meta.Title; info.Author = meta.Author; info.Subject = meta.Subject;
                info.Keywords = meta.Keywords; info.Creator = meta.Creator;
            }
            doc.Save(output);
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
    }

    /// <summary>Compression: re-encodes images inside the PDF. level 0 - gentle, 1 - recommended, 2 - strong, 3 - pages as images.</summary>
    public static async Task<long> CompressAsync(string input, string output, int level, JobContext ctx)
    {
        if (level >= 3)
        {
            await RasterizeAsync(input, output, 110, 55, ctx);
            return new FileInfo(output).Length;
        }

        var (maxSide, quality) = level switch { 0 => (2400, 80), 1 => (1700, 68), _ => (1200, 50) };
        await Task.Run(() =>
        {
            using var doc = OpenSharp(input, PdfDocumentOpenMode.Modify);
            var seen = new HashSet<PdfDictionary>();
            for (var p = 0; p < doc.PageCount; p++)
            {
                ctx.Token.ThrowIfCancellationRequested();
                ctx.Report(0.9 * p / doc.PageCount);
                foreach (var img in PageImages(doc.Pages[p]))
                    if (seen.Add(img)) TryRecompress(img, maxSide, quality);
            }
            doc.Options.CompressContentStreams = true;
            doc.Options.NoCompression = false;
            doc.Save(output);
        }, ctx.Token);

        if (new FileInfo(output).Length >= new FileInfo(input).Length)
        {
            // not smaller - keep a copy of the original so the result is never worse
            File.Copy(input, output, true);
        }
        return new FileInfo(output).Length;
    }

    static IEnumerable<PdfDictionary> PageImages(PdfPage page)
    {
        var resources = page.Elements.GetDictionary("/Resources");
        var xobjects = resources?.Elements.GetDictionary("/XObject");
        if (xobjects == null) yield break;
        foreach (var item in xobjects.Elements.Values)
        {
            if ((item as PdfReference)?.Value is not PdfDictionary dict) continue;
            var subtype = dict.Elements.GetName("/Subtype");
            if (subtype == "/Image") yield return dict;
            else if (subtype == "/Form")
            {
                var formRes = dict.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject");
                if (formRes == null) continue;
                foreach (var inner in formRes.Elements.Values)
                    if ((inner as PdfReference)?.Value is PdfDictionary d2 && d2.Elements.GetName("/Subtype") == "/Image")
                        yield return d2;
            }
        }
    }

    static void TryRecompress(PdfDictionary img, int maxSide, int quality)
    {
        try
        {
            if (img.Stream == null) return;
            if (img.Elements.GetBoolean("/ImageMask")) return;
            var width = img.Elements.GetInteger("/Width");
            var height = img.Elements.GetInteger("/Height");
            if (width < 64 || height < 64) return;
            var bpc = img.Elements.GetInteger("/BitsPerComponent");
            var cs = img.Elements["/ColorSpace"];
            var csName = cs is PdfName n ? n.Value : null;
            int comps;
            if (csName == "/DeviceRGB") comps = 3;
            else if (csName == "/DeviceGray") comps = 1;
            else if (cs is PdfReference { Value: PdfArray arr } && arr.Elements.GetName(0) == "/ICCBased" ||
                     cs is PdfArray arr2 && arr2.Elements.GetName(0) == "/ICCBased")
            {
                var iccArr = (cs as PdfArray) ?? (PdfArray)((PdfReference)cs).Value;
                var icc = (iccArr.Elements[1] as PdfReference)?.Value as PdfDictionary;
                comps = icc?.Elements.GetInteger("/N") ?? 0;
                if (comps is not (1 or 3)) return;
            }
            else return;

            var filter = img.Elements["/Filter"];
            var filterName = filter is PdfName fn ? fn.Value : filter is PdfArray fa && fa.Elements.Count == 1 ? fa.Elements.GetName(0) : null;
            if (img.Elements.ContainsKey("/Decode")) return;

            MagickImage? magick = null;
            if (filterName == "/DCTDecode")
            {
                magick = new MagickImage(img.Stream.Value);
            }
            else if (filterName == "/FlateDecode" && bpc == 8)
            {
                var raw = img.Stream.UnfilteredValue;
                if (raw.Length != width * height * comps) return;
                magick = new MagickImage();
                if (comps == 1)
                {
                    // gray → RGB (PixelMapping has no single-channel mode)
                    var rgb = new byte[raw.Length * 3];
                    for (var i = 0; i < raw.Length; i++) rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = raw[i];
                    raw = rgb;
                }
                magick.ReadPixels(raw, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGB));
                if (comps == 1) magick.ColorType = ColorType.Grayscale;
            }
            if (magick == null) return;

            using (magick)
            {
                if (magick.ColorSpace == ColorSpace.CMYK) return;
                if (magick.Width > maxSide || magick.Height > maxSide)
                    magick.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide));
                magick.Quality = (uint)quality;
                magick.Strip();
                if (comps == 1) magick.ColorType = ColorType.Grayscale;
                var jpeg = magick.ToByteArray(MagickFormat.Jpeg);
                if (jpeg.Length >= img.Stream.Length * 0.92) return;
                img.Stream.Value = jpeg;
                img.Elements.SetName("/Filter", "/DCTDecode");
                img.Elements.Remove("/DecodeParms");
                img.Elements.SetInteger("/Width", (int)magick.Width);
                img.Elements.SetInteger("/Height", (int)magick.Height);
                img.Elements.SetInteger("/BitsPerComponent", 8);
                img.Elements.SetInteger("/Length", jpeg.Length);
                if (comps == 1) img.Elements.SetName("/ColorSpace", "/DeviceGray");
                else img.Elements.SetName("/ColorSpace", "/DeviceRGB");
            }
        }
        catch
        {
            // unusual images are left as they are
        }
    }

    /// <summary>PDF made of rendered pages (maximum compression or a fixed-layout EPUB).</summary>
    public static async Task RasterizeAsync(string input, string output, int dpi, int quality, JobContext ctx)
    {
        var win = await WinRt.OpenPdfAsync(input);
        using var pdf = new SharpDocument();
        for (uint i = 0; i < win.PageCount; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            ctx.Report((double)i / win.PageCount);
            var png = await WinRt.RenderPageAsync(win, i, dpi);
            var (wPt, hPt) = WinRt.PageSizePt(win, i);
            using var mi = new MagickImage(png);
            mi.Quality = (uint)quality;
            using var ms = new MemoryStream(mi.ToByteArray(MagickFormat.Jpeg));
            var ximg = XImage.FromStream(ms);
            var page = pdf.AddPage();
            page.Width = XUnit.FromPoint(wPt);
            page.Height = XUnit.FromPoint(hPt);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawImage(ximg, 0, 0, wPt, hPt);
        }
        pdf.Save(output);
    }
}

