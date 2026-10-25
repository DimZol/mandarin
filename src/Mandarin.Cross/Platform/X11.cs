using System.Runtime.InteropServices;
using Mandarin.Core;

namespace Mandarin.Platform;

/// <summary>
/// The few X11 calls Mandarin needs on Linux: the pointer position and the global drag gesture (XInput2 raw events).
/// Raw events only observe input - nothing is grabbed or swallowed. Under Wayland they reach only X11 programs and
/// XWayland knows the pointer only over X11 windows, so there <see cref="Evdev"/> and <see cref="KWin"/> take over.
/// </summary>
public static class X11
{
    const string LibX11 = "libX11.so.6", LibXi = "libXi.so.6";

    [DllImport(LibX11)] static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(LibX11)] static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(LibX11)] static extern int XConnectionNumber(IntPtr display);
    [DllImport(LibX11)] static extern int XPending(IntPtr display);
    [DllImport(LibX11)] static extern int XNextEvent(IntPtr display, IntPtr ev);
    [DllImport(LibX11)] static extern bool XGetEventData(IntPtr display, IntPtr cookie);
    [DllImport(LibX11)] static extern void XFreeEventData(IntPtr display, IntPtr cookie);
    [DllImport(LibX11)] static extern bool XQueryExtension(IntPtr display, string name, out int opcode, out int ev, out int err);
    [DllImport(LibX11)] static extern ulong XkbKeycodeToKeysym(IntPtr display, byte keycode, int group, int level);
    [DllImport(LibX11)] static extern int XFlush(IntPtr display);
    [DllImport(LibX11)] static extern int XDefaultScreen(IntPtr display);
    [DllImport(LibX11)] static extern int XDisplayWidth(IntPtr display, int screen);
    [DllImport(LibX11)] static extern int XDisplayHeight(IntPtr display, int screen);
    [DllImport(LibX11)]
    static extern bool XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [DllImport(LibXi)] static extern int XIQueryVersion(IntPtr display, ref int major, ref int minor);
    [DllImport(LibXi)] static extern int XISelectEvents(IntPtr display, IntPtr window, ref XIEventMask masks, int count);

    [StructLayout(LayoutKind.Sequential)]
    struct XIEventMask { public int deviceid; public int mask_len; public IntPtr mask; }

    [StructLayout(LayoutKind.Sequential)]
    struct XGenericEventCookie
    {
        public int type; public ulong serial; public int send_event; public IntPtr display;
        public int extension; public int evtype; public uint cookie; public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XIRawEvent
    {
        public int type; public ulong serial; public int send_event; public IntPtr display;
        public int extension; public int evtype; public ulong time; public int deviceid; public int sourceid; public int detail;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PollFd { public int fd; public short events; public short revents; }

    [DllImport("libc", SetLastError = true)] static extern int poll(ref PollFd fds, uint count, int timeout);

    const int GenericEvent = 35, XIAllMasterDevices = 1;
    const int XI_RawKeyPress = 13, XI_RawKeyRelease = 14, XI_RawButtonPress = 15, XI_RawButtonRelease = 16, XI_RawMotion = 17;
    const uint Button1Mask = 1 << 8;

    public static bool IsWayland =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase) ||
        (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 } && Environment.GetEnvironmentVariable("DISPLAY") is not { Length: > 0 });

    public static bool Available => OsInfo.IsLinux && Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 };

    static (int x, int y, uint mask)? Query()
    {
        if (!Available) return null;
        var d = XOpenDisplay(IntPtr.Zero);
        if (d == IntPtr.Zero) return null;
        try
        {
            return XQueryPointer(d, XDefaultRootWindow(d), out _, out _, out var x, out var y, out _, out _, out var mask) ? (x, y, mask) : null;
        }
        finally { XCloseDisplay(d); }
    }

    /// <summary>The pointer position; null under Wayland, where XWayland remembers only where it left its windows.</summary>
    public static (int x, int y)? PointerPosition() => !IsWayland && Query() is { } q ? (q.x, q.y) : null;

    /// <summary>The left button is held (for the wheel watchdog); null when unknown.</summary>
    public static bool? LeftButtonDown() => !IsWayland && Query() is { } q ? (q.mask & Button1Mask) != 0 : null;

    /// <summary>Size of the X11 root window: the whole desktop in X11 pixels.</summary>
    public static (int width, int height)? RootSize()
    {
        if (!Available) return null;
        var d = XOpenDisplay(IntPtr.Zero);
        if (d == IntPtr.Zero) return null;
        try
        {
            var screen = XDefaultScreen(d);
            return (XDisplayWidth(d, screen), XDisplayHeight(d, screen));
        }
        finally { XCloseDisplay(d); }
    }

    /// <summary>Global drag gesture on X11: drag with the left button and tap a trigger key.</summary>
    public sealed class Gesture : IDisposable
    {
        Thread? _thread;
        volatile bool _stop;

        public event Action<int, int, uint>? KeyTapped;   // x, y, virtual key (Windows codes)
        public event Action? ButtonReleased;
        public event Action? Escape;
        public volatile bool Enabled = true;
        static readonly bool Debug = Environment.GetEnvironmentVariable("MANDARIN_X11_DEBUG") == "1";

        public bool Install()
        {
            if (!Available) return false;
            var ready = new ManualResetEventSlim();
            var ok = false;
            _thread = new Thread(() =>
            {
                var d = XOpenDisplay(IntPtr.Zero);
                if (d == IntPtr.Zero) { ready.Set(); return; }
                try
                {
                    if (!XQueryExtension(d, "XInputExtension", out var opcode, out _, out _)) { ready.Set(); return; }
                    int major = 2, minor = 0;
                    if (XIQueryVersion(d, ref major, ref minor) != 0) { ready.Set(); return; }
                    var maskBytes = new byte[3];
                    foreach (var e in new[] { XI_RawKeyPress, XI_RawKeyRelease, XI_RawButtonPress, XI_RawButtonRelease, XI_RawMotion })
                        maskBytes[e >> 3] |= (byte)(1 << (e & 7));
                    var maskPtr = Marshal.AllocHGlobal(maskBytes.Length);
                    Marshal.Copy(maskBytes, 0, maskPtr, maskBytes.Length);
                    var mask = new XIEventMask { deviceid = XIAllMasterDevices, mask_len = maskBytes.Length, mask = maskPtr };
                    XISelectEvents(d, XDefaultRootWindow(d), ref mask, 1);
                    XFlush(d);
                    ok = true;
                    Log.Info($"X11 gesture: XInput2 {major}.{minor}, opcode {opcode}");
                    ready.Set();
                    Loop(d, opcode);
                    Marshal.FreeHGlobal(maskPtr);
                }
                catch (Exception ex) { Log.Error("X11 gesture", ex); ready.Set(); }
                finally { XCloseDisplay(d); }
            }) { IsBackground = true, Name = "Mandarin X11 input" };
            _thread.Start();
            ready.Wait(2000);
            return ok;
        }

        void Loop(IntPtr d, int opcode)
        {
            var ev = Marshal.AllocHGlobal(256); // XEvent is 192 bytes
            var fd = new PollFd { fd = XConnectionNumber(d), events = 1 /* POLLIN */ };
            bool down = false, dragging = false;
            int downX = 0, downY = 0;
            long downTime = 0;
            var pressed = new HashSet<uint>();
            try
            {
                while (!_stop)
                {
                    if (XPending(d) == 0)
                    {
                        poll(ref fd, 1, 250);
                        continue;
                    }
                    XNextEvent(d, ev);
                    var cookie = Marshal.PtrToStructure<XGenericEventCookie>(ev);
                    if (cookie.type != GenericEvent || cookie.extension != opcode || !XGetEventData(d, ev)) continue;
                    try
                    {
                        cookie = Marshal.PtrToStructure<XGenericEventCookie>(ev);
                        var raw = Marshal.PtrToStructure<XIRawEvent>(cookie.data);
                        if (Debug) Log.Info($"X11 raw event {cookie.evtype} detail {raw.detail}");
                        switch (cookie.evtype)
                        {
                            case XI_RawButtonPress when raw.detail == 1:
                                down = true; dragging = false; downTime = Environment.TickCount64;
                                if (Pointer(d) is { } p0) (downX, downY) = (p0.x, p0.y);
                                break;
                            case XI_RawMotion when down && !dragging:
                                dragging = Moved(d, downX, downY);
                                break;
                            case XI_RawButtonRelease when raw.detail == 1:
                                var was = dragging || Moved(d, downX, downY);
                                down = dragging = false;
                                if (was) UiThread.Post(() => ButtonReleased?.Invoke());
                                break;
                            case XI_RawKeyPress:
                                var vk = VirtualKey(XkbKeycodeToKeysym(d, (byte)raw.detail, 0, 0));
                                if (vk == 0 || !pressed.Add(vk)) break; // auto-repeat is not a new tap
                                // some input sources send no raw motion: check where the pointer is now
                                if (down && !dragging) dragging = Moved(d, downX, downY);
                                if (Enabled && down && dragging && Environment.TickCount64 - downTime > 120)
                                {
                                    if (vk == 0x1B) UiThread.Post(() => Escape?.Invoke());
                                    else if (Pointer(d) is { } p2) UiThread.Post(() => KeyTapped?.Invoke(p2.x, p2.y, vk));
                                }
                                break;
                            case XI_RawKeyRelease:
                                pressed.Remove(VirtualKey(XkbKeycodeToKeysym(d, (byte)raw.detail, 0, 0)));
                                break;
                        }
                    }
                    finally { XFreeEventData(d, ev); }
                }
            }
            finally { Marshal.FreeHGlobal(ev); }
        }

        static bool Moved(IntPtr d, int x0, int y0) => Pointer(d) is { } p && (Math.Abs(p.x - x0) > 8 || Math.Abs(p.y - y0) > 8);

        static (int x, int y)? Pointer(IntPtr d) =>
            XQueryPointer(d, XDefaultRootWindow(d), out _, out _, out var x, out var y, out _, out _, out _) ? (x, y) : null;

        /// <summary>X keysym → the Windows virtual key code the trigger keys are described with.</summary>
        static uint VirtualKey(ulong keysym) => keysym switch
        {
            0xffe1 or 0xffe2 => 0x10, // Shift
            0xffe3 or 0xffe4 => 0x11, // Control
            0xffe9 or 0xffea or 0xff7e or 0xfe03 => 0x12, // Alt, Mode_switch, AltGr
            0xff09 => 0x09,           // Tab
            0x20 => 0x20,             // space
            0x60 => 0xC0,             // grave
            0xff1b => 0x1B,           // Escape
            _ => 0
        };

        public void Dispose()
        {
            _stop = true;
            _thread?.Join(1000);
        }
    }
}
