using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Mandarin.Core;

namespace Mandarin.Interop;

public enum WheelMode { Convert, Tools, Actions }

/// <summary>
/// Global gesture tracking: the user drags something with the left mouse button and taps
/// a trigger key. The wheel then appears. The wheel receives the files through regular Drag&amp;Drop.
/// The hooks only observe: key presses are never swallowed or recorded, so other programs are not affected
/// (for example Shift for snapping while dragging in graphics editors).
/// The hooks live on a separate thread with their own message loop: if the UI thread is busy, Windows will not remove them on timeout.
/// </summary>
public sealed class DragGesture : IDisposable
{
    readonly Native.HookProc _mouseProc, _keyProc;
    IntPtr _mouseHook, _keyHook;
    readonly Dispatcher _ui;
    Thread? _thread;
    uint _threadId;

    // state is only changed on the hook thread
    bool _buttonDown;
    int _downX, _downY;
    long _downTime;
    bool _dragging;
    readonly HashSet<uint> _pressed = new();

    /// <summary>Gesture recognized: wheel mode and cursor screen coordinates (physical pixels).</summary>
    public event Action<WheelMode, int, int>? Triggered;
    /// <summary>Mouse button released (end of the drag).</summary>
    public event Action? ButtonReleased;
    /// <summary>Esc pressed while dragging.</summary>
    public event Action? Escape;

    public volatile bool Enabled = true;

    public DragGesture(Dispatcher ui)
    {
        _ui = ui;
        _mouseProc = MouseProc;
        _keyProc = KeyProc;
    }

    public void Install()
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var mod = Native.GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, mod, 0);
            _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, mod, 0);
            var error = Marshal.GetLastWin32Error();
            ready.Set();
            if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
                _ui.BeginInvoke(() => Log.Error($"Could not install input hooks: {error}"));
            // our own message loop: low-level hooks are not called without one
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
            if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook);
            _mouseHook = _keyHook = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "Mandarin input hooks",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
        ready.Wait(2000);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
    }

    bool DragActive => _dragging && _buttonDown;

    IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (int)wParam;
            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            switch (msg)
            {
                case Native.WM_LBUTTONDOWN:
                    _buttonDown = true;
                    _dragging = false;
                    _downX = data.pt.X; _downY = data.pt.Y;
                    _downTime = Environment.TickCount64;
                    break;
                case Native.WM_MOUSEMOVE when _buttonDown && !_dragging:
                    var dx = Math.Abs(data.pt.X - _downX);
                    var dy = Math.Abs(data.pt.Y - _downY);
                    if (dx > Native.GetSystemMetrics(Native.SM_CXDRAG) * 2 || dy > Native.GetSystemMetrics(Native.SM_CYDRAG) * 2)
                        _dragging = true;
                    break;
                case Native.WM_LBUTTONUP:
                    var wasDragging = _dragging;
                    _buttonDown = false;
                    _dragging = false;
                    if (wasDragging) _ui.BeginInvoke(() => ButtonReleased?.Invoke(), DispatcherPriority.Input);
                    break;
            }
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (int)wParam;
            var vk = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam).vkCode;
            if (msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN)
            {
                // auto-repeat of a held key is not a new press
                var isRepeat = !_pressed.Add(vk);
                if (!isRepeat && Enabled && DragActive && Environment.TickCount64 - _downTime > 120)
                {
                    if (vk == 0x1B)
                        _ui.BeginInvoke(() => Escape?.Invoke());
                    else if (ModeFor(vk) is { } mode)
                    {
                        Native.GetCursorPos(out var p);
                        _ui.BeginInvoke(() => Triggered?.Invoke(mode, p.X, p.Y), DispatcherPriority.Input);
                    }
                }
            }
            else if (msg is Native.WM_KEYUP or Native.WM_SYSKEYUP)
            {
                _pressed.Remove(vk);
            }
        }
        return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    static WheelMode? ModeFor(uint vk)
    {
        var s = AppSettings.Current;
        if (Matches(s.ConvertKey, vk)) return WheelMode.Convert;
        if (Matches(s.ToolsKey, vk)) return WheelMode.Tools;
        if (Matches(s.ActionsKey, vk)) return WheelMode.Actions;
        return null;
    }

    public static bool Matches(TriggerKey key, uint vk) => key switch
    {
        TriggerKey.Shift => vk is 0x10 or 0xA0 or 0xA1,
        TriggerKey.Ctrl => vk is 0x11 or 0xA2 or 0xA3,
        TriggerKey.Alt => vk is 0x12 or 0xA4 or 0xA5,
        TriggerKey.Tab => vk == 0x09,
        TriggerKey.Space => vk == 0x20,
        TriggerKey.Backquote => vk == 0xC0,
        _ => false
    };

    public static string KeyLabel(TriggerKey key) => key switch
    {
        TriggerKey.Shift => "Shift",
        TriggerKey.Ctrl => "Ctrl",
        TriggerKey.Alt => "Alt",
        TriggerKey.Tab => "Tab",
        TriggerKey.Space => L.T("Space"),
        TriggerKey.Backquote => "`",
        _ => "—"
    };

    public static string KeySymbol(TriggerKey key) => key switch
    {
        TriggerKey.Shift => "⇧",
        TriggerKey.Ctrl => "Ctrl",
        TriggerKey.Alt => "Alt",
        TriggerKey.Tab => "⇥",
        TriggerKey.Space => "␣",
        TriggerKey.Backquote => "`",
        _ => ""
    };

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public Native.POINT pt; }

    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
