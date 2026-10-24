using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ImageMagick;
using ImageMagick.Drawing;
using Mandarin.Core;
using Mandarin.Engines;
using Mandarin.UI;
using Mandarin.UI.Tools;

namespace Mandarin.Platform;

/// <summary>
/// Self-check of the cross-platform build: "Mandarin --selftest &lt;empty folder&gt; [shots|engines]". Works only inside the
/// given folder: generates small test files in &lt;folder&gt;/in, writes results to &lt;folder&gt;/out and window renders to
/// &lt;folder&gt;/shots. Nothing else on the computer is touched.
/// </summary>
public static class SelfCheck
{
    static readonly List<(string name, bool ok, string info)> Results = new();

    public static async Task<int> RunAsync(string root, string what)
    {
        root = Path.GetFullPath(root);
        var input = Path.Combine(root, "in");
        Directory.CreateDirectory(input);
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { /* no console attached */ }
        Console.WriteLine($"Mandarin self-check on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Generate(input);
        if (what is "all" or "engines") await Engines(input, Path.Combine(root, "out"));
        if (what is "all" or "shots") await Shots(input, Path.Combine(root, "shots"));
        var failed = Results.Count(r => !r.ok);
        Console.WriteLine($"\n{Results.Count - failed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Generate(string dir)
    {
        using (var img = new MagickImage(new MagickColor("#FFB46A"), 900, 600))
        {
            new Drawables().FillColor(new MagickColor("#F26B1D")).Circle(450, 300, 450, 80).Draw(img);
            img.Write(Path.Combine(dir, "photo.jpg"), MagickFormat.Jpeg);
        }
        using (var icon = new MagickImage(MagickColors.Transparent, 16, 16))
        {
            new Drawables().FillColor(new MagickColor("#F26B1D")).Circle(8, 8, 8, 2).FillColor(new MagickColor("#3FB56E")).Rectangle(7, 5, 9, 11).Draw(icon);
            icon.Write(Path.Combine(dir, "icon16.png"), MagickFormat.Png);
        }
        File.WriteAllText(Path.Combine(dir, "notes.md"), "# Mandarin\n\nA **test** document with a list:\n\n- one\n- two\n\n| a | b |\n|---|---|\n| 1 | 2 |\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "letter.rtf"),
            @"{\rtf1\ansi\ansicpg1251\deff0{\fonttbl{\f0 Arial;}}{\colortbl;\red0\green0\blue0;}\f0\fs24 {\b Hello} {\i world}\par \'cf\'f0\'e8\'e2\'e5\'f2, \u1084?\u1080?\u1088?\par Line three\par}", Encoding.ASCII);
        File.WriteAllText(Path.Combine(dir, "subs.srt"), "1\n00:00:01,000 --> 00:00:02,500\nHello\n\n2\n00:00:03,000 --> 00:00:04,000\nWorld\n", new UTF8Encoding(false));
        var folder = Path.Combine(dir, "pack");
        Directory.CreateDirectory(Path.Combine(folder, "inner"));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(folder, "inner", "b with a long name that needs more than one hundred characters in total to test pax records in the tar writer.txt"), "beta");
        // a two-page PDF from the photo
        PdfEngine.ImagesToPdf(new[] { Path.Combine(dir, "photo.jpg"), Path.Combine(dir, "photo.jpg") }, Path.Combine(dir, "doc.pdf"), null);
    }

    static async Task Check(string name, Func<Task<string>> test)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var info = await test();
            Results.Add((name, true, info));
            Console.WriteLine($"✓ {name}  {info}  ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            Results.Add((name, false, ex.Message));
            Console.WriteLine($"✗ {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static async Task<string> Convert(string input, string target, string outDir)
    {
        Directory.CreateDirectory(outDir);
        AppSettings.Current.Output = OutputLocation.CustomFolder;
        AppSettings.Current.CustomOutputFolder = outDir;
        var job = new Job { Title = "test", Glyph = "", Inputs = new[] { input } };
        var ctx = new JobContext(job);
        await Formats.ConvertOne(input, target, ctx);
        if (job.Outputs.Count == 0) throw new Exception("no output");
        var o = job.Outputs[0];
        var size = File.Exists(o) ? new FileInfo(o).Length : Directory.GetFiles(o, "*", SearchOption.AllDirectories).Length;
        if (size == 0) throw new Exception("empty output");
        return $"{Path.GetFileName(o)} {(File.Exists(o) ? OutputPaths.HumanSize(size) : size + " files")}";
    }

    static async Task Engines(string input, string output)
    {
        string In(string n) => Path.Combine(input, n);
        await Check("image → PNG", () => Convert(In("photo.jpg"), "png", output));
        await Check("image → WEBP", () => Convert(In("photo.jpg"), "webp", output));
        await Check("image → AVIF", () => Convert(In("photo.jpg"), "avif", output));
        await Check("PDF → PNG (PDFium)", () => Convert(In("doc.pdf"), "png", output));
        await Check("PDF page render", async () =>
        {
            var doc = await WinRt.OpenPdfAsync(In("doc.pdf"));
            var png = await WinRt.RenderPageAsync(doc, 0, 96);
            using var img = new MagickImage(png);
            var corner = img.GetPixels().GetPixel(1, 1).ToColor()!;
            if (corner.A != 255) throw new Exception("transparent background");
            return $"{doc.PageCount} pages, {img.Width}×{img.Height}";
        });
        await Check("Markdown → DOCX", () => Convert(In("notes.md"), "docx", output));
        await Check("Markdown → HTML", () => Convert(In("notes.md"), "html", output));
        await Check("RTF → TXT (built-in reader)", async () =>
        {
            AppSettings.Current.DocumentEngine = DocumentEngine.BuiltIn;
            var r = await Convert(In("letter.rtf"), "txt", output);
            var text = File.ReadAllText(Directory.GetFiles(output, "letter*.txt").First());
            if (!text.Contains("Hello") || !text.Contains("Привет") || !text.Contains("мир")) throw new Exception("text lost: " + text);
            AppSettings.Current.DocumentEngine = DocumentEngine.Auto;
            return r;
        });
        await Check("Markdown → PDF (" + (HtmlEngine.ChromiumPath != null ? "Chromium" : OfficeEngine.LibreInstalled ? "LibreOffice" : "no printer") + ")",
            () => Convert(In("notes.md"), "pdf", output));
        await Check("subtitles SRT → VTT", () => Convert(In("subs.srt"), "vtt", output));
        await Check("folder → ZIP", () => Convert(In("pack"), "zip", output));
        await Check("folder → TAR", () => Convert(In("pack"), "tar", output));
        await Check("TAR extraction (long names)", async () =>
        {
            var tar = Directory.GetFiles(output, "pack*.tar").First();
            var job = new Job { Title = "x", Glyph = "", Inputs = new[] { tar } };
            await ArchiveEngine.ExtractAsync(tar, new JobContext(job));
            var files = Directory.GetFiles(job.Outputs[0], "*", SearchOption.AllDirectories);
            if (!files.Any(f => Path.GetFileName(f).StartsWith("b with a long name"))) throw new Exception("long name lost: " + string.Join(", ", files.Select(Path.GetFileName)));
            if (File.ReadAllText(files.First(f => f.EndsWith("a.txt"))) != "alpha") throw new Exception("content changed");
            return $"{files.Length} files";
        });
        if (Ffmpeg.Available)
        {
            await Check("FFmpeg: tone → MP3", async () =>
            {
                var wav = In("tone.wav");
                await Ffmpeg.RunAsync(new[] { "-f", "lavfi", "-i", "sine=frequency=440:duration=1", wav }, 1, null, CancellationToken.None);
                return await Convert(wav, "mp3", output);
            });
        }
        else Console.WriteLine("- FFmpeg not found: media checks skipped");
        Console.WriteLine($"  OCR: {(WinRt.OcrAvailable ? WinRt.TesseractPath : "Tesseract not found")}; LibreOffice: {OfficeEngine.LibreOfficePath ?? "not found"}; browser: {HtmlEngine.ChromiumPath ?? "not found"}");
    }

    // ---------- window renders ----------

    static async Task Shots(string input, string dir)
    {
        Directory.CreateDirectory(dir);
        string In(string n) => Path.Combine(input, n);
        AppSettings.Current.Output = OutputLocation.NextToOriginal;
        foreach (var (palette, accent) in new[] { ("windows", "#F26B1D"), ("steel", "#4F8CFF"), ("light", "#22B3A6") })
        {
            AppSettings.Current.Palette = palette;
            AppSettings.Current.AccentColor = accent;
            Look.Apply(Application.Current!);
            await Shot(() => HomeWindow.CreatePreview("home"), Path.Combine(dir, $"home-{palette}.png"), 900);
            await Shot(() => HomeWindow.CreatePreview("settings"), Path.Combine(dir, $"settings-{palette}.png"), 900);
            await Shot(() => WheelWindow.CreatePreview(WheelMode.Convert, new[] { In("photo.jpg") }, 1), Path.Combine(dir, $"wheel-{palette}.png"), 900);
        }
        AppSettings.Current.Palette = "windows";
        AppSettings.Current.AccentColor = Look.DefaultAccent;
        Look.Apply(Application.Current!);
        await Shot(() => WheelWindow.CreatePreview(WheelMode.Tools, new[] { In("photo.jpg") }, 2), Path.Combine(dir, "wheel-tools.png"), 700);
        await Shot(() => HomeWindow.CreatePreview("formats"), Path.Combine(dir, "formats.png"), 700);
        await Shot(() => new CropWindow(new[] { In("photo.jpg") }), Path.Combine(dir, "crop.png"), 1500);
        await Shot(() => new CropWindow(new[] { In("icon16.png") }), Path.Combine(dir, "crop-small.png"), 1500);
        await Shot(() => new PhotoEditorWindow(In("photo.jpg")), Path.Combine(dir, "photo-editor.png"), 2000);
        await Shot(() => new AnnotateWindow(In("photo.jpg")), Path.Combine(dir, "annotate.png"), 1500);
        await Shot(() => new BackgroundWindow(new[] { In("photo.jpg") }), Path.Combine(dir, "background.png"), 2000);
        await Shot(() => new CollageWindow(new[] { In("photo.jpg"), In("icon16.png"), In("photo.jpg") }), Path.Combine(dir, "collage.png"), 2000);
        await Shot(() => new RedactImageWindow(In("photo.jpg")), Path.Combine(dir, "redact.png"), 1500);
        await Shot(() => new ImageCompressDialog(new[] { In("photo.jpg") }), Path.Combine(dir, "compress.png"), 700);
        await Shot(() => new PdfOrganizeWindow(In("doc.pdf")), Path.Combine(dir, "pdf-pages.png"), 2000);
        if (Ffmpeg.Available && File.Exists(In("tone.wav")))
            await Shot(() => new MediaEditorWindow(In("tone.wav"), MediaEditMode.Trim), Path.Combine(dir, "audio-trim.png"), 2500);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, byte[] bits, byte[] info, uint usage);

    /// <summary>Windows: what the window really shows (through the window manager); the render target misdraws translucent text.</summary>
    static MagickImage Print(Window w)
    {
        var hwnd = w.TryGetPlatformHandle()!.Handle;
        var size = w.ClientSize * w.RenderScaling;
        int width = (int)size.Width, height = (int)size.Height;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(mem, bmp);
        PrintWindow(hwnd, mem, 3); // PW_CLIENTONLY | PW_RENDERFULLCONTENT
        var info = new byte[40];
        BitConverter.GetBytes(40).CopyTo(info, 0); BitConverter.GetBytes(width).CopyTo(info, 4); BitConverter.GetBytes(-height).CopyTo(info, 8);
        BitConverter.GetBytes((short)1).CopyTo(info, 12); BitConverter.GetBytes((short)32).CopyTo(info, 14);
        var bits = new byte[width * height * 4];
        GetDIBits(mem, bmp, 0, (uint)height, bits, info, 0);
        SelectObject(mem, old); DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        for (var i = 3; i < bits.Length; i += 4) bits[i] = 255;
        var img = new MagickImage();
        img.ReadPixels(bits, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
        return img;
    }

    static async Task Shot(Func<Window> make, string path, int waitMs)
    {
        Window? w = null;
        var sw = Stopwatch.StartNew();
        try
        {
            w = make();
            w.ShowInTaskbar = false;
            w.ShowActivated = false;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Position = new PixelPoint(-20000, -20000);
            // the render target is transparent: subpixel text would get colored fringes there
            RenderOptions.SetTextRenderingMode(w, TextRenderingMode.Antialias);
            w.Show();
            await Task.Delay(waitMs);
            var content = (Control)w.Content!;
            var size = content.Bounds.Size;
            if (OsInfo.IsWindows && w is not WheelWindow)
            {
                using var printed = Print(w);
                printed.Write(path, MagickFormat.Png);
                Results.Add((Path.GetFileName(path), true, ""));
                Console.WriteLine($"✓ {Path.GetFileName(path)} {(int)size.Width}×{(int)size.Height}");
                return;
            }
            var scale = double.TryParse(Environment.GetEnvironmentVariable("MANDARIN_SHOT_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sc) ? sc : 1.0;
            var px = new PixelSize((int)(size.Width * scale), (int)(size.Height * scale));
            using var layer = new RenderTargetBitmap(px, new Vector(96 * scale, 96 * scale));
            layer.Render(content);
            using var ms = new MemoryStream();
            layer.Save(ms);
            using var shot = new MagickImage(ms.ToArray());
            if (w is not WheelWindow)
            {
                // the window background is drawn by the window itself, not by its content
                var bg = (w.Background as ISolidColorBrush ?? Look.WindowBackground).Color;
                using var back = new MagickImage(new MagickColor(bg.R, bg.G, bg.B), shot.Width, shot.Height);
                back.Composite(shot, CompositeOperator.Over);
                back.Write(path, MagickFormat.Png);
            }
            else shot.Write(path, MagickFormat.Png);
            Results.Add((Path.GetFileName(path), true, ""));
            Console.WriteLine($"✓ {Path.GetFileName(path)} {(int)size.Width}×{(int)size.Height}");
        }
        catch (Exception ex)
        {
            Results.Add((Path.GetFileName(path), false, ex.Message));
            Console.WriteLine($"✗ {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { w?.Close(); } catch { /* already closed */ }
        }
    }
}
