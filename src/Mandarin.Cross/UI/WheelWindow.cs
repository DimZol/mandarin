using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mandarin.Core;
using Mandarin.Platform;
using Path = Avalonia.Controls.Shapes.Path;

namespace Mandarin.UI;

/// <summary>
/// Radial menu: appears under the pointer while dragging (files arrive with the drop) or for files known in advance
/// (main window, file manager, command line).
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
    readonly bool _placed;

    readonly double _size;
    readonly Canvas _canvas = new();
    readonly List<SectorVisual> _sectors = new();
    readonly TextBlock _centerTitle = new(), _centerSub = new();
    readonly Image _centerThumb = new();
    readonly StackPanel _pill = new() { Orientation = Orientation.Horizontal };
    Look.WheelColors _c = Look.Wheel;

    const double PillHeight = 44;

    sealed record SectorVisual(Canvas Layer, SolidColorBrush Fill, SolidColorBrush Stroke, SolidColorBrush Text);

    public static WheelWindow? Current { get; private set; }

    double R => _size / 2;
    double R1 => R * 0.42;
    double R2 => R * 0.97;
    double RC => R * 0.37;
    Point Center => new(Width / 2, PillHeight + R + 6);

    WheelWindow(WheelMode mode, IReadOnlyList<string> files, bool dragMode, int x, int y, bool placed)
    {
        _mode = mode;
        _files = files;
        _dragMode = dragMode;
        _screenX = x; _screenY = y;
        _placed = placed;
        _size = Math.Clamp(AppSettings.Current.WheelSize, 240, 480);

        Title = "Mandarin";
#if AVALONIA12
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
#else
        SystemDecorations = SystemDecorations.None;
#endif
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        TransparencyBackgroundFallback = Look.WindowBackground;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = !dragMode;
        // almost transparent background: the window must accept the drop over the whole wheel
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Width = Math.Max(_size + 60, 470);
        Height = _size + PillHeight + 24;
        FontFamily = Look.UiFont;
        Look.GrayscaleText(this);
        WindowStartupLocation = placed ? WindowStartupLocation.Manual : WindowStartupLocation.CenterScreen;

        _canvas.Width = Width; _canvas.Height = Height;
        _canvas.Opacity = 0;
        Motion.Xf(_canvas);
        _canvas.RenderTransformOrigin = new RelativePoint(0.5, (PillHeight + R + 6) / Height, RelativeUnit.Relative);
        Content = _canvas;

        Build();

        if (dragMode)
        {
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragEnterEvent, (_, e) => Guard(() => OnDragEnter(e), e));
            AddHandler(DragDrop.DragOverEvent, (_, e) => Guard(() => OnDragOver(e), e));
            AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetHover(-1));
            AddHandler(DragDrop.DropEvent, (_, e) => Guard(() => OnDrop(e), e));
        }
        else
        {
            PointerMoved += (_, e) => SetHover(HitTest(e.GetPosition(this)));
            PointerReleased += (_, e) =>
            {
                var p = e.GetPosition(this);
                var idx = HitTest(p);
                if (idx >= 0) Invoke(idx);
                else if (Distance(p, Center) > R2 + 6 && p.Y > PillHeight) CloseWheel();
            };
            Deactivated += (_, _) => { if (!_done && _placed) CloseWheel(); };
            KeyDown += OnKeyDown;
        }
        Opened += (_, _) =>
        {
            PlaceAtPointer();
            if (_dragMode) StartWatchdog();
        };
    }

    static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ---------- Opening ----------

    public static void ShowForDrag(WheelMode mode, (int x, int y)? pos)
    {
        if (Current is { _dragMode: true, _done: false } existing)
        {
            existing.SwitchMode(mode);
            return;
        }
        Current?.CloseWheel();
        var w = new WheelWindow(mode, Array.Empty<string>(), true, pos?.x ?? 0, pos?.y ?? 0, pos != null);
        Current = w;
        w.Show();
        Log.Info($"Wheel shown for a drag at {(pos is { } p ? $"({p.x},{p.y})" : "the screen center")}");
    }

    public static async void ShowForFiles(IReadOnlyList<string> files, WheelMode mode = WheelMode.Convert)
    {
        Current?.CloseWheel();
        // under KDE Wayland KWin is asked where the pointer is; without a position the wheel opens in the screen center
        var pos = await Desktop.CursorPosAsync();
        Current?.CloseWheel();
        var w = new WheelWindow(mode, files, false, pos?.x ?? 0, pos?.y ?? 0, pos != null);
        Current = w;
        w.Show();
        w.Activate();
        w.Reveal();
        Log.Info($"Wheel shown for {files.Count} files at {(pos is { } p ? $"({p.x},{p.y})" : "the screen center")}");
    }

    /// <summary>Wheel for previews and screenshots: centered and visible right away.</summary>
    public static WheelWindow CreatePreview(WheelMode mode, IReadOnlyList<string> files, int hover = -1)
    {
        var w = new WheelWindow(mode, files, false, 0, 0, false) { _revealed = true };
        w._canvas.Opacity = 1;
        if (hover >= 0) w.SetHover(hover);
        return w;
    }

    public static void OnGlobalButtonReleased()
    {
        var w = Current;
        if (w is not { _dragMode: true }) return;
        if (!w._revealed) { w.CloseWheel(); return; }
        // the drop arrives slightly after the button is released - give it time
        DispatcherTimer.RunOnce(() => { if (!w._done) w.CloseWheel(); }, TimeSpan.FromMilliseconds(350));
    }

    public static void CloseCurrent() => Current?.CloseWheel();

    /// <summary>Guard against a stuck wheel: if the button release was lost, close by the real state of the button.</summary>
    void StartWatchdog()
    {
        var releasedSince = 0L;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            if (_done) { timer.Stop(); return; }
            if (Gesture.LeftButtonDown() is not false) { releasedSince = 0; return; }
            if (releasedSince == 0) releasedSince = Environment.TickCount64;
            else if (Environment.TickCount64 - releasedSince > 600) { timer.Stop(); CloseWheel(); }
        };
        timer.Start();
    }

    void PlaceAtPointer()
    {
        if (!_placed) return;
        var screen = Screens.ScreenFromPoint(new PixelPoint(_screenX, _screenY)) ?? Screens.Primary;
        if (screen == null) return;
        var scale = screen.Scaling;
        var work = screen.WorkingArea;
        var wPx = (int)Math.Round(Width * scale);
        var hPx = (int)Math.Round(Height * scale);
        var left = _screenX - wPx / 2;
        var top = _screenY - (int)Math.Round((PillHeight + R + 6) * scale);
        left = Math.Clamp(left, work.X, Math.Max(work.X, work.Right - wPx));
        top = Math.Clamp(top, work.Y, Math.Max(work.Y, work.Bottom - hPx));
        Position = new PixelPoint(left, top);
    }

    // ---------- Building ----------

    void Build()
    {
        _canvas.Children.Clear();
        _sectors.Clear();
        _c = Look.Wheel;
        _items = _files.Count > 0 ? Catalog.Items(_mode, _files) : new List<WheelItem>();
        var center = Center;

        var ring = new Border
        {
            Width = R * 2 + 8, Height = R * 2 + 8, CornerRadius = new CornerRadius(R + 4), Background = _c.Ring, BorderBrush = _c.RingStroke,
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 28, OffsetY = 6, Color = Color.FromArgb((byte)(Look.IsDark ? 140 : 56), 0, 0, 0) })
        };
        Canvas.SetLeft(ring, center.X - R - 4);
        Canvas.SetTop(ring, center.Y - R - 4);
        _canvas.Children.Add(ring);

        var n = _items.Count;
        if (n > 0)
        {
            var step = 360.0 / n;
            const double gap = 5;
            for (var i = 0; i < n; i++)
            {
                var a0 = -90 - step / 2 + i * step;
                var a1 = a0 + step;
                var fill = new SolidColorBrush(_c.Sector.Color);
                var stroke = new SolidColorBrush(_c.SectorStroke.Color);
                var text = new SolidColorBrush(_c.Text.Color);
                var layer = new Canvas { Width = Width, Height = Height, IsHitTestVisible = false };
                Motion.Xf(layer);
                layer.RenderTransformOrigin = new RelativePoint(center.X / Width, center.Y / Height, RelativeUnit.Relative);
                layer.Children.Add(new Path { Data = n == 1 ? Ring(center, R1, R2) : Sector(center, R1, R2, a0, a1, gap), Fill = fill, Stroke = stroke, StrokeThickness = 1 });
                _canvas.Children.Add(layer);
                _sectors.Add(new SectorVisual(layer, fill, stroke, text));

                var mid = (a0 + a1) / 2 * Math.PI / 180;
                var rm = (R1 + R2) / 2;
                var px = center.X + Math.Cos(mid) * rm;
                var py = center.Y + Math.Sin(mid) * rm;
                var fontSize = n > 8 ? 9.5 : 10.5;
                var labelWidth = Math.Min(2 * Math.PI * rm / n - 6, 92);
                var iconSize = n > 8 ? 16.0 : 19.0;

                var glyph = Icons.MakePath(_items[i].Glyph, iconSize, text);
                glyph.IsHitTestVisible = false;
                var label = new TextBlock
                {
                    Text = _items[i].Label, FontSize = _items[i].Label.Length > 9 ? fontSize - 1.5 : fontSize, FontWeight = FontWeight.SemiBold, Foreground = text,
                    TextAlignment = TextAlignment.Center, Width = labelWidth, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false
                };
                Canvas.SetLeft(glyph, px - iconSize / 2); Canvas.SetTop(glyph, py - iconSize - 2);
                Canvas.SetLeft(label, px - labelWidth / 2); Canvas.SetTop(label, py + 1);
                layer.Children.Add(glyph);
                layer.Children.Add(label);
            }
        }

        var hub = new Border
        {
            Width = RC * 2, Height = RC * 2, CornerRadius = new CornerRadius(RC), Background = _c.Center, BorderBrush = _c.SectorStroke, BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 10, OffsetY = 1, Color = Color.FromArgb(30, 0, 0, 0) })
        };
        Canvas.SetLeft(hub, center.X - RC); Canvas.SetTop(hub, center.Y - RC);
        _canvas.Children.Add(hub);

        _centerThumb.Width = RC * 0.95; _centerThumb.Height = RC * 0.8;
        _centerThumb.Stretch = Stretch.Uniform;
        _centerThumb.IsHitTestVisible = false;
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
        _centerTitle.FontSize = 12.5; _centerTitle.FontWeight = FontWeight.SemiBold; _centerTitle.Foreground = _c.Text;
        _centerSub.FontSize = 10.5; _centerSub.Foreground = _c.SubText;

        BuildPill();
        UpdateCenter();
        if (_files.Count > 0 && Thumbs.Cached(_files[0], 128) == null) _ = LoadThumb();
    }

    async Task LoadThumb()
    {
        await Thumbs.GetAsync(_files[0], 128);
        UpdateCenter();
    }

    void BuildPill()
    {
        _pill.Children.Clear();
        if (_pill.Parent is Border previous) previous.Child = null; // the panel is reused when rebuilding
        var border = new Border
        {
            Background = _c.Pill, CornerRadius = new CornerRadius(15), Padding = new Thickness(5), BorderBrush = _c.RingStroke, BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 14, OffsetY = 2, Color = Color.FromArgb(46, 0, 0, 0) })
        };
        foreach (var m in new[] { WheelMode.Convert, WheelMode.Tools, WheelMode.Actions })
        {
            var key = Catalog.ModeKey(m);
            var active = m == _mode;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var keySymbol = Gesture.KeySymbol(key);
            if (keySymbol.Length > 0)
                content.Children.Add(new Border
                {
                    Background = active ? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) : (Look.IsDark ? Look.Brush(255, 255, 255, 20) : Look.Brush(0, 0, 0, 14)),
                    CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(0, 0, 6, 0),
                    Child = new TextBlock { Text = keySymbol, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = active ? _c.HoverText : _c.SubText }
                });
            content.Children.Add(new TextBlock
            {
                Text = Catalog.ModeTitle(m), FontSize = 12, FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = active ? _c.HoverText : _c.Text, VerticalAlignment = VerticalAlignment.Center
            });
            var idle = new SolidColorBrush(Colors.Transparent);
            var chip = new Border
            {
                Background = active ? _c.Hover : idle, CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 4, 10, 4), Child = content, Tag = m,
                Cursor = _dragMode ? null : new Cursor(StandardCursorType.Hand)
            };
            if (!_dragMode)
            {
                chip.PointerReleased += (s, e) => { e.Handled = true; SwitchMode((WheelMode)((Border)s!).Tag!); };
                if (!active)
                {
                    var tint = Look.IsDark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0);
                    chip.PointerEntered += (_, _) => Motion.ColorTo(idle, tint, 120);
                    chip.PointerExited += (_, _) => Motion.ColorTo(idle, Colors.Transparent, 200);
                }
            }
            _pill.Children.Add(chip);
        }
        border.Child = _pill;
        border.HorizontalAlignment = HorizontalAlignment.Center;
        border.VerticalAlignment = VerticalAlignment.Top;
        border.Margin = new Thickness(0, 2, 0, 0);
        // centered by layout: the text is measured with the real font
        _canvas.Children.Add(new Grid { Width = Width, Height = PillHeight, Children = { border } });
    }

    /// <summary>Ring sector between rays a0 and a1 (degrees); each edge is offset in parallel so the gaps keep one width.</summary>
    static Geometry Sector(Point c, double r1, double r2, double a0, double a1, double gap)
    {
        Point P(double r, double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        double Shift(double r) => Math.Asin(Math.Min(1, gap / 2 / r)) * 180 / Math.PI;
        double o0 = a0 + Shift(r2), o1 = a1 - Shift(r2);
        double i0 = a0 + Shift(r1), i1 = a1 - Shift(r1);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(r2, o0), true);
            ctx.ArcTo(P(r2, o1), new Size(r2, r2), 0, o1 - o0 > 180, SweepDirection.Clockwise);
            ctx.LineTo(P(r1, i1));
            ctx.ArcTo(P(r1, i0), new Size(r1, r1), 0, i1 - i0 > 180, SweepDirection.CounterClockwise);
            ctx.EndFigure(true);
        }
        return g;
    }

    static Geometry Ring(Point c, double r1, double r2) =>
        new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(new Rect(c.X - r2, c.Y - r2, r2 * 2, r2 * 2)),
            new EllipseGeometry(new Rect(c.X - r1, c.Y - r1, r1 * 2, r1 * 2)));

    void UpdateCenter()
    {
        var before = _centerTitle.Text ?? "";
        UpdateCenterContent();
        if (_revealed && before.Length > 0 && before != _centerTitle.Text)
        {
            Motion.Opacity(_centerTitle, 0.25, 1, 160);
            Motion.MoveY(_centerTitle, 4, 0, 200);
        }
    }

    void UpdateCenterContent()
    {
        var center = Center;
        var thumb = _files.Count > 0 ? Thumbs.Cached(_files[0], 128) : null;
        if (_hover >= 0 && _hover < _items.Count)
        {
            _centerThumb.Source = thumb;
            _centerThumb.IsVisible = thumb != null;
            _centerTitle.Text = _items[_hover].Title;
            _centerSub.Text = thumb != null ? "" : Catalog.Describe(_files);
            Canvas.SetTop(_centerTitle, thumb != null ? center.Y + RC * 0.1 : center.Y - 22);
            Canvas.SetTop(_centerSub, center.Y + 12);
            return;
        }
        if (_files.Count == 0)
        {
            _centerThumb.IsVisible = false;
            _centerTitle.Text = Catalog.ModeTitle(_mode);
            _centerSub.Text = L.T("Release the file on an option");
            Canvas.SetTop(_centerTitle, center.Y - 22);
            Canvas.SetTop(_centerSub, center.Y + 2);
            return;
        }
        _centerThumb.Source = thumb;
        _centerThumb.IsVisible = thumb != null;
        _centerTitle.Text = _items.Count == 0 ? L.T("No options") : Catalog.Describe(_files);
        _centerSub.Text = _items.Count == 0 ? L.T("for these files") : "";
        Canvas.SetTop(_centerTitle, thumb != null ? center.Y + RC * 0.12 : center.Y - 12);
        Canvas.SetTop(_centerSub, thumb != null ? center.Y + RC * 0.12 + 18 : center.Y + 8);
    }

    // ---------- Interaction ----------

    int HitTest(Point p)
    {
        if (_items.Count == 0) return -1;
        var dx = p.X - Center.X; var dy = p.Y - Center.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < RC + 4 || dist > R2 + 40) return -1;
        var deg = Math.Atan2(dy, dx) * 180 / Math.PI;
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

    void Highlight(SectorVisual s, bool on)
    {
        var ms = on ? 110 : 220;
        Motion.ColorTo(s.Fill, (on ? _c.Hover : _c.Sector).Color, ms);
        Motion.ColorTo(s.Stroke, (on ? _c.Hover : _c.SectorStroke).Color, ms);
        Motion.ColorTo(s.Text, (on ? _c.HoverText : _c.Text).Color, ms);
        Motion.ScaleTo(s.Layer, Motion.CurrentScale(s.Layer), on ? 1.045 : 1, on ? 260 : 220, on ? Motion.Spring : Motion.EaseOut);
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
            _canvas.Opacity = 1;
            return;
        }
        Motion.Opacity(_canvas, 0, 1, 140);
        Motion.ScaleTo(_canvas, 0.8, 1, 320, Motion.Spring);
        AnimateSectorsIn(0.55);
    }

    void AnimateSectorsIn(double fromScale)
    {
        if (!Motion.Enabled) return;
        var step = Math.Min(22, 180 / Math.Max(1, _sectors.Count));
        for (var i = 0; i < _sectors.Count; i++)
        {
            var s = _sectors[i];
            s.Layer.Opacity = 0;
            Motion.Opacity(s.Layer, 0, 1, 170, Motion.EaseOut, i * step);
            Motion.ScaleTo(s.Layer, fromScale, 1, 340, Motion.Spring, i * step);
        }
    }

    static void Guard(Action a, DragEventArgs e)
    {
        try { a(); }
        catch (Exception ex)
        {
            Log.Error("Wheel: drag handling error", ex);
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    static List<string> ReadFiles(DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles();
        if (items == null) return new List<string>();
        return items.Select(i => i.TryGetLocalPath()).Where(p => p != null && (File.Exists(p) || Directory.Exists(p))).Select(p => p!).ToList();
    }

    void OnDragEnter(DragEventArgs e)
    {
        if (_files.Count == 0)
        {
            var files = ReadFiles(e);
            if (files.Count == 0)
            {
                e.DragEffects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
            _files = files;
            Build();
            Reveal();
        }
        OnDragOver(e);
    }

    void OnDragOver(DragEventArgs e)
    {
        if (_files.Count == 0) { e.DragEffects = DragDropEffects.None; e.Handled = true; return; }
        var idx = HitTest(e.GetPosition(this));
        SetHover(idx);
        // never Move: the file manager must not delete the originals
        e.DragEffects = idx < 0 ? DragDropEffects.None
            : e.DragEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
            : e.DragEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
            : DragDropEffects.Copy;
        e.Handled = true;
    }

    void OnDrop(DragEventArgs e)
    {
        var idx = HitTest(e.GetPosition(this));
        Log.Info($"Wheel: drop on sector {idx}");
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        if (_files.Count == 0) _files = ReadFiles(e);
        if (idx >= 0) Invoke(idx);
        else CloseWheel();
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: CloseWheel(); return;
            case Key.Enter when _hover >= 0: Invoke(_hover); return;
            case Key.Right or Key.Down when _items.Count > 0: SetHover((_hover + 1) % _items.Count); e.Handled = true; return;
            case Key.Left or Key.Up when _items.Count > 0: SetHover((_hover - 1 + _items.Count) % _items.Count); e.Handled = true; return;
        }
        uint vk = e.Key switch
        {
            Key.LeftShift or Key.RightShift => 0x10, Key.LeftCtrl or Key.RightCtrl => 0x11, Key.LeftAlt or Key.RightAlt => 0x12,
            Key.Tab => 0x09, Key.Space => 0x20, Key.OemTilde => 0xC0, _ => 0
        };
        foreach (var m in new[] { WheelMode.Convert, WheelMode.Tools, WheelMode.Actions })
            if (vk != 0 && Gesture.Matches(Catalog.ModeKey(m), vk)) { SwitchMode(m); e.Handled = true; return; }
    }

    void Invoke(int idx)
    {
        if (_done || idx < 0 || idx >= _items.Count) return;
        var item = _items[idx];
        var files = _files.ToList();
        CloseWheel(idx);
        Dispatcher.UIThread.Post(() =>
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
        DragDrop.SetAllowDrop(this, false);
        Background = Brushes.Transparent;
        if (chosen >= 0 && chosen < _sectors.Count) Motion.ScaleTo(_sectors[chosen].Layer, Motion.CurrentScale(_sectors[chosen].Layer), 1.1, 120);
        Motion.ScaleTo(_canvas, Motion.CurrentScale(_canvas), chosen >= 0 ? 1.05 : 0.88, 170, Motion.EaseInOut);
        Motion.Opacity(_canvas, _canvas.Opacity, 0, 170, Motion.EaseInOut, chosen >= 0 ? 60 : 0, () =>
        {
            try { Close(); } catch { /* already closed */ }
        });
    }
}
