using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Path = Avalonia.Controls.Shapes.Path;

namespace Mandarin.UI.Tools;

/// <summary>
/// Screen scale of content shown inside a Viewbox: handles, frames and lines of the overlays are sized in screen units,
/// so they look the same on a 16×16 icon and on a 6000-pixel photo.
/// </summary>
public static class ScreenUnits
{
    /// <summary>How many content units one screen point covers (1 when the element is not on screen).</summary>
    public static double Of(Visual v)
    {
        if (TopLevel.GetTopLevel(v) is not { } top) return 1;
        var m = v.TransformToVisual(top);
        if (m is not { } matrix) return 1;
        var w = new Point(100, 0).Transform(matrix) - new Point(0, 0).Transform(matrix);
        var len = Math.Sqrt(w.X * w.X + w.Y * w.Y);
        return len > 0.001 ? 100 / len : 1;
    }

    public static void Track(Control c, Action<double> changed)
    {
        double last = 0;
        c.LayoutUpdated += (_, _) =>
        {
            var u = Of(c);
            if (Math.Abs(u - last) <= last * 0.002) return;
            last = u;
            changed(u);
        };
    }

    /// <summary>Crisp pixels for a picture shown at least twice its size (pixel art, icons), smooth scaling otherwise.</summary>
    public static void SharpWhenEnlarged(Image image)
    {
        void Update()
        {
            if (image.Source is not Bitmap bmp || bmp.PixelSize.Width == 0 || image.Bounds.Width == 0) return;
            var zoom = image.Bounds.Width / Of(image) / bmp.PixelSize.Width;
            RenderOptions.SetBitmapInterpolationMode(image, zoom >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        }
        Track(image, _ => Update());
        image.PropertyChanged += (_, e) => { if (e.Property == Image.SourceProperty) Update(); };
    }
}

/// <summary>Crop frame with handles and shading outside. Coordinates are in content pixels.</summary>
public sealed class CropOverlay : Canvas
{
    enum Drag { None, Move, New, N, S, E, W, NE, NW, SE, SW }

    readonly Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), IsHitTestVisible = false, UseLayoutRounding = false };
    readonly Rectangle _frame = new() { Stroke = Brushes.White, IsHitTestVisible = false, UseLayoutRounding = false };
    readonly List<Line> _thirds = new();
    readonly List<Rectangle> _handles = new();
    Rect _rect;
    Drag _drag;
    Point _start;
    Rect _startRect;
    double _u = 1;

    public double ContentWidth { get; private set; }
    public double ContentHeight { get; private set; }
    public double? Aspect { get; private set; }
    public Rect Selection => _rect;
    public event Action? Changed;

    public CropOverlay()
    {
        Background = Brushes.Transparent;
        // inside a Viewbox layout rounding works in content pixels: a 16×16 icon would get handles a whole pixel wide
        UseLayoutRounding = false;
        ClipToBounds = true;
        Children.Add(_shade);
        for (var i = 0; i < 4; i++)
        {
            var l = new Line { Stroke = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), IsHitTestVisible = false, UseLayoutRounding = false };
            _thirds.Add(l); Children.Add(l);
        }
        Children.Add(_frame);
        for (var i = 0; i < 8; i++)
        {
            var h = new Rectangle { Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)), IsHitTestVisible = false, UseLayoutRounding = false };
            _handles.Add(h); Children.Add(h);
        }
        PointerPressed += OnDown;
        PointerMoved += OnMove;
        PointerReleased += (_, e) => { _drag = Drag.None; e.Pointer.Capture(null); Changed?.Invoke(); };
        ScreenUnits.Track(this, u => { _u = u; Redraw(); });
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

    void ApplyAspect(double a)
    {
        var cx = _rect.X + _rect.Width / 2; var cy = _rect.Y + _rect.Height / 2;
        double w = ContentWidth, h = w / a;
        if (h > ContentHeight) { h = ContentHeight; w = h * a; }
        _rect = Clamp(new Rect(cx - w / 2, cy - h / 2, w, h));
        if (Zoomed) _rect = Clamp(new Rect(Math.Round(_rect.X), Math.Round(_rect.Y), _rect.Width, _rect.Height));
    }

    Rect Clamp(Rect r)
    {
        var w = Math.Min(Math.Max(r.Width, MinSide), ContentWidth);
        var h = Math.Min(Math.Max(r.Height, MinSide), ContentHeight);
        var x = Math.Clamp(r.X, 0, ContentWidth - w);
        var y = Math.Clamp(r.Y, 0, ContentHeight - h);
        return new Rect(x, y, w, h);
    }

    double HandleSize => 12 * _u;
    double MinSide => Math.Max(1, 6 * _u);
    bool Zoomed => _u < 1;

    void Redraw()
    {
        if (ContentWidth <= 0) return;
        _shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, ContentWidth, ContentHeight)), new RectangleGeometry(_rect));
        var t = _u;
        _frame.StrokeThickness = t * 1.5;
        SetLeft(_frame, _rect.X); SetTop(_frame, _rect.Y);
        _frame.Width = _rect.Width; _frame.Height = _rect.Height;
        for (var i = 0; i < 2; i++)
        {
            var fx = _rect.X + _rect.Width * (i + 1) / 3; var fy = _rect.Y + _rect.Height * (i + 1) / 3;
            _thirds[i].StartPoint = new Point(fx, _rect.Top); _thirds[i].EndPoint = new Point(fx, _rect.Bottom);
            _thirds[i + 2].StartPoint = new Point(_rect.Left, fy); _thirds[i + 2].EndPoint = new Point(_rect.Right, fy);
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

    static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    void OnDown(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        var pts = HandlePoints();
        var tol = HandleSize * 1.2;
        var kinds = new[] { Drag.NW, Drag.N, Drag.NE, Drag.E, Drag.SE, Drag.S, Drag.SW, Drag.W };
        _drag = Drag.None;
        for (var i = 0; i < 8; i++)
            if (Dist(pts[i], p) < tol) { _drag = kinds[i]; break; }
        if (_drag == Drag.None) _drag = _rect.Contains(p) ? Drag.Move : Drag.New;
        _start = p;
        _startRect = _rect;
        e.Pointer.Capture(this);
    }

    void OnMove(object? sender, PointerEventArgs e)
    {
        if (_drag == Drag.None) return;
        var p = e.GetPosition(this);
        var d = p - _start;
        var r = _startRect;
        double l = r.Left, t = r.Top, rr = r.Right, b = r.Bottom;
        switch (_drag)
        {
            case Drag.Move:
                if (Zoomed) d = new Point(Math.Round(d.X), Math.Round(d.Y));
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
        if (Zoomed) { l = Math.Round(l); rr = Math.Round(rr); t = Math.Round(t); b = Math.Round(b); }
        l = Math.Clamp(l, 0, ContentWidth); rr = Math.Clamp(rr, 0, ContentWidth);
        t = Math.Clamp(t, 0, ContentHeight); b = Math.Clamp(b, 0, ContentHeight);
        var nr = new Rect(new Point(Math.Min(l, rr), Math.Min(t, b)), new Point(Math.Max(l, rr), Math.Max(t, b)));
        if (Aspect is { } a && nr.Width > 0 && nr.Height > 0)
        {
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
        if (nr.Width < MinSide || nr.Height < MinSide) return;
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
    Rect _moveRect;
    double _u = 1;
    public double ContentWidth { get; private set; }
    public double ContentHeight { get; private set; }
    public event Action? SelectionChanged;
    public event Action? RegionsChanged;
    public Region? Selected => _selected;
    public Func<(double start, double end)>? DefaultTime;

    public RegionsOverlay()
    {
        Background = Brushes.Transparent;
        UseLayoutRounding = false;
        ClipToBounds = true;
        Focusable = true;
        PointerPressed += OnDown;
        PointerMoved += OnMove;
        PointerReleased += OnUp;
        KeyDown += (_, e) => { if (e.Key == Key.Delete) RemoveSelected(); };
        ScreenUnits.Track(this, u =>
        {
            _u = u;
            foreach (var r in Regions) r.Shape.StrokeThickness = r == _selected ? Stroke * 2.5 : Stroke;
        });
    }

    public void SetContentSize(double w, double h)
    {
        ContentWidth = w; ContentHeight = h; Width = w; Height = h;
    }

    double Stroke => 1.5 * _u;
    double MinSide => Math.Max(1, 4 * _u);
    bool Zoomed => _u < 1;
    Point Snap(Point p) => Zoomed ? new Point(Math.Round(p.X), Math.Round(p.Y)) : p;

    public Region? Add(Rect r, double start = 0, double end = double.MaxValue)
    {
        if (double.IsNaN(r.Width) || double.IsInfinity(r.X) || double.IsInfinity(r.Y)) return null;
        var accent = Look.Accent;
        var reg = new Region { Rect = r, Start = start, End = end };
        reg.Shape = new Rectangle
        {
            Stroke = new SolidColorBrush(accent), StrokeThickness = Stroke,
            Fill = new SolidColorBrush(Color.FromArgb(70, accent.R, accent.G, accent.B)),
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 2 }, UseLayoutRounding = false
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

    public void ShowAt(double t)
    {
        foreach (var r in Regions) r.Shape.Opacity = t >= r.Start && t <= r.End ? 1 : 0.25;
    }

    void OnDown(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        var p = e.GetPosition(this);
        var hit = Regions.LastOrDefault(r => r.Rect.Contains(p));
        if (hit != null)
        {
            Select(hit);
            _moving = true; _moveStart = p; _moveRect = hit.Rect;
            e.Pointer.Capture(this);
            return;
        }
        Select(null);
        _start = Snap(p);
        var (s, en) = DefaultTime?.Invoke() ?? (0, double.MaxValue);
        _drawing = Add(new Rect(_start, new Size(0, 0)), s, en);
        e.Pointer.Capture(this);
    }

    void OnMove(object? sender, PointerEventArgs e)
    {
        if (!_moving && _drawing == null) return;
        var p = e.GetPosition(this);
        p = new Point(Math.Clamp(p.X, 0, ContentWidth), Math.Clamp(p.Y, 0, ContentHeight));
        if (_moving && _selected != null)
        {
            var d = p - _moveStart;
            if (Zoomed) d = new Point(Math.Round(d.X), Math.Round(d.Y));
            var r = _moveRect;
            var x = Math.Clamp(r.X + d.X, 0, ContentWidth - r.Width);
            var y = Math.Clamp(r.Y + d.Y, 0, ContentHeight - r.Height);
            _selected.Rect = new Rect(x, y, r.Width, r.Height);
            Place(_selected);
        }
        else if (_drawing != null)
        {
            var q = Snap(p);
            _drawing.Rect = new Rect(new Point(Math.Min(_start.X, q.X), Math.Min(_start.Y, q.Y)), new Point(Math.Max(_start.X, q.X), Math.Max(_start.Y, q.Y)));
            Place(_drawing);
        }
    }

    void OnUp(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        _moving = false;
        if (_drawing != null)
        {
            if (_drawing.Rect.Width < MinSide || _drawing.Rect.Height < MinSide)
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
