using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Mandarin.Core;
using Mandarin.Engines;

namespace Mandarin.UI.Tools;

/// <summary>PDF pages: order (drag a page onto another place), rotation, duplication, deletion.</summary>
public sealed class PdfOrganizeWindow : ToolWindow
{
    sealed class PageItem
    {
        public int Source;
        public int Rotation;
        public Bitmap? Thumb;
        public bool Selected;
    }

    readonly string _file;
    readonly List<PageItem> _pages = new();
    readonly WrapPanel _panel = new();
    readonly TextBlock _status = new() { Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    int _anchor = -1;
    PageItem? _dragItem;
    Border? _dragTile;
    Point _dragStart;
    bool _dragging, _fannedIn;

    public PdfOrganizeWindow(string file) : base(L.T("Organize pages"), Path.GetFileName(file), 1100, 780)
    {
        _file = file;
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(Ui.IconButton("", L.T("Rotate left"), () => RotateSel(-90), 36).Also(b => b.RenderTransform = new ScaleTransform(-1, 1)));
        bar.Children.Add(Ui.IconButton("", L.T("Rotate right"), () => RotateSel(90), 36));
        bar.Children.Add(Ui.IconButton("", L.T("Duplicate"), Duplicate, 36));
        bar.Children.Add(Ui.IconButton("", L.T("Delete (Delete)"), DeleteSel, 36));
        bar.Children.Add(Ui.IconButton("", L.T("Move left"), () => MoveSel(-1), 36));
        bar.Children.Add(Ui.IconButton("", L.T("Move right"), () => MoveSel(1), 36));
        bar.Children.Add(Ui.IconButton("", L.T("Select all (Ctrl+A)"), () => { foreach (var p in _pages) p.Selected = true; Rebuild(); }, 36));
        bar.Children.Add(_status);
        var scroll = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(scroll);
        Body.Content = dock;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Delete) { DeleteSel(); e.Handled = true; }
            if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control) { foreach (var p in _pages) p.Selected = true; Rebuild(); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddCancel();
        AddButton(L.T("Save PDF"), Save, true);
        Opened += async (_, _) => await LoadAsync();
    }

    async Task LoadAsync()
    {
        try
        {
            var doc = await WinRt.OpenPdfAsync(_file);
            for (var i = 0; i < doc.PageCount; i++) _pages.Add(new PageItem { Source = i });
            Rebuild();
            for (uint i = 0; i < doc.PageCount; i++)
            {
                var png = await WinRt.RenderPageAsync(doc, i, 0, 220);
                var bmp = new Bitmap(new MemoryStream(png));
                foreach (var p in _pages.Where(p => p.Source == i)) p.Thumb = bmp;
                if (i % 6 == 5 || i == doc.PageCount - 1) Rebuild();
            }
        }
        catch (Exception ex)
        {
            await ShowMessage(ex is UserFacingException ? ex.Message : L.T("Could not open the PDF: ") + ex.Message);
            Close();
        }
    }

    void Rebuild()
    {
        _panel.Children.Clear();
        for (var i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            var index = i;
            var img = new Image { Source = page.Thumb, Width = 150, Height = 200, Stretch = Stretch.Uniform, RenderTransform = new RotateTransform(page.Rotation) };
            var label = new TextBlock { Text = $"{i + 1}" + (page.Source != i ? L.F("  (p. {0})", page.Source + 1) : ""), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
            var stack = new StackPanel { Children = { new Grid { Width = 200, Height = 200, Children = { img } }, label } };
            var accent = Look.Accent;
            var tile = new Border
            {
                Child = stack, Margin = new Thickness(6), Padding = new Thickness(8), CornerRadius = new CornerRadius(8), Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = new Thickness(2),
                BorderBrush = page.Selected ? Look.Brush(accent) : Brushes.Transparent,
                Background = page.Selected ? Look.Brush(Color.FromArgb(30, accent.R, accent.G, accent.B)) : (Look.IsDark ? Look.Brush(255, 255, 255, 8) : Look.Brush(0, 0, 0, 6)),
                Tag = page
            };
            tile.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed) return;
                Click(index, e.KeyModifiers);
                _dragItem = page;
                _dragStart = e.GetPosition(_panel);
                _dragging = false;
                e.Pointer.Capture(_panel);
            };
            Motion.HoverLift(tile);
            _panel.Children.Add(tile);
        }
        if (!_fannedIn && _panel.Children.Count > 0)
        {
            _fannedIn = true;
            Motion.Stagger(_panel, 30, 0, 14, 20);
        }
        _status.Text = L.F("Pages: {0}. Selected: {1}. Drag pages to change the order.", _pages.Count, _pages.Count(p => p.Selected));
        _panel.PointerMoved -= OnDragMove; _panel.PointerMoved += OnDragMove;
        _panel.PointerReleased -= OnDragEnd; _panel.PointerReleased += OnDragEnd;
    }

    void OnDragMove(object? sender, PointerEventArgs e)
    {
        if (_dragItem == null) return;
        var p = e.GetPosition(_panel);
        if (!_dragging && Math.Abs(p.X - _dragStart.X) + Math.Abs(p.Y - _dragStart.Y) < 10) return;
        _dragging = true;
        _dragTile ??= _panel.Children.OfType<Border>().FirstOrDefault(b => b.Tag == _dragItem);
        if (_dragTile != null) _dragTile.Opacity = 0.5;
    }

    void OnDragEnd(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        var item = _dragItem;
        var dragging = _dragging;
        _dragItem = null; _dragTile = null; _dragging = false;
        if (item == null || !dragging) { Rebuild(); return; }
        var pos = e.GetPosition(_panel);
        var target = _panel.Children.OfType<Border>().Select((b, i) => (b, i)).FirstOrDefault(t => t.b.Bounds.Contains(pos));
        var to = target.b == null ? _pages.Count - 1 : target.i;
        var from = _pages.IndexOf(item);
        if (from >= 0 && from != to)
        {
            _pages.RemoveAt(from);
            _pages.Insert(Math.Min(to, _pages.Count), item);
        }
        Rebuild();
    }

    void Click(int index, KeyModifiers mods)
    {
        if (mods.HasFlag(KeyModifiers.Shift) && _anchor >= 0)
        {
            var (a, b) = (Math.Min(_anchor, index), Math.Max(_anchor, index));
            for (var i = 0; i < _pages.Count; i++) _pages[i].Selected = i >= a && i <= b;
        }
        else if (mods.HasFlag(KeyModifiers.Control)) _pages[index].Selected = !_pages[index].Selected;
        else
        {
            foreach (var p in _pages) p.Selected = false;
            _pages[index].Selected = true;
            _anchor = index;
        }
        foreach (var tile in _panel.Children.OfType<Border>())
        {
            var p = (PageItem)tile.Tag!;
            var accent = Look.Accent;
            tile.BorderBrush = p.Selected ? Look.Brush(accent) : Brushes.Transparent;
            tile.Background = p.Selected ? Look.Brush(Color.FromArgb(30, accent.R, accent.G, accent.B)) : (Look.IsDark ? Look.Brush(255, 255, 255, 8) : Look.Brush(0, 0, 0, 6));
        }
        _status.Text = L.F("Pages: {0}. Selected: {1}. Drag pages to change the order.", _pages.Count, _pages.Count(p => p.Selected));
    }

    void RotateSel(int deg)
    {
        foreach (var p in _pages.Where(p => p.Selected)) p.Rotation = ((p.Rotation + deg) % 360 + 360) % 360;
        Rebuild();
    }

    void Duplicate()
    {
        for (var i = _pages.Count - 1; i >= 0; i--)
            if (_pages[i].Selected)
                _pages.Insert(i + 1, new PageItem { Source = _pages[i].Source, Rotation = _pages[i].Rotation, Thumb = _pages[i].Thumb });
        Rebuild();
    }

    void DeleteSel()
    {
        if (_pages.All(p => p.Selected)) { _status.Text = L.T("Cannot delete every page"); return; }
        _pages.RemoveAll(p => p.Selected);
        Rebuild();
    }

    void MoveSel(int d)
    {
        var idx = _pages.FindIndex(p => p.Selected);
        if (idx < 0) return;
        var j = idx + d;
        if (j < 0 || j >= _pages.Count) return;
        (_pages[idx], _pages[j]) = (_pages[j], _pages[idx]);
        Rebuild();
    }

    void Save()
    {
        var pages = _pages.Select(p => (p.Source, p.Rotation)).ToList();
        var file = _file;
        JobManager.Run(L.F("{0} - pages", Path.GetFileName(file)), "", new[] { file }, ctx => Task.Run(() =>
        {
            var output = OutputPaths.File(file, "pdf", L.T(" (organized)"));
            PdfEngine.Organize(file, pages, output);
            ctx.AddOutput(output);
        }));
        Close();
    }
}
