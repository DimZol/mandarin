using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Mandarin.UI.Tools;

/// <summary>Timeline: audio waveform, trim range, cut/frame markers, marked fragments, playhead.</summary>
public sealed class Timeline : FrameworkElement
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
    public Color MarkerColor { get; set; } = Theme.Accent;

    public event Action<double>? Seek;
    public event Action? Changed;

    enum DragKind { None, Seek, In, Out, NewRange, MoveRange }
    DragKind _drag;
    double _dragStartT;
    (double start, double end) _rangeAtStart;

    public Timeline()
    {
        Height = 84;
        Focusable = true;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    double X(double t) => Duration <= 0 ? 0 : t / Duration * ActualWidth;
    double T(double x) => Math.Clamp(x / Math.Max(1, ActualWidth) * Duration, 0, Duration);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        var dark = Theme.IsDark;
        var bg = dark ? Color.FromRgb(0x24, 0x24, 0x24) : Color.FromRgb(0xEE, 0xEE, 0xEE);
        dc.DrawRoundedRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, w, h), 6, 6);

        // waveform
        var waveColor = dark ? Color.FromRgb(0x8A, 0x8A, 0x8A) : Color.FromRgb(0x9A, 0x9A, 0x9A);
        var activeWave = Theme.Accent;
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
                var inside = EditMode == Mode.Range ? t >= In && t <= Out : true;
                var color = inside && EditMode == Mode.Range ? activeWave : waveColor;
                dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(x, mid - amp, 1.4, amp * 2));
            }
        }
        else
        {
            dc.DrawRectangle(new SolidColorBrush(waveColor), null, new Rect(0, h / 2 - 1, w, 2));
        }

        // trim range
        if (EditMode == Mode.Range)
        {
            var shade = new SolidColorBrush(Color.FromArgb(dark ? (byte)150 : (byte)120, 0, 0, 0));
            dc.DrawRectangle(shade, null, new Rect(0, 0, X(In), h));
            dc.DrawRectangle(shade, null, new Rect(X(Out), 0, Math.Max(0, w - X(Out)), h));
            var pen = new Pen(new SolidColorBrush(Theme.Accent), 2);
            dc.DrawRectangle(null, pen, new Rect(X(In), 1, Math.Max(1, X(Out) - X(In)), h - 2));
            DrawHandle(dc, X(In), h);
            DrawHandle(dc, X(Out), h);
        }

        // marked fragments
        if (EditMode == Mode.Ranges)
            for (var i = 0; i < Ranges.Count; i++)
            {
                var (s, e) = Ranges[i];
                var sel = i == SelectedRange;
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(sel ? (byte)150 : (byte)95, 0xE5, 0x39, 0x35)),
                    new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), sel ? 2 : 1), new Rect(X(s), 0, Math.Max(2, X(e) - X(s)), h));
            }

        // markers
        if (EditMode == Mode.Markers)
            for (var i = 0; i < Markers.Count; i++)
            {
                var x = X(Markers[i]);
                var sel = i == SelectedMarker;
                var brush = new SolidColorBrush(sel ? Color.FromRgb(0xE5, 0x39, 0x35) : MarkerColor);
                dc.DrawRectangle(brush, null, new Rect(x - 1, 0, 2, h));
                var tri = new StreamGeometry();
                using (var c = tri.Open())
                {
                    c.BeginFigure(new Point(x - 6, 0), true, true);
                    c.LineTo(new Point(x + 6, 0), true, true);
                    c.LineTo(new Point(x, 8), true, true);
                }
                dc.DrawGeometry(brush, null, tri);
            }

        // playhead
        var px = X(Position);
        dc.DrawRectangle(new SolidColorBrush(dark ? Colors.White : Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(px - 1, 0, 2, h));
    }

    static void DrawHandle(DrawingContext dc, double x, double h)
    {
        var r = new Rect(x - 5, h / 2 - 16, 10, 32);
        dc.DrawRoundedRectangle(new SolidColorBrush(Theme.Accent), new Pen(Brushes.White, 1.5), r, 3, 3);
        dc.DrawRectangle(Brushes.White, null, new Rect(x - 1, h / 2 - 7, 2, 14));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var x = e.GetPosition(this).X;
        var t = T(x);
        _drag = DragKind.Seek;
        if (EditMode == Mode.Range)
        {
            if (Math.Abs(x - X(In)) < 10) _drag = DragKind.In;
            else if (Math.Abs(x - X(Out)) < 10) _drag = DragKind.Out;
        }
        else if (EditMode == Mode.Markers)
        {
            SelectedMarker = Markers.FindIndex(m => Math.Abs(X(m) - x) < 7);
        }
        else if (EditMode == Mode.Ranges)
        {
            SelectedRange = Ranges.FindIndex(r => t >= r.start && t <= r.end);
            if (SelectedRange >= 0) { _drag = DragKind.MoveRange; _rangeAtStart = Ranges[SelectedRange]; }
            else _drag = DragKind.NewRange;
        }
        _dragStartT = t;
        CaptureMouse();
        if (_drag == DragKind.Seek) Seek?.Invoke(t);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var x = e.GetPosition(this).X;
        if (EditMode == Mode.Range && _drag == DragKind.None)
            Cursor = Math.Abs(x - X(In)) < 10 || Math.Abs(x - X(Out)) < 10 ? Cursors.SizeWE : Cursors.Hand;
        if (e.LeftButton != MouseButtonState.Pressed || _drag == DragKind.None) return;
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
                    var d = t - _dragStartT;
                    var len = _rangeAtStart.end - _rangeAtStart.start;
                    var s = Math.Clamp(_rangeAtStart.start + d, 0, Duration - len);
                    Ranges[SelectedRange] = (s, s + len);
                }
                Changed?.Invoke();
                break;
        }
        InvalidateVisual();
    }

    bool _resizingNew;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == DragKind.NewRange) Seek?.Invoke(_dragStartT);
        _drag = DragKind.None;
        _resizingNew = false;
        ReleaseMouseCapture();
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

    public static string Fmt(double s) => Ui.Time(s);
}
