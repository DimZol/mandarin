using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mandarin.Core;
using Mandarin.Engines;

namespace Mandarin.UI.Tools;

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
