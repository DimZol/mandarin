using Mandarin.Engines;

namespace Mandarin.Core;

/// <summary>Conversion matrix: what can be turned into what, plus starting a conversion.</summary>
public static class Formats
{
    static readonly Dictionary<FileCategory, string[]> Targets = new()
    {
        [FileCategory.Image] = new[] { "jpg", "png", "webp", "heic", "tiff", "avif", "bmp", "pdf", "ico" },
        [FileCategory.Video] = new[] { "mp4", "mov", "mkv", "webm", "avi", "wmv", "gif", "mp3" },
        [FileCategory.Audio] = new[] { "mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma" },
        [FileCategory.Pdf] = new[] { "docx", "pptx", "xlsx", "jpg", "png", "epub", "txt", "md", "html" },
        [FileCategory.Document] = new[] { "docx", "odt", "pdf", "txt", "md", "html", "epub", "png", "jpg" },
        [FileCategory.Slides] = new[] { "pdf", "jpg", "png", "pptx" },
        [FileCategory.Spreadsheet] = new[] { "xlsx", "ods", "csv", "tsv", "pdf" },
        [FileCategory.Epub] = new[] { "pdf", "docx", "odt", "txt", "md", "html" },
        [FileCategory.Subtitle] = new[] { "srt", "vtt", "txt" },
        [FileCategory.Archive] = new[] { "zip", "tar", "gzip" },
        [FileCategory.Folder] = new[] { "zip", "tar", "gzip" },
    };

    /// <summary>Normalizes synonyms: jpeg → jpg, tif → tiff…</summary>
    public static string Canonical(string ext) => ext switch
    {
        "jpeg" or "jfif" or "jpe" => "jpg",
        "tif" => "tiff",
        "heif" => "heic",
        "htm" => "html",
        "markdown" => "md",
        "aif" => "aiff",
        "tgz" or "tar.gz" => "gzip",
        "oga" => "ogg",
        "pptm" or "ppsx" or "pps" => "pptx",
        "docm" => "docx",
        "xlsm" => "xlsx",
        _ => ext
    };

    public static IReadOnlyList<string> AllTargets(FileCategory c) =>
        Targets.TryGetValue(c, out var t) ? t : Array.Empty<string>();

    /// <summary>Formats available for the selected files.</summary>
    public static List<string> TargetsFor(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return new();
        var cats = files.Select(FileKinds.Classify).Distinct().ToList();
        if (cats.Contains(FileCategory.Unknown)) return new();

        List<string> list;
        if (cats.Count == 1)
        {
            list = AllTargets(cats[0]).ToList();
        }
        else
        {
            // mixed selection: only formats common to all (video + audio → MP3 and so on)
            IEnumerable<string> common = AllTargets(cats[0]);
            foreach (var c in cats.Skip(1)) common = common.Intersect(AllTargets(c));
            list = common.ToList();
        }

        // drop the format all files are already in
        var exts = files.Select(f => Canonical(FileKinds.Ext(f))).Distinct().ToList();
        if (exts.Count == 1) list.Remove(exts[0]);

        // special cases
        if (cats.Count == 1 && cats[0] == FileCategory.Image && files.All(f => FileKinds.Ext(f) == "gif"))
            list = new List<string> { "mp4", "webm", "png", "jpg", "webp", "pdf" };
        if (cats.Count == 1 && cats[0] == FileCategory.Slides && exts.All(e => e == "pptx")) list.Remove("pptx");
        if (cats.Count == 1 && cats[0] == FileCategory.Document && exts.All(e => e is "doc" or "rtf"))
            list = new List<string> { "docx", "pdf", "odt", "txt", "md", "html", "epub" };
        // only Word and LibreOffice can write ODT - hide the option without them
        if (!Engines.OfficeEngine.Native(Engines.OfficeApp.Word)) list.Remove("odt");
        return list;
    }

    public static string Label(string target) => target switch
    {
        "gzip" => "GZIP",
        _ => target.ToUpperInvariant()
    };

    /// <summary>Starts converting the selection to the chosen format.</summary>
    public static Job Convert(IReadOnlyList<string> files, string target)
    {
        var title = files.Count == 1
            ? $"{System.IO.Path.GetFileName(files[0])} → {Label(target)}"
            : L.F("Converting to {0} ({1})", Label(target), files.Count);
        var glyph = FileKinds.CategoryGlyph(FileKinds.Classify(files[0]));
        return JobManager.RunBatch(title, glyph, files, (f, ctx) => ConvertOne(f, target, ctx));
    }

    public static Task ConvertOne(string input, string target, JobContext ctx)
    {
        var cat = FileKinds.Classify(input);
        if (Canonical(FileKinds.Ext(input)) == target && cat != FileCategory.Folder)
            return Task.CompletedTask; // already in the target format - skip it, nothing gets overwritten

        return cat switch
        {
            FileCategory.Image => ImageEngine.ConvertAsync(input, target, ctx),
            FileCategory.Video or FileCategory.Audio => MediaEngine.ConvertAsync(input, target, ctx),
            FileCategory.Pdf => target is "jpg" or "png"
                ? PdfEngine.ToImagesAsync(input, target, ctx)
                : DocumentConverter.ConvertAsync(input, target, ctx),
            FileCategory.Document or FileCategory.Epub => DocumentConverter.ConvertAsync(input, target, ctx),
            FileCategory.Slides => DocumentConverter.SlidesConvertAsync(input, target, ctx),
            FileCategory.Spreadsheet => SheetEngine.ConvertAsync(input, target, ctx),
            FileCategory.Subtitle => SubtitleEngine.ConvertAsync(input, target, ctx),
            FileCategory.Archive or FileCategory.Folder => ArchiveEngine.ConvertAsync(input, target, ctx),
            _ => throw new UserFacingException(L.T("This file type is not supported"))
        };
    }
}
