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
    public static async Task RunAsync(string root, bool palettesOnly = false)
    {
        var inDir = Path.Combine(root, "in");
        var shots = Path.Combine(root, "shots");
        Directory.CreateDirectory(shots);
        string In(string n) => n == "photo.jpg" && Environment.GetEnvironmentVariable("MANDARIN_DEMO_PHOTO") is { Length: > 0 } demo ? demo : Path.Combine(inDir, n);

        if (!palettesOnly) await MainShots(In, shots, inDir);
        await PaletteShots(In, shots);
        Console.WriteLine("Screenshots: " + shots);
    }

    static async Task MainShots(Func<string, string> In, string shots, string inDir)
    {
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
        // a 16×16 icon: handles, frames and areas keep their on-screen size, the pixels stay crisp
        var icon = Path.Combine(inDir, "icon16.png");
        MakeIcon(icon);
        await Shot(() => new CropWindow(new[] { icon }), Path.Combine(shots, "19-crop-small.png"), 1500);
        await Shot(() => RegionsPreview(icon), Path.Combine(shots, "20-regions-small.png"), 800);
        await Shot(() => new AnnotateWindow(icon), Path.Combine(shots, "21-annotate-small.png"), 1500);
    }

    static async Task PaletteShots(Func<string, string> In, string shots)
    {
        // interface palettes and accent colors (LaserPrep set): settings, a tool window and the wheel in each
        var settings = Mandarin.Core.AppSettings.Current;
        foreach (var (palette, accent, on) in new[] { ("steel", "#4F8CFF", true), ("light", "#22B3A6", true), ("amber", "#F26B1D", false), ("graphite", "#E8608F", true), ("lilac", "#9A6CF0", true) })
        {
            settings.Palette = palette; settings.AccentColor = accent; settings.AccentOn = on;
            Mandarin.App.ApplyThemeTo(Application.Current);
            var tag = $"{palette}-{(on ? accent.TrimStart('#') : "no-accent")}";
            await Shot(() => HomeWindow.CreatePreview("settings"), Path.Combine(shots, $"30-settings-{tag}.png"), 900);
            await Shot(() => new CropWindow(new[] { In("photo.jpg") }), Path.Combine(shots, $"31-crop-{tag}.png"), 1500);
            await Shot(() => WheelWindow.CreatePreview(WheelMode.Convert, new[] { In("photo.jpg") }, 1), Path.Combine(shots, $"32-wheel-{tag}.png"), 600, transparent: true);
        }
        // switching on the fly: the open main window is rebuilt in the new palette
        settings.Palette = "windows"; settings.AccentColor = Theme.DefaultAccent; settings.AccentOn = true;
        Mandarin.App.ApplyThemeTo(Application.Current);
        var instance = typeof(HomeWindow).GetField("_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        await Shot(() => HomeWindow.CreatePreview("settings").Also(w => instance.SetValue(null, w)), Path.Combine(shots, "33-restyled-coffee-3FB56E.png"), 900, afterShow: async _ =>
        {
            settings.Palette = "coffee"; settings.AccentColor = "#3FB56E";
            Mandarin.App.ApplyTheme();
            await Task.Delay(600);
        });
        instance.SetValue(null, null);
        settings.Palette = null; settings.AccentColor = Theme.DefaultAccent; settings.AccentOn = true;
        Mandarin.App.ApplyThemeTo(Application.Current);
    }

    static void MakeIcon(string path)
    {
        using var img = new ImageMagick.MagickImage(ImageMagick.MagickColors.Transparent, 16, 16);
        var rows = new[]
        {
            "................", ".....oooooo.....", "...oooooooooo...", "..oooOOOOOOooo..", "..ooOOOOOOOOoo..", ".ooOOOOOOOOOOoo.",
            ".ooOOOOggOOOOoo.", ".ooOOOggggOOOoo.", ".ooOOOOggOOOOoo.", ".ooOOOOOOOOOOoo.", "..ooOOOOOOOOoo..", "..oooOOOOOOooo..",
            "...oooooooooo...", ".....oooooo.....", "................", "................"
        };
        var px = new byte[16 * 16 * 4];
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
        {
            var (r, g, b, a) = rows[y][x] switch { 'o' => (0xD9, 0x56, 0x0C, 255), 'O' => (0xF2, 0x6B, 0x1D, 255), 'g' => (0x3F, 0xB5, 0x6E, 255), _ => (0, 0, 0, 0) };
            var i = (y * 16 + x) * 4;
            px[i] = (byte)r; px[i + 1] = (byte)g; px[i + 2] = (byte)b; px[i + 3] = (byte)a;
        }
        img.ImportPixels(px, new ImageMagick.PixelImportSettings(16, 16, ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA));
        img.Write(path, ImageMagick.MagickFormat.Png);
    }

    static Window RegionsPreview(string icon)
    {
        var bmp = new BitmapImage(new Uri(icon));
        var image = new System.Windows.Controls.Image { Source = bmp, Stretch = Stretch.Fill };
        ScreenUnits.SharpWhenEnlarged(image);
        var overlay = new RegionsOverlay();
        overlay.SetContentSize(16, 16);
        var surface = new System.Windows.Controls.Grid { Width = 16, Height = 16, Children = { image, overlay } };
        overlay.Add(new Rect(2, 9, 5, 4));
        overlay.Select(overlay.Add(new Rect(6, 5, 4, 4)));
        return new Window
        {
            Width = 520, Height = 520, WindowStyle = WindowStyle.None,
            Content = new System.Windows.Controls.Border { Padding = new Thickness(10), Child = new System.Windows.Controls.Viewbox { Child = surface } }
        };
    }

    static async Task Shot(Func<Window> make, string path, int waitMs, bool transparent = false, Func<Window, Task>? afterShow = null)
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
            if (afterShow != null) await afterShow(w);
            w.UpdateLayout();
            var content = (FrameworkElement)w.Content;
            var width = content.ActualWidth; var height = content.ActualHeight;
            const double scale = 1.5;
            var rtb = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var rect = new Rect(0, 0, width, height);
                if (!transparent) dc.DrawRectangle(Theme.WindowBackground ?? new SolidColorBrush(Theme.IsDark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)), null, rect);
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
