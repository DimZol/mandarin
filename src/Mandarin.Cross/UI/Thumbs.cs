using Avalonia.Media.Imaging;
using ImageMagick;
using Mandarin.Core;
using Mandarin.Engines;

namespace Mandarin.UI;

/// <summary>File thumbnails: pictures through Magick.NET, a video frame through FFmpeg, the first PDF page through PDFium.</summary>
public static class Thumbs
{
    static readonly Dictionary<string, Bitmap?> Cache = new();

    public static Bitmap? Cached(string path, int size)
    {
        lock (Cache) return Cache.TryGetValue(Key(path, size), out var b) ? b : null;
    }

    static string Key(string path, int size) => $"{path}|{size}|{(File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0)}";

    public static async Task<Bitmap?> GetAsync(string path, int size)
    {
        var key = Key(path, size);
        lock (Cache) if (Cache.TryGetValue(key, out var cached)) return cached;
        Bitmap? result = null;
        try
        {
            var png = await Task.Run(async () => FileKinds.Classify(path) switch
            {
                FileCategory.Image => ImageThumb(path, size),
                FileCategory.Video when Ffmpeg.Available => await VideoThumbAsync(path, size),
                FileCategory.Pdf => await PdfThumbAsync(path, size),
                _ => null
            });
            if (png != null) result = new Bitmap(new MemoryStream(png));
        }
        catch { /* no thumbnail - the category icon is shown instead */ }
        lock (Cache)
        {
            if (Cache.Count > 300) Cache.Clear();
            Cache[key] = result;
        }
        return result;
    }

    static byte[] ImageThumb(string path, int size)
    {
        using var img = ImageEngine.Load(path);
        img.Thumbnail((uint)size, (uint)size);
        return img.ToByteArray(MagickFormat.Png);
    }

    static async Task<byte[]?> VideoThumbAsync(string path, int size)
    {
        var dir = OutputPaths.TempDir();
        try
        {
            var png = Path.Combine(dir, "frame.png");
            foreach (var at in new[] { "1", "0" })
            {
                await Ffmpeg.RunCaptureAsync(Ffmpeg.FfmpegPath!, new[] { "-hide_banner", "-loglevel", "error", "-ss", at, "-i", path, "-frames:v", "1",
                    "-vf", $"scale={size}:{size}:force_original_aspect_ratio=decrease", "-y", png }, CancellationToken.None);
                if (File.Exists(png)) return await File.ReadAllBytesAsync(png);
            }
            return null;
        }
        finally { OutputPaths.TryDelete(dir); }
    }

    static async Task<byte[]> PdfThumbAsync(string path, int size)
    {
        var doc = await WinRt.OpenPdfAsync(path);
        return await WinRt.RenderPageAsync(doc, 0, 0, (uint)size);
    }

    /// <summary>A bitmap from straight (not premultiplied) BGRA pixels.</summary>
    public static WriteableBitmap FromBgra(byte[] bgra, int width, int height)
    {
        var bmp = new WriteableBitmap(new Avalonia.PixelSize(width, height), new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        CopyInto(bmp, bgra);
        return bmp;
    }

    /// <summary>Replaces the pixels of a bitmap of the same size (straight alpha in, premultiplied inside).</summary>
    public static void CopyInto(WriteableBitmap bmp, byte[] bgra)
    {
        var width = bmp.PixelSize.Width;
        var row = new byte[width * 4];
        using var fb = bmp.Lock();
        for (var y = 0; y < bmp.PixelSize.Height; y++)
        {
            Buffer.BlockCopy(bgra, y * width * 4, row, 0, row.Length);
            for (var i = 0; i < row.Length; i += 4)
            {
                var a = row[i + 3];
                if (a == 255) continue;
                row[i] = (byte)(row[i] * a / 255); row[i + 1] = (byte)(row[i + 1] * a / 255); row[i + 2] = (byte)(row[i + 2] * a / 255);
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
    }

    /// <summary>A downscaled copy for display and the original dimensions.</summary>
    public static (Bitmap bmp, int fullW, int fullH) Preview(string file, int maxSide)
    {
        using var img = ImageEngine.Load(file);
        int fw = (int)img.Width, fh = (int)img.Height;
        if (img.Width > maxSide || img.Height > maxSide) img.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide));
        img.ColorSpace = ColorSpace.sRGB;
        using var pixels = img.GetPixelsUnsafe();
        var data = pixels.ToByteArray(PixelMapping.BGRA)!;
        return (FromBgra(data, (int)img.Width, (int)img.Height), fw, fh);
    }
}
