using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Mandarin.SelfTest;

/// <summary>
/// Records the README demo: a real File Explorer window shows a folder with one photo, the photo is dragged,
/// Shift is tapped, the file is dropped on the PNG sector of the running Mandarin wheel.
/// Only the Explorer file pane (and the progress card in the corner) is captured - never the navigation pane
/// or the rest of the desktop. Frames are written as PNG files into &lt;root&gt;\frames.
/// Requires a running Mandarin instance; uses the mouse for about ten seconds.
/// </summary>
public static class DemoRecorder
{
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr h, out ICONINFO info);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000, KEYUP = 0x0002;
    const int Fps = 15;

    public static async Task<int> RunAsync(string root, string photo, int sectorCount, int wheelSize)
    {
        if (Process.GetProcessesByName("LogonUI").Length > 0)
        {
            Console.WriteLine("✗ The screen is locked; nothing to record");
            return 1;
        }
        var folder = Path.Combine(root, "Mandarin demo");
        var frames = Path.Combine(root, "frames");
        // only this test's own folders are recreated
        foreach (var d in new[] { folder, frames }) if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(frames);
        var file = Path.Combine(folder, "mandarins.jpg");
        File.Copy(photo, file);
        var expected = Path.Combine(folder, "mandarins.png");

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        var before = new HashSet<long>();
        foreach (dynamic w in shell.Windows()) { try { before.Add((long)w.HWND); } catch { } }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false });
        dynamic? win = null;
        for (var i = 0; i < 40 && win == null; i++)
        {
            await Task.Delay(250);
            foreach (dynamic w in shell.Windows())
            {
                try { if (string.Equals((string)w.Document.Folder.Self.Path, folder, StringComparison.OrdinalIgnoreCase)) win = w; } catch { }
            }
        }
        if (win == null) { Console.WriteLine("✗ Explorer window not found"); return 1; }
        var hwnd = new IntPtr((long)win.HWND);
        try
        {
            if (before.Contains(hwnd.ToInt64()))
            {
                // opened as a tab in a window the user already had: do not move or record it
                Console.WriteLine("✗ The folder opened as a tab of an existing Explorer window; recording skipped");
                return 1;
            }
            win.Document.CurrentViewMode = 1; // icons
            try { win.Document.IconSize = 192; } catch { }

            // a mid-size window flush with the bottom-right corner, where the progress card appears
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            const int ww = 1200, wh = 760;
            ShowWindow(hwnd, 9); // restore
            SetWindowPos(hwnd, IntPtr.Zero, work.Right - ww, work.Bottom - wh, ww, wh, 0x0040);
            SetForegroundWindow(hwnd);
            await Task.Delay(1500);

            var (item, pane) = FindItem(hwnd);
            if (item.IsEmpty) { Console.WriteLine("✗ The photo was not found in the Explorer view"); return 1; }
            // the file pane only; downward to the bottom of the work area so the progress card is included
            var region = Rectangle.FromLTRB(pane.Left, pane.Top, work.Right, work.Bottom);
            region.Width -= region.Width % 2; region.Height -= region.Height % 2;
            Console.WriteLine($"item {item}, pane {pane}, region {region}");

            // bring the window to front with a click on an empty part of the pane
            MouseTo(pane.Right - 60, pane.Bottom - 60); await Task.Delay(100);
            Button(LEFTDOWN); await Task.Delay(50); Button(LEFTUP);
            await Task.Delay(700);

            using var cts = new CancellationTokenSource();
            var recording = Task.Run(() => Record(region, frames, cts.Token));

            var dpi = 1.0;
            var start = new Point(pane.Right - 150, pane.Bottom - 110);
            var target = new Point(item.Left + item.Width / 2, item.Top + item.Height / 2);
            var middle = new Point(pane.Left + pane.Width / 2, pane.Top + pane.Height / 2 + 30);

            MouseTo(start.X, start.Y);
            await Task.Delay(700);
            await Glide(start, target, 700);
            await Task.Delay(350);
            Button(LEFTDOWN); await Task.Delay(250);
            await Glide(target, middle, 900);
            await Task.Delay(450);
            Key(0x10, false); await Task.Delay(70); Key(0x10, true); // Shift
            await Task.Delay(900);
            GetCursorPos(out var c);
            var r = (0.42 + 0.97) / 2 * wheelSize / 2 * dpi;
            var ang = (-90 + 360.0 / sectorCount * 0) * Math.PI / 180;
            var sector = new Point(c.X + (int)(Math.Cos(ang) * r), c.Y + (int)(Math.Sin(ang) * r));
            await Glide(new Point(c.X, c.Y), sector, 550);
            await Task.Delay(1000);
            Button(LEFTUP);

            // wait until the result is complete: it exists and its size has stopped changing
            long last = -1; var stable = 0;
            for (var i = 0; i < 300 && stable < 6; i++)
            {
                await Task.Delay(100);
                var size = File.Exists(expected) ? new FileInfo(expected).Length : -1;
                stable = size > 0 && size == last ? stable + 1 : 0;
                last = size;
            }
            await Task.Delay(300);
            await Glide(sector, new Point(pane.Right - 150, pane.Bottom - 110), 700);
            await Task.Delay(1800);
            cts.Cancel();
            var count = await recording;

            var ok = File.Exists(expected);
            Console.WriteLine(ok ? $"✓ Recorded {count} frames into {frames}" : $"✗ No PNG appeared ({count} frames recorded)");
            return ok ? 0 : 1;
        }
        finally
        {
            try { win.Quit(); } catch { }
        }
    }

    static (Rectangle item, Rectangle pane) FindItem(IntPtr hwnd)
    {
        var rootEl = AutomationElement.FromHandle(hwnd);
        var items = rootEl.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        foreach (AutomationElement el in items)
        {
            if (!el.Current.Name.StartsWith("mandarins", StringComparison.OrdinalIgnoreCase)) continue;
            var parent = TreeWalker.ControlViewWalker.GetParent(el);
            while (parent != null && parent.Current.ControlType != ControlType.List) parent = TreeWalker.ControlViewWalker.GetParent(parent);
            if (parent == null) continue;
            return (ToRect(el.Current.BoundingRectangle), ToRect(parent.Current.BoundingRectangle));
        }
        return (Rectangle.Empty, Rectangle.Empty);
    }

    static Rectangle ToRect(System.Windows.Rect r) => new((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);

    static async Task Glide(Point from, Point to, int ms)
    {
        var steps = Math.Max(1, ms / 15);
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var e = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; // ease in-out
            MouseTo(from.X + (int)((to.X - from.X) * e), from.Y + (int)((to.Y - from.Y) * e));
            await Task.Delay(15);
        }
    }

    static int Record(Rectangle region, string folder, CancellationToken ct)
    {
        var queue = new BlockingCollection<(int n, Bitmap bmp)>(boundedCapacity: 40);
        var writer = Task.Run(() =>
        {
            foreach (var (n, bmp) in queue.GetConsumingEnumerable())
                using (bmp) bmp.Save(Path.Combine(folder, $"f{n:D4}.png"), ImageFormat.Png);
        });
        var sw = Stopwatch.StartNew();
        var n = 0;
        while (!ct.IsCancellationRequested)
        {
            var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var screen = GetDC(IntPtr.Zero);
                var hdc = g.GetHdc();
                // CAPTUREBLT: otherwise layered windows such as the wheel and the drag image are missing
                BitBlt(hdc, 0, 0, region.Width, region.Height, screen, region.Left, region.Top, 0x00CC0020 | 0x40000000);
                DrawCursor(hdc, region);
                g.ReleaseHdc(hdc);
                ReleaseDC(IntPtr.Zero, screen);
            }
            queue.Add((n++, bmp));
            var next = n * 1000 / Fps;
            var wait = next - (int)sw.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep(wait);
        }
        queue.CompleteAdding();
        writer.Wait();
        return n;
    }

    static void DrawCursor(IntPtr hdc, Rectangle region)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;
        var hx = 0; var hy = 0;
        if (GetIconInfo(ci.hCursor, out var info))
        {
            hx = info.xHotspot; hy = info.yHotspot;
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
        }
        DrawIconEx(hdc, ci.pt.X - hx - region.Left, ci.pt.Y - hy - region.Top, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
    }

    static void MouseTo(int x, int y)
    {
        var vx = GetSystemMetrics(76); var vy = GetSystemMetrics(77); var vw = GetSystemMetrics(78); var vh = GetSystemMetrics(79);
        var input = new INPUT
        {
            type = 0,
            U = new InputUnion { mi = new MOUSEINPUT { dx = (int)((x - vx) * 65535.0 / (vw - 1)), dy = (int)((y - vy) * 65535.0 / (vh - 1)), dwFlags = MOVE | ABSOLUTE | VIRTUALDESK } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    static void Button(uint flag)
    {
        var input = new INPUT { type = 0, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flag } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    static void Key(ushort vk, bool up)
    {
        var input = new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYUP : 0 } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
}
