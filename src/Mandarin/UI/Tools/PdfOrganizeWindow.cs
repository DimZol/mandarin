using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mandarin.Core;
using Mandarin.Engines;

namespace Mandarin.UI.Tools;

/// <summary>PDF pages: order (drag and drop), rotation, duplication, deletion.</summary>
public sealed class PdfOrganizeWindow : ToolWindow
{
    sealed class PageItem
    {
        public int Source;
        public int Rotation;
        public BitmapSource? Thumb;
        public bool Selected;
    }

    readonly string _file;
    readonly List<PageItem> _pages = new();
    readonly WrapPanel _panel = new();
    readonly TextBlock _status = new() { Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    int _anchor = -1;
    PageItem? _dragItem;
    Point _dragStart;
    bool _fannedIn;

    public PdfOrganizeWindow(string file) : base(L.T("Organize pages"), Path.GetFileName(file), 1100, 780)
    {
        _file = file;
        ResizeMode = ResizeMode.CanResize;
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(Ui.IconButton("", L.T("Rotate left"), () => RotateSel(-90), 36).Also(b => b.LayoutTransform = new ScaleTransform(-1, 1)));
        bar.Children.Add(Ui.IconButton("", L.T("Rotate right"), () => RotateSel(90), 36));
        bar.Children.Add(Ui.IconButton("", L.T("Duplicate"), Duplicate, 36));
        bar.Children.Add(Ui.IconButton("", L.T("Delete (Delete)"), DeleteSel, 36));
        bar.Children.Add(Ui.IconButton("", L.T("Move left"), () => MoveSel(-1), 36));
        bar.Children.Add(Ui.IconButton("", L.T("Move right"), () => MoveSel(1), 36));
        bar.Children.Add(Ui.IconButton("", L.T("Select all (Ctrl+A)"), () => { foreach (var p in _pages) p.Selected = true; Rebuild(); }, 36));
        bar.Children.Add(_status);
        var scroll = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(scroll);
        Body.Content = dock;
        _panel.AllowDrop = true;
        _panel.Drop += OnDrop;
        _panel.DragOver += (_, e) => { e.Effects = _dragItem != null ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) { DeleteSel(); e.Handled = true; }
            if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { foreach (var p in _pages) p.Selected = true; Rebuild(); e.Handled = true; }
        };
        AddCancel();
        AddButton(L.T("Save PDF"), Save, true);
        Loaded += async (_, _) => await LoadAsync();
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
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(png);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                foreach (var p in _pages.Where(p => p.Source == i)) p.Thumb = bmp;
                if (i % 6 == 5 || i == doc.PageCount - 1) Rebuild();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex is UserFacingException ? ex.Message : L.T("Could not open the PDF: ") + ex.Message, "Mandarin");
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
            var img = new Image { Source = page.Thumb, Width = 150, Height = 200, Stretch = Stretch.Uniform };
            img.LayoutTransform = new RotateTransform(page.Rotation);
            var label = new TextBlock { Text = $"{i + 1}" + (page.Source != i ? L.F("  (p. {0})", page.Source + 1) : ""), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
            var stack = new StackPanel();
            stack.Children.Add(new Grid { Width = 200, Height = 200, Children = { img } });
            stack.Children.Add(label);
            var tile = new Border
            {
                Child = stack, Margin = new Thickness(6), Padding = new Thickness(8), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand,
                BorderThickness = new Thickness(2),
                BorderBrush = page.Selected ? Theme.Brush(Theme.Accent) : Brushes.Transparent,
                Background = page.Selected ? Theme.Brush(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B, 30) : (Theme.IsDark ? Theme.Brush(255, 255, 255, 8) : Theme.Brush(0, 0, 0, 6)),
                Tag = page
            };
            tile.MouseLeftButtonDown += (_, e) =>
            {
                Click(index);
                _dragItem = page;
                _dragStart = e.GetPosition(this);
            };
            tile.MouseMove += (_, e) =>
            {
                if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
                if ((e.GetPosition(this) - _dragStart).Length < 8) return;
                DragDrop.DoDragDrop(tile, new DataObject("MandarinPage", page), DragDropEffects.Move);
                _dragItem = null;
            };
            Motion.HoverLift(tile);
            _panel.Children.Add(tile);
        }
        // the first time, the pages fan in one after another
        if (!_fannedIn && _panel.Children.Count > 0)
        {
            _fannedIn = true;
            Motion.Stagger(_panel, 30, 0, 14, 20);
        }
        var removed = Enumerable.Range(0, _pages.Count == 0 ? 0 : _pages.Max(p => p.Source) + 1).Count(i => _pages.All(p => p.Source != i));
        _status.Text = L.F("Pages: {0}. Selected: {1}. Drag pages to change the order.", _pages.Count, _pages.Count(p => p.Selected));
    }

    void Click(int index)
    {
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Shift) && _anchor >= 0)
        {
            var (a, b) = (Math.Min(_anchor, index), Math.Max(_anchor, index));
            for (var i = 0; i < _pages.Count; i++) _pages[i].Selected = i >= a && i <= b;
        }
        else if (mods.HasFlag(ModifierKeys.Control)) _pages[index].Selected = !_pages[index].Selected;
        else
        {
            foreach (var p in _pages) p.Selected = false;
            _pages[index].Selected = true;
            _anchor = index;
        }
        Rebuild();
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("MandarinPage") is not PageItem item) return;
        // find the tile under the cursor
        var pos = e.GetPosition(_panel);
        var target = _panel.Children.OfType<Border>().Select((b, i) => (b, i)).FirstOrDefault(t =>
        {
            var p = t.b.TranslatePoint(new Point(0, 0), _panel);
            return new Rect(p, t.b.RenderSize).Contains(pos);
        });
        var to = target.b == null ? _pages.Count - 1 : target.i;
        var from = _pages.IndexOf(item);
        if (from < 0 || from == to) return;
        _pages.RemoveAt(from);
        _pages.Insert(Math.Min(to, _pages.Count), item);
        Rebuild();
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
