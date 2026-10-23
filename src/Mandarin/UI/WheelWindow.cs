using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Mandarin.Core;
using Mandarin.Interop;
using Path = System.Windows.Shapes.Path;

namespace Mandarin.UI;

/// <summary>
/// Radial menu: appears under the cursor while dragging (files arrive with Drop)
/// or on a click (files known in advance: main window, "Send to" and so on).
/// </summary>
public sealed class WheelWindow : Window
{
    readonly bool _dragMode;
    WheelMode _mode;
    IReadOnlyList<string> _files;
    List<WheelItem> _items = new();
    int _hover = -1;
    bool _revealed, _done;
    readonly int _screenX, _screenY;

    readonly double _size;
    readonly Canvas _canvas = new();
    readonly Grid _root = new();
    readonly List<SectorVisual> _sectors = new();
    readonly TextBlock _centerTitle = new(), _centerSub = new();
    readonly Image _centerThumb = new();
    readonly StackPanel _pill = new() { Orientation = Orientation.Horizontal };
    readonly ScaleTransform _scale = new(0.86, 0.86);
    Theme.WheelColors _c = Theme.Wheel;

    const double PillHeight = 44;

    /// <summary>One sector: its own layer (scaled around the wheel center) and brushes that can be animated.</summary>
    sealed record SectorVisual(Canvas Layer, ScaleTransform Scale, SolidColorBrush Fill, SolidColorBrush Stroke, SolidColorBrush Text);

    public static WheelWindow? Current { get; private set; }

    double R => _size / 2;
    double R1 => R * 0.42;   // inner radius of the sectors
    double R2 => R * 0.97;   // outer radius of the sectors
    double RC => R * 0.37;   // center circle
    Point Center => new(Width / 2, PillHeight + R + 6);

    WheelWindow(WheelMode mode, IReadOnlyList<string> files, bool dragMode, int x, int y)
    {
        _mode = mode;
        _files = files;
        _dragMode = dragMode;
        _screenX = x; _screenY = y;
        _size = Math.Clamp(AppSettings.Current.WheelSize, 240, 480);

        Title = "Mandarin";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = !dragMode;
        // almost transparent background: the window must accept Drop over the whole wheel
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Width = Math.Max(_size + 60, 470); // the mode bar is wider than a small wheel
        Height = _size + PillHeight + 24;
        AllowDrop = dragMode;
        FontFamily = Theme.UiFont;
        UseLayoutRounding = true;
        Motion.SetSkipEntrance(this, true);

        _root.Children.Add(_canvas);
        _canvas.RenderTransformOrigin = new Point(0.5, (PillHeight + R + 6) / Height);
        _canvas.RenderTransform = _scale;
        _canvas.Opacity = 0;
        Content = _root;

        Build();

        if (dragMode)
        {
            // exceptions in OLE handlers silently turn into "cannot drop" - log them
            DragEnter += (s, e) => Guard(() => OnDragEnter(s, e), e);
            DragOver += (s, e) => Guard(() => OnDragOver(s, e), e);
            DragLeave += (_, _) => SetHover(-1);
            Drop += (s, e) => Guard(() => OnDrop(s, e), e);
        }
        else
        {
            MouseMove += (_, e) => SetHover(HitTest(e.GetPosition(this)));
            MouseLeftButtonUp += (_, e) =>
            {
                var idx = HitTest(e.GetPosition(this));
                if (idx >= 0) Invoke(idx);
                else if ((e.GetPosition(this) - Center).Length > R2 + 6 && e.GetPosition(this).Y > PillHeight) CloseWheel();
            };
            Deactivated += (_, _) => { if (!_done && _screenX != int.MinValue) CloseWheel(); };
            KeyDown += OnKeyDown;
        }
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (dragMode) Native.MakeNoActivate(hwnd);
            PlaceAtCursor();
        };
        DpiChanged += (_, _) => PlaceAtCursor();
        Loaded += (_, _) =>
        {
            PlaceAtCursor();
            if (_dragMode) StartWatchdog();
        };
        ContentRendered += (_, _) => PlaceAtCursor();
    }

    // ---------- Opening ----------

    /// <summary>Wheel for the drag gesture: files arrive with DragEnter.</summary>
    public static void ShowForDrag(WheelMode mode, int x, int y)
    {
        if (Current is { _dragMode: true, _done: false } existing)
        {
            existing.SwitchMode(mode);
            return;
        }
        Current?.CloseWheel();
        var w = new WheelWindow(mode, Array.Empty<string>(), true, x, y);
        Current = w;
        w.Show();
        Log.Info($"Wheel shown for a drag at ({x},{y})");
    }

    /// <summary>Wheel with files already known (opened by a click).</summary>
    public static void ShowForFiles(IReadOnlyList<string> files, WheelMode mode = WheelMode.Convert, int? x = null, int? y = null)
    {
        Current?.CloseWheel();
        var (cx, cy) = x.HasValue ? (x.Value, y!.Value) : Native.CursorPos();
        var w = new WheelWindow(mode, files, false, cx, cy);
        Current = w;
        w.Show();
        w.Activate();
        w.Reveal();
    }

    /// <summary>Wheel for previews/screenshots: not tied to the cursor and visible right away.</summary>
    public static WheelWindow CreatePreview(WheelMode mode, IReadOnlyList<string> files, int hover = -1, bool hidden = false)
    {
        var w = new WheelWindow(mode, files, false, int.MinValue, int.MinValue);
        if (hidden) return w; // revealed later with PreviewReveal, to record the opening animation
        w._revealed = true;
        w._canvas.Opacity = 1;
        w._scale.ScaleX = w._scale.ScaleY = 1;
        if (hover >= 0) w.SetHover(hover);
        return w;
    }

    // hooks for off-screen motion previews
    public void PreviewReveal() => Reveal();
    public void PreviewHover(int index) => SetHover(index);
    public void PreviewChoose(int index) => CloseWheel(index);

    public static void OnGlobalButtonReleased()
    {
        var w = Current;
        if (w is not { _dragMode: true }) return;
        // the wheel never received files (a window, text or an editor object was dragged) - close at once,
        // so the invisible window does not catch the next click
        if (!w._revealed) { w.CloseWheel(); return; }
        // Drop arrives slightly after the button is released - give it time
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => { timer.Stop(); if (!w._done) w.CloseWheel(); };
        timer.Start();
    }

    public static void CloseCurrent() => Current?.CloseWheel();

    /// <summary>
    /// Guard against a stuck wheel: if the button-release event was lost, close
    /// based on the actual state of the left mouse button.
    /// </summary>
    void StartWatchdog()
    {
        var releasedSince = 0L;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            if (_done) { timer.Stop(); return; }
            var swapped = Native.GetSystemMetrics(Native.SM_SWAPBUTTON) != 0;
            var down = (Native.GetAsyncKeyState(swapped ? 0x02 : 0x01) & 0x8000) != 0;
            if (down) { releasedSince = 0; return; }
            if (releasedSince == 0) releasedSince = Environment.TickCount64;
            else if (Environment.TickCount64 - releasedSince > 600) { timer.Stop(); CloseWheel(); }
        };
        timer.Start();
    }

    void PlaceAtCursor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || _screenX == int.MinValue) return;
        var (work, scale) = Native.MonitorAt(_screenX, _screenY);
        var wPx = (int)Math.Round(Width * scale);
        var hPx = (int)Math.Round(Height * scale);
        var cyOffset = (int)Math.Round((PillHeight + R + 6) * scale);
        var left = _screenX - wPx / 2;
        var top = _screenY - cyOffset;
        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - wPx));
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - hPx));
        SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // ---------- Building ----------

    void Build()
    {
        _canvas.Children.Clear();
        _sectors.Clear();
        _c = Theme.Wheel;
        _items = _files.Count > 0 ? Catalog.Items(_mode, _files) : new List<WheelItem>();
        var center = Center;

        // shadowed backing ring
        var ring = new Ellipse
        {
            Width = R * 2 + 8, Height = R * 2 + 8, Fill = _c.Ring, Stroke = _c.RingStroke, StrokeThickness = 1,
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = Theme.IsDark ? 0.55 : 0.22, Color = Colors.Black }
        };
        Canvas.SetLeft(ring, center.X - R - 4);
        Canvas.SetTop(ring, center.Y - R - 4);
        _canvas.Children.Add(ring);

        var n = _items.Count;
        if (n > 0)
        {
            var step = 360.0 / n;
            const double gap = 5; // gap between sectors, equally wide along its whole length
            for (var i = 0; i < n; i++)
            {
                var a0 = -90 - step / 2 + i * step;
                var a1 = a0 + step;
                var fill = new SolidColorBrush(((SolidColorBrush)_c.Sector).Color);
                var stroke = new SolidColorBrush(((SolidColorBrush)_c.SectorStroke).Color);
                var text = new SolidColorBrush(((SolidColorBrush)_c.Text).Color);
                var scale = new ScaleTransform(1, 1);
                // a full-size layer per sector, so it can grow outward from the wheel center
                var layer = new Canvas
                {
                    Width = Width, Height = Height, IsHitTestVisible = false,
                    RenderTransformOrigin = new Point(center.X / Width, center.Y / Height), RenderTransform = scale
                };
                var path = new Path
                {
                    Data = n == 1 ? Ring(center, R1, R2) : Sector(center, R1, R2, a0, a1, gap),
                    Fill = fill,
                    Stroke = stroke,
                    StrokeThickness = 1,
                    SnapsToDevicePixels = true
                };
                layer.Children.Add(path);
                _canvas.Children.Add(layer);
                _sectors.Add(new SectorVisual(layer, scale, fill, stroke, text));

                var mid = (a0 + a1) / 2 * Math.PI / 180;
                var rm = (R1 + R2) / 2;
                var px = center.X + Math.Cos(mid) * rm;
                var py = center.Y + Math.Sin(mid) * rm;
                var fontSize = n > 8 ? 9.5 : 10.5;
                var labelWidth = Math.Min(2 * Math.PI * rm / n - 6, 92);

                var glyph = new TextBlock
                {
                    Text = _items[i].Glyph, FontFamily = Theme.IconFont, FontSize = n > 8 ? 15 : 17,
                    Foreground = text, TextAlignment = TextAlignment.Center, Width = labelWidth, IsHitTestVisible = false
                };
                var label = new TextBlock
                {
                    Text = _items[i].Label, FontSize = _items[i].Label.Length > 9 ? fontSize - 1.5 : fontSize,
                    FontWeight = FontWeights.SemiBold, Foreground = text,
                    TextAlignment = TextAlignment.Center, Width = labelWidth, TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(glyph, px - labelWidth / 2); Canvas.SetTop(glyph, py - 20);
                Canvas.SetLeft(label, px - labelWidth / 2); Canvas.SetTop(label, py + 1);
                layer.Children.Add(glyph);
                layer.Children.Add(label);
            }
        }

        // center
        var hub = new Ellipse
        {
            Width = RC * 2, Height = RC * 2, Fill = _c.Center, Stroke = _c.SectorStroke, StrokeThickness = 1,
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Opacity = 0.12, Color = Colors.Black }
        };
        Canvas.SetLeft(hub, center.X - RC); Canvas.SetTop(hub, center.Y - RC);
        _canvas.Children.Add(hub);

        _centerThumb.Width = RC * 0.95; _centerThumb.Height = RC * 0.8;
        _centerThumb.Stretch = Stretch.Uniform;
        _centerThumb.IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(_centerThumb, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(_centerThumb, center.X - _centerThumb.Width / 2);
        Canvas.SetTop(_centerThumb, center.Y - RC * 0.78);
        _canvas.Children.Add(_centerThumb);

        foreach (var t in new[] { _centerTitle, _centerSub })
        {
            t.Width = RC * 1.7; t.TextAlignment = TextAlignment.Center; t.TextWrapping = TextWrapping.Wrap;
            t.IsHitTestVisible = false; t.MaxHeight = 40; t.TextTrimming = TextTrimming.CharacterEllipsis;
            Canvas.SetLeft(t, center.X - RC * 0.85);
            _canvas.Children.Add(t);
        }
        _centerTitle.FontSize = 12.5; _centerTitle.FontWeight = FontWeights.SemiBold; _centerTitle.Foreground = _c.Text;
        _centerSub.FontSize = 10.5; _centerSub.Foreground = _c.SubText;

        BuildPill();
        UpdateCenter();
    }

    void BuildPill()
    {
        _pill.Children.Clear();
        if (_pill.Parent is Border previous) previous.Child = null; // the panel is reused when rebuilding
        var border = new Border
        {
            Background = _c.Pill, CornerRadius = new CornerRadius(15), Padding = new Thickness(5),
            BorderBrush = _c.RingStroke, BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.18, Color = Colors.Black },
            Child = _pill
        };
        foreach (var m in new[] { WheelMode.Convert, WheelMode.Tools, WheelMode.Actions })
        {
            var key = Catalog.ModeKey(m);
            var active = m == _mode;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var keySymbol = DragGesture.KeySymbol(key);
            if (keySymbol.Length > 0)
                content.Children.Add(new Border
                {
                    Background = active ? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) : (Theme.IsDark ? Theme.Brush(255, 255, 255, 20) : Theme.Brush(0, 0, 0, 14)),
                    CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(0, 0, 6, 0),
                    Child = new TextBlock { Text = keySymbol, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = active ? _c.HoverText : _c.SubText }
                });
            content.Children.Add(new TextBlock
            {
                Text = Catalog.ModeTitle(m), FontSize = 12, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = active ? _c.HoverText : _c.Text, VerticalAlignment = VerticalAlignment.Center
            });
            var idle = new SolidColorBrush(Colors.Transparent);
            var chip = new Border
            {
                Background = active ? _c.Hover : idle, CornerRadius = new CornerRadius(11),
                Padding = new Thickness(10, 4, 10, 4), Child = content, Cursor = _dragMode ? null : Cursors.Hand, Tag = m
            };
            if (!_dragMode)
            {
                chip.MouseLeftButtonUp += (s, e) => { e.Handled = true; SwitchMode((WheelMode)((Border)s!).Tag); };
                if (!active)
                {
                    var tint = Theme.IsDark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0);
                    chip.MouseEnter += (_, _) => Motion.ColorTo(idle, tint, 120);
                    chip.MouseLeave += (_, _) => Motion.ColorTo(idle, Colors.Transparent, 200);
                }
            }
            _pill.Children.Add(chip);
        }
        border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(border, (Width - border.DesiredSize.Width) / 2);
        Canvas.SetTop(border, 2);
        _canvas.Children.Add(border);
    }

    /// <summary>
    /// Ring sector between rays a0 and a1 (in degrees). Each edge is offset from its ray by gap/2
    /// in parallel, so the gap between neighboring sectors keeps the same width along its whole length
    /// instead of widening towards the outer edge as an angular gap would.
    /// </summary>
    static Geometry Sector(Point c, double r1, double r2, double a0, double a1, double gap)
    {
        Point P(double r, double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        // at radius r, an offset of h along the normal equals a rotation by asin(h / r)
        double Shift(double r) => Math.Asin(Math.Min(1, gap / 2 / r)) * 180 / Math.PI;
        double o0 = a0 + Shift(r2), o1 = a1 - Shift(r2);
        double i0 = a0 + Shift(r1), i1 = a1 - Shift(r1);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(r2, o0), true, true);
            ctx.ArcTo(P(r2, o1), new Size(r2, r2), 0, o1 - o0 > 180, SweepDirection.Clockwise, true, true);
            ctx.LineTo(P(r1, i1), true, true);
            ctx.ArcTo(P(r1, i0), new Size(r1, r1), 0, i1 - i0 > 180, SweepDirection.Counterclockwise, true, true);
        }
        g.Freeze();
        return g;
    }

    static Geometry Ring(Point c, double r1, double r2)
    {
        var g = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(c, r2, r2), new EllipseGeometry(c, r1, r1));
        g.Freeze();
        return g;
    }

    void UpdateCenter()
    {
        var before = _centerTitle.Text;
        UpdateCenterContent();
        // the caption under the thumbnail fades into the new text
        if (_revealed && before.Length > 0 && before != _centerTitle.Text)
        {
            Motion.FromTo(_centerTitle, OpacityProperty, 0.25, 1, 160);
            if (Motion.Translate(_centerTitle) is { } t) Motion.FromTo(t, TranslateTransform.YProperty, 4, 0, 200);
        }
    }

    void UpdateCenterContent()
    {
        var center = Center;
        if (_hover >= 0 && _hover < _items.Count)
        {
            // keep the file thumbnail visible and put the action title under it
            var hoverThumb = _files.Count > 0 ? ShellThumbs.Get(_files[0], 128) : null;
            _centerThumb.Source = hoverThumb;
            _centerThumb.Visibility = hoverThumb != null ? Visibility.Visible : Visibility.Collapsed;
            _centerTitle.Text = _items[_hover].Title;
            _centerSub.Text = hoverThumb != null ? "" : Catalog.Describe(_files);
            Canvas.SetTop(_centerTitle, hoverThumb != null ? center.Y + RC * 0.1 : center.Y - 22);
            Canvas.SetTop(_centerSub, center.Y + 12);
            return;
        }
        if (_files.Count == 0)
        {
            _centerThumb.Visibility = Visibility.Collapsed;
            _centerTitle.Text = Catalog.ModeTitle(_mode);
            _centerSub.Text = L.T("Release the file on an option");
            Canvas.SetTop(_centerTitle, center.Y - 22);
            Canvas.SetTop(_centerSub, center.Y + 2);
            return;
        }
        var thumb = ShellThumbs.Get(_files[0], 128);
        _centerThumb.Source = thumb;
        _centerThumb.Visibility = thumb != null ? Visibility.Visible : Visibility.Collapsed;
        _centerTitle.Text = _items.Count == 0 ? L.T("No options") : Catalog.Describe(_files);
        _centerSub.Text = _items.Count == 0 ? L.T("for these files") : "";
        Canvas.SetTop(_centerTitle, thumb != null ? center.Y + RC * 0.12 : center.Y - 12);
        Canvas.SetTop(_centerSub, thumb != null ? center.Y + RC * 0.12 + 18 : center.Y + 8);
    }

    // ---------- Interaction ----------

    int HitTest(Point p)
    {
        if (_items.Count == 0) return -1;
        var v = p - Center;
        var dist = v.Length;
        if (dist < RC + 4 || dist > R2 + 40) return -1;
        var deg = Math.Atan2(v.Y, v.X) * 180 / Math.PI;
        var step = 360.0 / _items.Count;
        var rel = (deg + 90 + step / 2 + 720) % 360;
        return Math.Clamp((int)(rel / step), 0, _items.Count - 1);
    }

    void SetHover(int idx)
    {
        if (idx == _hover) return;
        if (_hover >= 0 && _hover < _sectors.Count) Highlight(_sectors[_hover], false);
        _hover = idx;
        if (_hover >= 0 && _hover < _sectors.Count) Highlight(_sectors[_hover], true);
        UpdateCenter();
    }

    /// <summary>The hovered sector takes the accent color and moves slightly outward; the old one settles back.</summary>
    void Highlight(SectorVisual s, bool on)
    {
        var ms = on ? 110 : 220;
        Motion.ColorTo(s.Fill, ((SolidColorBrush)(on ? _c.Hover : _c.Sector)).Color, ms);
        Motion.ColorTo(s.Stroke, ((SolidColorBrush)(on ? _c.Hover : _c.SectorStroke)).Color, ms);
        Motion.ColorTo(s.Text, ((SolidColorBrush)(on ? _c.HoverText : _c.Text)).Color, ms);
        Motion.ScaleTo(s.Scale, on ? 1.045 : 1, on ? 260 : 220, on ? Motion.Spring : Motion.EaseOut);
    }

    void SwitchMode(WheelMode mode)
    {
        if (mode == _mode && _items.Count > 0) return;
        _mode = mode;
        _hover = -1;
        Build();
        _canvas.Opacity = _revealed ? 1 : 0;
        if (_revealed) AnimateSectorsIn(0.75);
    }

    void Reveal()
    {
        if (_revealed) return;
        _revealed = true;
        if (!Motion.Enabled)
        {
            // off-screen previews and "animation effects off": appear at once
            _canvas.BeginAnimation(OpacityProperty, null);
            _canvas.Opacity = 1;
            Motion.ScaleTo(_scale, 1, 0);
            return;
        }
        Motion.FromTo(_canvas, OpacityProperty, 0, 1, 140);
        Motion.ScaleFromTo(_scale, 0.8, 1, 320, Motion.Spring);
        AnimateSectorsIn(0.55);
    }

    /// <summary>Sectors grow out of the center one after another, clockwise from the top.</summary>
    void AnimateSectorsIn(double fromScale)
    {
        if (!Motion.Enabled) return;
        var step = Math.Min(22, 180 / Math.Max(1, _sectors.Count));
        for (var i = 0; i < _sectors.Count; i++)
        {
            var s = _sectors[i];
            Motion.FromTo(s.Layer, OpacityProperty, 0, 1, 170, Motion.EaseOut, i * step);
            Motion.ScaleFromTo(s.Scale, fromScale, 1, 340, Motion.Spring, i * step);
        }
    }

    static void Guard(Action a, DragEventArgs e)
    {
        try { a(); }
        catch (Exception ex)
        {
            Log.Error("Wheel: drag handling error", ex);
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    static List<string> ReadFiles(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            return files.Where(f => File.Exists(f) || Directory.Exists(f)).ToList();
        return new List<string>();
    }

    void OnDragEnter(object sender, DragEventArgs e)
    {
        if (_files.Count == 0)
        {
            var files = ReadFiles(e.Data);
            if (files.Count == 0)
            {
                // not files being dragged (text, a selection) - no wheel needed
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
            _files = files;
            Build();
            Reveal();
        }
        OnDragOver(sender, e);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (_files.Count == 0) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        var idx = HitTest(e.GetPosition(this));
        SetHover(idx);
        // never Move: Explorer must not delete the originals
        e.Effects = idx < 0 ? DragDropEffects.None
            : e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
            : e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
            : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        var idx = HitTest(e.GetPosition(this));
        Log.Info($"Wheel: drop on sector {idx}");
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        if (_files.Count == 0) _files = ReadFiles(e.Data);
        if (idx >= 0) Invoke(idx);
        else CloseWheel();
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape: CloseWheel(); return;
            case Key.Enter when _hover >= 0: Invoke(_hover); return;
            case Key.Right or Key.Down when _items.Count > 0: SetHover((_hover + 1) % _items.Count); e.Handled = true; return;
            case Key.Left or Key.Up when _items.Count > 0: SetHover((_hover - 1 + _items.Count) % _items.Count); e.Handled = true; return;
        }
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        foreach (var m in new[] { WheelMode.Convert, WheelMode.Tools, WheelMode.Actions })
            if (DragGesture.Matches(Catalog.ModeKey(m), vk)) { SwitchMode(m); e.Handled = true; return; }
    }

    void Invoke(int idx)
    {
        if (_done || idx < 0 || idx >= _items.Count) return;
        var item = _items[idx];
        var files = _files.ToList();
        CloseWheel(idx);
        // run after closing so tool dialogs get the focus
        Dispatcher.BeginInvoke(() =>
        {
            try { item.Run(files); }
            catch (Exception ex)
            {
                Log.Error("Wheel action", ex);
                Hud.ShowError(item.Title, ex is UserFacingException ? ex.Message : JobManager.Describe(ex));
            }
        });
    }

    void CloseWheel() => CloseWheel(-1);

    /// <summary>
    /// Closes the wheel: the chosen sector flashes and the wheel fades out growing slightly, a cancelled wheel
    /// shrinks away. The window stops taking input at once, so it never catches the next click.
    /// </summary>
    void CloseWheel(int chosen)
    {
        if (_done) return;
        _done = true;
        if (Current == this) Current = null;
        if (!Motion.Enabled || !_revealed || !IsVisible)
        {
            try { Close(); } catch { /* already closed */ }
            return;
        }
        IsHitTestVisible = false;
        AllowDrop = false;
        Background = Brushes.Transparent; // fully transparent pixels let clicks through the layered window
        if (chosen >= 0 && chosen < _sectors.Count) Motion.ScaleTo(_sectors[chosen].Scale, 1.1, 120);
        Motion.ScaleTo(_scale, chosen >= 0 ? 1.05 : 0.88, 170, Motion.EaseInOut);
        Motion.FromTo(_canvas, OpacityProperty, _canvas.Opacity, 0, 170, Motion.EaseInOut, chosen >= 0 ? 60 : 0, () =>
        {
            try { Close(); } catch { /* already closed */ }
        });
    }
}
