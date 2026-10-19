using System.IO;
using ImageMagick;
using Mandarin.Core;

namespace Mandarin.Engines;

/// <summary>Image conversion with Magick.NET (local, no external programs).</summary>
public static class ImageEngine
{
    public static MagickFormat FormatFor(string ext) => ext.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => MagickFormat.Jpeg,
        "png" => MagickFormat.Png,
        "webp" => MagickFormat.WebP,
        "heic" or "heif" => MagickFormat.Heic,
        "tif" or "tiff" => MagickFormat.Tiff,
        "avif" => MagickFormat.Avif,
        "bmp" => MagickFormat.Bmp,
        "gif" => MagickFormat.Gif,
        "ico" => MagickFormat.Ico,
        "pdf" => MagickFormat.Pdf,
        _ => throw new UserFacingException(L.F("Writing {0} is not supported", ext.ToUpperInvariant()))
    };

    public static bool SupportsAlpha(string ext) => ext is "png" or "webp" or "tiff" or "tif" or "avif" or "heic" or "gif" or "ico" or "bmp";

    static MagickReadSettings ReadSettings(string path)
    {
        var s = new MagickReadSettings();
        if (FileKinds.Ext(path) == "svg")
        {
            s.Density = new Density(192); // crisp rasterization of vector graphics
            s.BackgroundColor = MagickColors.Transparent;
        }
        return s;
    }

    public static MagickImage Load(string path)
    {
        var img = new MagickImage(path, ReadSettings(path));
        img.AutoOrient();
        return img;
    }

    public static async Task ConvertAsync(string input, string targetExt, JobContext ctx)
    {
        targetExt = targetExt.ToLowerInvariant();
        if (targetExt == "pdf")
        {
            var outPdf = OutputPaths.File(input, "pdf");
            await Task.Run(() => PdfEngine.ImagesToPdf(new[] { input }, outPdf, ctx), ctx.Token);
            ctx.AddOutput(outPdf);
            return;
        }
        if (targetExt is "mp4" or "webm")
        {
            await MediaEngine.ConvertAsync(input, targetExt, ctx);
            return;
        }

        var output = OutputPaths.File(input, targetExt);
        await Task.Run(() =>
        {
            var animated = FileKinds.IsAnimatedCandidate(input) && targetExt is "gif" or "webp";
            if (animated)
            {
                using var frames = new MagickImageCollection(input);
                if (frames.Count > 1)
                {
                    frames.Coalesce();
                    foreach (var f in frames) ApplyQuality((MagickImage)f, targetExt);
                    frames.Write(output, FormatFor(targetExt));
                    return;
                }
            }

            using var img = Load(input);
            ctx.Report(0.3);
            PrepareForTarget(img, targetExt);
            ctx.Token.ThrowIfCancellationRequested();
            Write(img, output, targetExt);
        }, ctx.Token);
        ctx.AddOutput(output);
    }

    /// <summary>Writes with HEIC support: if Magick.NET has no encoder, the Windows codec is used.</summary>
    public static void Write(MagickImage img, string output, string ext)
    {
        if (ext is "heic" or "heif")
        {
            try
            {
                img.Write(output, MagickFormat.Heic);
                return;
            }
            catch (MagickMissingDelegateErrorException) { }
            using var clone = (MagickImage)img.Clone();
            clone.ColorSpace = ColorSpace.sRGB;
            byte[] px;
            using (var pixels = clone.GetPixelsUnsafe()) px = pixels.ToByteArray(PixelMapping.BGRA)!;
            var bytes = WinRt.EncodeHeifAsync(px, (int)clone.Width, (int)clone.Height, (int)img.Quality).GetAwaiter().GetResult();
            File.WriteAllBytes(output, bytes);
            return;
        }
        img.Write(output, FormatFor(ext));
    }

    public static void PrepareForTarget(MagickImage img, string targetExt)
    {
        if (!SupportsAlpha(targetExt) && img.HasAlpha)
        {
            img.ColorAlpha(MagickColors.White);
            img.HasAlpha = false;
        }
        if (targetExt == "ico")
        {
            var size = Math.Min(256, Math.Max(img.Width, img.Height));
            img.Resize(new MagickGeometry((uint)size, (uint)size) { IgnoreAspectRatio = false });
            img.Extent((uint)size, (uint)size, Gravity.Center, MagickColors.Transparent);
            img.Settings.SetDefine(MagickFormat.Ico, "auto-resize", "256,128,64,48,32,16");
        }
        if (!AppSettings.Current.KeepMetadata) StripMetadata(img);
        ApplyQuality(img, targetExt);
    }

    public static void ApplyQuality(MagickImage img, string targetExt, int? quality = null)
    {
        var s = AppSettings.Current;
        img.Quality = (uint)(quality ?? targetExt switch
        {
            "jpg" or "jpeg" => s.JpegQuality,
            "webp" => s.WebpQuality,
            "avif" => s.AvifQuality,
            "heic" or "heif" => s.HeicQuality,
            "png" => 95,
            _ => 92
        });
        if (targetExt is "tif" or "tiff") img.Settings.Compression = CompressionMethod.LZW;
        if (targetExt is "jpg" or "jpeg") img.Settings.Interlace = Interlace.Plane;
    }

    public static void StripMetadata(MagickImage img)
    {
        var icc = img.GetColorProfile();
        img.Strip();
        if (icc != null) img.SetProfile(icc); // keep the color profile, otherwise colors shift
    }

    // ---------- Compression ----------

    public enum CompressMode { Preset, Percent, TargetSize }

    public sealed record CompressOptions(CompressMode Mode, int Preset /*0 high, 1 medium, 2 strong*/, int Percent,
        long TargetBytes, int? MaxDimension, bool StripMeta);

    public static long Compress(string input, string output, CompressOptions o, CancellationToken ct)
    {
        var ext = FileKinds.Ext(output);
        using var src = Load(input);
        if (o.MaxDimension is { } max && (src.Width > max || src.Height > max))
            src.Resize(new MagickGeometry((uint)max, (uint)max));
        if (o.StripMeta) StripMetadata(src);
        var original = new FileInfo(input).Length;

        long target = o.Mode switch
        {
            CompressMode.Percent => (long)(original * (1 - o.Percent / 100.0)),
            CompressMode.TargetSize => o.TargetBytes,
            _ => 0
        };

        byte[] Encode(MagickImage im, int q)
        {
            using var c = (MagickImage)im.Clone();
            if (ext == "png")
            {
                if (q < 90) c.Quantize(new QuantizeSettings { Colors = (uint)Math.Clamp(q * 3, 16, 256), DitherMethod = DitherMethod.FloydSteinberg });
                c.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
            }
            else ApplyQuality(c, ext, q);
            return c.ToByteArray(FormatFor(ext));
        }

        byte[] best;
        if (o.Mode == CompressMode.Preset)
        {
            best = Encode(src, o.Preset switch { 0 => 82, 1 => 68, _ => 50 });
        }
        else
        {
            // binary search over quality, then shrink the dimensions if that was not enough
            var working = (MagickImage)src.Clone();
            best = Encode(working, 40);
            for (var attempt = 0; attempt < 6; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                int lo = 20, hi = 95; byte[]? found = null;
                while (lo <= hi)
                {
                    var mid = (lo + hi) / 2;
                    var data = Encode(working, mid);
                    if (data.Length <= target) { found = data; lo = mid + 1; }
                    else hi = mid - 1;
                }
                if (found != null) { best = found; break; }
                best = Encode(working, 20);
                if (working.Width < 64 || working.Height < 64) break;
                working.Resize(new Percentage(80));
            }
            working.Dispose();
        }

        if (best.Length >= original && ext == FileKinds.Ext(input) && o.MaxDimension == null)
        {
            // compression did not help - keep a copy of the original so the file does not grow
            File.Copy(input, output);
            return original;
        }
        File.WriteAllBytes(output, best);
        return best.LongLength;
    }

    // ---------- Metadata ----------

    public sealed class PhotoMeta
    {
        public string? Make, Model, Artist, Copyright, Description, DateTaken, Software;
        public double? Latitude, Longitude;
    }

    public static PhotoMeta ReadMeta(string path)
    {
        using var img = new MagickImage(path);
        var m = new PhotoMeta();
        var exif = img.GetExifProfile();
        if (exif == null) return m;
        m.Make = exif.GetValue(ExifTag.Make)?.Value;
        m.Model = exif.GetValue(ExifTag.Model)?.Value;
        m.Artist = exif.GetValue(ExifTag.Artist)?.Value;
        m.Copyright = exif.GetValue(ExifTag.Copyright)?.Value;
        m.Description = exif.GetValue(ExifTag.ImageDescription)?.Value;
        m.Software = exif.GetValue(ExifTag.Software)?.Value;
        m.DateTaken = exif.GetValue(ExifTag.DateTimeOriginal)?.Value ?? exif.GetValue(ExifTag.DateTime)?.Value;
        m.Latitude = Gps(exif.GetValue(ExifTag.GPSLatitude)?.Value, exif.GetValue(ExifTag.GPSLatitudeRef)?.Value);
        m.Longitude = Gps(exif.GetValue(ExifTag.GPSLongitude)?.Value, exif.GetValue(ExifTag.GPSLongitudeRef)?.Value);
        return m;
    }

    static double? Gps(Rational[]? v, string? reference)
    {
        if (v is not { Length: 3 }) return null;
        var deg = v[0].ToDouble() + v[1].ToDouble() / 60 + v[2].ToDouble() / 3600;
        return reference is "S" or "W" ? -deg : deg;
    }

    static Rational[] ToDms(double value)
    {
        value = Math.Abs(value);
        var d = Math.Floor(value);
        var mFull = (value - d) * 60;
        var m = Math.Floor(mFull);
        var s = (mFull - m) * 60;
        return new[] { new Rational(d), new Rational(m), new Rational((uint)Math.Round(s * 1000), 1000) };
    }

    public static void WriteMeta(string input, string output, PhotoMeta m)
    {
        using var img = new MagickImage(input);
        var exif = img.GetExifProfile() ?? new ExifProfile();
        void SetS(ExifTag<string> tag, string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) exif.RemoveValue(tag);
            else exif.SetValue(tag, v.Trim());
        }
        SetS(ExifTag.Make, m.Make);
        SetS(ExifTag.Model, m.Model);
        SetS(ExifTag.Artist, m.Artist);
        SetS(ExifTag.Copyright, m.Copyright);
        SetS(ExifTag.ImageDescription, m.Description);
        SetS(ExifTag.DateTimeOriginal, m.DateTaken);
        if (m.Latitude is { } lat && m.Longitude is { } lon)
        {
            exif.SetValue(ExifTag.GPSLatitude, ToDms(lat));
            exif.SetValue(ExifTag.GPSLatitudeRef, lat >= 0 ? "N" : "S");
            exif.SetValue(ExifTag.GPSLongitude, ToDms(lon));
            exif.SetValue(ExifTag.GPSLongitudeRef, lon >= 0 ? "E" : "W");
        }
        else
        {
            exif.RemoveValue(ExifTag.GPSLatitude); exif.RemoveValue(ExifTag.GPSLatitudeRef);
            exif.RemoveValue(ExifTag.GPSLongitude); exif.RemoveValue(ExifTag.GPSLongitudeRef);
        }
        img.SetProfile(exif);
        if (FileKinds.Ext(input) is "jpg" or "jpeg") img.Quality = Math.Max(img.Quality, 92);
        img.Write(output);
    }

    public static void RemoveMeta(string input, string output)
    {
        using var img = new MagickImage(input);
        img.AutoOrient();
        StripMetadata(img);
        if (FileKinds.Ext(input) is "jpg" or "jpeg") img.Quality = Math.Max(img.Quality, 92);
        img.Write(output);
    }

    // ---------- Shared helpers for editors ----------

    /// <summary>BGRA buffer from a file (for previews and WPF editors).</summary>
    public static (byte[] pixels, int width, int height) LoadBgra(string path, int? maxSide = null)
    {
        using var img = Load(path);
        if (maxSide is { } m && (img.Width > m || img.Height > m)) img.Resize(new MagickGeometry((uint)m, (uint)m));
        img.ColorSpace = ColorSpace.sRGB;
        using var pixels = img.GetPixelsUnsafe();
        var data = pixels.ToByteArray(PixelMapping.BGRA) ?? throw new UserFacingException(L.T("Could not read the pixels"));
        return (data, (int)img.Width, (int)img.Height);
    }

    public static void SaveBgra(byte[] bgra, int width, int height, string output, string? metadataSource = null)
    {
        var settings = new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA);
        using var img = new MagickImage();
        img.ReadPixels(bgra, settings);
        var ext = FileKinds.Ext(output);
        if (metadataSource != null && AppSettings.Current.KeepMetadata)
        {
            try
            {
                using var src = new MagickImage(metadataSource);
                var exif = src.GetExifProfile();
                if (exif != null)
                {
                    exif.SetValue(ExifTag.Orientation, (ushort)1);
                    exif.RemoveThumbnail();
                    img.SetProfile(exif);
                }
                var icc = src.GetColorProfile();
                if (icc != null) img.SetProfile(icc);
            }
            catch { /* metadata is optional */ }
        }
        PrepareForTarget(img, ext);
        Write(img, output, ext);
    }

    public static string OutputExtFor(string input)
    {
        var ext = FileKinds.Ext(input);
        return ext switch
        {
            "jpeg" or "jfif" or "jpe" => "jpg",
            "tif" => "tiff",
            "heif" => "heic",
            "svg" or "psd" or "tga" or "dng" or "cr2" or "cr3" or "nef" or "arw" or "orf" or "rw2" or "raf" or "jxl" => "png",
            _ => ext
        };
    }
}

