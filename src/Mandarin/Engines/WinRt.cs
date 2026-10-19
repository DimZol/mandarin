using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Mandarin.Core;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace Mandarin.Engines;

/// <summary>Built-in Windows features: PDF rendering, OCR, face detection. Everything works offline.</summary>
public static class WinRt
{
    public sealed record OcrWord(string Text, double X, double Y, double W, double H);
    public sealed record OcrLine(string Text, double X, double Y, double W, double H, IReadOnlyList<OcrWord> Words);

    public static async Task<WinPdf.PdfDocument> OpenPdfAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        try
        {
            return await WinPdf.PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B))
        {
            throw new UserFacingException(L.T("The PDF is password protected"));
        }
    }

    /// <summary>Renders a page to PNG bytes at the given DPI (or pixel width).</summary>
    public static async Task<byte[]> RenderPageAsync(WinPdf.PdfDocument doc, uint index, double dpi, uint? widthPx = null)
    {
        using var page = doc.GetPage(index);
        var opts = new WinPdf.PdfPageRenderOptions
        {
            DestinationWidth = widthPx ?? (uint)Math.Max(1, Math.Round(page.Size.Width / 96.0 * dpi)),
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            BitmapEncoderId = BitmapEncoder.PngEncoderId
        };
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, opts);
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    public static (double w, double h) PageSizePt(WinPdf.PdfDocument doc, uint index)
    {
        using var page = doc.GetPage(index);
        return (page.Size.Width * 72 / 96.0, page.Size.Height * 72 / 96.0);
    }

    /// <summary>HEIC encoding with the built-in Windows codec (HEIF and HEVC extensions).</summary>
    public static async Task<byte[]> EncodeHeifAsync(byte[] bgra, int width, int height, int quality)
    {
        using var stream = new InMemoryRandomAccessStream();
        var props = new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue(Math.Clamp(quality, 1, 100) / 100f, Windows.Foundation.PropertyType.Single) }
        };
        BitmapEncoder encoder;
        try
        {
            encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.HeifEncoderId, stream, props);
        }
        catch (Exception ex)
        {
            Core.Log.Error("HEIF encoder", ex);
            throw new UserFacingException(L.T("To save HEIC, install \"HEIF Image Extensions\" and \"HEVC Video Extensions\" from the Microsoft Store."));
        }
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    static OcrEngine? _ocr;

    public static OcrEngine Ocr
    {
        get
        {
            _ocr ??= OcrEngine.TryCreateFromUserProfileLanguages();
            if (_ocr == null)
                foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
                    if ((_ocr = OcrEngine.TryCreateFromLanguage(lang)) != null) break;
            return _ocr ?? throw new UserFacingException(
                L.T("Text recognition is unavailable: install a language pack with OCR in Windows Settings → Time & language → Language."));
        }
    }

    static async Task<SoftwareBitmap> DecodeAsync(byte[] image, BitmapPixelFormat format)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(image.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var bmp = await decoder.GetSoftwareBitmapAsync(format, BitmapAlphaMode.Premultiplied);
        return bmp;
    }

    /// <summary>Recognizes text in an image (PNG/JPG bytes). Coordinates are in image pixels.</summary>
    public static async Task<IReadOnlyList<OcrLine>> RecognizeAsync(byte[] image)
    {
        var engine = Ocr;
        using var bmp = await DecodeAsync(image, BitmapPixelFormat.Bgra8);
        SoftwareBitmap input = bmp;
        double scale = 1;
        var max = OcrEngine.MaxImageDimension;
        if (bmp.PixelWidth > max || bmp.PixelHeight > max)
        {
            // Windows OCR has a size limit - shrink with Magick
            using var mi = new ImageMagick.MagickImage(image);
            scale = (double)max / Math.Max(mi.Width, mi.Height);
            mi.Resize(new ImageMagick.Percentage(scale * 100));
            input = await DecodeAsync(mi.ToByteArray(ImageMagick.MagickFormat.Png), BitmapPixelFormat.Bgra8);
        }
        var result = await engine.RecognizeAsync(input);
        if (!ReferenceEquals(input, bmp)) input.Dispose();

        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            var words = line.Words.Select(w => new OcrWord(w.Text,
                w.BoundingRect.X / scale, w.BoundingRect.Y / scale, w.BoundingRect.Width / scale, w.BoundingRect.Height / scale)).ToList();
            if (words.Count == 0) continue;
            var x = words.Min(w => w.X); var y = words.Min(w => w.Y);
            var r = words.Max(w => w.X + w.W); var b = words.Max(w => w.Y + w.H);
            lines.Add(new OcrLine(line.Text, x, y, r - x, b - y, words));
        }
        return lines;
    }

    /// <summary>Face rectangles in the image (in pixels).</summary>
    public static async Task<IReadOnlyList<(double x, double y, double w, double h)>> DetectFacesAsync(byte[] image)
    {
        if (!FaceDetector.IsSupported) return Array.Empty<(double, double, double, double)>();
        using var mi = new ImageMagick.MagickImage(image);
        var origW = mi.Width;
        double scale = 1;
        if (Math.Max(mi.Width, mi.Height) > 1600)
        {
            scale = 1600.0 / Math.Max(mi.Width, mi.Height);
            mi.Resize(new ImageMagick.Percentage(scale * 100));
        }
        mi.Format = ImageMagick.MagickFormat.Png;
        using var bgra = await DecodeAsync(mi.ToByteArray(), BitmapPixelFormat.Bgra8);
        using var gray = SoftwareBitmap.Convert(bgra, BitmapPixelFormat.Gray8);
        var detector = await FaceDetector.CreateAsync();
        var faces = await detector.DetectFacesAsync(gray);
        return faces.Select(f =>
        {
            var b = f.FaceBox;
            // a small margin around the face to cover hair and chin
            double pad = b.Width * 0.18;
            return ((b.X - pad) / scale, (b.Y - pad * 1.4) / scale, (b.Width + pad * 2) / scale, (b.Height + pad * 2.6) / scale);
        }).ToList();
    }
}
