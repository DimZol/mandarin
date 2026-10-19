using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mandarin.Interop;
using Mandarin.UI;
using Mandarin.UI.Tools;

namespace Mandarin.SelfTest;

/// <summary>Renders Mandarin windows to PNG off screen - for visual checks without touching the desktop.</summary>
public static class UiShots
{
    public static async Task RunAsync(string root)
    {
        var inDir = Path.Combine(root, "in");
        var shots = Path.Combine(root, "shots");
        Directory.CreateDirectory(shots);
        // the same accent colors as in the app's App.xaml
        var res = Application.Current.Resources;
        res["SystemAccentColor"] = Color.FromRgb(0xF2, 0x6B, 0x1D); res["SystemAccentColorLight1"] = Color.FromRgb(0xF5, 0x82, 0x3D);
        res["SystemAccentColorLight2"] = Color.FromRgb(0xF8, 0x9A, 0x5E); res["SystemAccentColorLight3"] = Color.FromRgb(0xFB, 0xB5, 0x85);
        res["SystemAccentColorDark1"] = Color.FromRgb(0xD9, 0x5A, 0x10); res["SystemAccentColorDark2"] = Color.FromRgb(0xB8, 0x4A, 0x0B); res["SystemAccentColorDark3"] = Color.FromRgb(0x93, 0x3A, 0x07);
        string In(string n) => n == "photo.jpg" && Environment.GetEnvironmentVariable("MANDARIN_DEMO_PHOTO") is { Length: > 0 } demo ? demo : Path.Combine(inDir, n);

        await Shot(() => HomeWindow.CreatePreview("home"), Path.Combine(shots, "01-home.png"), 1200);
        await Shot(() => HomeWindow.CreatePreview("settings"), Path.Combine(shots, "02-settings.png"), 800);
        await Shot(() => HomeWindow.CreatePreview("formats"), Path.Combine(shots, "03-formats.png"), 800);
        await Shot(() => WheelWindow.CreatePreview(WheelMode.Convert, new[] { In("photo.jpg") }, 0), Path.Combine(shots, "04-wheel-convert.png"), 600, transparent: true);
        await Shot(() => WheelWindow.CreatePreview(WheelMode.Tools, new[] { In("photo.jpg") }, 2), Path.Combine(shots, "05-wheel-tools.png"), 600, transparent: true);
        await Shot(() => WheelWindow.CreatePreview(WheelMode.Actions, new[] { In("photo.jpg") }, 0), Path.Combine(shots, "06-wheel-actions.png"), 600, transparent: true);
        await Shot(() => WheelWindow.CreatePreview(WheelMode.Tools, new[] { In("clip.mp4") }, 3), Path.Combine(shots, "07-wheel-video-tools.png"), 600, transparent: true);
        await Shot(() => new PhotoEditorWindow(In("photo.jpg")), Path.Combine(shots, "08-photo-editor.png"), 2500);
        await Shot(() => new AnnotateWindow(In("photo.jpg")), Path.Combine(shots, "09-annotate.png"), 2000);
        await Shot(() => new CropWindow(new[] { In("photo.jpg") }), Path.Combine(shots, "10-crop.png"), 2000);
        await Shot(() => new BackgroundWindow(new[] { In("photo.jpg") }), Path.Combine(shots, "11-background.png"), 2500);
        await Shot(() => new CollageWindow(new[] { In("photo.jpg"), In("logo.png"), In("photo.jpg") }), Path.Combine(shots, "12-collage.png"), 2500);
        await Shot(() => new ImageCompressDialog(new[] { In("photo.jpg") }), Path.Combine(shots, "13-compress.png"), 800);
        await Shot(() => new MediaEditorWindow(In("clip.mp4"), MediaEditMode.Trim), Path.Combine(shots, "14-video-trim.png"), 4000);
        await Shot(() => new MediaEditorWindow(In("tone.mp3"), MediaEditMode.Bleep), Path.Combine(shots, "15-audio-bleep.png"), 4000);
        await Shot(() => new VideoOverlayWindow(In("clip.mp4"), false), Path.Combine(shots, "16-video-crop.png"), 4000);
        await Shot(() => new PdfOrganizeWindow(In("doc.pdf")), Path.Combine(shots, "17-pdf-pages.png"), 2500);
        await Shot(() => new RedactImageWindow(In("photo.jpg")), Path.Combine(shots, "18-redact.png"), 2000);
        Console.WriteLine("Screenshots: " + shots);
    }

    static async Task Shot(Func<Window> make, string path, int waitMs, bool transparent = false)
    {
        Window? w = null;
        try
        {
            w = make();
            w.ShowInTaskbar = false;
            w.ShowActivated = false;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = -30000; w.Top = -30000;
            w.Show();
            w.Left = -30000; w.Top = -30000;
            await Task.Delay(waitMs);
            w.UpdateLayout();
            var content = (FrameworkElement)w.Content;
            var width = content.ActualWidth; var height = content.ActualHeight;
            const double scale = 1.5;
            var rtb = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var rect = new Rect(0, 0, width, height);
                if (!transparent) dc.DrawRectangle(new SolidColorBrush(Theme.IsDark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)), null, rect);
                dc.DrawRectangle(new VisualBrush(content) { Viewbox = rect, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill }, null, rect);
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            await using (var fs = File.Create(path)) enc.Save(fs);
            Console.WriteLine($"✓ {Path.GetFileName(path)} {(int)width}×{(int)height}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { w?.Close(); } catch { }
        }
    }
}
