using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Mandarin.UI.Tools;

/// <summary>Timeline: audio waveform, trim range, cut/frame markers, marked fragments, playhead.</summary>
public sealed class Timeline : Control
{
    public enum Mode { Plain, Range, Markers, Ranges }

    public Mode EditMode { get; set; } = Mode.Plain;
    public double Duration { get; set; } = 1;
    public double Position { get; set; }
    public double In { get; set; }
    public double Out { get; set; } = 1;
    public float[]? Peaks { get; set; }
    public List<double> Markers { get; } = new();
    public List<(double start, double end)> Ranges { get; } = new();
    public int SelectedMarker { get; private set; } = -1;
    public int SelectedRange { get; private set; } = -1;

    public event Action<double>? Seek;
    public event Action? Changed;

    enum DragKind { None, Seek, In, Out, NewRange, MoveRange }
    DragKind _drag;
    double _dragStartT;
    (double start, double end) _rangeAtStart;
    bool _resizingNew;

    public Timeline()
    {
        Height = 84;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        ClipToBounds = true;
    }

    double W => Bounds.Width;
    double X(double t) => Duration <= 0 ? 0 : t / Duration * W;
    double T(double x) => Math.Clamp(x / Math.Max(1, W) * Duration, 0, Duration);

    public override void Render(DrawingContext dc)
    {
        var w = Bounds.Width; var h = Bounds.Height;
        var dark = Look.IsDark;
        var accent = Look.Accent;
        dc.DrawRectangle(dark ? Look.Brush(0x24, 0x24, 0x24) : Look.Brush(0xEE, 0xEE, 0xEE), null, new Rect(0, 0, w, h), 6, 6);

        var wave = dark ? Look.Brush(0x8A, 0x8A, 0x8A) : Look.Brush(0x9A, 0x9A, 0x9A);
        var active = new SolidColorBrush(accent);
        if (Peaks is { Length: > 0 } peaks)
        {
            var mid = h / 2;
            var cols = (int)Math.Max(1, w / 2);
            for (var i = 0; i < cols; i++)
            {
                var from = (int)((long)i * peaks.Length / cols);
                var to = Math.Max(from + 1, (int)((long)(i + 1) * peaks.Length / cols));
                float max = 0;
                for (var k = from; k < to && k < peaks.Length; k++) max = Math.Max(max, peaks[k]);
                var amp = Math.Max(1, Math.Sqrt(max) * (h / 2 - 6));
                var x = i * 2.0;
                var t = T(x);
                var inside = EditMode != Mode.Range || (t >= In && t <= Out);
                dc.DrawRectangle(inside && EditMode == Mode.Range ? active : wave, null, new Rect(x, mid - amp, 1.4, amp * 2));
            }
        }
        else dc.DrawRectangle(wave, null, new Rect(0, h / 2 - 1, w, 2));

        if (EditMode == Mode.Range)
        {
            var shade = new SolidColorBrush(Color.FromArgb(dark ? (byte)150 : (byte)120, 0, 0, 0));
            dc.DrawRectangle(shade, null, new Rect(0, 0, X(In), h));
            dc.DrawRectangle(shade, null, new Rect(X(Out), 0, Math.Max(0, w - X(Out)), h));
            dc.DrawRectangle(null, new Pen(active, 2), new Rect(X(In), 1, Math.Max(1, X(Out) - X(In)), h - 2));
            DrawHandle(dc, X(In), h, active);
            DrawHandle(dc, X(Out), h, active);
        }

        if (EditMode == Mode.Ranges)
            for (var i = 0; i < Ranges.Count; i++)
            {
                var (s, e) = Ranges[i];
                var sel = i == SelectedRange;
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(sel ? (byte)150 : (byte)95, 0xE5, 0x39, 0x35)),
                    new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), sel ? 2 : 1), new Rect(X(s), 0, Math.Max(2, X(e) - X(s)), h));
            }

        if (EditMode == Mode.Markers)
            for (var i = 0; i < Markers.Count; i++)
            {
                var x = X(Markers[i]);
                var brush = i == SelectedMarker ? new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)) : active;
                dc.DrawRectangle(brush, null, new Rect(x - 1, 0, 2, h));
                var tri = new StreamGeometry();
                using (var c = tri.Open())
                {
                    c.BeginFigure(new Point(x - 6, 0), true);
                    c.LineTo(new Point(x + 6, 0));
                    c.LineTo(new Point(x, 8));
                    c.EndFigure(true);
                }
                dc.DrawGeometry(brush, null, tri);
            }

        var px = X(Position);
        dc.DrawRectangle(dark ? Brushes.White : Look.Brush(0x20, 0x20, 0x20), null, new Rect(px - 1, 0, 2, h));
    }

    static void DrawHandle(DrawingContext dc, double x, double h, IBrush accent)
    {
        dc.DrawRectangle(accent, new Pen(Brushes.White, 1.5), new Rect(x - 5, h / 2 - 16, 10, 32), 3, 3);
        dc.DrawRectangle(Brushes.White, null, new Rect(x - 1, h / 2 - 7, 2, 14));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        var x = e.GetPosition(this).X;
        var t = T(x);
        _drag = DragKind.Seek;
        if (EditMode == Mode.Range)
        {
            if (Math.Abs(x - X(In)) < 10) _drag = DragKind.In;
            else if (Math.Abs(x - X(Out)) < 10) _drag = DragKind.Out;
        }
        else if (EditMode == Mode.Markers) SelectedMarker = Markers.FindIndex(m => Math.Abs(X(m) - x) < 7);
        else if (EditMode == Mode.Ranges)
        {
            SelectedRange = Ranges.FindIndex(r => t >= r.start && t <= r.end);
            if (SelectedRange >= 0) { _drag = DragKind.MoveRange; _rangeAtStart = Ranges[SelectedRange]; }
            else _drag = DragKind.NewRange;
        }
        _dragStartT = t;
        e.Pointer.Capture(this);
        if (_drag == DragKind.Seek) Seek?.Invoke(t);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var x = e.GetPosition(this).X;
        if (EditMode == Mode.Range && _drag == DragKind.None)
            Cursor = new Cursor(Math.Abs(x - X(In)) < 10 || Math.Abs(x - X(Out)) < 10 ? StandardCursorType.SizeWestEast : StandardCursorType.Hand);
        if (_drag == DragKind.None || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var t = T(x);
        var minLen = Math.Min(0.1, Duration / 100);
        switch (_drag)
        {
            case DragKind.Seek: Seek?.Invoke(t); break;
            case DragKind.In: In = Math.Min(t, Out - minLen); Seek?.Invoke(In); Changed?.Invoke(); break;
            case DragKind.Out: Out = Math.Max(t, In + minLen); Seek?.Invoke(Out); Changed?.Invoke(); break;
            case DragKind.NewRange when Math.Abs(t - _dragStartT) > Duration / 300:
                Ranges.Add((Math.Min(t, _dragStartT), Math.Max(t, _dragStartT)));
                SelectedRange = Ranges.Count - 1;
                _rangeAtStart = Ranges[^1];
                _drag = DragKind.MoveRange;
                _dragStartT = t;
                _resizingNew = true;
                Changed?.Invoke();
                break;
            case DragKind.MoveRange when SelectedRange >= 0:
                if (_resizingNew)
                {
                    var anchor = _rangeAtStart.start;
                    Ranges[SelectedRange] = (Math.Min(anchor, t), Math.Max(anchor, t));
                }
                else
                {
                    var len = _rangeAtStart.end - _rangeAtStart.start;
                    var s = Math.Clamp(_rangeAtStart.start + t - _dragStartT, 0, Duration - len);
                    Ranges[SelectedRange] = (s, s + len);
                }
                Changed?.Invoke();
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_drag == DragKind.NewRange) Seek?.Invoke(_dragStartT);
        _drag = DragKind.None;
        _resizingNew = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    public void DeleteSelected()
    {
        if (EditMode == Mode.Markers && SelectedMarker >= 0) { Markers.RemoveAt(SelectedMarker); SelectedMarker = -1; }
        else if (EditMode == Mode.Ranges && SelectedRange >= 0) { Ranges.RemoveAt(SelectedRange); SelectedRange = -1; }
        else return;
        Changed?.Invoke();
        InvalidateVisual();
    }

    public void Refresh() => InvalidateVisual();
}
