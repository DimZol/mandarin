using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ClosedXML.Excel;
using ExcelDataReader;
using Mandarin.Core;

namespace Mandarin.Engines;

public sealed class Sheet
{
    public string Name { get; set; } = "Sheet1";
    public List<List<object?>> Rows { get; } = new();
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);
}

/// <summary>Spreadsheets: XLSX, XLS (read-only), ODS, CSV, TSV → each other and PDF.</summary>
public static class SheetEngine
{
    static SheetEngine() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static async Task ConvertAsync(string input, string target, JobContext ctx)
    {
        var src = FileKinds.Ext(input);
        var output = OutputPaths.File(input, target);

        if (target == "pdf")
        {
            // only Excel/LibreOffice keep sheet formatting; without them it is a plain table via HTML
            var native = src is "xlsx" or "xls" or "xlsm" or "ods" &&
                         await OfficeEngine.TrySaveAsAsync(OfficeApp.Excel, input, output, "pdf", e => ctx.Detail(L.T("Exporting with ") + e), ctx.Token);
            if (!native)
            {
                var sheets = await Task.Run(() => Read(input), ctx.Token);
                var html = ToHtml(sheets, FileKinds.BaseName(input));
                var wide = sheets.Any(s => s.ColumnCount > 7);
                await HtmlEngine.HtmlToPdfAsync(html, output, wide, ctx.Token);
            }
            ctx.AddOutput(output);
            return;
        }

        // Excel keeps formatting and formulas - use it for rich formats
        // Excel/LibreOffice keep formulas and formatting; the built-in path carries values only
        if (src is "xlsx" or "xls" or "xlsm" or "ods" && target is "xlsx" or "ods" &&
            await OfficeEngine.TrySaveAsAsync(OfficeApp.Excel, input, output, target, e => ctx.Detail(L.T("Converting with ") + e), ctx.Token))
        {
            ctx.AddOutput(output);
            return;
        }

        var data = await Task.Run(() => Read(input), ctx.Token);
        ctx.Report(0.5);
        if (target is "csv" or "tsv" && data.Count(s => s.Rows.Count > 0) > 1)
        {
            // CSV has no sheets - one file per sheet in a separate folder
            OutputPaths.TryDelete(output);
            var folder = OutputPaths.Folder(input, $" — {target.ToUpperInvariant()}");
            Directory.CreateDirectory(folder);
            foreach (var s in data.Where(s => s.Rows.Count > 0))
                WriteDelimited(s, OutputPaths.UniqueFile(folder, s.Name, target), target == "tsv" ? '\t' : ',');
            ctx.AddOutput(folder);
            return;
        }
        await Task.Run(() => Write(data, output, target), ctx.Token);
        ctx.AddOutput(output);
    }

    // ---------- Reading ----------

    public static List<Sheet> Read(string path) => FileKinds.Ext(path) switch
    {
        "xlsx" or "xlsm" => ReadXlsx(path),
        "xls" => ReadXls(path),
        "ods" => ReadOds(path),
        "tsv" => new List<Sheet> { ReadDelimited(path, '\t') },
        _ => new List<Sheet> { ReadDelimited(path, null) }
    };

    static List<Sheet> ReadXlsx(string path)
    {
        using var wb = new XLWorkbook(path);
        var result = new List<Sheet>();
        foreach (var ws in wb.Worksheets)
        {
            var sheet = new Sheet { Name = ws.Name };
            var used = ws.RangeUsed();
            if (used != null)
            {
                var lastRow = used.LastRow().RowNumber();
                var lastCol = used.LastColumn().ColumnNumber();
                for (var r = 1; r <= lastRow; r++)
                {
                    var row = new List<object?>();
                    for (var c = 1; c <= lastCol; c++)
                    {
                        var v = ws.Cell(r, c).Value;
                        row.Add(v.Type switch
                        {
                            XLDataType.Number => v.GetNumber(),
                            XLDataType.Boolean => v.GetBoolean(),
                            XLDataType.DateTime => v.GetDateTime(),
                            XLDataType.TimeSpan => v.GetTimeSpan().ToString(),
                            XLDataType.Text => v.GetText(),
                            XLDataType.Error => "#" + v.GetError(),
                            _ => null
                        });
                    }
                    TrimRow(row);
                    sheet.Rows.Add(row);
                }
            }
            result.Add(sheet);
        }
        return result;
    }

    static List<Sheet> ReadXls(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var result = new List<Sheet>();
        do
        {
            var sheet = new Sheet { Name = reader.Name ?? $"Sheet{result.Count + 1}" };
            while (reader.Read())
            {
                var row = new List<object?>();
                for (var c = 0; c < reader.FieldCount; c++) row.Add(reader.GetValue(c));
                TrimRow(row);
                sheet.Rows.Add(row);
            }
            result.Add(sheet);
        } while (reader.NextResult());
        return result;
    }

    static readonly XNamespace OfficeNs = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    static readonly XNamespace TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    static readonly XNamespace TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

    static List<Sheet> ReadOds(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("content.xml") ?? throw new UserFacingException(L.T("Damaged ODS file"));
        XDocument xml;
        using (var s = entry.Open()) xml = SafeXml.Load(s);
        var result = new List<Sheet>();
        foreach (var table in xml.Descendants(TableNs + "table"))
        {
            var sheet = new Sheet { Name = (string?)table.Attribute(TableNs + "name") ?? $"Sheet{result.Count + 1}" };
            foreach (var rowEl in table.Descendants(TableNs + "table-row"))
            {
                var row = new List<object?>();
                foreach (var cell in rowEl.Elements().Where(e => e.Name == TableNs + "table-cell" || e.Name == TableNs + "covered-table-cell"))
                {
                    var repeat = Math.Min((int?)cell.Attribute(TableNs + "number-columns-repeated") ?? 1, 1024);
                    object? value = null;
                    var type = (string?)cell.Attribute(OfficeNs + "value-type");
                    var text = string.Join("\n", cell.Elements(TextNs + "p").Select(p => p.Value));
                    switch (type)
                    {
                        case "float" or "percentage" or "currency":
                            value = double.TryParse((string?)cell.Attribute(OfficeNs + "value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : text;
                            break;
                        case "date":
                            value = DateTime.TryParse((string?)cell.Attribute(OfficeNs + "date-value"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : text;
                            break;
                        case "boolean":
                            value = (string?)cell.Attribute(OfficeNs + "boolean-value") == "true";
                            break;
                        default:
                            value = text.Length == 0 ? null : text;
                            break;
                    }
                    for (var i = 0; i < repeat; i++) row.Add(value);
                }
                TrimRow(row);
                var rowRepeat = Math.Min((int?)rowEl.Attribute(TableNs + "number-rows-repeated") ?? 1, row.Count == 0 ? 1 : 1000);
                for (var i = 0; i < rowRepeat; i++) sheet.Rows.Add(new List<object?>(row));
            }
            while (sheet.Rows.Count > 0 && sheet.Rows[^1].Count == 0) sheet.Rows.RemoveAt(sheet.Rows.Count - 1);
            result.Add(sheet);
        }
        return result;
    }

    static void TrimRow(List<object?> row)
    {
        while (row.Count > 0 && (row[^1] == null || row[^1] is string { Length: 0 } || row[^1] is DBNull)) row.RemoveAt(row.Count - 1);
        for (var i = 0; i < row.Count; i++) if (row[i] is DBNull) row[i] = null;
    }

    public static string ReadTextAutoEncoding(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251).GetString(bytes); // legacy single-byte (Windows-1251) CSV/TXT files
        }
    }

    static Sheet ReadDelimited(string path, char? delimiter)
    {
        var text = ReadTextAutoEncoding(path);
        var firstLine = text.Split('\n').FirstOrDefault() ?? "";
        var d = delimiter ?? new[] { ',', ';', '\t', '|' }.OrderByDescending(c => firstLine.Count(x => x == c)).First();
        var sheet = new Sheet { Name = FileKinds.BaseName(path) };
        var row = new List<object?>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            if (c == '"' && field.Length == 0) quoted = true;
            else if (c == d) { row.Add(Typed(field.ToString())); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(Typed(field.ToString())); field.Clear();
                TrimRow(row);
                sheet.Rows.Add(row);
                row = new List<object?>();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(Typed(field.ToString())); TrimRow(row); sheet.Rows.Add(row); }
        while (sheet.Rows.Count > 0 && sheet.Rows[^1].Count == 0) sheet.Rows.RemoveAt(sheet.Rows.Count - 1);
        return sheet;
    }

    static object? Typed(string s)
    {
        if (s.Length == 0) return null;
        if (s.Length < 20 && !s.StartsWith('0') || s == "0" || s.StartsWith("0.") || s.StartsWith("0,"))
        {
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        }
        return s;
    }

    // ---------- Writing ----------

    public static void Write(List<Sheet> sheets, string output, string target)
    {
        switch (target)
        {
            case "xlsx": WriteXlsx(sheets, output); break;
            case "ods": WriteOds(sheets, output); break;
            case "tsv": WriteDelimited(sheets.FirstOrDefault(s => s.Rows.Count > 0) ?? new Sheet(), output, '\t'); break;
            case "csv": WriteDelimited(sheets.FirstOrDefault(s => s.Rows.Count > 0) ?? new Sheet(), output, ','); break;
            default: throw new UserFacingException(L.F("A spreadsheet cannot be saved as {0}", target.ToUpperInvariant()));
        }
    }

    public static void WriteXlsx(List<Sheet> sheets, string output)
    {
        using var wb = new XLWorkbook();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sheets)
        {
            var name = SafeSheetName(s.Name, names);
            var ws = wb.Worksheets.Add(name);
            for (var r = 0; r < s.Rows.Count; r++)
                for (var c = 0; c < s.Rows[r].Count; c++)
                {
                    var cell = ws.Cell(r + 1, c + 1);
                    switch (s.Rows[r][c])
                    {
                        case null: break;
                        case double d: cell.Value = d; break;
                        case int i: cell.Value = i; break;
                        case long l: cell.Value = l; break;
                        case decimal m: cell.Value = (double)m; break;
                        case float f: cell.Value = f; break;
                        case bool b: cell.Value = b; break;
                        case DateTime dt: cell.Value = dt; break;
                        case var o: cell.Value = o.ToString(); break;
                    }
                }
            if (s.Rows.Count > 0)
            {
                ws.Row(1).Style.Font.Bold = s.Rows.Count > 1;
                ws.Columns(1, Math.Max(1, s.ColumnCount)).AdjustToContents(1, Math.Min(s.Rows.Count, 200), 6, 60);
            }
        }
        if (sheets.Count == 0) wb.Worksheets.Add("Sheet1");
        wb.SaveAs(output);
    }

    static string SafeSheetName(string name, HashSet<string> used)
    {
        foreach (var c in new[] { ':', '\\', '/', '?', '*', '[', ']' }) name = name.Replace(c, '_');
        name = name.Trim().Trim('\'');
        if (name.Length == 0) name = "Sheet";
        if (name.Length > 31) name = name[..31];
        var baseName = name;
        for (var i = 2; !used.Add(name); i++)
        {
            var suffix = $" ({i})";
            name = (baseName.Length + suffix.Length > 31 ? baseName[..(31 - suffix.Length)] : baseName) + suffix;
        }
        return name;
    }

    public static void WriteDelimited(Sheet sheet, string output, char delimiter)
    {
        var sb = new StringBuilder();
        foreach (var row in sheet.Rows)
        {
            sb.Append(string.Join(delimiter, row.Select(v =>
            {
                var s = Format(v);
                if (delimiter == '\t') return s.Replace('\t', ' ').Replace("\r", "").Replace('\n', ' ');
                return s.IndexOfAny(new[] { delimiter, '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
            })));
            sb.Append("\r\n");
        }
        // the BOM makes Excel read non-Latin text correctly
        File.WriteAllText(output, sb.ToString(), new UTF8Encoding(true));
    }

    public static string Format(object? v) => v switch
    {
        null => "",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd") : dt.ToString("yyyy-MM-dd HH:mm:ss"),
        _ => v.ToString() ?? ""
    };

    public static void WriteOds(List<Sheet> sheets, string output)
    {
        using var fs = new FileStream(output, FileMode.CreateNew);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var mt = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var w = new StreamWriter(mt.Open(), new UTF8Encoding(false))) w.Write("application/vnd.oasis.opendocument.spreadsheet");

        var manifest = zip.CreateEntry("META-INF/manifest.xml");
        using (var w = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
            w.Write("""
                <?xml version="1.0" encoding="UTF-8"?>
                <manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.2">
                 <manifest:file-entry manifest:full-path="/" manifest:version="1.2" manifest:media-type="application/vnd.oasis.opendocument.spreadsheet"/>
                 <manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/>
                </manifest:manifest>
                """);

        var content = zip.CreateEntry("content.xml");
        using var stream = content.Open();
        using var x = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        const string office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        const string table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        const string text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        x.WriteStartDocument();
        x.WriteStartElement("office", "document-content", office);
        x.WriteAttributeString("xmlns", "table", null, table);
        x.WriteAttributeString("xmlns", "text", null, text);
        x.WriteAttributeString("office", "version", office, "1.2");
        x.WriteStartElement("office", "body", office);
        x.WriteStartElement("office", "spreadsheet", office);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sheets.DefaultIfEmpty(new Sheet()))
        {
            x.WriteStartElement("table", "table", table);
            x.WriteAttributeString("table", "name", table, SafeSheetName(s.Name, names));
            foreach (var row in s.Rows)
            {
                x.WriteStartElement("table", "table-row", table);
                if (row.Count == 0)
                {
                    x.WriteStartElement("table", "table-cell", table);
                    x.WriteEndElement();
                }
                foreach (var v in row)
                {
                    x.WriteStartElement("table", "table-cell", table);
                    switch (v)
                    {
                        case null: break;
                        case double or int or long or float or decimal:
                            var num = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                            x.WriteAttributeString("office", "value-type", office, "float");
                            x.WriteAttributeString("office", "value", office, num.ToString("R", CultureInfo.InvariantCulture));
                            x.WriteElementString("text", "p", text, num.ToString(CultureInfo.InvariantCulture));
                            break;
                        case bool b:
                            x.WriteAttributeString("office", "value-type", office, "boolean");
                            x.WriteAttributeString("office", "boolean-value", office, b ? "true" : "false");
                            x.WriteElementString("text", "p", text, b ? "TRUE" : "FALSE");
                            break;
                        case DateTime dt:
                            x.WriteAttributeString("office", "value-type", office, "date");
                            x.WriteAttributeString("office", "date-value", office, dt.ToString("yyyy-MM-ddTHH:mm:ss"));
                            x.WriteElementString("text", "p", text, Format(dt));
                            break;
                        default:
                            x.WriteAttributeString("office", "value-type", office, "string");
                            foreach (var line in (v.ToString() ?? "").Split('\n'))
                                x.WriteElementString("text", "p", text, line);
                            break;
                    }
                    x.WriteEndElement();
                }
                x.WriteEndElement();
            }
            x.WriteEndElement();
        }
        x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
        x.WriteEndDocument();
    }

    public static string ToHtml(List<Sheet> sheets, string title)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var s in sheets.Where(s => s.Rows.Count > 0))
        {
            if (!first) sb.Append("<div style=\"page-break-before: always\"></div>");
            first = false;
            if (sheets.Count > 1) sb.Append($"<h2 class=\"sheet-title\">{WebUtility.HtmlEncode(s.Name)}</h2>");
            sb.Append("<table>");
            var cols = s.ColumnCount;
            foreach (var row in s.Rows)
            {
                sb.Append("<tr>");
                for (var c = 0; c < cols; c++)
                {
                    var v = c < row.Count ? row[c] : null;
                    var align = v is double or int or long or float or decimal ? " style=\"text-align:right\"" : "";
                    sb.Append($"<td{align}>{WebUtility.HtmlEncode(Format(v))}</td>");
                }
                sb.Append("</tr>");
            }
            sb.Append("</table>");
        }
        return HtmlEngine.WrapDocument(title, sb.ToString(),
            "@page { size: A4 landscape; margin: 12mm; } body{max-width:none;font-size:9pt;} td{white-space:nowrap;}");
    }
}
