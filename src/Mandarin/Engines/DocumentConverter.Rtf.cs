using System.IO;
using System.Net;
using System.Text;
using System.Windows.Documents;
using Mandarin.Core;

namespace Mandarin.Engines;

public static partial class DocumentConverter
{
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
}
