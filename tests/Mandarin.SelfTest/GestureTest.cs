using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Mandarin.SelfTest;

/// <summary>
/// Live gesture check: a test window starts a real OLE drag&amp;drop of a file, SendInput taps Shift,
/// the cursor moves to the PNG sector and releases the button. A running Mandarin must create a PNG next to the file.
/// A screenshot around the cursor is captured while the wheel is open.
/// </summary>
public static class GestureTest
{
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    struct POINT { public int X, Y; }

    const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000, KEYUP = 0x0002;

    static void MouseTo(int x, int y, uint extra = 0)
    {
        var vx = GetSystemMetrics(76); var vy = GetSystemMetrics(77); var vw = GetSystemMetrics(78); var vh = GetSystemMetrics(79);
        var input = new INPUT
        {
            type = 0,
            U = new InputUnion { mi = new MOUSEINPUT { dx = (int)((x - vx) * 65535.0 / (vw - 1)), dy = (int)((y - vy) * 65535.0 / (vh - 1)), dwFlags = MOVE | ABSOLUTE | VIRTUALDESK | extra } }
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

    public static async Task<int> RunAsync(string file, string shotPath, int sectorIndex, int sectorCount, int wheelSize)
    {
        var expected = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".png");
        if (File.Exists(expected)) File.Delete(expected); // output of a previous run of this test

        var tile = new Border
        {
            Width = 160, Height = 120, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0x6B, 0x1D)),
            CornerRadius = new CornerRadius(12),
            Child = new TextBlock { Text = Path.GetFileName(file), Foreground = System.Windows.Media.Brushes.White, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 16 }
        };
        var win = new Window
        {
            Width = 200, Height = 160, WindowStyle = WindowStyle.ToolWindow, Topmost = true, Title = "Mandarin gesture test",
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = tile, ShowInTaskbar = false
        };
        tile.MouseLeftButtonDown += (_, _) =>
        {
            var list = new StringCollection { file };
            var data = new DataObject();
            data.SetFileDropList(list);
            var effect = DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            Console.WriteLine("DoDragDrop finished: " + effect);
        };
        win.Show();
        await Task.Delay(500);
        var p = tile.PointToScreen(new System.Windows.Point(80, 60));
        int sx = (int)p.X, sy = (int)p.Y;
        var dpi = VisualTreeHelper.GetDpi(tile).DpiScaleX;

        _ = Task.Run(async () =>
        {
            MouseTo(sx, sy); await Task.Delay(150);
            Button(LEFTDOWN); await Task.Delay(150);
            for (var i = 1; i <= 8; i++) { MouseTo(sx + i * 4, sy + i * 2); await Task.Delay(40); }
            await Task.Delay(300);
            Key(0x10, false); await Task.Delay(60); Key(0x10, true); // Shift
            await Task.Delay(500);
            GetCursorPos(out var c);
            Console.WriteLine($"start ({sx},{sy}), cursor after Shift ({c.X},{c.Y}), dpi {dpi}");
            // sector 0 is at the top, the rest go clockwise
            var r = (0.42 + 0.97) / 2 * wheelSize / 2 * dpi;
            var ang = (-90 + 360.0 / sectorCount * sectorIndex) * Math.PI / 180;
            int tx = c.X + (int)(Math.Cos(ang) * r), ty = c.Y + (int)(Math.Sin(ang) * r);
            for (var i = 1; i <= 10; i++) { MouseTo(c.X + (tx - c.X) * i / 10, c.Y + (ty - c.Y) * i / 10); await Task.Delay(35); }
            await Task.Delay(400);
            CaptureAround(c.X, c.Y - (int)(24 * dpi), (int)(wheelSize * dpi * 1.45), shotPath);
            Button(LEFTUP);
        });

        for (var i = 0; i < 60 && !File.Exists(expected); i++) await Task.Delay(250);
        await Task.Delay(800);
        win.Close();
        var ok = File.Exists(expected) && new FileInfo(expected).Length > 0;
        Console.WriteLine(ok ? $"✓ Gesture worked: created {expected}" : "✗ Gesture failed: no PNG appeared");
        return ok ? 0 : 1;
    }

    static void CaptureAround(int x, int y, int size, string path)
    {
        try
        {
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                // CAPTUREBLT: otherwise layered windows such as the wheel do not appear in the capture
                var screen = GetDC(IntPtr.Zero);
                var hdc = g.GetHdc();
                BitBlt(hdc, 0, 0, size, size, screen, x - size / 2, y - size / 2, 0x00CC0020 | 0x40000000);
                g.ReleaseHdc(hdc);
                ReleaseDC(IntPtr.Zero, screen);
            }
            bmp.Save(path, ImageFormat.Png);
        }
        catch (Exception ex) { Console.WriteLine("screenshot: " + ex.Message); }
    }
}
