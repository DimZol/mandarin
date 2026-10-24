using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using ClosedXML.Excel;
using ImageMagick;
using ImageMagick.Drawing;
using Mandarin.Core;
using Mandarin.Engines;
using Mandarin.UI.Tools;

namespace Mandarin.SelfTest;

/// <summary>
/// Self-test for the Mandarin engines. Works ONLY inside the given folder:
/// generates small test files in &lt;root&gt;\in and writes results to &lt;root&gt;\out.
/// </summary>
public static class Program
{
    static readonly List<(string name, bool ok, string info)> Results = new();
    static string _in = "", _out = "";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("usage: Mandarin.SelfTest <empty-test-folder> [filter]"); return 2; }
        var root = Path.GetFullPath(args[0]);
        var filter = args.Length > 1 ? args[1] : null;
        Console.OutputEncoding = Encoding.UTF8;
        L.Init(AppLanguage.English); // test expectations use English output names
        if (Enum.TryParse<AppLanguage>(Environment.GetEnvironmentVariable("MANDARIN_LANG"), out var lang)) L.Init(lang);
        // no GPU: the test must not depend on other programs keeping the graphics card busy
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var code = 0;
        app.Startup += async (_, _) =>
        {
            try
            {
                if (filter == "probe-noop") { new Window { Width = 100, Height = 100, Left = -5000 }.Also(w => { w.Show(); w.Close(); }); }
                else if (filter == "probe-webview") { await HtmlEngine.HtmlToPdfAsync(HtmlEngine.WrapDocument("t", "<p>t</p>"), Path.Combine(root, "probe.pdf")); HtmlEngine.Shutdown(); await Task.Delay(1500); }
                else if (filter == "probe-ffmpeg")
                {
                    await Ffmpeg.RunAsync(new[] { "-f", "lavfi", "-i", "sine=frequency=440:duration=1", Path.Combine(root, "probe.mp3") }, 1, null, CancellationToken.None);
                }
                else if (filter == "probe-generate") { _in = Path.Combine(root, "in"); _out = Path.Combine(root, "out"); Directory.CreateDirectory(_in); await GenerateAsync(); }
                else if (filter == "probe-winpdf")
                {
                    var pdfPath = Path.Combine(root, "probe-in.pdf");
                    var d = await WinRt.OpenPdfAsync(pdfPath);
                    await WinRt.RenderPageAsync(d, 0, 72);
                }
                else if (filter is "ui" or "ui-palettes") { Mandarin.App.ApplyThemeTo(app); await UiShots.RunAsync(root, filter == "ui-palettes"); }
                else if (filter == "gesture")
                {
                    AppSettings.Load();
                    Directory.CreateDirectory(Path.Combine(root, "shots"));
                    code = await GestureTest.RunAsync(Path.Combine(root, "in", "photo.jpg"), Path.Combine(root, "shots", "00-live-wheel.png"),
                        0, Formats.TargetsFor(new[] { Path.Combine(root, "in", "photo.jpg") }).Count, AppSettings.Current.WheelSize);
                }
                else if (filter == "motion")
                {
                    Mandarin.App.ApplyThemeTo(app);
                    var photo = Environment.GetEnvironmentVariable("MANDARIN_DEMO_PHOTO") ?? Path.Combine(root, "in", "photo.jpg");
                    code = await MotionShots.RunAsync(root, photo);
                }
                else if (filter == "record")
                {
                    AppSettings.Load();
                    var photo = Environment.GetEnvironmentVariable("MANDARIN_DEMO_PHOTO") ?? Path.Combine(root, "in", "photo.jpg");
                    code = await DemoRecorder.RunAsync(root, photo, Formats.TargetsFor(new[] { photo }).Count, AppSettings.Current.WheelSize);
                }
                else code = await RunAsync(root, filter);
            }
            catch (Exception ex) { Console.WriteLine("FATAL: " + ex); code = 1; }
            finally
            {
                HtmlEngine.Shutdown();
                Console.Out.Flush();
                Environment.Exit(code); // do not wait for background components (WebView2, COM)
            }
        };
        app.Run();
        return code;
    }

    static async Task<int> RunAsync(string root, string? filter)
    {
        _in = Path.Combine(root, "in");
        _out = Path.Combine(root, "out");
        // only the test's own subfolders are cleaned
        foreach (var d in new[] { _in, _out }) if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(_in);
        Directory.CreateDirectory(_out);

        AppSettings.Current.Output = OutputLocation.CustomFolder;
        AppSettings.Current.CustomOutputFolder = _out;
        AppSettings.Current.PdfImageDpi = 110;
        AppSettings.Current.PlaySound = false;
        if (Enum.TryParse<DocumentEngine>(Environment.GetEnvironmentVariable("MANDARIN_ENGINE"), out var engine))
            AppSettings.Current.DocumentEngine = engine;
        Console.WriteLine($"Document engine: {AppSettings.Current.DocumentEngine}; Word→{OfficeEngine.For(OfficeApp.Word)}, Excel→{OfficeEngine.For(OfficeApp.Excel)}, PowerPoint→{OfficeEngine.For(OfficeApp.PowerPoint)}");

        Console.WriteLine($"ffmpeg: {Ffmpeg.FfmpegPath ?? "none"}; Word: {OfficeEngine.WordInstalled}; PowerPoint: {OfficeEngine.PowerPointInstalled}; Excel: {OfficeEngine.ExcelInstalled}");
        var inputs = await GenerateAsync();
        Console.WriteLine($"Generated input files: {inputs.Count}");

        // ---- conversion to every available format
        foreach (var input in inputs)
        {
            foreach (var target in Formats.TargetsFor(new[] { input }))
            {
                var name = $"convert {Path.GetFileName(input)} → {target}";
                if (filter != null && !filter.Split('|').Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;
                await Check(name, ctx => Formats.ConvertOne(input, target, ctx));
            }
        }

        if (filter == null || filter == "tools") await ToolsAsync(inputs);

        Console.WriteLine();
        Console.WriteLine($"TOTAL: {Results.Count(r => r.ok)} passed, {Results.Count(r => !r.ok)} failed");
        foreach (var r in Results.Where(r => !r.ok)) Console.WriteLine($"  ✗ {r.name}: {r.info}");
        var size = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Console.WriteLine($"Disk used by the test: {OutputPaths.HumanSize(size)}");
        return Results.Any(r => !r.ok) ? 1 : 0;
    }

    static async Task Check(string name, Func<JobContext, Task> work, int timeoutSec = 240)
    {
        var job = new Job { Title = name, Glyph = "", Inputs = Array.Empty<string>() };
        var ctx = new JobContext(job);
        job.Cancel.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        var sw = Stopwatch.StartNew();
        try
        {
            await work(ctx);
            var outs = job.Outputs.ToList();
            var bad = outs.Where(o => !(File.Exists(o) && new FileInfo(o).Length > 0) && !(Directory.Exists(o) && Directory.EnumerateFileSystemEntries(o).Any())).ToList();
            if (outs.Count == 0) throw new Exception("no result");
            if (bad.Count > 0) throw new Exception("empty result: " + string.Join(", ", bad.Select(Path.GetFileName)));
            var info = string.Join(", ", outs.Select(o => File.Exists(o) ? $"{Path.GetFileName(o)} {OutputPaths.HumanSize(new FileInfo(o).Length)}" : $"[{Path.GetFileName(o)}] {Directory.GetFiles(o, "*", SearchOption.AllDirectories).Length} files"));
            Results.Add((name, true, info));
            Console.WriteLine($"✓ {name} ({sw.Elapsed.TotalSeconds:0.0}s): {info}");
        }
        catch (Exception ex)
        {
            var msg = ex is UserFacingException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
            Results.Add((name, false, msg));
            Console.WriteLine($"✗ {name} ({sw.Elapsed.TotalSeconds:0.0}s): {msg}");
            if (ex is not UserFacingException) Console.WriteLine("    " + ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim());
        }
    }

    // ---------------- generating test files ----------------

    const string SampleHtmlBody = """
        <h1>Mandarin test document</h1>
        <p>This is the <strong>first</strong> paragraph with <em>formatting</em> and a link to an <a href="https://example.com">example</a>.
        A second sentence makes the paragraph long enough to wrap across several lines when printed to PDF. Ünïcödé: café, naïve, Zürich.</p>
        <h2>To-do list</h2>
        <ul><li>Convert images</li><li>Compress video</li><li>Merge PDFs</li></ul>
        <ol><li>First step</li><li>Second step</li></ol>
        <h2>Table</h2>
        <table><tr><th>Product</th><th>Quantity</th><th>Price</th></tr>
        <tr><td>Mandarins</td><td>12</td><td>350</td></tr><tr><td>Oranges</td><td>5</td><td>420</td></tr></table>
        <h3>Quote</h3>
        <blockquote>Everything happens locally.</blockquote>
        <p>Last paragraph of the document. The quick brown fox jumps over the lazy dog.</p>
        """;

    static async Task<List<string>> GenerateAsync()
    {
        var list = new List<string>();
        string P(string name) => Path.Combine(_in, name);

        // images
        using (var img = new MagickImage(new MagickColor("#FFB46B"), 900, 600))
        {
            new Drawables().FillColor(new MagickColor("#F26B1D")).Circle(450, 300, 450, 120).Draw(img);
            new Drawables().FillColor(MagickColors.White).FontPointSize(48).Text(260, 320, "Mandarin test").Draw(img);
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "TestCam");
            exif.SetValue(ExifTag.Model, "M-1");
            exif.SetValue(ExifTag.Artist, "Tester");
            img.SetProfile(exif);
            img.Quality = 90;
            img.Write(P("photo.jpg"), MagickFormat.Jpeg);
        }
        list.Add(P("photo.jpg"));
        using (var img = new MagickImage(MagickColors.Transparent, 256, 256))
        {
            new Drawables().FillColor(new MagickColor("#43A047")).Circle(128, 128, 128, 20).Draw(img);
            img.Write(P("logo.png"), MagickFormat.Png);
        }
        list.Add(P("logo.png"));
        await File.WriteAllTextAsync(P("vector.svg"), "<svg xmlns='http://www.w3.org/2000/svg' width='200' height='120'><rect width='200' height='120' rx='16' fill='#F26B1D'/><circle cx='100' cy='60' r='40' fill='white'/></svg>");
        list.Add(P("vector.svg"));

        // video and audio
        if (Ffmpeg.Available)
        {
            await Ffmpeg.RunAsync(new[] { "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=3", "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", P("clip.mp4") }, 3, null, CancellationToken.None);
            list.Add(P("clip.mp4"));
            await Ffmpeg.RunAsync(new[] { "-f", "lavfi", "-i", "aevalsrc=if(gt(t\\,1)\\,0.5*sin(2*PI*440*t)\\,0):s=44100:d=4",
                "-c:a", "libmp3lame", "-q:a", "4", P("tone.mp3") }, 4, null, CancellationToken.None);
            list.Add(P("tone.mp3"));
            await Ffmpeg.RunAsync(new[] { "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=1", P("anim.gif") }, 1, null, CancellationToken.None);
            list.Add(P("anim.gif"));
        }

        // documents
        var html = HtmlEngine.WrapDocument("Test document", SampleHtmlBody);
        await File.WriteAllTextAsync(P("page.html"), html, new UTF8Encoding(false));
        list.Add(P("page.html"));
        await HtmlEngine.HtmlToPdfAsync(html, P("doc.pdf"));
        list.Add(P("doc.pdf"));
        await HtmlEngine.HtmlToDocxAsync(html, P("report.docx"), "Report");
        list.Add(P("report.docx"));
        await File.WriteAllTextAsync(P("notes.md"), "# Notes\n\nText with **bold** and *italic*, plus Ünïcödé: café, naïve, Zürich.\n\n## List\n\n- one\n- two\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n```\ncode block\n```\n", new UTF8Encoding(false));
        list.Add(P("notes.md"));
        await File.WriteAllTextAsync(P("plain.txt"), "First line of text.\nSecond line.\n\nNew paragraph.", new UTF8Encoding(false));
        list.Add(P("plain.txt"));
        EpubEngine.FromHtml(html, "Test book", "Tester", P("book.epub"));
        list.Add(P("book.epub"));

        // spreadsheets
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("Sales");
            ws.Cell(1, 1).Value = "Date"; ws.Cell(1, 2).Value = "Product"; ws.Cell(1, 3).Value = "Amount";
            for (var i = 0; i < 10; i++)
            {
                ws.Cell(i + 2, 1).Value = new DateTime(2026, 1, i + 1);
                ws.Cell(i + 2, 2).Value = $"Item {i + 1}";
                ws.Cell(i + 2, 3).Value = 100.5 * (i + 1);
            }
            wb.Worksheets.Add("Second").Cell(1, 1).Value = "Another sheet";
            wb.SaveAs(P("sheet.xlsx"));
        }
        list.Add(P("sheet.xlsx"));
        await File.WriteAllTextAsync(P("data.csv"), "name;value\n\"Smith, Jr.\";10\nZoë;20.5\n", new UTF8Encoding(true));
        list.Add(P("data.csv"));

        // presentation: from the PDF with our own PPTX writer
        var win = await WinRt.OpenPdfAsync(P("doc.pdf"));
        var slides = new List<(byte[], bool)>();
        for (uint i = 0; i < win.PageCount; i++) slides.Add((await WinRt.RenderPageAsync(win, i, 0, 800), true));
        PptxWriter.Write(P("slides.pptx"), slides, 16.0 / 9, "Test");
        list.Add(P("slides.pptx"));

        // subtitles
        await File.WriteAllTextAsync(P("subs.srt"), "1\n00:00:01,000 --> 00:00:02,500\nHello!\n\n2\n00:00:03,000 --> 00:00:04,000\n<i>Second</i> line\n", new UTF8Encoding(false));
        list.Add(P("subs.srt"));

        // archives
        var pack = P("pack");
        Directory.CreateDirectory(Path.Combine(pack, "sub"));
        await File.WriteAllTextAsync(Path.Combine(pack, "a.txt"), "A");
        await File.WriteAllTextAsync(Path.Combine(pack, "sub", "b.txt"), "B");
        ZipFile.CreateFromDirectory(pack, P("pack.zip"));
        list.Add(P("pack.zip"));
        list.Add(pack);
        return list;
    }

    // ---------------- tools ----------------

    static async Task ToolsAsync(List<string> inputs)
    {
        string In(string n) => inputs.First(i => Path.GetFileName(i) == n);
        var photo = In("photo.jpg");
        var pdf = In("doc.pdf");

        await Check("image compress target 30KB", ctx => Task.Run(() =>
        {
            var o = OutputPaths.File(photo, "jpg", " (compressed)");
            var size = ImageEngine.Compress(photo, o, new ImageEngine.CompressOptions(ImageEngine.CompressMode.TargetSize, 1, 50, 30 * 1024, null, true), ctx.Token);
            if (size > 30 * 1024) throw new Exception($"size {size}");
            ctx.AddOutput(o);
        }));
        await Check("image metadata write/read", ctx => Task.Run(() =>
        {
            var o = OutputPaths.File(photo, "jpg", " (metadata)");
            ImageEngine.WriteMeta(photo, o, new ImageEngine.PhotoMeta { Artist = "Mandarine Ünïcode", Latitude = 55.75, Longitude = 37.62, Make = "TestCam" });
            var m = ImageEngine.ReadMeta(o);
            if (m.Artist != "Mandarine Ünïcode" || m.Latitude is null || Math.Abs(m.Latitude.Value - 55.75) > 0.001) throw new Exception($"mismatch: {m.Artist} {m.Latitude}");
            ctx.AddOutput(o);
        }));
        await Check("image strip metadata", ctx => Task.Run(() =>
        {
            var o = OutputPaths.File(photo, "jpg", " (no metadata)");
            ImageEngine.RemoveMeta(photo, o);
            if (ImageEngine.ReadMeta(o).Make != null) throw new Exception("EXIF is still there");
            ctx.AddOutput(o);
        }));
        await Check("image redact blur+pixel+solid", ctx => Task.Run(() =>
        {
            using var img = ImageEngine.Load(photo);
            Mandarin.UI.Tools.RedactImageWindow.ApplyRedaction(img, new[] { new Rect(100, 100, 200, 120) }, 3, default);
            Mandarin.UI.Tools.RedactImageWindow.ApplyRedaction(img, new[] { new Rect(400, 100, 200, 120) }, 4, default);
            Mandarin.UI.Tools.RedactImageWindow.ApplyRedaction(img, new[] { new Rect(100, 300, 200, 120) }, 0, default);
            var o = OutputPaths.File(photo, "jpg", " (redacted)");
            img.Write(o);
            ctx.AddOutput(o);
        }));
        await Check("photo adjust pipeline", ctx => Task.Run(() =>
        {
            var (px, w, h) = ImageEngine.LoadBgra(photo);
            new PhotoAdjust { Exposure = 0.5, Contrast = 20, Saturation = 30, Vignette = 40, Sharpness = 30, Filter = 3 }.Apply(px, w, h);
            var o = OutputPaths.File(photo, "jpg", " (edited)");
            ImageEngine.SaveBgra(px, w, h, o, photo);
            ctx.AddOutput(o);
        }));
        await Check("images → pdf", ctx => Task.Run(() =>
        {
            var o = OutputPaths.UniqueFile(_out, "Images", "pdf");
            PdfEngine.ImagesToPdf(new[] { photo, In("logo.png") }, o, ctx);
            ctx.AddOutput(o);
        }));
        await Check("face detection (no faces expected)", async ctx =>
        {
            var faces = await WinRt.DetectFacesAsync(await File.ReadAllBytesAsync(photo));
            ctx.AddOutput(photo);
            ctx.Detail($"faces: {faces.Count}");
        });
        await Check("OCR on rendered pdf page", async ctx =>
        {
            var doc = await WinRt.OpenPdfAsync(pdf);
            var png = await WinRt.RenderPageAsync(doc, 0, 150);
            var lines = await WinRt.RecognizeAsync(png);
            if (lines.Count == 0) throw new Exception("nothing was recognized");
            var o = Path.Combine(_out, "ocr.txt");
            await File.WriteAllLinesAsync(o, lines.Select(l => l.Text));
            ctx.AddOutput(o);
        });

        // PDF
        await Check("pdf compress", async ctx =>
        {
            var src = Path.Combine(_out, "Images.pdf");
            var o = OutputPaths.File(src, "pdf", " (compressed)");
            await PdfEngine.CompressAsync(src, o, 2, ctx);
            ctx.AddOutput(o);
        });
        await Check("pdf compress rasterize", async ctx =>
        {
            var o = OutputPaths.File(pdf, "pdf", " (raster)");
            await PdfEngine.CompressAsync(pdf, o, 3, ctx);
            ctx.AddOutput(o);
        });
        await Check("pdf split", ctx => Task.Run(() => ctx.AddOutput(PdfEngine.Split(pdf, ctx))));
        await Check("pdf merge", ctx => Task.Run(() =>
        {
            var o = OutputPaths.UniqueFile(_out, "Merged document", "pdf");
            PdfEngine.Merge(new[] { pdf, Path.Combine(_out, "Images.pdf") }, o, ctx);
            ctx.AddOutput(o);
        }));
        await Check("pdf organize", ctx => Task.Run(() =>
        {
            var o = OutputPaths.File(pdf, "pdf", " (organized)");
            PdfEngine.Organize(pdf, new[] { (0, 90), (0, 0) }, o);
            if (PdfEngine.PageCount(o) != 2) throw new Exception("expected 2 pages");
            ctx.AddOutput(o);
        }));
        await Check("pdf metadata", ctx => Task.Run(() =>
        {
            var o = OutputPaths.File(pdf, "pdf", " (properties)");
            PdfEngine.WriteMeta(pdf, o, new PdfEngine.PdfMeta("Title", "Author", "Theme", "keyword", "Mandarin"));
            if (PdfEngine.ReadMeta(o).Author != "Author") throw new Exception("the author was not written");
            ctx.AddOutput(o);
        }));

        // archives
        await Check("archive extract", ctx => ArchiveEngine.ExtractAsync(In("pack.zip"), ctx));
        await Check("archive zip-slip protection", async ctx =>
        {
            var evil = Path.Combine(_in, "evil.zip");
            using (var z = ZipFile.Open(evil, ZipArchiveMode.Create)) z.CreateEntry("../../escape.txt");
            try
            {
                await ArchiveEngine.ExtractAsync(evil, ctx);
                throw new Exception("a dangerous archive was extracted!");
            }
            catch (UserFacingException) { ctx.AddOutput(evil); }
            if (File.Exists(Path.Combine(Path.GetDirectoryName(_in)!, "escape.txt"))) throw new Exception("a file escaped the folder");
        });

        // safety checks
        await Check("safety: unique output names under parallel jobs", ctx => Task.Run(() =>
        {
            var a = OutputPaths.UniqueFile(_out, "same", "txt");
            var b = OutputPaths.UniqueFile(_out, "same", "txt");
            if (a == b) throw new Exception("identical names");
            File.WriteAllText(a, "a");
            ctx.AddOutput(a);
        }));
        await Check("safety: device names sanitized", ctx => Task.Run(() =>
        {
            if (OutputPaths.Sanitize("CON") != "_CON" || OutputPaths.Sanitize("name. ") != "name") throw new Exception(OutputPaths.Sanitize("CON"));
            var o = Path.Combine(_out, "sanitize.txt"); File.WriteAllText(o, "ok"); ctx.AddOutput(o);
        }));
        await Check("safety: html cannot embed files outside its folder", async ctx =>
        {
            var htmlDir = Path.Combine(_in, "htmldir");
            Directory.CreateDirectory(htmlDir);
            File.Copy(photo, Path.Combine(htmlDir, "inside.jpg"));
            var html = $"<html><body><img src=\"inside.jpg\"/><img src=\"{photo}\"/><img src=\"../photo.jpg\"/><img src=\"file:///{photo.Replace('\\', '/')}\"/></body></html>";
            var result = HtmlEngine.InlineLocalImages(html, htmlDir);
            var embedded = System.Text.RegularExpressions.Regex.Matches(result, "data:image").Count;
            if (embedded != 1) throw new Exception($"images embedded: {embedded}, expected 1");
            var o = Path.Combine(_out, "inline.html"); await File.WriteAllTextAsync(o, result); ctx.AddOutput(o);
        });

        if (!Ffmpeg.Available) return;
        var clip = In("clip.mp4");
        await Check("safety: ffmpeg never overwrites existing files", async ctx =>
        {
            var victim = Path.Combine(_out, "existing.mp3");
            await File.WriteAllTextAsync(victim, "do not touch");
            try
            {
                await Ffmpeg.RunAsync(new[] { "-f", "lavfi", "-i", "sine=duration=1", victim }, 1, null, ctx.Token, victim);
                throw new Exception("ffmpeg wrote over an existing file");
            }
            catch (UserFacingException) { }
            if (!File.Exists(victim) || await File.ReadAllTextAsync(victim) != "do not touch") throw new Exception("the existing file was changed or deleted");
            ctx.AddOutput(victim);
        });
        var tone = In("tone.mp3");
        await Check("video compress 480p", ctx => MediaEngine.CompressVideoAsync(clip, new MediaEngine.VideoCompressOptions(28, 120, null, 96), ctx));
        await Check("video compress target size", ctx => MediaEngine.CompressVideoAsync(clip, new MediaEngine.VideoCompressOptions(28, null, 60_000, 64), ctx));
        await Check("video mute", ctx => MediaEngine.MuteAsync(clip, ctx));
        await Check("video trim exact", ctx => MediaEngine.TrimAsync(clip, 0.5, 2.0, true, ctx));
        await Check("video trim fast", ctx => MediaEngine.TrimAsync(clip, 0.0, 2.0, false, ctx));
        await Check("video split", ctx => MediaEngine.SplitAsync(clip, new[] { 1.0, 2.0 }, ctx));
        await Check("video crop", ctx => MediaEngine.CropVideoAsync(clip, new MediaEngine.CropRect(40, 20, 200, 150), ctx));
        await Check("video speed 2x", ctx => MediaEngine.SpeedAsync(clip, 2, ctx));
        await Check("video speed 0.25x", ctx => MediaEngine.SpeedAsync(clip, 0.25, ctx));
        await Check("video snapshots", ctx => MediaEngine.SnapshotsAsync(clip, new[] { 0.5, 1.5 }, ctx));
        await Check("video redact blur", ctx => MediaEngine.RedactVideoAsync(clip, new[] { new MediaEngine.RedactRegion(new MediaEngine.CropRect(10, 10, 100, 80), 0.5, 2) }, true, "000000", ctx));
        await Check("video redact solid", ctx => MediaEngine.RedactVideoAsync(clip, new[] { new MediaEngine.RedactRegion(new MediaEngine.CropRect(10, 10, 100, 80), 0, 99) }, false, "FF0000", ctx));
        await Check("video join", ctx => MediaEngine.JoinVideosAsync(new[] { clip, clip }, ctx));
        await Check("video metadata", ctx => MediaEngine.WriteMetadataAsync(clip, new() { ["title"] = "Test", ["artist"] = "Mandarin" }, false, ctx));
        await Check("video metadata remove", ctx => MediaEngine.WriteMetadataAsync(clip, new(), true, ctx));
        await Check("audio compress", ctx => MediaEngine.CompressAudioAsync(tone, 64, null, true, ctx));
        await Check("audio normalize", ctx => MediaEngine.NormalizeAsync(tone, -16, ctx));
        await Check("audio channels mono→stereo", ctx => MediaEngine.ChannelsAsync(tone, false, ctx));
        await Check("audio visualize", ctx => MediaEngine.VisualizeAsync(tone, MediaEngine.VisualShape.Square, null, ctx));
        await Check("audio visualize with image", ctx => MediaEngine.VisualizeAsync(tone, MediaEngine.VisualShape.Landscape, photo, ctx));
        await Check("audio bleep", ctx => MediaEngine.BleepAsync(tone, new[] { (1.5, 2.0), (3.0, 3.3) }, ctx));
        await Check("audio join", ctx => MediaEngine.JoinAudioAsync(new[] { tone, tone }, ctx));
        await Check("audio silence detect", async ctx =>
        {
            var peaks = await Ffmpeg.ReadPeaksAsync(tone, 2000, ctx.Token);
            var (s, e) = MediaEngine.DetectSilenceBounds(peaks, 4);
            if (s < 0.7 || s > 1.1) throw new Exception($"silence start {s:0.00}");
            ctx.AddOutput(tone);
            ctx.Detail($"{s:0.00}..{e:0.00}");
        });
        await Check("audio metadata", ctx => MediaEngine.WriteMetadataAsync(tone, new() { ["artist"] = "Test", ["album"] = "Album" }, false, ctx));
        await Check("preview proxy", async ctx =>
        {
            var p = await MediaEngine.MakePreviewProxyAsync(clip, true, ctx.Token);
            ctx.AddOutput(p);
        });
    }
}
