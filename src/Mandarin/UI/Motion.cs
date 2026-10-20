using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mandarin.UI;

/// <summary>
/// Shared motion: window entrances, staggered content, press feedback on buttons, hover lift for cards
/// and smooth mouse-wheel scrolling. Turned off entirely when Windows animation effects are off.
/// </summary>
public static class Motion
{
    static bool _initialized;

    /// <summary>Animations run only inside the app (not in off-screen renders) and only if Windows animates too.</summary>
    public static bool Enabled => _initialized && SystemParameters.ClientAreaAnimation;

    public static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseInOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });
    public static readonly IEasingFunction Spring = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 });

    static IEasingFunction Frozen(EasingFunctionBase e) { e.Freeze(); return e; }

    /// <summary>Windows with their own entrance (wheel, cards, shelf, tool windows) opt out of the generic one.</summary>
    public static readonly DependencyProperty SkipEntranceProperty =
        DependencyProperty.RegisterAttached("SkipEntrance", typeof(bool), typeof(Motion), new PropertyMetadata(false));

    public static void SetSkipEntrance(DependencyObject d, bool value) => d.SetValue(SkipEntranceProperty, value);

    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        foreach (var type in new[] { typeof(Button), typeof(ToggleButton) })
        {
            EventManager.RegisterClassHandler(type, UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPress), true);
            EventManager.RegisterClassHandler(type, UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((s, _) => OnRelease(s)), true);
            EventManager.RegisterClassHandler(type, UIElement.MouseLeaveEvent, new MouseEventHandler((s, _) => OnRelease(s)), true);
        }
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));
    }

    // ---------- primitives ----------

    /// <summary>Animates to a value and keeps it (for transforms and brushes the app owns).</summary>
    public static void To(IAnimatable target, DependencyProperty dp, double to, int ms, IEasingFunction? ease = null, int delay = 0)
    {
        if (!Enabled) { ((DependencyObject)target).SetValue(dp, to); target.BeginAnimation(dp, null); return; }
        target.BeginAnimation(dp, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease ?? EaseOut, BeginTime = TimeSpan.FromMilliseconds(delay)
        });
    }

    /// <summary>Animates from → to, then leaves the plain value: later code can set the property as usual.</summary>
    public static void FromTo(IAnimatable target, DependencyProperty dp, double from, double to, int ms, IEasingFunction? ease = null, int delay = 0, Action? done = null)
    {
        var d = (DependencyObject)target;
        if (!Enabled) { target.BeginAnimation(dp, null); d.SetValue(dp, to); done?.Invoke(); return; }
        d.SetValue(dp, from); // shown during the delay
        var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease ?? EaseOut, BeginTime = TimeSpan.FromMilliseconds(delay)
        };
        a.Completed += (_, _) =>
        {
            d.SetValue(dp, to);
            target.BeginAnimation(dp, null);
            done?.Invoke();
        };
        target.BeginAnimation(dp, a);
    }

    public static void ColorTo(SolidColorBrush brush, Color to, int ms)
    {
        if (brush.IsFrozen) return;
        if (!Enabled) { brush.BeginAnimation(SolidColorBrush.ColorProperty, null); brush.Color = to; return; }
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
    }

    static readonly DependencyProperty OwnTransformProperty =
        DependencyProperty.RegisterAttached("OwnTransform", typeof(TransformGroup), typeof(Motion), new PropertyMetadata(null));

    /// <summary>
    /// A scale (around the center) + translate pair that motion helpers share on one element;
    /// null if the element already uses a render transform of its own.
    /// </summary>
    static TransformGroup? Own(UIElement el)
    {
        if (el.GetValue(OwnTransformProperty) is TransformGroup g && ReferenceEquals(el.RenderTransform, g)) return g;
        if (el.RenderTransform is { } rt && rt != Transform.Identity && rt is not MatrixTransform { Matrix.IsIdentity: true }) return null;
        g = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } };
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = g;
        el.SetValue(OwnTransformProperty, g);
        return g;
    }

    public static TranslateTransform? Translate(UIElement el) => Own(el)?.Children[1] as TranslateTransform;

    public static ScaleTransform? Scale(UIElement el) => Own(el)?.Children[0] as ScaleTransform;

    public static void ScaleTo(ScaleTransform s, double to, int ms, IEasingFunction? ease = null, int delay = 0)
    {
        To(s, ScaleTransform.ScaleXProperty, to, ms, ease, delay);
        To(s, ScaleTransform.ScaleYProperty, to, ms, ease, delay);
    }

    public static void ScaleFromTo(ScaleTransform s, double from, double to, int ms, IEasingFunction? ease = null, int delay = 0)
    {
        FromTo(s, ScaleTransform.ScaleXProperty, from, to, ms, ease, delay);
        FromTo(s, ScaleTransform.ScaleYProperty, from, to, ms, ease, delay);
    }

    // ---------- compositions ----------

    /// <summary>Fades the element in while it slides up by <paramref name="dy"/> pixels.</summary>
    public static void Entrance(UIElement el, int delay = 0, double dy = 12, int ms = 280, double dx = 0)
    {
        if (!Enabled) return;
        var opacity = el.Opacity;
        FromTo(el, UIElement.OpacityProperty, 0, opacity, Math.Min(ms, 220), EaseOut, delay);
        if (Translate(el) is { } t)
        {
            if (dy != 0) FromTo(t, TranslateTransform.YProperty, dy, 0, ms, EaseOut, delay);
            if (dx != 0) FromTo(t, TranslateTransform.XProperty, dx, 0, ms, EaseOut, delay);
        }
    }

    /// <summary>Entrance for the children of a panel, one after another.</summary>
    public static void Stagger(Panel panel, int step = 24, int start = 0, double dy = 10, int maxItems = 14)
    {
        if (!Enabled) return;
        var i = 0;
        foreach (UIElement child in panel.Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            Entrance(child, start + Math.Min(i++, maxItems) * step, dy);
        }
    }

    /// <summary>Scales the element in from <paramref name="from"/> with a light overshoot.</summary>
    public static void Pop(UIElement el, double from = 0.6, int delay = 0, int ms = 340)
    {
        if (!Enabled) return;
        FromTo(el, UIElement.OpacityProperty, 0, el.Opacity, 160, EaseOut, delay);
        if (Scale(el) is { } s) ScaleFromTo(s, from, 1, ms, Spring, delay);
    }

    /// <summary>Shrinks and fades the element out, then runs <paramref name="done"/>.</summary>
    public static void PopOut(UIElement el, Action done, int ms = 160)
    {
        if (!Enabled) { done(); return; }
        el.IsHitTestVisible = false;
        if (Scale(el) is { } s) ScaleTo(s, 0.7, ms, EaseInOut);
        FromTo(el, UIElement.OpacityProperty, el.Opacity, 0, ms, EaseInOut, 0, done);
    }

    /// <summary>A short horizontal shake, for errors.</summary>
    public static void Shake(UIElement el)
    {
        if (!Enabled || Translate(el) is not { } t) return;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        double[] xs = { -9, 8, -6, 4, -2, 0 };
        for (var i = 0; i < xs.Length; i++)
            a.KeyFrames.Add(new EasingDoubleKeyFrame(xs[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70 * (i + 1))), EaseOut));
        a.Completed += (_, _) => { t.X = 0; t.BeginAnimation(TranslateTransform.XProperty, null); };
        t.BeginAnimation(TranslateTransform.XProperty, a);
    }

    /// <summary>Lifts the element slightly while the mouse is over it.</summary>
    public static void HoverLift(UIElement el, double lift = 3)
    {
        el.MouseEnter += (_, _) => { if (Translate(el) is { } t) To(t, TranslateTransform.YProperty, -lift, 180); };
        el.MouseLeave += (_, _) => { if (Translate(el) is { } t) To(t, TranslateTransform.YProperty, 0, 240); };
    }

    /// <summary>Moves a progress bar to a new value smoothly instead of jumping.</summary>
    public static void SmoothValue(RangeBase bar, double value)
    {
        if (!Enabled) { bar.BeginAnimation(RangeBase.ValueProperty, null); bar.Value = value; return; }
        bar.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(320)) { EasingFunction = EaseOut });
    }

    // ---------- global handlers ----------

    static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window w || !ReferenceEquals(e.OriginalSource, w) || (bool)w.GetValue(SkipEntranceProperty)) return;
        if (w.Content is UIElement content) Entrance(content, 0, 16, 320);
    }

    static void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (!Enabled || sender is not FrameworkElement { IsEnabled: true } el || Scale(el) is not { } s) return;
        ScaleTo(s, 0.95, 90);
    }

    static void OnRelease(object sender)
    {
        // only buttons that were pressed already carry the motion transforms
        if (sender is not FrameworkElement el || el.GetValue(OwnTransformProperty) is null || Scale(el) is not { } s) return;
        if (s.ScaleX >= 0.999 && !s.HasAnimatedProperties) return;
        ScaleTo(s, 1, 320, Spring);
    }

    // smooth scrolling: the offset glides to the target instead of jumping a fixed step per notch
    sealed class ScrollState { public double Target, LastSet; public bool Running; }
    static readonly ConditionalWeakTable<ScrollViewer, ScrollState> Scrolls = new();

    static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || !Enabled || sender is not ScrollViewer sv || sv.CanContentScroll) return;
        if (Keyboard.Modifiers != ModifierKeys.None || sv.ScrollableHeight <= 0) return;
        // a scrollable viewer nested under the cursor gets the wheel first
        for (var d = e.OriginalSource as DependencyObject; d != null && !ReferenceEquals(d, sv); d = Parent(d))
            if (d is ScrollViewer inner && inner.ScrollableHeight > 0) return;

        var st = Scrolls.GetOrCreateValue(sv);
        if (!st.Running) st.Target = sv.VerticalOffset;
        var step = 56.0 * Math.Max(1, SystemParameters.WheelScrollLines) / 3;
        st.Target = Math.Clamp(st.Target - e.Delta / 120.0 * step, 0, sv.ScrollableHeight);
        e.Handled = true;
        if (st.Running) return;

        st.Running = true;
        st.LastSet = sv.VerticalOffset;
        var clock = Stopwatch.StartNew();
        var last = 0.0;
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            var now = clock.Elapsed.TotalSeconds;
            var dt = Math.Clamp(now - last, 0.001, 0.05);
            last = now;
            var current = sv.VerticalOffset;
            // the user dragged the scroll bar or the viewer went away: stop following the old target
            var interrupted = Math.Abs(current - st.LastSet) > 2 || !sv.IsLoaded;
            var diff = st.Target - current;
            if (interrupted || Math.Abs(diff) < 0.5)
            {
                if (!interrupted) sv.ScrollToVerticalOffset(st.Target);
                st.Running = false;
                CompositionTarget.Rendering -= tick;
                return;
            }
            st.LastSet = current + diff * (1 - Math.Exp(-dt * 16));
            sv.ScrollToVerticalOffset(st.LastSet);
        };
        CompositionTarget.Rendering += tick;
    }

    static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
}
