using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Mandarin.Core;
using Mandarin.Engines;
using Path = Avalonia.Controls.Shapes.Path;

namespace Mandarin.UI.Tools;

/// <summary>Photo annotations: arrows, shapes, lines, pen, marker, text and numbered callouts.</summary>
public sealed class AnnotateWindow : ToolWindow
{
    enum Tool { Select, Arrow, Rect, Ellipse, Line, Pen, Marker, Text, Number }

    readonly string _file;
    readonly Grid _surface = new() { UseLayoutRounding = false };
    readonly Canvas _canvas = new() { Background = Brushes.Transparent, ClipToBounds = true, UseLayoutRounding = false };
    readonly Image _image = new() { Stretch = Stretch.Fill };
    readonly Viewbox _viewbox = new() { Stretch = Stretch.Uniform };
    int _w, _h;
    Tool _tool = Tool.Arrow;
    Color _color = Ui.Palette[1];
    double _thickness = 6;
    int _nextNumber = 1;
    readonly Stack<Control> _undo = new();

    Point _start;
    Control? _drawing;
    Polyline? _poly;
    Control? _selected;
    Point _moveStart;
    bool _moving;

    public AnnotateWindow(string file) : base(L.T("Annotate the photo"), System.IO.Path.GetFileName(file), 1180, 780)
    {
        _file = file;

        var toggles = new List<ToggleButton>();
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        void T(Tool t, string glyph, string tip)
        {
            var b = Ui.ToolToggle(glyph, tip, toggles, () => _tool = t);
            if (t == _tool) b.IsChecked = true;
            bar.Children.Add(b);
        }
        T(Tool.Select, "", L.T("Select and move (Delete removes)"));
        T(Tool.Arrow, "", L.T("Arrow"));
        T(Tool.Rect, "", L.T("Rectangle"));
        T(Tool.Ellipse, "", L.T("Ellipse"));
        T(Tool.Line, "", L.T("Line"));
        T(Tool.Pen, "", L.T("Pen"));
        T(Tool.Marker, "", L.T("Marker"));
        T(Tool.Text, "", L.T("Text"));
        T(Tool.Number, "", L.T("Numbered callout"));
        bar.Children.Add(new Border { Width = 16 });
        bar.Children.Add(Ui.ColorPicker(_color, c => { _color = c; Recolor(); }));
        bar.Children.Add(new Border { Width = 16 });
        var (thick, thickSlider) = Ui.LabeledSlider(L.T("Thickness"), 1, 20, 4, "0", v => _thickness = StrokeFor(v));
        thick.Width = 300;
        bar.Children.Add(thick);
        bar.Children.Add(Ui.IconButton("", L.T("Undo (Ctrl+Z)"), Undo));

        _surface.Children.Add(_image);
        _surface.Children.Add(_canvas);
        ScreenUnits.SharpWhenEnlarged(_image);
        _viewbox.Child = _surface;
        var host = new Border
        {
            Child = _viewbox, CornerRadius = new CornerRadius(8), ClipToBounds = true,
            Background = Look.IsDark ? Look.Brush(0, 0, 0, 60) : Look.Brush(0, 0, 0, 12)
        };
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(host);
        Body.Content = dock;

        _canvas.PointerPressed += OnDown;
        _canvas.PointerMoved += OnMove;
        _canvas.PointerReleased += OnUp;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control) { Undo(); e.Handled = true; }
            if (e.Key == Key.Delete && _selected != null && FocusManager?.GetFocusedElement() is not TextBox)
            {
                _canvas.Children.Remove(_selected); _selected = null; e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        AddCancel();
        AddButton(L.T("Save a copy"), Save, true);
        Opened += async (_, _) =>
        {
            try
            {
                var (px, w, h) = await Task.Run(() => ImageEngine.LoadBgra(_file));
                _w = w; _h = h;
                _image.Source = Thumbs.FromBgra(px, w, h);
                _surface.Width = w; _surface.Height = h;
                _thickness = StrokeFor(thickSlider.Value);
            }
            catch (Exception ex)
            {
                await ShowMessage(L.T("Could not open the image: ") + ex.Message);
                Close();
            }
        };
    }

    /// <summary>Size of the drawing relative to the image: 1 from 500 to 1000 pixels, more on large photos, down to 1/4 on icons.</summary>
    double Scale
    {
        get
        {
            var n = Math.Max(_w, _h);
            return n >= 500 ? Math.Max(1, n / 1000.0) : Math.Max(0.25, n / 500.0);
        }
    }

    double StrokeFor(double v) => Math.Max(1, v * Scale);

    IBrush Stroke => new SolidColorBrush(_color);

    void OnDown(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(_canvas);
        _start = p;
        e.Pointer.Capture(_canvas);
        switch (_tool)
        {
            case Tool.Select:
                Select(FindTopElement(e.Source as Visual));
                if (_selected != null) { _moving = true; _moveStart = p; }
                return;
            case Tool.Arrow or Tool.Line:
                _drawing = new Path { Stroke = Stroke, StrokeThickness = _thickness, StrokeLineCap = PenLineCap.Round, Fill = Stroke, StrokeJoin = PenLineJoin.Round };
                break;
            case Tool.Rect:
                _drawing = new Rectangle { Stroke = Stroke, StrokeThickness = _thickness, RadiusX = _thickness, RadiusY = _thickness };
                break;
            case Tool.Ellipse:
                _drawing = new Ellipse { Stroke = Stroke, StrokeThickness = _thickness };
                break;
            case Tool.Pen or Tool.Marker:
                var marker = _tool == Tool.Marker;
                _poly = new Polyline
                {
                    Stroke = marker ? new SolidColorBrush(Color.FromArgb(110, _color.R, _color.G, _color.B)) : Stroke,
                    StrokeThickness = marker ? _thickness * 4 : _thickness, StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round,
                    Points = new Avalonia.Collections.AvaloniaList<Point> { p }
                };
                _drawing = _poly;
                break;
            case Tool.Text:
                e.Pointer.Capture(null);
                AddText(p);
                return;
            case Tool.Number:
                e.Pointer.Capture(null);
                AddNumber(p);
                return;
        }
        if (_drawing != null)
        {
            _drawing.RenderTransform = new TranslateTransform();
            _canvas.Children.Add(_drawing);
            Update(p, e.KeyModifiers);
        }
    }

    void OnMove(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        if (_moving && _selected?.RenderTransform is TranslateTransform tt)
        {
            tt.X += p.X - _moveStart.X; tt.Y += p.Y - _moveStart.Y;
            _moveStart = p;
            return;
        }
        if (_drawing != null && e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) Update(p, e.KeyModifiers);
    }

    void OnUp(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        _moving = false;
        if (_drawing == null) return;
        var p = e.GetPosition(_canvas);
        var dist = Math.Sqrt((p.X - _start.X) * (p.X - _start.X) + (p.Y - _start.Y) * (p.Y - _start.Y));
        if (dist < 3 && _tool is not (Tool.Pen or Tool.Marker)) _canvas.Children.Remove(_drawing);
        else _undo.Push(_drawing);
        _drawing = null; _poly = null;
    }

    void Update(Point p, KeyModifiers mods)
    {
        switch (_drawing)
        {
            case Path path:
                path.Data = _tool == Tool.Arrow ? ArrowGeometry(_start, p, _thickness, Scale) : new LineGeometry(_start, p);
                break;
            case Rectangle or Ellipse:
                var x = Math.Min(_start.X, p.X); var y = Math.Min(_start.Y, p.Y);
                var w = Math.Abs(p.X - _start.X); var h = Math.Abs(p.Y - _start.Y);
                if (mods.HasFlag(KeyModifiers.Shift)) { w = h = Math.Max(w, h); }
                Canvas.SetLeft(_drawing, x); Canvas.SetTop(_drawing, y);
                _drawing.Width = w; _drawing.Height = h;
                break;
            case Polyline poly:
                poly.Points.Add(p);
                break;
        }
    }

    static Geometry ArrowGeometry(Point a, Point b, double t, double scale)
    {
        var vx = b.X - a.X; var vy = b.Y - a.Y;
        var len = Math.Sqrt(vx * vx + vy * vy);
        if (len < 1) return new LineGeometry(a, b);
        vx /= len; vy /= len;
        double nx = -vy, ny = vx;
        var head = Math.Min(len * 0.45, t * 4.5 + 10 * scale);
        var baseP = new Point(b.X - vx * head, b.Y - vy * head);
        var g = new GeometryGroup();
        g.Children.Add(new LineGeometry(a, new Point(baseP.X + vx * t, baseP.Y + vy * t)));
        var tri = new StreamGeometry();
        using (var c = tri.Open())
        {
            c.BeginFigure(b, true);
            c.LineTo(new Point(baseP.X + nx * head * 0.55, baseP.Y + ny * head * 0.55));
            c.LineTo(new Point(baseP.X - nx * head * 0.55, baseP.Y - ny * head * 0.55));
            c.EndFigure(true);
        }
        g.Children.Add(tri);
        return g;
    }

    void AddText(Point p)
    {
        var box = new TextBox
        {
            FontSize = Math.Max(14 * Scale, _thickness * 5), Foreground = Stroke, Background = Brushes.Transparent, AcceptsReturn = true,
            FontWeight = FontWeight.SemiBold, Padding = new Thickness(0), MinHeight = 0
        };
        // the editing box is an on-screen control: its frame and width do not depend on the image size
        var u = ScreenUnits.Of(_canvas);
        box.BorderThickness = new Thickness(u);
        box.MinWidth = 80 * u;
        Canvas.SetLeft(box, p.X); Canvas.SetTop(box, p.Y);
        _canvas.Children.Add(box);
        box.AttachedToVisualTree += (_, _) => box.Focus();
        box.LostFocus += (_, _) =>
        {
            _canvas.Children.Remove(box);
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            var tb = new TextBlock
            {
                Text = box.Text, FontSize = box.FontSize, Foreground = box.Foreground, FontWeight = FontWeight.SemiBold,
                RenderTransform = new TranslateTransform(),
                Effect = new DropShadowEffect { BlurRadius = 4 * Scale, OffsetX = 0, OffsetY = Scale, Opacity = 0.45, Color = Colors.Black }
            };
            Canvas.SetLeft(tb, p.X + 3 * Scale); Canvas.SetTop(tb, p.Y + 2 * Scale);
            _canvas.Children.Add(tb);
            _undo.Push(tb);
        };
    }

    void AddNumber(Point p)
    {
        var d = Math.Max(26 * Scale, _thickness * 7);
        var grid = new Grid { Width = d, Height = d, RenderTransform = new TranslateTransform() };
        grid.Children.Add(new Ellipse { Fill = Stroke, Stroke = Brushes.White, StrokeThickness = Math.Max(Math.Min(2, d / 10), d / 14) });
        grid.Children.Add(new TextBlock
        {
            Text = _nextNumber++.ToString(), Foreground = IsLight(_color) ? Brushes.Black : Brushes.White, FontWeight = FontWeight.Bold,
            FontSize = d * 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        Canvas.SetLeft(grid, p.X - d / 2); Canvas.SetTop(grid, p.Y - d / 2);
        _canvas.Children.Add(grid);
        _undo.Push(grid);
    }

    static bool IsLight(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 170;

    Control? FindTopElement(Visual? v)
    {
        while (v != null && v != _canvas)
        {
            if (v is Control c && c.GetVisualParent() == _canvas) return c;
            v = v.GetVisualParent();
        }
        return null;
    }

    void Select(Control? el)
    {
        if (_selected != null) _selected.Opacity = 1;
        _selected = el;
        if (_selected != null) _selected.Opacity = 0.75;
    }

    void Recolor()
    {
        if (_selected is Shape s)
        {
            s.Stroke = Stroke;
            if (s is Path) s.Fill = Stroke;
        }
        else if (_selected is TextBlock tb) tb.Foreground = Stroke;
    }

    void Undo()
    {
        while (_undo.Count > 0)
        {
            var el = _undo.Pop();
            if (_canvas.Children.Contains(el))
            {
                _canvas.Children.Remove(el);
                if (el is Grid) _nextNumber = Math.Max(1, _nextNumber - 1);
                return;
            }
        }
    }

    void Save()
    {
        if (_w == 0) return;
        Select(null);
        // render at the full image resolution
        using var rtb = new RenderTargetBitmap(new PixelSize(_w, _h), new Vector(96, 96));
        rtb.Render(_surface);
        var px = Offscreen.Pixels(rtb);
        var file = _file;
        int w = _w, h = _h;
        JobManager.Run(L.F("{0} - annotations", System.IO.Path.GetFileName(file)), "", new[] { file }, ctx => Task.Run(() =>
        {
            var output = OutputPaths.File(file, ImageEngine.OutputExtFor(file), L.T(" (annotated)"));
            ImageEngine.SaveBgra(px, w, h, output, file);
            ctx.AddOutput(output);
        }));
        Close();
    }
}
