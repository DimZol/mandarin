using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;

using Mandarin.Core;

namespace Mandarin.Engines;

/// <summary>Our own DOCX and ODT parser producing clean HTML (headings, lists, tables, images, links).</summary>
public static class DocxReader
{
    public static string ToHtml(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var main = doc.MainDocumentPart ?? throw new Core.UserFacingException(L.T("Empty document"));
        var ctx = new Ctx(main);
        var sb = new StringBuilder();
        var body = main.Document?.Body;
        if (body != null) RenderBlocks(body.ChildElements, sb, ctx);
        ctx.CloseLists(sb, -1);
        var title = doc.PackageProperties.Title;
        return HtmlEngine.WrapDocument(string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title, sb.ToString());
    }

    sealed class Ctx
    {
        public readonly MainDocumentPart Main;
        public readonly Dictionary<string, (string name, int? outline, string? basedOn)> Styles = new();
        public readonly List<(string tag, int level)> OpenLists = new();

        public Ctx(MainDocumentPart main)
        {
            Main = main;
            var styles = main.StyleDefinitionsPart?.Styles;
            if (styles != null)
                foreach (var s in styles.Elements<W.Style>())
                {
                    var id = s.StyleId?.Value;
                    if (id == null) continue;
                    var name = s.StyleName?.Val?.Value ?? id;
                    var outline = s.StyleParagraphProperties?.OutlineLevel?.Val?.Value;
                    Styles[id] = (name, outline, s.BasedOn?.Val?.Value);
                }
        }

        public void CloseLists(StringBuilder sb, int toLevel)
        {
            while (OpenLists.Count > 0 && OpenLists[^1].level > toLevel)
            {
                sb.Append($"</li></{OpenLists[^1].tag}>\n");
                OpenLists.RemoveAt(OpenLists.Count - 1);
            }
        }

        public int? HeadingLevel(string? styleId)
        {
            for (var depth = 0; styleId != null && depth < 8; depth++)
            {
                if (!Styles.TryGetValue(styleId, out var st)) break;
                var n = st.name.ToLowerInvariant();
                if (n == "title") return 1;
                if (n == "subtitle") return 2;
                if (n.StartsWith("heading ") && int.TryParse(n[8..], out var lvl)) return Math.Clamp(lvl, 1, 6);
                if (st.outline is { } o && o < 9) return Math.Clamp(o + 1, 1, 6);
                styleId = st.basedOn;
            }
            return null;
        }

        public bool IsBullet(int numId, int ilvl)
        {
            var numbering = Main.NumberingDefinitionsPart?.Numbering;
            var inst = numbering?.Elements<W.NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numId);
            var absId = inst?.AbstractNumId?.Val?.Value;
            var abs = numbering?.Elements<W.AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == absId);
            var lvl = abs?.Elements<W.Level>().FirstOrDefault(l => l.LevelIndex?.Value == ilvl);
            var fmt = lvl?.NumberingFormat?.Val?.Value;
            return fmt == null || fmt == W.NumberFormatValues.Bullet || fmt == W.NumberFormatValues.None;
        }
    }

    static void RenderBlocks(IEnumerable<OpenXmlElement> elements, StringBuilder sb, Ctx ctx)
    {
        foreach (var el in elements)
        {
            switch (el)
            {
                case W.Paragraph p:
                    RenderParagraph(p, sb, ctx);
                    break;
                case W.Table t:
                    ctx.CloseLists(sb, -1);
                    RenderTable(t, sb, ctx);
                    break;
                case W.SdtBlock sdt when sdt.SdtContentBlock != null:
                    RenderBlocks(sdt.SdtContentBlock.ChildElements, sb, ctx);
                    break;
            }
        }
    }

    static void RenderParagraph(W.Paragraph p, StringBuilder sb, Ctx ctx)
    {
        var pPr = p.ParagraphProperties;
        var styleId = pPr?.ParagraphStyleId?.Val?.Value;
        var numPr = pPr?.NumberingProperties;
        var inner = RenderInlines(p.ChildElements, ctx);

        if (numPr?.NumberingId?.Val?.Value is { } numId && numId != 0)
        {
            var ilvl = numPr.NumberingLevelReference?.Val?.Value ?? 0;
            var tag = ctx.IsBullet(numId, ilvl) ? "ul" : "ol";
            if (ctx.OpenLists.Count > 0 && ctx.OpenLists[^1].level >= ilvl)
            {
                ctx.CloseLists(sb, ilvl);
                if (ctx.OpenLists.Count > 0 && ctx.OpenLists[^1].level == ilvl)
                {
                    if (ctx.OpenLists[^1].tag == tag) { sb.Append("</li>\n<li>").Append(inner); return; }
                    ctx.CloseLists(sb, ilvl - 1);
                }
            }
            sb.Append($"<{tag}>\n<li>").Append(inner);
            ctx.OpenLists.Add((tag, ilvl));
            return;
        }

        ctx.CloseLists(sb, -1);
        if (ctx.HeadingLevel(styleId) is { } h)
        {
            sb.Append($"<h{h}>{inner}</h{h}>\n");
            return;
        }
        var align = pPr?.Justification?.Val?.Value;
        var style = align == W.JustificationValues.Center ? " style=\"text-align:center\"" :
            align == W.JustificationValues.Right ? " style=\"text-align:right\"" : "";
        var name = styleId != null && ctx.Styles.TryGetValue(styleId, out var st) ? st.name.ToLowerInvariant() : "";
        if (name.Contains("quote")) sb.Append($"<blockquote><p>{inner}</p></blockquote>\n");
        else if (inner.Trim().Length == 0) sb.Append("<p>&nbsp;</p>\n");
        else sb.Append($"<p{style}>{inner}</p>\n");
    }

    static string RenderInlines(IEnumerable<OpenXmlElement> elements, Ctx ctx)
    {
        var sb = new StringBuilder();
        foreach (var el in elements)
        {
            switch (el)
            {
                case W.Run r:
                    sb.Append(RenderRun(r, ctx));
                    break;
                case W.Hyperlink h:
                    var href = h.Id?.Value is { } rid
                        ? ctx.Main.HyperlinkRelationships.FirstOrDefault(x => x.Id == rid)?.Uri.ToString()
                        : h.Anchor?.Value is { } anchor ? "#" + anchor : null;
                    var content = RenderInlines(h.ChildElements, ctx);
                    sb.Append(href != null ? $"<a href=\"{WebUtility.HtmlEncode(href)}\">{content}</a>" : content);
                    break;
                case W.SimpleField or W.SdtRun or W.InsertedRun:
                    sb.Append(RenderInlines(el.ChildElements, ctx));
                    break;
                case W.SdtContentRun sc:
                    sb.Append(RenderInlines(sc.ChildElements, ctx));
                    break;
            }
        }
        return sb.ToString();
    }

    static string RenderRun(W.Run r, Ctx ctx)
    {
        var rp = r.RunProperties;
        var sb = new StringBuilder();
        foreach (var c in r.ChildElements)
        {
            switch (c)
            {
                case W.Text t: sb.Append(WebUtility.HtmlEncode(t.Text)); break;
                case W.TabChar: sb.Append("&emsp;"); break;
                case W.Break br when br.Type?.Value != W.BreakValues.Page: sb.Append("<br/>"); break;
                case W.CarriageReturn: sb.Append("<br/>"); break;
                case W.Drawing d: sb.Append(RenderDrawing(d, ctx)); break;
                case W.NoBreakHyphen: sb.Append("&#8209;"); break;
                case W.SymbolChar sym when sym.Char?.Value is { } code:
                    if (int.TryParse(code, System.Globalization.NumberStyles.HexNumber, null, out var cp)) sb.Append($"&#{cp};");
                    break;
            }
        }
        var text = sb.ToString();
        if (text.Length == 0 || rp == null) return text;
        bool On(W.OnOffType? v) => v != null && (v.Val == null || v.Val.Value);
        if (On(rp.Bold)) text = $"<strong>{text}</strong>";
        if (On(rp.Italic)) text = $"<em>{text}</em>";
        if (rp.Underline?.Val?.Value is { } u && u != W.UnderlineValues.None) text = $"<u>{text}</u>";
        if (On(rp.Strike)) text = $"<s>{text}</s>";
        if (rp.VerticalTextAlignment?.Val?.Value == W.VerticalPositionValues.Superscript) text = $"<sup>{text}</sup>";
        if (rp.VerticalTextAlignment?.Val?.Value == W.VerticalPositionValues.Subscript) text = $"<sub>{text}</sub>";
        var fonts = rp.RunFonts?.Ascii?.Value ?? "";
        if (fonts.Contains("Courier") || fonts.Contains("Consolas") || fonts.Contains("Mono")) text = $"<code>{text}</code>";
        return text;
    }

    static string RenderDrawing(W.Drawing d, Ctx ctx)
    {
        var blip = d.Descendants<A.Blip>().FirstOrDefault();
        var id = blip?.Embed?.Value;
        if (id == null) return "";
        try
        {
            if (ctx.Main.GetPartById(id) is not ImagePart part) return "";
            using var s = part.GetStream();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            var mime = part.ContentType;
            if (mime is "image/x-emf" or "image/x-wmf" or "image/tiff")
            {
                using var mi = new ImageMagick.MagickImage(bytes);
                bytes = mi.ToByteArray(ImageMagick.MagickFormat.Png);
                mime = "image/png";
            }
            var extent = d.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>().FirstOrDefault();
            var width = extent?.Cx?.Value is { } cx ? $" width=\"{(int)(cx / 9525.0)}\"" : "";
            return $"<img src=\"data:{mime};base64,{Convert.ToBase64String(bytes)}\"{width} alt=\"\"/>";
        }
        catch
        {
            return "";
        }
    }

    static void RenderTable(W.Table t, StringBuilder sb, Ctx ctx)
    {
        sb.Append("<table>\n");
        var first = true;
        foreach (var row in t.Elements<W.TableRow>())
        {
            var header = first && row.TableRowProperties?.GetFirstChild<W.TableHeader>() != null;
            sb.Append("<tr>");
            foreach (var cell in row.Elements<W.TableCell>())
            {
                if (cell.TableCellProperties?.VerticalMerge is { } vm && vm.Val?.Value != W.MergedCellValues.Restart) continue;
                var span = cell.TableCellProperties?.GridSpan?.Val?.Value ?? 1;
                var tag = header ? "th" : "td";
                sb.Append($"<{tag}{(span > 1 ? $" colspan=\"{span}\"" : "")}>");
                var parts = cell.Elements<W.Paragraph>().Select(p => RenderInlines(p.ChildElements, ctx)).ToList();
                sb.Append(string.Join("<br/>", parts));
                foreach (var nested in cell.Elements<W.Table>()) RenderTable(nested, sb, ctx);
                sb.Append($"</{tag}>");
            }
            sb.Append("</tr>\n");
            first = false;
        }
        sb.Append("</table>\n");
    }

    // ---------- ODT (without Office) ----------

    public static string OdtToHtml(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("content.xml") ?? throw new Core.UserFacingException(L.T("Damaged ODT file"));
        XDocument xml;
        using (var s = entry.Open()) xml = SafeXml.Load(s);
        XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        XNamespace table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        XNamespace draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
        XNamespace xlink = "http://www.w3.org/1999/xlink";
        XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        var body = xml.Root?.Element(office + "body")?.Element(office + "text");
        var sb = new StringBuilder();
        if (body == null) return HtmlEngine.WrapDocument(Path.GetFileNameWithoutExtension(path), "");

        string Inline(XElement e)
        {
            var b = new StringBuilder();
            foreach (var n in e.Nodes())
            {
                if (n is XText t) b.Append(WebUtility.HtmlEncode(t.Value));
                else if (n is XElement c)
                {
                    if (c.Name == text + "s") b.Append(' ', (int?)c.Attribute(text + "c") ?? 1);
                    else if (c.Name == text + "tab") b.Append("&emsp;");
                    else if (c.Name == text + "line-break") b.Append("<br/>");
                    else if (c.Name == text + "a") b.Append($"<a href=\"{WebUtility.HtmlEncode((string?)c.Attribute(xlink + "href") ?? "")}\">{Inline(c)}</a>");
                    else if (c.Name == draw + "frame")
                    {
                        var href = (string?)c.Element(draw + "image")?.Attribute(xlink + "href");
                        var imgEntry = href == null ? null : zip.GetEntry(href);
                        if (imgEntry != null)
                        {
                            using var s = imgEntry.Open();
                            using var ms = new MemoryStream();
                            s.CopyTo(ms);
                            b.Append($"<img src=\"data:{HtmlEngine.MimeFor(href!)};base64,{Convert.ToBase64String(ms.ToArray())}\" alt=\"\"/>");
                        }
                    }
                    else b.Append(Inline(c));
                }
            }
            return b.ToString();
        }

        void Blocks(XElement parent)
        {
            foreach (var e in parent.Elements())
            {
                if (e.Name == text + "h")
                {
                    var lvl = Math.Clamp((int?)e.Attribute(text + "outline-level") ?? 1, 1, 6);
                    sb.Append($"<h{lvl}>{Inline(e)}</h{lvl}>\n");
                }
                else if (e.Name == text + "p") sb.Append($"<p>{Inline(e)}</p>\n");
                else if (e.Name == text + "list")
                {
                    sb.Append("<ul>\n");
                    foreach (var item in e.Elements(text + "list-item"))
                    {
                        sb.Append("<li>");
                        var save = sb.Length;
                        foreach (var child in item.Elements())
                        {
                            if (child.Name == text + "p" || child.Name == text + "h") sb.Append(Inline(child));
                            else if (child.Name == text + "list") Blocks(new XElement("wrap", child));
                        }
                        sb.Append("</li>\n");
                    }
                    sb.Append("</ul>\n");
                }
                else if (e.Name == table + "table")
                {
                    sb.Append("<table>\n");
                    foreach (var row in e.Descendants(table + "table-row"))
                    {
                        sb.Append("<tr>");
                        foreach (var cell in row.Elements(table + "table-cell"))
                            sb.Append("<td>").Append(string.Join("<br/>", cell.Elements(text + "p").Select(Inline))).Append("</td>");
                        sb.Append("</tr>\n");
                    }
                    sb.Append("</table>\n");
                }
                else if (e.Name == text + "section") Blocks(e);
            }
        }

        Blocks(body);
        return HtmlEngine.WrapDocument(Path.GetFileNameWithoutExtension(path), sb.ToString());
    }
}

