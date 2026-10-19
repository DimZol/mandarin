using System.IO;

namespace Mandarin.Core;

public enum FileCategory
{
    Unknown, Folder, Image, Video, Audio, Pdf, Document, Slides, Spreadsheet, Epub, Subtitle, Archive
}

public static class FileKinds
{
    static readonly Dictionary<string, FileCategory> Map = new(StringComparer.OrdinalIgnoreCase);

    static FileKinds()
    {
        void Add(FileCategory c, params string[] exts) { foreach (var e in exts) Map[e] = c; }

        Add(FileCategory.Image, "jpg", "jpeg", "jfif", "jpe", "png", "webp", "heic", "heif", "tif", "tiff", "avif",
            "bmp", "gif", "svg", "ico", "psd", "tga", "dng", "cr2", "cr3", "nef", "arw", "orf", "rw2", "raf", "jxl");
        Add(FileCategory.Video, "mp4", "mov", "mkv", "webm", "avi", "wmv", "m4v", "flv", "mpg", "mpeg", "3gp", "ts", "mts", "m2ts", "ogv");
        Add(FileCategory.Audio, "mp3", "m4a", "aac", "wav", "flac", "ogg", "oga", "opus", "aiff", "aif", "wma", "alac", "amr", "m4b");
        Add(FileCategory.Pdf, "pdf");
        Add(FileCategory.Document, "docx", "doc", "docm", "odt", "rtf", "md", "markdown", "txt", "html", "htm");
        Add(FileCategory.Slides, "pptx", "ppt", "pptm", "odp", "ppsx", "pps");
        Add(FileCategory.Spreadsheet, "xlsx", "xls", "xlsm", "ods", "csv", "tsv");
        Add(FileCategory.Epub, "epub");
        Add(FileCategory.Subtitle, "srt", "vtt");
        Add(FileCategory.Archive, "zip", "tar", "gz", "tgz", "tar.gz", "rar", "7z", "bz2", "tbz2", "tar.bz2", "xz", "txz", "tar.xz");
    }

    /// <summary>Lower-case extension without the dot; "tar.gz" for compound archive extensions.</summary>
    public static string Ext(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        foreach (var compound in new[] { "tar.gz", "tar.bz2", "tar.xz" })
            if (name.EndsWith("." + compound)) return compound;
        return Path.GetExtension(name).TrimStart('.');
    }

    /// <summary>File name without extension (aware of compound extensions such as .tar.gz).</summary>
    public static string BaseName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (Directory.Exists(path)) return name;
        var ext = Ext(path);
        return ext.Length == 0 ? name : name[..^(ext.Length + 1)];
    }

    public static FileCategory Classify(string path)
    {
        if (Directory.Exists(path)) return FileCategory.Folder;
        return Map.TryGetValue(Ext(path), out var c) ? c : FileCategory.Unknown;
    }

    public static bool IsAnimatedCandidate(string path) => Ext(path) is "gif" or "webp";

    public static string CategoryName(FileCategory c) => c switch
    {
        FileCategory.Image => L.T("Images"),
        FileCategory.Video => L.T("Video"),
        FileCategory.Audio => L.T("Audio"),
        FileCategory.Pdf => "PDF",
        FileCategory.Document => L.T("Documents"),
        FileCategory.Slides => L.T("Presentations"),
        FileCategory.Spreadsheet => L.T("Spreadsheets"),
        FileCategory.Epub => "EPUB",
        FileCategory.Subtitle => L.T("Subtitles"),
        FileCategory.Archive => L.T("Archives"),
        FileCategory.Folder => L.T("Folders"),
        _ => L.T("Files")
    };

    public static string CategoryGlyph(FileCategory c) => c switch
    {
        FileCategory.Image => "",
        FileCategory.Video => "",
        FileCategory.Audio => "",
        FileCategory.Pdf => "",
        FileCategory.Document => "",
        FileCategory.Slides => "",
        FileCategory.Spreadsheet => "",
        FileCategory.Epub => "",
        FileCategory.Subtitle => "",
        FileCategory.Archive => "",
        FileCategory.Folder => "",
        _ => ""
    };
}
