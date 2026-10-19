using System.IO;
using System.IO.Compression;
using System.Text;

namespace Mandarin.Engines;

/// <summary>Minimal but fully valid PPTX: one image slide per page (PDF → PowerPoint without Office).</summary>
public static class PptxWriter
{
    const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    const string NsP = "http://schemas.openxmlformats.org/presentationml/2006/main";
    const string NsRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <param name="slides">JPEG/PNG bytes of the slides</param>
    /// <param name="aspect">slide width/height</param>
    public static void Write(string output, IReadOnlyList<(byte[] data, bool png)> slides, double aspect, string title)
    {
        long cx = 12192000;
        long cy = (long)Math.Round(cx / aspect);
        if (cy > 51206400) { cy = 51206400; cx = (long)(cy * aspect); }
        if (cy < 914400) { cy = 914400; cx = (long)(cy * aspect); }

        using var fs = new FileStream(output, FileMode.CreateNew);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        void Text(string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
            w.Write(content);
        }

        var ct = new StringBuilder();
        ct.Append($"<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        ct.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        ct.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        ct.Append("<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>");
        ct.Append("<Default Extension=\"png\" ContentType=\"image/png\"/>");
        ct.Append("<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>");
        ct.Append("<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>");
        ct.Append("<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>");
        ct.Append("<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>");
        ct.Append("<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>");
        for (var i = 1; i <= slides.Count; i++)
            ct.Append($"<Override PartName=\"/ppt/slides/slide{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
        ct.Append("</Types>");
        Text("[Content_Types].xml", ct.ToString());

        Text("_rels/.rels", $"<Relationships xmlns=\"{NsRel}\">" +
            $"<Relationship Id=\"rId1\" Type=\"{RelBase}/officeDocument\" Target=\"ppt/presentation.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
            "</Relationships>");

        Text("docProps/core.xml",
            "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
            "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" " +
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            $"<dc:title>{System.Net.WebUtility.HtmlEncode(title)}</dc:title><dc:creator>Mandarin</dc:creator>" +
            $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</dcterms:created>" +
            "</cp:coreProperties>");

        var sldIds = new StringBuilder();
        var presRels = new StringBuilder($"<Relationships xmlns=\"{NsRel}\">");
        presRels.Append($"<Relationship Id=\"rId1\" Type=\"{RelBase}/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>");
        presRels.Append($"<Relationship Id=\"rId2\" Type=\"{RelBase}/theme\" Target=\"theme/theme1.xml\"/>");
        for (var i = 1; i <= slides.Count; i++)
        {
            sldIds.Append($"<p:sldId id=\"{255 + i}\" r:id=\"rId{i + 2}\"/>");
            presRels.Append($"<Relationship Id=\"rId{i + 2}\" Type=\"{RelBase}/slide\" Target=\"slides/slide{i}.xml\"/>");
        }
        presRels.Append("</Relationships>");

        Text("ppt/presentation.xml",
            $"<p:presentation xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\" saveSubsetFonts=\"1\">" +
            "<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>" +
            $"<p:sldIdLst>{sldIds}</p:sldIdLst>" +
            $"<p:sldSz cx=\"{cx}\" cy=\"{cy}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/>" +
            "<p:defaultTextStyle><a:defPPr><a:defRPr lang=\"ru-RU\"/></a:defPPr></p:defaultTextStyle>" +
            "</p:presentation>");
        Text("ppt/_rels/presentation.xml.rels", presRels.ToString());

        const string emptyTree =
            "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
            "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

        Text("ppt/slideMasters/slideMaster1.xml",
            $"<p:sldMaster xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\">" +
            $"<p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>{emptyTree}</p:spTree></p:cSld>" +
            "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" " +
            "accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
            "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>" +
            "<p:txStyles><p:titleStyle><a:lvl1pPr><a:defRPr sz=\"4400\"/></a:lvl1pPr></p:titleStyle>" +
            "<p:bodyStyle><a:lvl1pPr><a:defRPr sz=\"2800\"/></a:lvl1pPr></p:bodyStyle>" +
            "<p:otherStyle><a:lvl1pPr><a:defRPr sz=\"1800\"/></a:lvl1pPr></p:otherStyle></p:txStyles>" +
            "</p:sldMaster>");
        Text("ppt/slideMasters/_rels/slideMaster1.xml.rels", $"<Relationships xmlns=\"{NsRel}\">" +
            $"<Relationship Id=\"rId1\" Type=\"{RelBase}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
            $"<Relationship Id=\"rId2\" Type=\"{RelBase}/theme\" Target=\"../theme/theme1.xml\"/>" +
            "</Relationships>");

        Text("ppt/slideLayouts/slideLayout1.xml",
            $"<p:sldLayout xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\" type=\"blank\" preserve=\"1\">" +
            $"<p:cSld name=\"Blank\"><p:spTree>{emptyTree}</p:spTree></p:cSld>" +
            "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
        Text("ppt/slideLayouts/_rels/slideLayout1.xml.rels", $"<Relationships xmlns=\"{NsRel}\">" +
            $"<Relationship Id=\"rId1\" Type=\"{RelBase}/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/>" +
            "</Relationships>");

        Text("ppt/theme/theme1.xml", Theme);

        for (var i = 1; i <= slides.Count; i++)
        {
            var (data, png) = slides[i - 1];
            var media = $"image{i}.{(png ? "png" : "jpeg")}";
            var me = zip.CreateEntry($"ppt/media/{media}", CompressionLevel.NoCompression);
            using (var s = me.Open()) s.Write(data);

            Text($"ppt/slides/slide{i}.xml",
                $"<p:sld xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\"><p:cSld><p:spTree>{emptyTree}" +
                $"<p:pic><p:nvPicPr><p:cNvPr id=\"2\" name=\"Page {i}\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>" +
                "<p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>" +
                $"<p:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>" +
                "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
            Text($"ppt/slides/_rels/slide{i}.xml.rels", $"<Relationships xmlns=\"{NsRel}\">" +
                $"<Relationship Id=\"rId1\" Type=\"{RelBase}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
                $"<Relationship Id=\"rId2\" Type=\"{RelBase}/image\" Target=\"../media/{media}\"/>" +
                "</Relationships>");
        }
    }

    const string Theme =
        "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Mandarin\"><a:themeElements>" +
        "<a:clrScheme name=\"Mandarin\">" +
        "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
        "<a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2>" +
        "<a:accent1><a:srgbClr val=\"F07A2A\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2>" +
        "<a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4>" +
        "<a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6>" +
        "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
        "<a:fontScheme name=\"Mandarin\"><a:majorFont><a:latin typeface=\"Segoe UI\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
        "<a:minorFont><a:latin typeface=\"Segoe UI\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
        "<a:fmtScheme name=\"Mandarin\">" +
        "<a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst>" +
        "<a:lnStyleLst><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst>" +
        "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
        "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst>" +
        "</a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";
}
