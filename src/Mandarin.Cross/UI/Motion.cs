using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Mandarin.Core;

namespace Mandarin.UI;

/// <summary>
/// Shared motion of the cross-platform interface: entrances, staggered content, hover lift, color fades and springs.
/// A small tween engine on the UI thread (the same on Avalonia 11 and 12). Turned off when the system turns animations off.
/// </summary>
public static class Motion
{
    static bool _systemAllows = true;

    public static bool Enabled { get; private set; }

    public static readonly Func<double, double> EaseOut = t => 1 - Math.Pow(1 - t, 3);
    public static readonly Func<double, double> EaseInOut = t => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    public static readonly Func<double, double> Spring = t =>
    {
        const double s = 0.45 * 1.70158;
        var u = t - 1;
        return u * u * ((s + 1) * u + s) + 1;
    };

    [DllImport("user32.dll")]
    static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);

    public static void Init()
    {
        try
        {
            if (OsInfo.IsWindows && SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out var on, 0)) _systemAllows = on;
            else if (OsInfo.IsLinux && Platform.Desktop.Which("gsettings") is { } gs)
            {
                var psi = new ProcessStartInfo(gs) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "get", "org.gnome.desktop.interface", "enable-animations" }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEnd();
                if (p.WaitForExit(1500) && output.Trim() == "false") _systemAllows = false;
            }
        }
        catch { /* keep the default */ }
        Enabled = _systemAllows;
    }

    // ---------- the tween engine ----------

    sealed class Tween
    {
        public required Action<double> Set;
        public double From, To;
        public long Start;
        public int Delay, Duration;
        public required Func<double, double> Ease;
        public Action? Done;
        public bool Started;
    }

    static readonly Dictionary<(object, string), Tween> Active = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static DispatcherTimer? _timer;

    /// <summary>Animates a value; a new animation of the same target and name replaces the running one.</summary>
    public static void Animate(object target, string name, double from, double to, int ms, Action<double> set,
        Func<double, double>? ease = null, int delay = 0, Action? done = null)
    {
        if (!Enabled || ms <= 0)
        {
            Active.Remove((target, name));
            set(to);
            done?.Invoke();
            return;
        }
        Active[(target, name)] = new Tween
        {
            Set = set, From = from, To = to, Start = Clock.ElapsedMilliseconds, Delay = delay, Duration = ms, Ease = ease ?? EaseOut, Done = done
        };
        if (delay == 0) set(from);
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => Tick());
        _timer.Start();
    }

    static void Tick()
    {
        var now = Clock.ElapsedMilliseconds;
        foreach (var (key, t) in Active.ToList())
        {
            var elapsed = now - t.Start - t.Delay;
            if (elapsed < 0) continue;
            if (!t.Started) { t.Started = true; }
            var p = Math.Min(1, elapsed / (double)t.Duration);
            t.Set(t.From + (t.To - t.From) * t.Ease(p));
            if (p >= 1)
            {
                Active.Remove(key);
                t.Done?.Invoke();
            }
        }
        if (Active.Count == 0) _timer?.Stop();
    }

    // ---------- helpers ----------

    /// <summary>A scale + translate pair the helpers share on one element.</summary>
    public static (ScaleTransform scale, TranslateTransform move) Xf(Visual v)
    {
        if (v.RenderTransform is TransformGroup { Children: [ScaleTransform s, TranslateTransform t] }) return (s, t);
        var scale = new ScaleTransform(1, 1);
        var move = new TranslateTransform();
        v.RenderTransform = new TransformGroup { Children = { scale, move } };
        v.RenderTransformOrigin = RelativePoint.Center;
        return (scale, move);
    }

    public static void Opacity(Visual v, double from, double to, int ms, Func<double, double>? ease = null, int delay = 0, Action? done = null) =>
        Animate(v, "opacity", from, to, ms, x => v.Opacity = x, ease, delay, done);

    public static void MoveY(Visual v, double from, double to, int ms, Func<double, double>? ease = null, int delay = 0)
    {
        var (_, m) = Xf(v);
        Animate(v, "y", from, to, ms, x => m.Y = x, ease, delay);
    }

    public static void MoveX(Visual v, double from, double to, int ms, Func<double, double>? ease = null, int delay = 0, Action? done = null)
    {
        var (_, m) = Xf(v);
        Animate(v, "x", from, to, ms, x => m.X = x, ease, delay, done);
    }

    public static void ScaleTo(Visual v, double from, double to, int ms, Func<double, double>? ease = null, int delay = 0, Action? done = null)
    {
        var (s, _) = Xf(v);
        Animate(v, "scale", from, to, ms, x => { s.ScaleX = x; s.ScaleY = x; }, ease, delay, done);
    }

    public static double CurrentScale(Visual v) => Xf(v).scale.ScaleX;

    public static double CurrentY(Visual v) => Xf(v).move.Y;

    public static void ColorTo(SolidColorBrush brush, Color to, int ms)
    {
        var from = brush.Color;
        Animate(brush, "color", 0, 1, ms, t => brush.Color = Look.Mix(from, to, t));
    }

    /// <summary>Fades the element in while it slides up by <paramref name="dy"/> pixels.</summary>
    public static void Entrance(Visual v, int delay = 0, double dy = 12, int ms = 280, double dx = 0)
    {
        if (!Enabled) return;
        v.Opacity = 0;
        Opacity(v, 0, 1, ms, EaseOut, delay);
        if (dy != 0) MoveY(v, dy, 0, ms + 60, EaseOut, delay);
        if (dx != 0) MoveX(v, dx, 0, ms + 60, EaseOut, delay);
    }

    /// <summary>Entrance for the children of a panel, one after another.</summary>
    public static void Stagger(Panel panel, int step = 24, int start = 0, double dy = 10, int maxItems = 14)
    {
        var i = 0;
        foreach (var child in panel.Children.ToList())
        {
            if (!child.IsVisible) continue;
            Entrance(child, start + Math.Min(i, maxItems) * step, dy);
            i++;
        }
    }

    public static void Pop(Visual v, double from = 0.6, int delay = 0, int ms = 340)
    {
        Opacity(v, 0, 1, ms / 2, EaseOut, delay);
        ScaleTo(v, from, 1, ms, Spring, delay);
    }

    public static void Shake(Visual v)
    {
        var (_, m) = Xf(v);
        Animate(v, "x", 0, 1, 380, t => m.X = Math.Sin(t * Math.PI * 6) * 7 * (1 - t), x => x);
    }

    /// <summary>Lifts the element slightly while the pointer is over it.</summary>
    public static void HoverLift(Control c, double lift = 3)
    {
        c.PointerEntered += (_, _) => MoveY(c, CurrentY(c), -lift, 180, EaseOut);
        c.PointerExited += (_, _) => MoveY(c, CurrentY(c), 0, 220, EaseOut);
    }

    /// <summary>Press feedback: the control shrinks a little while pressed.</summary>
    public static void Press(Control c)
    {
        c.AddHandler(InputElement.PointerPressedEvent, (_, _) => ScaleTo(c, CurrentScale(c), 0.96, 90), Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        c.AddHandler(InputElement.PointerReleasedEvent, (_, _) => ScaleTo(c, CurrentScale(c), 1, 260, Spring), Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        c.PointerExited += (_, _) => { if (CurrentScale(c) < 1) ScaleTo(c, CurrentScale(c), 1, 200); };
    }

    /// <summary>Moves a progress bar to a new value smoothly instead of jumping.</summary>
    public static void SmoothValue(RangeBase bar, double value)
    {
        if (value < bar.Value) { Active.Remove((bar, "value")); bar.Value = value; return; }
        Animate(bar, "value", bar.Value, value, 260, x => bar.Value = x, EaseOut);
    }
}
