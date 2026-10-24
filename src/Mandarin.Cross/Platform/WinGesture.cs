using System.Diagnostics;
using System.Runtime.InteropServices;
using Mandarin.Core;

namespace Mandarin.Platform;

/// <summary>
/// Global drag gesture on Windows 7/8 (the same low-level hooks as the Windows 10/11 app): the user drags something
/// with the left mouse button and taps a trigger key. The hooks only observe: nothing is swallowed or recorded.
/// They live on their own thread with a message loop, so a busy interface never makes Windows drop them.
/// </summary>
public sealed class WinGesture : IDisposable
{
    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    const int SM_CXDRAG = 68, SM_CYDRAG = 69, SM_SWAPBUTTON = 23;

    readonly HookProc _mouseProc, _keyProc;
    IntPtr _mouseHook, _keyHook;
    Thread? _thread;
    uint _threadId;
    bool _buttonDown, _dragging;
    int _downX, _downY;
    long _downTime;
    readonly HashSet<uint> _pressed = new();

    public event Action<int, int, uint>? KeyTapped;   // x, y, virtual key
    public event Action? ButtonReleased;
    public event Action? Escape;
    public volatile bool Enabled = true;

    public WinGesture()
    {
        _mouseProc = MouseProc;
        _keyProc = KeyProc;
    }

    /// <summary>The left button is held right now (for the wheel watchdog).</summary>
    public static bool LeftButtonDown() => (GetAsyncKeyState(GetSystemMetrics(SM_SWAPBUTTON) != 0 ? 0x02 : 0x01) & 0x8000) != 0;

    public bool Install()
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var mod = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
            _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, mod, 0);
            var error = Marshal.GetLastWin32Error();
            ready.Set();
            if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero) Log.Error($"Could not install input hooks: {error}");
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
        }) { IsBackground = true, Name = "Mandarin input hooks", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(2000);
        return _mouseHook != IntPtr.Zero && _keyHook != IntPtr.Zero;
    }

    IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            switch ((int)wParam)
            {
                case WM_LBUTTONDOWN:
                    _buttonDown = true; _dragging = false;
                    _downX = data.pt.X; _downY = data.pt.Y;
                    _downTime = Environment.TickCount64;
                    break;
                case WM_MOUSEMOVE when _buttonDown && !_dragging:
                    if (Math.Abs(data.pt.X - _downX) > GetSystemMetrics(SM_CXDRAG) * 2 || Math.Abs(data.pt.Y - _downY) > GetSystemMetrics(SM_CYDRAG) * 2)
                        _dragging = true;
                    break;
                case WM_LBUTTONUP:
                    var was = _dragging;
                    _buttonDown = _dragging = false;
                    if (was) UiThread.Post(() => ButtonReleased?.Invoke());
                    break;
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (int)wParam;
            var vk = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam).vkCode;
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var repeat = !_pressed.Add(vk);
                if (!repeat && Enabled && _dragging && _buttonDown && Environment.TickCount64 - _downTime > 120)
                {
                    if (vk == 0x1B) UiThread.Post(() => Escape?.Invoke());
                    else
                    {
                        GetCursorPos(out var p);
                        UiThread.Post(() => KeyTapped?.Invoke(p.X, p.Y, vk));
                    }
                }
            }
            else if (msg is WM_KEYUP or WM_SYSKEYUP) _pressed.Remove(vk);
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
    }
}
