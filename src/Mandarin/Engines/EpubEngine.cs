using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HtmlAgilityPack;
using Mandarin.Core;

namespace Mandarin.Engines;

/// <summary>Reads and writes EPUB 3 (with NCX for older readers).</summary>
public static class EpubEngine
{
    // ---------- Reading ----------

    public static string ToHtml(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var container = Load(zip, "META-INF/container.xml") ?? throw new UserFacingException(L.T("Damaged EPUB file: container.xml is missing"));
        XNamespace cn = "urn:oasis:names:tc:opendocument:xmlns:container";
        var opfPath = container.Descendants(cn + "rootfile").FirstOrDefault()?.Attribute("full-path")?.Value
                      ?? throw new UserFacingException(L.T("Damaged EPUB file"));
        var opf = Load(zip, opfPath) ?? throw new UserFacingException(L.T("Damaged EPUB file: OPF is missing"));
        var opfDir = Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? "";
        XNamespace opfNs = "http://www.idpf.org/2007/opf";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        var title = opf.Descendants(dc + "title").FirstOrDefault()?.Value ?? Path.GetFileNameWithoutExtension(path);
        var author = opf.Descendants(dc + "creator").FirstOrDefault()?.Value;
        var manifest = opf.Descendants(opfNs + "item")
            .Where(i => i.Attribute("id") != null && i.Attribute("href") != null)
            .ToDictionary(i => i.Attribute("id")!.Value, i => i.Attribute("href")!.Value);
        var spine = opf.Descendants(opfNs + "itemref").Select(i => i.Attribute("idref")?.Value ?? "").Where(manifest.ContainsKey);

        var sb = new StringBuilder();
        sb.Append($"<h1 class=\"book-title\">{WebUtility.HtmlEncode(title)}</h1>\n");
        if (!string.IsNullOrWhiteSpace(author)) sb.Append($"<p><em>{WebUtility.HtmlEncode(author)}</em></p>\n");
        foreach (var id in spine)
        {
            var href = Combine(opfDir, Uri.UnescapeDataString(manifest[id!].Split('#')[0]));
            var entry = zip.GetEntry(href);
            if (entry == null) continue;
            string xhtml;
            using (var r = new StreamReader(entry.Open(), Encoding.UTF8)) xhtml = r.ReadToEnd();
            var doc = new HtmlDocument();
            doc.LoadHtml(xhtml);
            var chapterDir = Path.GetDirectoryName(href)?.Replace('\\', '/') ?? "";
            foreach (var img in doc.DocumentNode.SelectNodes("//img[@src]|//image") ?? Enumerable.Empty<HtmlNode>())
            {
                var attr = img.Name == "image" ? (img.Attributes["xlink:href"] ?? img.Attributes["href"]) : img.Attributes["src"];
                if (attr == null) continue;
                var imgPath = Combine(chapterDir, Uri.UnescapeDataString(WebUtility.HtmlDecode(attr.Value) ?? ""));
                var imgEntry = zip.GetEntry(imgPath);
                if (imgEntry == null) continue;
                using var s = imgEntry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var dataUri = $"data:{HtmlEngine.MimeFor(imgPath)};base64,{Convert.ToBase64String(ms.ToArray())}";
                if (img.Name == "image")
                {
                    // SVG cover wrapper → plain img
                    var newImg = HtmlNode.CreateNode($"<img src=\"{dataUri}\" alt=\"\"/>");
                    var svg = img.Ancestors("svg").FirstOrDefault() ?? img;
                    svg.ParentNode?.ReplaceChild(newImg, svg);
                }
                else img.SetAttributeValue("src", dataUri);
            }
            var body = doc.DocumentNode.SelectSingleNode("//body");
            if (body == null) continue;
            body.SelectNodes(".//script|.//style")?.ToList().ForEach(n => n.Remove());
            sb.Append("<section>\n").Append(body.InnerHtml).Append("\n</section>\n");
        }
        return HtmlEngine.WrapDocument(title, sb.ToString());
    }

    static XDocument? Load(ZipArchive zip, string path)
    {
        var e = zip.GetEntry(path);
        if (e == null) return null;
        using var s = e.Open();
        return SafeXml.Load(s);
    }

    static string Combine(string dir, string rel)
    {
        var parts = new List<string>(dir.Length == 0 ? Array.Empty<string>() : dir.Split('/'));
        foreach (var p in rel.Split('/'))
        {
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (p != "." && p.Length > 0) parts.Add(p);
        }
        return string.Join('/', parts);
    }

    // ---------- Writing ----------

    sealed record Chapter(string Title, string BodyXhtml);

    /// <summary>EPUB from HTML: chapters split at h1/h2, images moved into files.</summary>
    public static void FromHtml(string html, string title, string author, string output)
    {
        var doc = new HtmlDocument { OptionOutputAsXml = false };
        doc.LoadHtml(html);
        var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        body.SelectNodes(".//script|.//style")?.ToList().ForEach(n => n.Remove());

        // images → separate files
        var images = new List<(string name, string mime, byte[] data)>();
        foreach (var img in body.SelectNodes(".//img[starts-with(@src,'data:')]") ?? Enumerable.Empty<HtmlNode>())
        {
            var m = Regex.Match(img.GetAttributeValue("src", ""), @"^data:(image/[\w.+-]+)[^;]*;base64,(.+)$", RegexOptions.Singleline);
            if (!m.Success) { img.Remove(); continue; }
            var mime = m.Groups[1].Value;
            var ext = mime switch { "image/png" => "png", "image/gif" => "gif", "image/svg+xml" => "svg", "image/webp" => "webp", _ => "jpg" };
            var data = Convert.FromBase64String(m.Groups[2].Value);
            if (ext == "webp")
            {
                using var mi = new ImageMagick.MagickImage(data);
                data = mi.ToByteArray(ImageMagick.MagickFormat.Png); ext = "png"; mime = "image/png";
            }
            var name = $"img{images.Count + 1:000}.{ext}";
            images.Add((name, mime, data));
            img.SetAttributeValue("src", $"images/{name}");
            if (string.IsNullOrEmpty(img.GetAttributeValue("alt", ""))) img.SetAttributeValue("alt", "");
        }

        // chapters
        var chapters = new List<Chapter>();
        var current = new StringBuilder();
        var currentTitle = title;
        var splitTag = body.SelectNodes(".//h1")?.Count >= 2 ? "h1" : "h2";
        void Flush()
        {
            if (current.ToString().Trim().Length > 0) chapters.Add(new Chapter(currentTitle, current.ToString()));
            current.Clear();
        }
        foreach (var node in Flatten(body))
        {
            if (node.Name == splitTag && current.Length > 400)
            {
                Flush();
                currentTitle = WebUtility.HtmlDecode(node.InnerText).Trim();
            }
            else if (node.Name == splitTag && chapters.Count == 0) currentTitle = WebUtility.HtmlDecode(node.InnerText).Trim();
            current.Append(ToXhtml(node));
            if (current.Length > 300_000) Flush(); // readers open very long chapters slowly
        }
        Flush();
        if (chapters.Count == 0) chapters.Add(new Chapter(title, "<p></p>"));

        Write(output, title, author, chapters, images, fixedLayout: null);
    }

    /// <summary>Top-level nodes, unwrapping section/div/article containers.</summary>
    static IEnumerable<HtmlNode> Flatten(HtmlNode parent)
    {
        foreach (var n in parent.ChildNodes)
        {
            if (n.NodeType == HtmlNodeType.Element && n.Name is "section" or "article" or "div" or "main" &&
                n.SelectNodes(".//h1|.//h2") != null)
            {
                foreach (var c in Flatten(n)) yield return c;
            }
            else if (n.NodeType == HtmlNodeType.Element || (n.NodeType == HtmlNodeType.Text && n.InnerText.Trim().Length > 0))
                yield return n;
        }
    }

    static string ToXhtml(HtmlNode node)
    {
        if (node.NodeType == HtmlNodeType.Text) return $"<p>{WebUtility.HtmlEncode(WebUtility.HtmlDecode(node.InnerText))}</p>";
        // HtmlAgilityPack XML output gives properly closed tags
        var tmp = new HtmlDocument { OptionOutputAsXml = true, OptionWriteEmptyNodes = true };
        tmp.LoadHtml(node.OuterHtml);
        var xml = tmp.DocumentNode.OuterHtml;
        xml = Regex.Replace(xml, @"^<\?xml[^>]*\?>", "");
        xml = Regex.Replace(xml, @"^\s*<span>(.*)</span>\s*$", "$1", RegexOptions.Singleline);
        return xml.Replace("&nbsp;", "&#160;");
    }

    /// <summary>Fixed-layout EPUB: every PDF page becomes an image.</summary>
    public static async Task FromPdfPagesAsync(string pdf, string output, string title, JobContext ctx)
    {
        var doc = await WinRt.OpenPdfAsync(pdf);
        var images = new List<(string, string, byte[])>();
        var chapters = new List<Chapter>();
        int w = 0, h = 0;
        for (uint i = 0; i < doc.PageCount; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            ctx.Report((double)i / doc.PageCount);
            var png = await WinRt.RenderPageAsync(doc, i, 150);
            using var mi = new ImageMagick.MagickImage(png);
            mi.Quality = 85;
            if (i == 0) { w = (int)mi.Width; h = (int)mi.Height; }
            var name = $"page{i + 1:0000}.jpg";
            images.Add((name, "image/jpeg", mi.ToByteArray(ImageMagick.MagickFormat.Jpeg)));
            chapters.Add(new Chapter(L.F("Page {0}", i + 1), L.F("<div class=\"page\"><img src=\"images/{0}\" alt=\"Page {1}\"/></div>", name, i + 1)));
        }
        Write(output, title, Environment.UserName, chapters, images, fixedLayout: (w, h));
    }

    static void Write(string output, string title, string author, List<Chapter> chapters,
        List<(string name, string mime, byte[] data)> images, (int w, int h)? fixedLayout)
    {
        var id = "urn:uuid:" + Guid.NewGuid();
        var lang = "ru";
        var t = WebUtility.HtmlEncode(title);
        using var fs = new FileStream(output, FileMode.CreateNew);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        // mimetype goes first and uncompressed
        var mt = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var w = new StreamWriter(mt.Open(), new UTF8Encoding(false))) w.Write("application/epub+zip");

        void Text(string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(content);
        }

        Text("META-INF/container.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """);

        var css = fixedLayout == null
            ? "body{font-family:serif;line-height:1.5;margin:0 4%;} h1,h2,h3{font-family:sans-serif;line-height:1.2;} img{max-width:100%;height:auto;} table{border-collapse:collapse;} td,th{border:1px solid #999;padding:2px 6px;} pre{white-space:pre-wrap;}"
            : "html,body{margin:0;padding:0;} .page{width:100%;height:100%;text-align:center;} .page img{width:100%;height:auto;}";
        Text("OEBPS/style.css", css);

        var viewport = fixedLayout is { } fl ? $"<meta name=\"viewport\" content=\"width={fl.w}, height={fl.h}\"/>" : "";
        for (var i = 0; i < chapters.Count; i++)
        {
            Text($"OEBPS/ch{i + 1:0000}.xhtml", $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE html>
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="{lang}" lang="{lang}">
                <head><meta charset="UTF-8"/>{viewport}<title>{WebUtility.HtmlEncode(chapters[i].Title)}</title><link rel="stylesheet" type="text/css" href="style.css"/></head>
                <body>
                {chapters[i].BodyXhtml}
                </body>
                </html>
                """);
        }
        foreach (var (name, _, data) in images)
        {
            var e = zip.CreateEntry($"OEBPS/images/{name}", CompressionLevel.NoCompression);
            using var s = e.Open();
            s.Write(data);
        }

        var navItems = string.Join("\n", chapters.Select((c, i) => $"      <li><a href=\"ch{i + 1:0000}.xhtml\">{WebUtility.HtmlEncode(c.Title)}</a></li>"));
        Text("OEBPS/nav.xhtml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="{lang}">
            <head><meta charset="UTF-8"/><title>{t}</title></head>
            <body>
              <nav epub:type="toc" id="toc"><h1>{WebUtility.HtmlEncode(L.T("Contents"))}</h1>
                <ol>
            {navItems}
                </ol>
              </nav>
            </body>
            </html>
            """);

        var ncxPoints = string.Join("\n", chapters.Select((c, i) =>
            $"    <navPoint id=\"np{i + 1}\" playOrder=\"{i + 1}\"><navLabel><text>{WebUtility.HtmlEncode(c.Title)}</text></navLabel><content src=\"ch{i + 1:0000}.xhtml\"/></navPoint>"));
        Text("OEBPS/toc.ncx", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
              <head><meta name="dtb:uid" content="{id}"/></head>
              <docTitle><text>{t}</text></docTitle>
              <navMap>
            {ncxPoints}
              </navMap>
            </ncx>
            """);

        var manifestItems = new StringBuilder();
        manifestItems.AppendLine("    <item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>");
        manifestItems.AppendLine("    <item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>");
        manifestItems.AppendLine("    <item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>");
        for (var i = 0; i < chapters.Count; i++)
            manifestItems.AppendLine($"    <item id=\"ch{i + 1}\" href=\"ch{i + 1:0000}.xhtml\" media-type=\"application/xhtml+xml\"/>");
        for (var i = 0; i < images.Count; i++)
            manifestItems.AppendLine($"    <item id=\"img{i + 1}\" href=\"images/{images[i].name}\" media-type=\"{images[i].mime}\"{(i == 0 ? " properties=\"cover-image\"" : "")}/>");
        var spineItems = string.Join("\n", chapters.Select((_, i) => $"    <itemref idref=\"ch{i + 1}\"/>"));
        var layoutMeta = fixedLayout != null
            ? "\n    <meta property=\"rendition:layout\">pre-paginated</meta>\n    <meta property=\"rendition:spread\">none</meta>"
            : "";

        Text("OEBPS/content.opf", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid" xml:lang="{lang}">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="bookid">{id}</dc:identifier>
                <dc:title>{t}</dc:title>
                <dc:creator>{WebUtility.HtmlEncode(author)}</dc:creator>
                <dc:language>{lang}</dc:language>
                <meta property="dcterms:modified">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</meta>{layoutMeta}
              </metadata>
              <manifest>
            {manifestItems}  </manifest>
              <spine toc="ncx">
            {spineItems}
              </spine>
            </package>
            """);
    }
}
