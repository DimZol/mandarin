using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mandarin.Core;
using Mandarin.Interop;
using Mandarin.UI;

namespace Mandarin.SelfTest;

/// <summary>
/// Records the app's animations off screen as PNG frames (about 30 fps): the wheel opening, moving between
/// sectors and closing on a choice, and a progress card sliding in, finishing and being dismissed.
/// Nothing is shown on the screen and the mouse is not used.
/// </summary>
public static class MotionShots
{
    const int FrameMs = 33;

    public static async Task<int> RunAsync(string root, string photo)
    {
        Motion.Init();
        if (!Motion.Enabled) { Console.WriteLine("✗ Windows animation effects are off - nothing to record"); return 1; }
        var res = Application.Current.Resources;
        res["SystemAccentColor"] = Color.FromRgb(0xF2, 0x6B, 0x1D);

        var wheelDir = Path.Combine(root, "motion-wheel");
        var hudDir = Path.Combine(root, "motion-hud");
        foreach (var d in new[] { wheelDir, hudDir }) { if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d); }

        // ---- wheel: open, hover PNG, move to WEBP, choose it
        var w = WheelWindow.CreatePreview(WheelMode.Convert, new[] { photo }, hidden: true);
        Offscreen(w);
        w.Show();
        await Task.Delay(300);
        var frames = 0;
        var content = (FrameworkElement)w.Content;
        async Task Record(FrameworkElement el, string dir, int ms)
        {
            for (var t = 0; t < ms; t += FrameMs)
            {
                Save(el, Path.Combine(dir, $"f{frames++:D4}.png"));
                await Task.Delay(FrameMs);
            }
        }
        w.PreviewReveal();
        await Record(content, wheelDir, 650);
        w.PreviewHover(0);
        await Record(content, wheelDir, 500);
        w.PreviewHover(1);
        await Record(content, wheelDir, 500);
        w.PreviewChoose(1);
        await Record(content, wheelDir, 350);
        Console.WriteLine($"✓ wheel: {frames} frames");

        // ---- progress card: slides in, progresses, finishes with a badge, is dismissed
        frames = 0;
        var hud = (Window)Activator.CreateInstance(typeof(Hud), nonPublic: true)!;
        Offscreen(hud);
        // the card window moves itself to the screen corner on every resize: keep it invisible there,
        // frames are rendered from its content, not from the screen
        hud.Opacity = 0;
        hud.Show();
        var job = new Job { Title = "mandarins.jpg → PNG", Glyph = "", Inputs = new[] { photo }, Detail = "mandarins.jpg" };
        job.State = JobState.Running;
        job.Progress = 0.05;
        Invoke(hud, "AddCard", job);
        Offscreen(hud);
        var hudContent = (FrameworkElement)hud.Content;
        await Record(hudContent, hudDir, 450);
        foreach (var p in new[] { 0.35, 0.7, 1.0 })
        {
            job.Progress = p;
            await Record(hudContent, hudDir, 330);
        }
        job.Outputs.Add(Path.ChangeExtension(photo, ".png"));
        job.State = JobState.Done;
        job.Detail = "mandarins.png";
        Invoke(hud, "Finished", job, false);
        await Record(hudContent, hudDir, 900);
        Invoke(hud, "RemoveCard", job);
        await Record(hudContent, hudDir, 500);
        Console.WriteLine($"✓ progress card: {frames} frames");
        hud.Close();

        // ---- main window: entrance, then three wheel notches on the settings page must glide, not jump
        frames = 0;
        var homeDir = Path.Combine(root, "motion-home");
        if (Directory.Exists(homeDir)) Directory.Delete(homeDir, true);
        Directory.CreateDirectory(homeDir);
        var home = HomeWindow.CreatePreview("settings");
        Offscreen(home);
        home.Show();
        var homeContent = (FrameworkElement)home.Content;
        await Record(homeContent, homeDir, 500);
        // the page's viewer, not the navigation list (that one scrolls by items)
        var viewer = FindChild<System.Windows.Controls.ScrollViewer>(homeContent, v => !v.CanContentScroll && v.ScrollableHeight > 0);
        if (viewer == null) { Console.WriteLine("✗ settings page has no scroll viewer"); return 1; }
        var start = viewer.VerticalOffset;
        for (var i = 0; i < 3; i++)
            viewer.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = viewer });
        var offsets = new List<double>();
        for (var t = 0; t < 700; t += FrameMs)
        {
            offsets.Add(viewer.VerticalOffset);
            Save(homeContent, Path.Combine(homeDir, $"f{frames++:D4}.png"));
            await Task.Delay(FrameMs);
        }
        var end = viewer.VerticalOffset;
        var distinct = offsets.Select(o => Math.Round(o)).Distinct().Count();
        Console.WriteLine($"scroll: {start:0} → {end:0}, {distinct} distinct positions on the way");
        home.Close();
        var ok = end > start + 100 && distinct >= 4;
        Console.WriteLine(ok ? "✓ smooth scrolling glides" : "✗ smooth scrolling did not glide");
        return ok ? 0 : 1;
    }

    static T? FindChild<T>(DependencyObject parent, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is T t && match(t)) return t;
            if (FindChild(c, match) is { } found) return found;
        }
        return null;
    }

    static void Offscreen(Window w)
    {
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -30000; w.Top = -30000;
    }

    static void Invoke(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    static void Save(FrameworkElement content, string path)
    {
        var width = Math.Max(1, content.ActualWidth);
        var height = Math.Max(1, content.ActualHeight);
        const double scale = 1.5;
        var rtb = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var rect = new Rect(0, 0, width, height);
            dc.DrawRectangle(new SolidColorBrush(Theme.IsDark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)), null, rect);
            dc.DrawRectangle(new VisualBrush(content) { Viewbox = rect, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill }, null, rect);
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
