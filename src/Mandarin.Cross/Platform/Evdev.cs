using System.Runtime.InteropServices;
using Mandarin.Core;

namespace Mandarin.Platform;

/// <summary>
/// The drag gesture under Wayland, where no program sees the input of the others: the events are read from the input
/// devices themselves (/dev/input/event*), the way Espanso or input-remapper do. Reading them needs the "input" group;
/// the events are only observed (nothing is grabbed), and only the left button, pointer motion and the trigger keys
/// are looked at. The pointer position comes from KWin on KDE Plasma; elsewhere the wheel opens in the screen center.
/// </summary>
public sealed class Evdev : IDisposable
{
    const int O_RDONLY = 0, O_NONBLOCK = 0x800, O_CLOEXEC = 0x80000, POLLIN = 1;
    const ushort EV_KEY = 1, EV_REL = 2, EV_ABS = 3;
    const ushort BTN_LEFT = 0x110, REL_X = 0, REL_Y = 1, ABS_X = 0, ABS_Y = 1, ABS_MT_POSITION_X = 0x35, ABS_MT_POSITION_Y = 0x36;
    const int EventSize = 24; // struct input_event on 64-bit Linux: timeval (16), type, code, value

    [StructLayout(LayoutKind.Sequential)]
    struct PollFd { public int fd; public short events; public short revents; }

    [DllImport("libc", SetLastError = true)] static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] static extern nint read(int fd, byte[] buffer, nint count);
    [DllImport("libc", SetLastError = true)] static extern int poll([In, Out] PollFd[] fds, nuint count, int timeout);

    readonly Dictionary<string, int> _devices = new();
    Thread? _thread;
    volatile bool _stop;
    static volatile bool _buttonDown;
    static bool _installed;

    public event Action<(int x, int y)?, uint>? KeyTapped;   // pointer position (null - unknown), virtual key (Windows codes)
    public event Action? ButtonReleased;
    public event Action? Escape;
    public volatile bool Enabled = true;

    /// <summary>A keyboard and a pointing device can be read (the user is in the "input" group).</summary>
    public static bool Readable()
    {
        bool CanOpen(IEnumerable<string> paths) => paths.Any(path =>
        {
            var fd = open(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
            if (fd < 0) return false;
            close(fd);
            return true;
        });
        var devices = Devices();
        return CanOpen(devices.Where(d => d.keyboard).Select(d => d.path)) && CanOpen(devices.Where(d => d.pointer).Select(d => d.path));
    }

    /// <summary>The left button is held (for the wheel watchdog); null when the devices are not read.</summary>
    public static bool? LeftButtonDown() => _installed ? _buttonDown : null;

    /// <summary>Keyboards and pointing devices from /proc/bus/input/devices (game pads, sensors and the rest are not read).</summary>
    static List<(string path, bool keyboard, bool pointer)> Devices()
    {
        var list = new List<(string, bool, bool)>();
        try
        {
            foreach (var line in File.ReadLines("/proc/bus/input/devices"))
            {
                // H: Handlers=sysrq kbd leds event3
                if (!line.StartsWith("H: Handlers=", StringComparison.Ordinal)) continue;
                var handlers = line[12..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var node = handlers.FirstOrDefault(h => h.StartsWith("event", StringComparison.Ordinal));
                bool keyboard = handlers.Contains("kbd"), pointer = handlers.Any(h => h.StartsWith("mouse", StringComparison.Ordinal));
                if (node != null && (keyboard || pointer)) list.Add(("/dev/input/" + node, keyboard, pointer));
            }
        }
        catch { /* no procfs */ }
        return list;
    }

    public bool Install()
    {
        if (!OsInfo.IsLinux) return false;
        Rescan();
        if (_devices.Count == 0) return false;
        Log.Info($"Wayland gesture: reading {_devices.Count} input devices");
        _installed = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Mandarin input devices" };
        _thread.Start();
        return true;
    }

    /// <summary>Opens the devices that appeared (a mouse plugged in) and forgets the ones that are gone.</summary>
    void Rescan()
    {
        var present = Devices().Select(d => d.path).ToHashSet();
        foreach (var gone in _devices.Keys.Where(p => !present.Contains(p)).ToList())
        {
            close(_devices[gone]);
            _devices.Remove(gone);
        }
        foreach (var path in present.Where(p => !_devices.ContainsKey(p)))
        {
            var fd = open(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
            if (fd >= 0) _devices[path] = fd;
        }
    }

    void Loop()
    {
        var buffer = new byte[EventSize * 64];
        bool dragging = false;
        long downTime = 0, lastScan = Environment.TickCount64;
        int moved = 0, absEvents = 0;
        var pressed = new HashSet<uint>();
        try
        {
            while (!_stop)
            {
                if (Environment.TickCount64 - lastScan > 3000) { Rescan(); lastScan = Environment.TickCount64; }
                var fds = _devices.Values.Select(fd => new PollFd { fd = fd, events = POLLIN }).ToArray();
                if (poll(fds, (nuint)fds.Length, 500) <= 0) continue;
                foreach (var p in fds)
                {
                    if ((p.revents & POLLIN) == 0)
                    {
                        // POLLERR / POLLHUP: the device was unplugged, the next scan forgets it
                        continue;
                    }
                    nint n;
                    while ((n = read(p.fd, buffer, buffer.Length)) >= EventSize)
                    {
                        for (var off = 0; off + EventSize <= n; off += EventSize)
                        {
                            var type = BitConverter.ToUInt16(buffer, off + 16);
                            var code = BitConverter.ToUInt16(buffer, off + 18);
                            var value = BitConverter.ToInt32(buffer, off + 20);
                            switch (type)
                            {
                                case EV_KEY when code == BTN_LEFT && value == 1:
                                    _buttonDown = true; dragging = false; moved = 0; absEvents = 0; downTime = Environment.TickCount64;
                                    break;
                                case EV_KEY when code == BTN_LEFT && value == 0:
                                    var was = dragging;
                                    _buttonDown = dragging = false;
                                    if (was) UiThread.Post(() => ButtonReleased?.Invoke());
                                    break;
                                case EV_REL when _buttonDown && !dragging && code is REL_X or REL_Y:
                                    moved += Math.Abs(value);
                                    dragging = moved > 8;
                                    break;
                                case EV_ABS when _buttonDown && !dragging && code is ABS_X or ABS_Y or ABS_MT_POSITION_X or ABS_MT_POSITION_Y:
                                    // a touchpad or a tablet: the units differ from device to device, a few reports mean motion
                                    dragging = ++absEvents > 6;
                                    break;
                                case EV_KEY when value == 1 && VirtualKey(code) is var vk and not 0:
                                    if (!pressed.Add(vk)) break;
                                    if (Enabled && _buttonDown && dragging && Environment.TickCount64 - downTime > 120)
                                    {
                                        if (vk == 0x1B) UiThread.Post(() => Escape?.Invoke());
                                        else _ = Report(vk);
                                    }
                                    break;
                                case EV_KEY when value == 0:
                                    pressed.Remove(VirtualKey(code));
                                    break;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) { Log.Error("Wayland gesture", ex); }
        finally
        {
            foreach (var fd in _devices.Values) close(fd);
            _devices.Clear();
            _installed = false;
        }
    }

    async Task Report(uint vk)
    {
        var pos = await KWin.CursorAsync();
        UiThread.Post(() => KeyTapped?.Invoke(pos, vk));
    }

    /// <summary>Linux key code → the Windows virtual key code the trigger keys are described with.</summary>
    static uint VirtualKey(ushort code) => code switch
    {
        42 or 54 => 0x10,  // Shift
        29 or 97 => 0x11,  // Control
        56 or 100 => 0x12, // Alt, AltGr
        15 => 0x09,        // Tab
        57 => 0x20,        // space
        41 => 0xC0,        // grave
        1 => 0x1B,         // Escape
        _ => 0
    };

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(1000);
    }
}
