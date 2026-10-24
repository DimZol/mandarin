using Mandarin.Core;

namespace Mandarin.Platform;

public enum WheelMode { Convert, Tools, Actions }

/// <summary>
/// The drag gesture of this system (Windows hooks or X11 raw events) and the trigger keys: drag a file, tap a key, the
/// wheel opens under the pointer.
/// </summary>
public sealed class Gesture : IDisposable
{
    readonly WinGesture? _win;
    readonly X11.Gesture? _x11;

    /// <summary>Wheel mode and the pointer position on the screen (physical pixels).</summary>
    public event Action<WheelMode, int, int>? Triggered;
    public event Action? ButtonReleased;
    public event Action? Escape;

    /// <summary>The gesture works on this system (Windows, or Linux with X11).</summary>
    public bool Works { get; }

    public Gesture()
    {
        if (OsInfo.IsWindows)
        {
            _win = new WinGesture();
            _win.KeyTapped += OnKey; _win.ButtonReleased += () => ButtonReleased?.Invoke(); _win.Escape += () => Escape?.Invoke();
            Works = _win.Install();
        }
        else if (X11.Available)
        {
            _x11 = new X11.Gesture();
            _x11.KeyTapped += OnKey; _x11.ButtonReleased += () => ButtonReleased?.Invoke(); _x11.Escape += () => Escape?.Invoke();
            Works = _x11.Install();
        }
    }

    public bool Enabled
    {
        set
        {
            if (_win != null) _win.Enabled = value;
            if (_x11 != null) _x11.Enabled = value;
        }
    }

    void OnKey(int x, int y, uint vk)
    {
        if (ModeFor(vk) is { } mode) Triggered?.Invoke(mode, x, y);
    }

    /// <summary>The left mouse button is held right now; null when the system does not tell.</summary>
    public static bool? LeftButtonDown() => OsInfo.IsWindows ? WinGesture.LeftButtonDown() : X11.LeftButtonDown();

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

    public void Dispose()
    {
        _win?.Dispose();
        _x11?.Dispose();
    }
}
