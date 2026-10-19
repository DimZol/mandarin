using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Mandarin.Core;

namespace Mandarin.Engines;

/// <summary>SRT ⇄ VTT keeping the timing, plus text extraction.</summary>
public static class SubtitleEngine
{
    public sealed record Cue(TimeSpan Start, TimeSpan End, string Text, string? Settings);

    static readonly Regex TimeRx = new(
        @"(?<s>(\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})\s*-->\s*(?<e>(\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})(?<set>[^\r\n]*)", RegexOptions.Compiled);

    public static List<Cue> Parse(string path)
    {
        var text = SheetEngine.ReadTextAutoEncoding(path).Replace("\r\n", "\n").Replace('\r', '\n');
        var cues = new List<Cue>();
        foreach (var block in Regex.Split(text, @"\n\s*\n"))
        {
            var lines = block.Split('\n').ToList();
            var idx = lines.FindIndex(l => TimeRx.IsMatch(l));
            if (idx < 0) continue;
            var m = TimeRx.Match(lines[idx]);
            var body = string.Join("\n", lines.Skip(idx + 1)).Trim();
            if (body.Length == 0) continue;
            cues.Add(new Cue(ParseTime(m.Groups["s"].Value), ParseTime(m.Groups["e"].Value), body, m.Groups["set"].Value.Trim()));
        }
        return cues;
    }

    static TimeSpan ParseTime(string s)
    {
        s = s.Replace(',', '.');
        var parts = s.Split(':');
        double h = 0, m, sec;
        if (parts.Length == 3) { h = double.Parse(parts[0], CultureInfo.InvariantCulture); m = double.Parse(parts[1], CultureInfo.InvariantCulture); sec = double.Parse(parts[2], CultureInfo.InvariantCulture); }
        else { m = double.Parse(parts[0], CultureInfo.InvariantCulture); sec = double.Parse(parts[1], CultureInfo.InvariantCulture); }
        return TimeSpan.FromSeconds(h * 3600 + m * 60 + sec);
    }

    static string Fmt(TimeSpan t, char sep) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{sep}{t.Milliseconds:000}";

    public static Task ConvertAsync(string input, string target, JobContext ctx)
    {
        var cues = Parse(input);
        if (cues.Count == 0) throw new UserFacingException(L.T("No subtitles were found in the file"));
        var output = OutputPaths.File(input, target);
        var sb = new StringBuilder();
        switch (target)
        {
            case "srt":
                for (var i = 0; i < cues.Count; i++)
                {
                    sb.Append(i + 1).Append('\n');
                    sb.Append(Fmt(cues[i].Start, ',')).Append(" --> ").Append(Fmt(cues[i].End, ',')).Append('\n');
                    sb.Append(Regex.Replace(cues[i].Text, @"</?(c|v|lang|ruby|rt)[^>]*>", "")).Append("\n\n");
                }
                break;
            case "vtt":
                sb.Append("WEBVTT\n\n");
                foreach (var c in cues)
                {
                    sb.Append(Fmt(c.Start, '.')).Append(" --> ").Append(Fmt(c.End, '.'));
                    if (!string.IsNullOrEmpty(c.Settings) && FileKinds.Ext(input) == "vtt") sb.Append(' ').Append(c.Settings);
                    sb.Append('\n').Append(c.Text).Append("\n\n");
                }
                break;
            case "txt":
                string? prev = null;
                foreach (var c in cues)
                {
                    var plain = Regex.Replace(c.Text, @"<[^>]+>|\{\\[^}]*\}", "").Replace('\n', ' ').Trim();
                    if (plain.Length == 0 || plain == prev) continue;
                    sb.Append(plain).Append('\n');
                    prev = plain;
                }
                break;
            default:
                throw new UserFacingException(L.F("Subtitles cannot be saved as {0}", target.ToUpperInvariant()));
        }
        File.WriteAllText(output, sb.ToString().Replace("\n", Environment.NewLine), new UTF8Encoding(false));
        ctx.AddOutput(output);
        return Task.CompletedTask;
    }
}
