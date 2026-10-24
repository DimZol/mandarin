using System.Net;
using System.Text;
using Mandarin.Core;

namespace Mandarin.Engines;

public static partial class DocumentConverter
{
    /// <summary>
    /// RTF without Office: a small reader of its text, paragraphs, bold, italics and underline (layout and tables are not
    /// kept). With LibreOffice installed RTF goes through it instead and keeps everything.
    /// </summary>
    static Task<string> RtfToHtmlAsync(string input) => Task.Run(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var rtf = File.ReadAllText(input, Encoding.Latin1);
        var body = new StringBuilder();
        var para = new StringBuilder();
        var encoding = Encoding.GetEncoding(1252);
        var bytes = new List<byte>();
        // formatting state per group
        var stack = new Stack<(bool b, bool i, bool u, bool skip, int uc)>();
        bool bold = false, italic = false, underline = false, skip = false;
        int uc = 1, skipChars = 0;
        bool openB = false, openI = false, openU = false;

        void FlushBytes()
        {
            if (bytes.Count == 0) return;
            Append(encoding.GetString(bytes.ToArray()));
            bytes.Clear();
        }
        void SyncTags()
        {
            if (openU && !underline) { para.Append("</u>"); openU = false; }
            if (openI && !italic) { para.Append("</em>"); openI = false; }
            if (openB && !bold) { para.Append("</strong>"); openB = false; }
            if (!openB && bold) { para.Append("<strong>"); openB = true; }
            if (!openI && italic) { para.Append("<em>"); openI = true; }
            if (!openU && underline) { para.Append("<u>"); openU = true; }
        }
        void Append(string text)
        {
            if (skip || text.Length == 0) return;
            SyncTags();
            para.Append(WebUtility.HtmlEncode(text));
        }
        void EndParagraph()
        {
            FlushBytes();
            bool b = bold, i = italic, u = underline;
            bold = italic = underline = false;
            SyncTags();
            bold = b; italic = i; underline = u;
            var text = para.ToString().Trim();
            body.Append(text.Length == 0 ? "" : $"<p>{text}</p>\n");
            para.Clear();
        }

        for (var pos = 0; pos < rtf.Length; pos++)
        {
            var c = rtf[pos];
            if (c == '{')
            {
                FlushBytes();
                stack.Push((bold, italic, underline, skip, uc));
                // destinations that are not text: fonts, colors, styles, pictures, metadata
                var rest = rtf.Substring(pos + 1, Math.Min(24, rtf.Length - pos - 1));
                if (rest.StartsWith("\\*") || rest.StartsWith("\\fonttbl") || rest.StartsWith("\\colortbl") || rest.StartsWith("\\stylesheet") ||
                    rest.StartsWith("\\info") || rest.StartsWith("\\pict") || rest.StartsWith("\\header") || rest.StartsWith("\\footer") ||
                    rest.StartsWith("\\listtable") || rest.StartsWith("\\listoverridetable") || rest.StartsWith("\\themedata") ||
                    rest.StartsWith("\\colorschememapping") || rest.StartsWith("\\latentstyles") || rest.StartsWith("\\datastore") ||
                    rest.StartsWith("\\xmlnstbl") || rest.StartsWith("\\rsidtbl") || rest.StartsWith("\\generator") || rest.StartsWith("\\object"))
                    skip = true;
                continue;
            }
            if (c == '}')
            {
                FlushBytes();
                if (stack.Count > 0) (bold, italic, underline, skip, uc) = stack.Pop();
                continue;
            }
            if (c == '\\' && pos + 1 < rtf.Length)
            {
                var n = rtf[pos + 1];
                if (n is '\\' or '{' or '}') { FlushBytes(); if (skipChars > 0) skipChars--; else Append(n.ToString()); pos++; continue; }
                if (n == '\'' && pos + 3 < rtf.Length)
                {
                    if (skipChars > 0) skipChars--;
                    else if (!skip && byte.TryParse(rtf.AsSpan(pos + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)) bytes.Add(b);
                    pos += 3;
                    continue;
                }
                if (n is '~') { FlushBytes(); Append(" "); pos++; continue; }
                if (n is '-' or '_') { pos++; continue; }
                if (n is '\r' or '\n') { EndParagraph(); pos++; continue; }
                // control word: letters, an optional signed number, an optional space
                var start = pos + 1;
                var end = start;
                while (end < rtf.Length && char.IsLetter(rtf[end])) end++;
                var word = rtf.Substring(start, end - start);
                var numStart = end;
                if (end < rtf.Length && (rtf[end] == '-' || char.IsDigit(rtf[end]))) { end++; while (end < rtf.Length && char.IsDigit(rtf[end])) end++; }
                int? num = end > numStart && int.TryParse(rtf.AsSpan(numStart, end - numStart), out var v) ? v : null;
                if (end < rtf.Length && rtf[end] == ' ') end++;
                pos = end - 1;
                switch (word)
                {
                    case "par" or "line" or "sect" or "page" or "row": EndParagraph(); break;
                    case "tab" or "cell": FlushBytes(); Append(" "); break;
                    case "b": FlushBytes(); bold = num != 0; break;
                    case "i": FlushBytes(); italic = num != 0; break;
                    case "ul": FlushBytes(); underline = num != 0; break;
                    case "ulnone": FlushBytes(); underline = false; break;
                    case "plain": FlushBytes(); bold = italic = underline = false; break;
                    case "ansicpg" when num is { } cp:
                        try { encoding = Encoding.GetEncoding(cp); } catch { /* unknown code page - keep the default */ }
                        break;
                    case "uc" when num is { } count: uc = count; break;
                    case "u" when num is { } code:
                        FlushBytes();
                        Append(((char)(code < 0 ? code + 65536 : code)).ToString());
                        skipChars = uc; // the ANSI substitute that follows
                        break;
                    case "emdash": FlushBytes(); Append("—"); break;
                    case "endash": FlushBytes(); Append("–"); break;
                    case "bullet": FlushBytes(); Append("•"); break;
                    case "lquote": FlushBytes(); Append("‘"); break;
                    case "rquote": FlushBytes(); Append("’"); break;
                    case "ldblquote": FlushBytes(); Append("“"); break;
                    case "rdblquote": FlushBytes(); Append("”"); break;
                }
                continue;
            }
            if (c is '\r' or '\n') continue;
            if (skipChars > 0) { skipChars--; continue; }
            if (skip) continue;
            if (c < 128) { FlushBytes(); Append(c.ToString()); }
            else bytes.Add((byte)c);
        }
        EndParagraph();
        return HtmlEngine.WrapDocument(FileKinds.BaseName(input), body.ToString());
    });
}
