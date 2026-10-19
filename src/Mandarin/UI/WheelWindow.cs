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
    readonly List<Path> _sectorPaths = new();
    readonly List<(TextBlock glyph, TextBlock label)> _sectorTexts = new();
    readonly TextBlock _centerTitle = new(), _centerSub = new();
    readonly Image _centerThumb = new();
    readonly StackPanel _pill = new() { Orientation = Orientation.Horizontal };
    readonly ScaleTransform _scale = new(0.86, 0.86);
    Theme.WheelColors _c = Theme.Wheel;

    const double PillHeight = 44;

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
    public static WheelWindow CreatePreview(WheelMode mode, IReadOnlyList<string> files, int hover = -1)
    {
        var w = new WheelWindow(mode, files, false, int.MinValue, int.MinValue);
        w._revealed = true;
        w._canvas.Opacity = 1;
        w._scale.ScaleX = w._scale.ScaleY = 1;
        if (hover >= 0) w.SetHover(hover);
        return w;
    }

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
        _sectorPaths.Clear();
        _sectorTexts.Clear();
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
                var path = new Path
                {
                    Data = n == 1 ? Ring(center, R1, R2) : Sector(center, R1, R2, a0, a1, gap),
                    Fill = _c.Sector,
                    Stroke = _c.SectorStroke,
                    StrokeThickness = 1,
                    SnapsToDevicePixels = true
                };
                _canvas.Children.Add(path);
                _sectorPaths.Add(path);

                var mid = (a0 + a1) / 2 * Math.PI / 180;
                var rm = (R1 + R2) / 2;
                var px = center.X + Math.Cos(mid) * rm;
                var py = center.Y + Math.Sin(mid) * rm;
                var fontSize = n > 8 ? 9.5 : 10.5;
                var labelWidth = Math.Min(2 * Math.PI * rm / n - 6, 92);

                var glyph = new TextBlock
                {
                    Text = _items[i].Glyph, FontFamily = Theme.IconFont, FontSize = n > 8 ? 15 : 17,
                    Foreground = _c.Text, TextAlignment = TextAlignment.Center, Width = labelWidth, IsHitTestVisible = false
                };
                var label = new TextBlock
                {
                    Text = _items[i].Label, FontSize = _items[i].Label.Length > 9 ? fontSize - 1.5 : fontSize,
                    FontWeight = FontWeights.SemiBold, Foreground = _c.Text,
                    TextAlignment = TextAlignment.Center, Width = labelWidth, TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(glyph, px - labelWidth / 2); Canvas.SetTop(glyph, py - 20);
                Canvas.SetLeft(label, px - labelWidth / 2); Canvas.SetTop(label, py + 1);
                _canvas.Children.Add(glyph);
                _canvas.Children.Add(label);
                _sectorTexts.Add((glyph, label));
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
                    Background = active ? Theme.Brush(255, 255, 255, 60) : (Theme.IsDark ? Theme.Brush(255, 255, 255, 20) : Theme.Brush(0, 0, 0, 14)),
                    CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(0, 0, 6, 0),
                    Child = new TextBlock { Text = keySymbol, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = active ? Brushes.White : _c.SubText }
                });
            content.Children.Add(new TextBlock
            {
                Text = Catalog.ModeTitle(m), FontSize = 12, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = active ? Brushes.White : _c.Text, VerticalAlignment = VerticalAlignment.Center
            });
            var chip = new Border
            {
                Background = active ? _c.Hover : Brushes.Transparent, CornerRadius = new CornerRadius(11),
                Padding = new Thickness(10, 4, 10, 4), Child = content, Cursor = _dragMode ? null : Cursors.Hand, Tag = m
            };
            if (!_dragMode) chip.MouseLeftButtonUp += (s, e) => { e.Handled = true; SwitchMode((WheelMode)((Border)s!).Tag); };
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
        var center = Center;
        if (_hover >= 0 && _hover < _items.Count)
        {
            _centerThumb.Visibility = Visibility.Collapsed;
            _centerTitle.Text = _items[_hover].Title;
            _centerSub.Text = Catalog.Describe(_files);
            Canvas.SetTop(_centerTitle, center.Y - 22);
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
        if (_hover >= 0 && _hover < _sectorPaths.Count)
        {
            _sectorPaths[_hover].Fill = _c.Sector;
            _sectorPaths[_hover].Stroke = _c.SectorStroke;
            _sectorTexts[_hover].glyph.Foreground = _c.Text;
            _sectorTexts[_hover].label.Foreground = _c.Text;
        }
        _hover = idx;
        if (_hover >= 0 && _hover < _sectorPaths.Count)
        {
            _sectorPaths[_hover].Fill = _c.Hover;
            _sectorPaths[_hover].Stroke = _c.Hover;
            _sectorTexts[_hover].glyph.Foreground = _c.HoverText;
            _sectorTexts[_hover].label.Foreground = _c.HoverText;
        }
        UpdateCenter();
    }

    void SwitchMode(WheelMode mode)
    {
        if (mode == _mode && _items.Count > 0) return;
        _mode = mode;
        _hover = -1;
        Build();
        _canvas.Opacity = _revealed ? 1 : 0;
    }

    void Reveal()
    {
        if (_revealed) return;
        _revealed = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _canvas.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)));
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
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
        CloseWheel();
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

    void CloseWheel()
    {
        if (_done) return;
        _done = true;
        if (Current == this) Current = null;
        try { Close(); } catch { /* already closed */ }
    }
}
