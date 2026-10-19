using System.IO;
using Mandarin.Core;
using Mandarin.Interop;

namespace Mandarin.UI;

/// <summary>Wheel item.</summary>
public sealed record WheelItem(string Label, string Glyph, string Title, Action<IReadOnlyList<string>> Run, string? Hint = null);

/// <summary>What the wheel shows for the selected files in each mode.</summary>
public static class Catalog
{
    public static string ModeTitle(WheelMode m) => m switch
    {
        WheelMode.Convert => L.T("Convert"),
        WheelMode.Tools => L.T("Tools"),
        _ => L.T("Actions")
    };

    public static TriggerKey ModeKey(WheelMode m) => m switch
    {
        WheelMode.Convert => AppSettings.Current.ConvertKey,
        WheelMode.Tools => AppSettings.Current.ToolsKey,
        _ => AppSettings.Current.ActionsKey
    };

    public static List<WheelItem> Items(WheelMode mode, IReadOnlyList<string> files) => mode switch
    {
        WheelMode.Convert => ConvertItems(files),
        WheelMode.Tools => ToolItems(files),
        _ => ActionItems(files)
    };

    static List<WheelItem> ConvertItems(IReadOnlyList<string> files)
    {
        var targets = Formats.TargetsFor(files);
        return targets.Select(t =>
        {
            var label = Formats.Label(t);
            var glyph = t switch
            {
                "pdf" => "",
                "mp3" or "m4a" or "wav" or "flac" or "ogg" or "opus" or "aiff" or "wma" => "",
                "mp4" or "mov" or "mkv" or "webm" or "avi" or "wmv" => "",
                "gif" => "",
                "zip" or "tar" or "gzip" => "",
                "docx" or "odt" or "txt" or "md" or "html" or "rtf" => "",
                "epub" => "",
                "xlsx" or "ods" or "csv" or "tsv" => "",
                "pptx" => "",
                "srt" or "vtt" => "",
                _ => ""
            };
            var title = files.Count > 1 ? L.F("Convert {0} to {1}", files.Count, label) : L.F("Convert to {0}", label);
            return new WheelItem(label, glyph, title, f => Formats.Convert(f, t));
        }).ToList();
    }

    static List<WheelItem> ToolItems(IReadOnlyList<string> files)
    {
        var cats = files.Select(FileKinds.Classify).Distinct().ToList();
        var list = new List<WheelItem>();
        var many = files.Count > 1;
        if (cats.Count != 1) return list;

        switch (cats[0])
        {
            case FileCategory.Image:
                list.Add(new(L.T("COMPRESS"), "", L.T("Compress images"), ToolLauncher.ImageCompress));
                list.Add(new(L.T("METADATA"), "", L.T("Metadata"), ToolLauncher.ImageMetadata));
                if (!many)
                {
                    list.Add(new(L.T("EDIT"), "", L.T("Edit the photo"), ToolLauncher.PhotoEditor));
                    list.Add(new(L.T("ANNOTATE"), "", L.T("Annotate"), ToolLauncher.Annotate));
                }
                list.Add(new(L.T("BACKGROUND"), "", L.T("Add a background"), ToolLauncher.AddBackground));
                list.Add(new(L.T("CROP"), "", L.T("Crop images"), ToolLauncher.Crop));
                if (!many) list.Add(new(L.T("REDACT"), "", L.T("Redact faces and text"), ToolLauncher.RedactImage));
                list.Add(new(L.T("ROTATE"), "", L.T("Rotate 90°"), ToolLauncher.RotateImages));
                if (many)
                {
                    list.Add(new(L.T("TO PDF"), "", L.T("Combine into one PDF"), ToolLauncher.ImagesToPdf));
                    list.Add(new(L.T("COLLAGE"), "", L.T("Make a collage"), ToolLauncher.Collage));
                }
                break;

            case FileCategory.Video:
                list.Add(new(L.T("COMPRESS"), "", L.T("Compress video"), ToolLauncher.VideoCompress));
                list.Add(new(L.T("METADATA"), "", L.T("Metadata"), ToolLauncher.MediaMetadata));
                list.Add(new(L.T("MUTE"), "", L.T("Remove the sound"), ToolLauncher.Mute));
                if (!many)
                {
                    list.Add(new(L.T("TRIM"), "", L.T("Trim by time"), ToolLauncher.TrimVideo));
                    list.Add(new(L.T("FRAME"), "", L.T("Crop the video"), ToolLauncher.CropVideo));
                    list.Add(new(L.T("SPEED"), "", L.T("Playback speed"), ToolLauncher.Speed));
                    list.Add(new(L.T("SNAPSHOT"), "", L.T("Save frames"), ToolLauncher.Snapshot));
                    list.Add(new(L.T("SPLIT"), "", L.T("Split into parts"), ToolLauncher.SplitVideo));
                    list.Add(new(L.T("REDACT"), "", L.T("Redact an area"), ToolLauncher.RedactVideo));
                }
                else list.Add(new(L.T("JOIN"), "", L.T("Join videos"), ToolLauncher.JoinVideos));
                break;

            case FileCategory.Audio:
                list.Add(new(L.T("COMPRESS"), "", L.T("Compress audio"), ToolLauncher.AudioCompress));
                list.Add(new(L.T("METADATA"), "", L.T("Tags and metadata"), ToolLauncher.MediaMetadata));
                list.Add(new(L.T("LOUDNESS"), "", L.T("Normalize loudness"), ToolLauncher.Normalize));
                if (!many) list.Add(new(L.T("VISUALIZE"), "", L.T("Video with a waveform"), ToolLauncher.Visualize));
                if (!many) list.Add(new(L.T("TRIM"), "", L.T("Trim the audio"), ToolLauncher.TrimAudio));
                list.Add(new(L.T("CHANNELS"), "", L.T("Mono / stereo"), ToolLauncher.Channels));
                if (!many) list.Add(new(L.T("BLEEP"), "", L.T("Bleep fragments"), ToolLauncher.Bleep));
                if (many) list.Add(new(L.T("JOIN"), "", L.T("Join audio"), ToolLauncher.JoinAudio));
                break;

            case FileCategory.Pdf:
                list.Add(new(L.T("COMPRESS"), "", L.T("Compress PDF"), ToolLauncher.PdfCompress));
                list.Add(new(L.T("METADATA"), "", L.T("Document properties"), ToolLauncher.PdfMetadata));
                list.Add(new(L.T("SPLIT"), "", L.T("Every page as a separate PDF"), ToolLauncher.PdfSplit));
                if (!many) list.Add(new(L.T("PAGES"), "", L.T("Organize pages"), ToolLauncher.PdfOrganize));
                if (many) list.Add(new(L.T("MERGE"), "", L.T("Merge PDFs"), ToolLauncher.PdfMerge));
                break;

            case FileCategory.Archive:
                list.Add(new(L.T("EXTRACT"), "", L.T("Extract the archive"), ToolLauncher.Extract));
                break;

            case FileCategory.Subtitle:
            case FileCategory.Document:
            case FileCategory.Spreadsheet:
            case FileCategory.Slides:
            case FileCategory.Epub:
            case FileCategory.Folder:
            case FileCategory.Unknown:
                break;
        }
        return list;
    }

    static List<WheelItem> ActionItems(IReadOnlyList<string> files) => new()
    {
        new(L.T("SHARE"), "", L.T("Share (nearby, mail, apps)"), FileActions.Share),
        new(L.T("SHELF"), "", L.T("Put on the shelf"), FileActions.Float),
        new(L.T("COPY"), "", L.T("Copy to the clipboard"), FileActions.Copy),
        new(L.T("MOVE"), "", L.T("Move to a folder…"), FileActions.Move),
        new(L.T("PATH"), "", L.T("Copy path"), FileActions.CopyPath),
        new(L.T("TERMINAL"), "", L.T("Open in Terminal"), FileActions.Terminal),
        new("ZIP", "", files.Count > 1 ? L.T("Pack the selection into ZIP") : L.T("Pack into ZIP"), FileActions.Zip),
    };

    public static string Describe(IReadOnlyList<string> files) =>
        files.Count switch
        {
            0 => "",
            1 => Path.GetFileName(files[0].TrimEnd('\\')),
            _ => $"{files.Count} {L.Plural(files.Count, "file", "files")}"
        };

    public static string Plural(int n, string one, string few, string many)
    {
        var m10 = n % 10; var m100 = n % 100;
        if (m10 == 1 && m100 != 11) return one;
        if (m10 is >= 2 and <= 4 && m100 is < 12 or > 14) return few;
        return many;
    }
}
