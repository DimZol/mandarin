using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Mandarin.UI.Tools;

/// <summary>Crop frame with handles and shading outside. Coordinates are in content pixels.</summary>
public sealed class CropOverlay : Canvas
{
    enum Drag { None, Move, New, N, S, E, W, NE, NW, SE, SW }

    readonly Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), IsHitTestVisible = false };
    readonly Rectangle _frame = new() { Stroke = Brushes.White, IsHitTestVisible = false };
    readonly List<Line> _thirds = new();
    readonly List<Rectangle> _handles = new();
    Rect _rect;
    Drag _drag;
    Point _start;
    Rect _startRect;

    public double ContentWidth { get; private set; }
    public double ContentHeight { get; private set; }
    /// <summary>Aspect ratio (width/height) or null for free.</summary>
    public double? Aspect { get; private set; }
    public Rect Selection => _rect;
    public event Action? Changed;

    public CropOverlay()
    {
        Background = Brushes.Transparent;
        ClipToBounds = true;
        Children.Add(_shade);
        for (var i = 0; i < 4; i++)
        {
            var l = new Line { Stroke = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), IsHitTestVisible = false };
            _thirds.Add(l); Children.Add(l);
        }
        Children.Add(_frame);
        for (var i = 0; i < 8; i++)
        {
            var h = new Rectangle { Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)), IsHitTestVisible = false };
            _handles.Add(h); Children.Add(h);
        }
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += (_, _) => { _drag = Drag.None; ReleaseMouseCapture(); Changed?.Invoke(); };
    }

    public void SetContentSize(double w, double h)
    {
        ContentWidth = w; ContentHeight = h;
        Width = w; Height = h;
        _rect = new Rect(0, 0, w, h);
        if (Aspect is { } a) ApplyAspect(a);
        Redraw();
    }

    public void SetAspect(double? aspect)
    {
        Aspect = aspect;
        if (aspect is { } a) ApplyAspect(a);
        Redraw();
        Changed?.Invoke();
    }

    public void SetSelection(Rect r)
    {
        _rect = Clamp(r);
        Redraw();
        Changed?.Invoke();
    }

    void ApplyAspect(double a)
    {
        // the largest rectangle of the requested aspect, centered on the current one
        var cx = _rect.X + _rect.Width / 2; var cy = _rect.Y + _rect.Height / 2;
        double w = ContentWidth, h = w / a;
        if (h > ContentHeight) { h = ContentHeight; w = h * a; }
        _rect = Clamp(new Rect(cx - w / 2, cy - h / 2, w, h));
    }

    Rect Clamp(Rect r)
    {
        var w = Math.Min(Math.Max(r.Width, 4), ContentWidth);
        var h = Math.Min(Math.Max(r.Height, 4), ContentHeight);
        var x = Math.Clamp(r.X, 0, ContentWidth - w);
        var y = Math.Clamp(r.Y, 0, ContentHeight - h);
        return new Rect(x, y, w, h);
    }

    double HandleSize => Math.Max(8, Math.Max(ContentWidth, ContentHeight) / 70);

    void Redraw()
    {
        var full = new RectangleGeometry(new Rect(0, 0, ContentWidth, ContentHeight));
        var inner = new RectangleGeometry(_rect);
        _shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, inner);
        var t = Math.Max(1, Math.Max(ContentWidth, ContentHeight) / 600);
        _frame.StrokeThickness = t * 1.5;
        SetLeft(_frame, _rect.X); SetTop(_frame, _rect.Y);
        _frame.Width = _rect.Width; _frame.Height = _rect.Height;
        for (var i = 0; i < 2; i++)
        {
            var fx = _rect.X + _rect.Width * (i + 1) / 3; var fy = _rect.Y + _rect.Height * (i + 1) / 3;
            _thirds[i].X1 = _thirds[i].X2 = fx; _thirds[i].Y1 = _rect.Top; _thirds[i].Y2 = _rect.Bottom;
            _thirds[i + 2].Y1 = _thirds[i + 2].Y2 = fy; _thirds[i + 2].X1 = _rect.Left; _thirds[i + 2].X2 = _rect.Right;
            _thirds[i].StrokeThickness = _thirds[i + 2].StrokeThickness = t;
        }
        var s = HandleSize;
        var pts = HandlePoints();
        for (var i = 0; i < 8; i++)
        {
            _handles[i].Width = _handles[i].Height = s;
            _handles[i].StrokeThickness = t;
            SetLeft(_handles[i], pts[i].X - s / 2); SetTop(_handles[i], pts[i].Y - s / 2);
        }
    }

    Point[] HandlePoints()
    {
        var r = _rect;
        return new[]
        {
            r.TopLeft, new Point(r.X + r.Width / 2, r.Top), r.TopRight, new Point(r.Right, r.Y + r.Height / 2),
            r.BottomRight, new Point(r.X + r.Width / 2, r.Bottom), r.BottomLeft, new Point(r.Left, r.Y + r.Height / 2)
        };
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        var pts = HandlePoints();
        var tol = HandleSize * 1.4;
        var kinds = new[] { Drag.NW, Drag.N, Drag.NE, Drag.E, Drag.SE, Drag.S, Drag.SW, Drag.W };
        _drag = Drag.None;
        for (var i = 0; i < 8; i++)
            if ((pts[i] - p).Length < tol) { _drag = kinds[i]; break; }
        if (_drag == Drag.None) _drag = _rect.Contains(p) ? Drag.Move : Drag.New;
        _start = p;
        _startRect = _rect;
        CaptureMouse();
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_drag == Drag.None || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        var d = p - _start;
        var r = _startRect;
        double l = r.Left, t = r.Top, rr = r.Right, b = r.Bottom;
        switch (_drag)
        {
            case Drag.Move:
                _rect = Clamp(new Rect(r.X + d.X, r.Y + d.Y, r.Width, r.Height));
                Redraw();
                return;
            case Drag.New:
                l = Math.Min(_start.X, p.X); rr = Math.Max(_start.X, p.X);
                t = Math.Min(_start.Y, p.Y); b = Math.Max(_start.Y, p.Y);
                break;
            default:
                var name = _drag.ToString();
                if (name.Contains('N')) t = r.Top + d.Y;
                if (name.Contains('S')) b = r.Bottom + d.Y;
                if (name.Contains('W')) l = r.Left + d.X;
                if (name.Contains('E')) rr = r.Right + d.X;
                break;
        }
        l = Math.Clamp(l, 0, ContentWidth); rr = Math.Clamp(rr, 0, ContentWidth);
        t = Math.Clamp(t, 0, ContentHeight); b = Math.Clamp(b, 0, ContentHeight);
        var nr = new Rect(new Point(Math.Min(l, rr), Math.Min(t, b)), new Point(Math.Max(l, rr), Math.Max(t, b)));
        if (Aspect is { } a && nr.Width > 1 && nr.Height > 1)
        {
            // fit the aspect ratio while keeping the opposite corner in place
            var horizontalDrag = _drag is Drag.E or Drag.W;
            double w = nr.Width, h = nr.Height;
            if (horizontalDrag || (_drag is not (Drag.N or Drag.S) && w / h > a)) h = w / a; else w = h * a;
            var name = _drag.ToString();
            var x = name.Contains('W') ? nr.Right - w : nr.Left;
            var y = name.Contains('N') ? nr.Bottom - h : nr.Top;
            if (_drag == Drag.New) { x = p.X < _start.X ? _start.X - w : _start.X; y = p.Y < _start.Y ? _start.Y - h : _start.Y; }
            nr = new Rect(x, y, w, h);
            if (nr.Right > ContentWidth || nr.Bottom > ContentHeight || nr.X < 0 || nr.Y < 0) return;
        }
        if (nr.Width < 4 || nr.Height < 4) return;
        _rect = nr;
        Redraw();
    }
}

/// <summary>Several rectangular areas (for redaction): drawing, selecting, deleting.</summary>
public sealed class RegionsOverlay : Canvas
{
    public sealed class Region
    {
        public Rect Rect;
        public double Start, End = double.MaxValue;
        public Rectangle Shape = null!;
    }

    public readonly List<Region> Regions = new();
    Region? _selected;
    Region? _drawing;
    Point _start;
    bool _moving;
    Point _moveStart;
    public double ContentWidth { get; private set; }
    public double ContentHeight { get; private set; }
    public event Action? SelectionChanged;
    public event Action? RegionsChanged;
    public Region? Selected => _selected;
    /// <summary>Default time for new areas (video).</summary>
    public Func<(double start, double end)>? DefaultTime;

    public RegionsOverlay()
    {
        Background = Brushes.Transparent;
        ClipToBounds = true;
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        Focusable = true;
        KeyDown += (_, e) => { if (e.Key == Key.Delete) RemoveSelected(); };
    }

    public void SetContentSize(double w, double h)
    {
        ContentWidth = w; ContentHeight = h; Width = w; Height = h;
    }

    double Stroke => Math.Max(1.5, Math.Max(ContentWidth, ContentHeight) / 500);

    public Region? Add(Rect r, double start = 0, double end = double.MaxValue)
    {
        // empty/degenerate areas (found outside the frame) are skipped - they break the layout
        if (r.IsEmpty || double.IsNaN(r.Width) || double.IsInfinity(r.X) || double.IsInfinity(r.Y)) return null;
        var reg = new Region { Rect = r, Start = start, End = end };
        reg.Shape = new Rectangle
        {
            Stroke = new SolidColorBrush(Theme.Accent), StrokeThickness = Stroke,
            Fill = new SolidColorBrush(Color.FromArgb(70, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B)),
            StrokeDashArray = new DoubleCollection { 4, 2 }
        };
        Place(reg);
        Children.Add(reg.Shape);
        Regions.Add(reg);
        RegionsChanged?.Invoke();
        return reg;
    }

    void Place(Region r)
    {
        SetLeft(r.Shape, r.Rect.X); SetTop(r.Shape, r.Rect.Y);
        r.Shape.Width = Math.Max(1, r.Rect.Width); r.Shape.Height = Math.Max(1, r.Rect.Height);
    }

    public void RemoveSelected()
    {
        if (_selected == null) return;
        Children.Remove(_selected.Shape);
        Regions.Remove(_selected);
        Select(null);
        RegionsChanged?.Invoke();
    }

    public void Clear()
    {
        foreach (var r in Regions) Children.Remove(r.Shape);
        Regions.Clear();
        Select(null);
        RegionsChanged?.Invoke();
    }

    public void Select(Region? r)
    {
        if (_selected != null) _selected.Shape.StrokeThickness = Stroke;
        _selected = r;
        if (_selected != null) _selected.Shape.StrokeThickness = Stroke * 2.5;
        SelectionChanged?.Invoke();
    }

    /// <summary>Area visibility at time t (for video).</summary>
    public void ShowAt(double t)
    {
        foreach (var r in Regions) r.Shape.Opacity = t >= r.Start && t <= r.End ? 1 : 0.25;
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        var hit = Regions.LastOrDefault(r => r.Rect.Contains(p));
        if (hit != null)
        {
            Select(hit);
            _moving = true; _moveStart = p;
            CaptureMouse();
            return;
        }
        Select(null);
        _start = p;
        var (s, en) = DefaultTime?.Invoke() ?? (0, double.MaxValue);
        _drawing = Add(new Rect(p, new Size(0, 0)), s, en);
        CaptureMouse();
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        p = new Point(Math.Clamp(p.X, 0, ContentWidth), Math.Clamp(p.Y, 0, ContentHeight));
        if (_moving && _selected != null)
        {
            var d = p - _moveStart;
            var r = _selected.Rect;
            r.X = Math.Clamp(r.X + d.X, 0, ContentWidth - r.Width);
            r.Y = Math.Clamp(r.Y + d.Y, 0, ContentHeight - r.Height);
            _selected.Rect = r;
            Place(_selected);
            _moveStart = p;
        }
        else if (_drawing != null)
        {
            _drawing.Rect = new Rect(_start, p);
            Place(_drawing);
        }
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        _moving = false;
        if (_drawing != null)
        {
            if (_drawing.Rect.Width < 4 || _drawing.Rect.Height < 4)
            {
                Children.Remove(_drawing.Shape);
                Regions.Remove(_drawing);
            }
            else Select(_drawing);
            _drawing = null;
            RegionsChanged?.Invoke();
        }
    }
}
