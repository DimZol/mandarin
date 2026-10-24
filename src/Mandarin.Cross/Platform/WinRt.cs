using System.Diagnostics;
using System.Globalization;
using Docnet.Core;
using Docnet.Core.Exceptions;
using Docnet.Core.Models;
using ImageMagick;
using Mandarin.Core;

namespace Mandarin.Engines;

/// <summary>
/// Cross-platform stand-in for the built-in Windows features of the main app: PDF pages are rendered with PDFium,
/// text is recognized with Tesseract (when installed), HEIC is written by Magick.NET. Face detection exists only in
/// the Windows 10/11 app. Everything works offline.
/// </summary>
public static class WinRt
{
    public sealed record OcrWord(string Text, double X, double Y, double W, double H);
    public sealed record OcrLine(string Text, double X, double Y, double W, double H, IReadOnlyList<OcrWord> Words);

    /// <summary>An opened PDF: the path and the page sizes in points.</summary>
    public sealed class PdfDoc
    {
        internal PdfDoc(string path, (double w, double h)[] sizes) { Path = path; Sizes = sizes; }
        public string Path { get; }
        internal (double w, double h)[] Sizes { get; }
        public uint PageCount => (uint)Sizes.Length;
    }

    // PDFium is not thread-safe
    static readonly object Gate = new();

    static UserFacingException Explain(Exception ex) =>
        ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0
            ? new UserFacingException(L.T("The PDF is password protected"))
            : new UserFacingException(L.T("Could not open the PDF: ") + ex.Message);

    public static Task<PdfDoc> OpenPdfAsync(string path) => Task.Run(() =>
    {
        try
        {
            lock (Gate)
            {
                using var reader = DocLib.Instance.GetDocReader(System.IO.Path.GetFullPath(path), new PageDimensions(1));
                var sizes = new (double, double)[reader.GetPageCount()];
                for (var i = 0; i < sizes.Length; i++)
                {
                    using var page = reader.GetPageReader(i);
                    sizes[i] = (page.GetPageWidth(), page.GetPageHeight());
                }
                return new PdfDoc(path, sizes);
            }
        }
        catch (DocnetException ex) { throw Explain(ex); }
    });

    /// <summary>Renders a page to PNG bytes at the given DPI (or pixel width) on a white background.</summary>
    public static Task<byte[]> RenderPageAsync(PdfDoc doc, uint index, double dpi, uint? widthPx = null) => Task.Run(() =>
    {
        var (wPt, _) = doc.Sizes[index];
        var scale = widthPx is { } w ? w / Math.Max(1, wPt) : dpi / 72.0;
        byte[] bgra;
        int width, height;
        try
        {
            lock (Gate)
            {
                using var reader = DocLib.Instance.GetDocReader(System.IO.Path.GetFullPath(doc.Path), new PageDimensions(scale));
                using var page = reader.GetPageReader((int)index);
                width = page.GetPageWidth();
                height = page.GetPageHeight();
                bgra = page.GetImage(RenderFlags.RenderAnnotations);
            }
        }
        catch (DocnetException ex) { throw Explain(ex); }
        using var img = new MagickImage();
        img.ReadPixels(bgra, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
        // PDFium leaves the paper transparent
        img.ColorAlpha(MagickColors.White);
        img.HasAlpha = false;
        return img.ToByteArray(MagickFormat.Png);
    });

    public static (double w, double h) PageSizePt(PdfDoc doc, uint index) => doc.Sizes[index];

    /// <summary>HEIC through Magick.NET; without its encoder the format is unavailable.</summary>
    public static Task<byte[]> EncodeHeifAsync(byte[] bgra, int width, int height, int quality) => Task.Run(() =>
    {
        try
        {
            using var img = new MagickImage();
            img.ReadPixels(bgra, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
            img.Quality = (uint)Math.Clamp(quality, 1, 100);
            return img.ToByteArray(MagickFormat.Heic);
        }
        catch (MagickException ex)
        {
            Log.Error("HEIC encoder", ex);
            throw new UserFacingException(L.T("Saving HEIC is not available on this system - choose AVIF: it is a related format and the files are even smaller."));
        }
    });

    // ---------- text recognition (Tesseract) ----------

    static string? _tesseract;

    /// <summary>The tesseract program, if installed.</summary>
    public static string? TesseractPath => _tesseract ??= FindTesseract();

    public static bool OcrAvailable => TesseractPath != null;

    static string? FindTesseract()
    {
        var exe = OsInfo.Exe("tesseract");
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim('"')).Where(System.IO.Path.IsPathFullyQualified).ToList();
        if (OsInfo.IsWindows)
        {
            dirs.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR"));
            dirs.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Tesseract-OCR"));
        }
        else dirs.AddRange(new[] { "/usr/bin", "/usr/local/bin" });
        return dirs.Select(d => System.IO.Path.Combine(d, exe)).FirstOrDefault(File.Exists);
    }

    static string? _langs;

    /// <summary>Recognition languages: English, Russian and the interface language - those that are installed.</summary>
    static string Languages()
    {
        if (_langs != null) return _langs;
        var installed = new HashSet<string>();
        try
        {
            var (_, output) = Run(TesseractPath!, new[] { "--list-langs" }, 15);
            foreach (var line in output.Split('\n')) installed.Add(line.Trim());
        }
        catch { /* use the default */ }
        var wanted = new[] { "eng", "rus", CultureInfo.CurrentUICulture.ThreeLetterISOLanguageName }.Distinct().Where(installed.Contains).ToList();
        return _langs = wanted.Count > 0 ? string.Join("+", wanted) : "eng";
    }

    static (int code, string stdout) Run(string exe, IEnumerable<string> args, int timeoutSec)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSec * 1000))
        {
            try { p.Kill(true); } catch { /* already exited */ }
            throw new UserFacingException(L.T("Text recognition did not finish in time"));
        }
        return (p.ExitCode, stdout.Result);
    }

    /// <summary>Recognizes text in an image (PNG/JPG bytes). Coordinates are in image pixels.</summary>
    public static Task<IReadOnlyList<OcrLine>> RecognizeAsync(byte[] image) => Task.Run<IReadOnlyList<OcrLine>>(() =>
    {
        if (TesseractPath == null)
            throw new UserFacingException(OsInfo.IsLinux
                ? L.T("Text recognition needs Tesseract: install it with your package manager (for example \"sudo apt install tesseract-ocr tesseract-ocr-rus\").")
                : L.T("Text recognition needs Tesseract OCR: install it (github.com/UB-Mannheim/tesseract) and restart Mandarin."));
        var dir = OutputPaths.TempDir();
        try
        {
            var png = System.IO.Path.Combine(dir, "page.png");
            File.WriteAllBytes(png, image);
            var (code, tsv) = Run(TesseractPath, new[] { png, "stdout", "-l", Languages(), "tsv" }, 300);
            if (code != 0) throw new UserFacingException(L.T("Text recognition failed"));
            // level page block par line word left top width height conf text
            var lines = new List<OcrLine>();
            foreach (var group in tsv.Split('\n').Skip(1)
                         .Select(l => l.TrimEnd('\r').Split('\t'))
                         .Where(c => c.Length >= 12 && c[0] == "5" && c[11].Trim().Length > 0)
                         .GroupBy(c => (c[2], c[3], c[4])))
            {
                var words = group.Select(c => new OcrWord(c[11].Trim(),
                    double.Parse(c[6], CultureInfo.InvariantCulture), double.Parse(c[7], CultureInfo.InvariantCulture),
                    double.Parse(c[8], CultureInfo.InvariantCulture), double.Parse(c[9], CultureInfo.InvariantCulture))).ToList();
                var x = words.Min(w => w.X); var y = words.Min(w => w.Y);
                var r = words.Max(w => w.X + w.W); var b = words.Max(w => w.Y + w.H);
                lines.Add(new OcrLine(string.Join(" ", words.Select(w => w.Text)), x, y, r - x, b - y, words));
            }
            return lines;
        }
        finally { OutputPaths.TryDelete(dir); }
    });

    /// <summary>Face detection is a built-in feature of Windows 10/11 - here areas are marked by hand.</summary>
    public static Task<IReadOnlyList<(double x, double y, double w, double h)>> DetectFacesAsync(byte[] image) =>
        throw new UserFacingException(L.T("Face detection is available in Mandarin for Windows 10/11. Mark the areas by hand - drag around them with the mouse."));
}
