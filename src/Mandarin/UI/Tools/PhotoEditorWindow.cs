using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mandarin.Core;
using Mandarin.Engines;

namespace Mandarin.UI.Tools;

/// <summary>Photo adjustment parameters. All values are −100..100 (sharpness and vignette 0..100), exposure in EV.</summary>
public sealed class PhotoAdjust
{
    public double Exposure, Contrast, Highlights, Shadows, Saturation, Vibrance, Temperature, Tint, Sharpness, Vignette, Fade;
    public int Filter; // 0 none, 1 B&W, 2 sepia, 3 vintage, 4 cool, 5 warm, 6 drama, 7 noir

    public static readonly string[] FilterNames = { L.T("None"), L.T("B&W"), L.T("Sepia"), L.T("Vintage"), L.T("Cool"), L.T("Warm"), L.T("Drama"), L.T("Noir") };

    public PhotoAdjust Clone() => (PhotoAdjust)MemberwiseClone();

    /// <summary>Applies the adjustments to a BGRA buffer in place.</summary>
    public void Apply(byte[] px, int w, int h, CancellationToken ct = default)
    {
        var exp = Math.Pow(2, Exposure);
        var con = Contrast / 100.0 * (Contrast > 0 ? 0.8 : 0.6);
        var sat = Saturation / 100.0;
        var vib = Vibrance / 100.0;
        var temp = Temperature / 100.0;
        var tint = Tint / 100.0;
        var hi = Highlights / 100.0;
        var sh = Shadows / 100.0;
        var vig = Vignette / 100.0;
        var fade = Fade / 100.0;
        var filter = Filter;
        if (filter == 6) { con += 0.25; sat -= 0.25; }
        if (filter == 7) { con += 0.35; }

        var cx = w / 2.0; var cy = h / 2.0;
        var maxD = Math.Sqrt(cx * cx + cy * cy);

        // value table for speed
        var toLin = new double[256];
        for (var i = 0; i < 256; i++) toLin[i] = i / 255.0;

        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
        {
            var row = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var i = row + x * 4;
                double b = toLin[px[i]], g = toLin[px[i + 1]], r = toLin[px[i + 2]];

                r *= exp; g *= exp; b *= exp;
                r += temp * 0.08; b -= temp * 0.08; g -= tint * 0.06;

                var l = 0.299 * r + 0.587 * g + 0.114 * b;
                if (hi != 0 || sh != 0)
                {
                    var lc = Math.Clamp(l, 0, 1);
                    var nl = lc + sh * 0.35 * (1 - lc) * (1 - lc) * lc * 2.2 + hi * 0.3 * lc * lc * (1 - lc) * 2.2;
                    if (lc > 0.002) { var k = nl / lc; r *= k; g *= k; b *= k; }
                    else { r += nl - lc; g += nl - lc; b += nl - lc; }
                    l = 0.299 * r + 0.587 * g + 0.114 * b;
                }
                if (con != 0)
                {
                    r = (r - 0.5) * (1 + con) + 0.5; g = (g - 0.5) * (1 + con) + 0.5; b = (b - 0.5) * (1 + con) + 0.5;
                    l = 0.299 * r + 0.587 * g + 0.114 * b;
                }
                if (sat != 0 || vib != 0)
                {
                    var mx = Math.Max(r, Math.Max(g, b)); var mn = Math.Min(r, Math.Min(g, b));
                    var s = 1 + sat + vib * (1 - Math.Clamp(mx - mn, 0, 1)) * 1.2;
                    r = l + (r - l) * s; g = l + (g - l) * s; b = l + (b - l) * s;
                }

                switch (filter)
                {
                    case 1 or 7: r = g = b = l; break;
                    case 2:
                        { var nr = l * 1.07 + 0.05; var ng = l * 0.95 + 0.02; var nb = l * 0.75; r = nr; g = ng; b = nb; }
                        break;
                    case 3:
                        r = r * 0.9 + 0.08; g = g * 0.85 + 0.06; b = b * 0.75 + 0.07;
                        break;
                    case 4: r *= 0.93; b = b * 1.06 + 0.03; break;
                    case 5: r = r * 1.06 + 0.02; b *= 0.9; break;
                }

                if (fade > 0) { r = r * (1 - fade * 0.25) + fade * 0.12; g = g * (1 - fade * 0.25) + fade * 0.12; b = b * (1 - fade * 0.25) + fade * 0.12; }
                if (vig > 0)
                {
                    var dx = x - cx; var dy = y - cy;
                    var d = Math.Sqrt(dx * dx + dy * dy) / maxD;
                    var t = Math.Clamp((d - 0.45) / 0.55, 0, 1);
                    var k = 1 - vig * 0.75 * t * t * (3 - 2 * t);
                    r *= k; g *= k; b *= k;
                }

                px[i] = (byte)Math.Clamp((int)(b * 255 + 0.5), 0, 255);
                px[i + 1] = (byte)Math.Clamp((int)(g * 255 + 0.5), 0, 255);
                px[i + 2] = (byte)Math.Clamp((int)(r * 255 + 0.5), 0, 255);
            }
        });

        if (Sharpness > 0) Sharpen(px, w, h, Sharpness / 100.0 * 1.2);
    }

    /// <summary>Simple 3×3 unsharp mask.</summary>
    static void Sharpen(byte[] px, int w, int h, double amount)
    {
        var src = (byte[])px.Clone();
        Parallel.For(1, h - 1, y =>
        {
            for (var x = 1; x < w - 1; x++)
            {
                var i = (y * w + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0;
                    for (var oy = -1; oy <= 1; oy++)
                        for (var ox = -1; ox <= 1; ox++)
                            sum += src[((y + oy) * w + x + ox) * 4 + c];
                    var blur = sum / 9.0;
                    var v = src[i + c] + (src[i + c] - blur) * amount;
                    px[i + c] = (byte)Math.Clamp((int)(v + 0.5), 0, 255);
                }
            }
        });
    }
}

public sealed class PhotoEditorWindow : ToolWindow
{
    readonly string _file;
    readonly PhotoAdjust _adj = new();
    readonly Image _preview = new() { Stretch = Stretch.Uniform };
    byte[] _source = Array.Empty<byte>();
    int _w, _h;
    WriteableBitmap? _bitmap;
    CancellationTokenSource? _renderCts;
    bool _showOriginal;
    readonly List<Slider> _sliders = new();

    public PhotoEditorWindow(string file) : base(L.T("Photo editor"), Path.GetFileName(file), 1180, 760)
    {
        _file = file;
        ResizeMode = ResizeMode.CanResize;
        WindowState = WindowState.Normal;
        ScreenUnits.SharpWhenEnlarged(_preview);

        var controls = new StackPanel { Width = 340, Margin = new Thickness(16, 0, 18, 0) };
        void S(string label, double min, double max, double val, string fmt, Action<double> set, double tick = 0)
        {
            var (row, slider) = Ui.LabeledSlider(label, min, max, val, fmt, v => { set(v); Render(); }, tick);
            _sliders.Add(slider);
            controls.Children.Add(row);
        }
        controls.Children.Add(Ui.Section(L.T("Lighting"), 0));
        S(L.T("Exposure"), -2, 2, 0, "+0.0;-0.0;0", v => _adj.Exposure = v);
        S(L.T("Contrast"), -100, 100, 0, "0", v => _adj.Contrast = v);
        S(L.T("Highlights"), -100, 100, 0, "0", v => _adj.Highlights = v);
        S(L.T("Shadows"), -100, 100, 0, "0", v => _adj.Shadows = v);
        controls.Children.Add(Ui.Section(L.T("Color")));
        S(L.T("Saturation"), -100, 100, 0, "0", v => _adj.Saturation = v);
        S(L.T("Vibrance"), -100, 100, 0, "0", v => _adj.Vibrance = v);
        S(L.T("Temperature"), -100, 100, 0, "0", v => _adj.Temperature = v);
        S(L.T("Tint"), -100, 100, 0, "0", v => _adj.Tint = v);
        controls.Children.Add(Ui.Section(L.T("Details and effects")));
        S(L.T("Sharpness"), 0, 100, 0, "0", v => _adj.Sharpness = v);
        S(L.T("Vignette"), 0, 100, 0, "0", v => _adj.Vignette = v);
        S(L.T("Fade"), 0, 100, 0, "0", v => _adj.Fade = v);
        controls.Children.Add(Ui.Section(L.T("Filter")));
        var filters = new ComboBox { ItemsSource = PhotoAdjust.FilterNames, SelectedIndex = 0 };
        filters.SelectionChanged += (_, _) => { _adj.Filter = filters.SelectedIndex; Render(); };
        controls.Children.Add(filters);
        controls.Children.Add(Ui.Note(L.T("Double-click a label to reset its slider.")));

        var scroll = new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var previewHost = new Border
        {
            Child = _preview, CornerRadius = new CornerRadius(8), ClipToBounds = true,
            Background = Theme.IsDark ? Theme.Brush(0, 0, 0, 60) : Theme.Brush(0, 0, 0, 12)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(previewHost); grid.Children.Add(scroll);
        Body.Content = grid;

        var compare = new Button { Content = L.T("Hold for before/after"), Padding = new Thickness(14, 6, 14, 7) };
        compare.PreviewMouseLeftButtonDown += (_, _) => { _showOriginal = true; ShowOriginal(); };
        compare.PreviewMouseLeftButtonUp += (_, _) => { _showOriginal = false; Render(); };
        var reset = new Button { Content = L.T("Reset"), Padding = new Thickness(14, 6, 14, 7), Margin = new Thickness(8, 0, 0, 0) };
        reset.Click += (_, _) => { foreach (var s in _sliders) s.Value = s.Minimum < 0 ? 0 : 0; filters.SelectedIndex = 0; };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(compare); left.Children.Add(reset);
        AddFooterLeft(left);
        AddCancel();
        AddButton(L.T("Save a copy"), Save, true);

        Loaded += async (_, _) => await LoadAsync();
    }

    async Task LoadAsync()
    {
        try
        {
            Cursor = Cursors.Wait;
            (_source, _w, _h) = await Task.Run(() => ImageEngine.LoadBgra(_file, 1600));
            _bitmap = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Bgra32, null);
            _preview.Source = _bitmap;
            Render();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("Could not open the image: ") + ex.Message, "Mandarin", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
        finally { Cursor = null; }
    }

    void ShowOriginal()
    {
        if (_bitmap == null) return;
        _bitmap.WritePixels(new Int32Rect(0, 0, _w, _h), _source, _w * 4, 0);
    }

    async void Render()
    {
        if (_bitmap == null || _showOriginal) return;
        _renderCts?.Cancel();
        var cts = _renderCts = new CancellationTokenSource();
        var adj = _adj.Clone();
        var buffer = (byte[])_source.Clone();
        try
        {
            await Task.Run(() => adj.Apply(buffer, _w, _h, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || _showOriginal) return;
            _bitmap.WritePixels(new Int32Rect(0, 0, _w, _h), buffer, _w * 4, 0);
        }
        catch (OperationCanceledException) { }
    }

    void Save()
    {
        var adj = _adj.Clone();
        var file = _file;
        JobManager.Run(L.F("{0} - edit", Path.GetFileName(file)), "", new[] { file }, ctx => Task.Run(() =>
        {
            ctx.Report(-1);
            var (px, w, h) = ImageEngine.LoadBgra(file);
            adj.Apply(px, w, h, ctx.Token);
            var output = OutputPaths.File(file, ImageEngine.OutputExtFor(file), L.T(" (edited)"));
            ImageEngine.SaveBgra(px, w, h, output, file);
            ctx.AddOutput(output);
        }));
        Close();
    }
}
