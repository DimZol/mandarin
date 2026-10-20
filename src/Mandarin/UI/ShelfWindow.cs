using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Mandarin.Core;
using Mandarin.Interop;

namespace Mandarin.UI;

/// <summary>Floating shelf: a temporary place for files that can be dragged into any app.</summary>
public sealed class ShelfWindow : Window
{
    static ShelfWindow? _instance;
    readonly WrapPanel _items = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock _empty;
    readonly List<string> _files = new();
    readonly HashSet<string> _shown = new(StringComparer.OrdinalIgnoreCase);
    readonly Border _panel;
    Point _dragStart;
    string? _dragCandidate;
    bool _left, _closeAnimated;

    ShelfWindow()
    {
        Title = L.T("Mandarin - shelf");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 232;
        SizeToContent = SizeToContent.Height;
        AllowDrop = true;
        FontFamily = Theme.UiFont;
        Motion.SetSkipEntrance(this, true);

        var dark = Theme.IsDark;
        var fg = dark ? Theme.Brush(0xF0, 0xF0, 0xF0) : Theme.Brush(0x20, 0x20, 0x20);
        _empty = new TextBlock
        {
            Text = L.T("Drop files here to keep\nthem at hand"), TextAlignment = TextAlignment.Center,
            Foreground = dark ? Theme.Brush(0xA0, 0xA0, 0xA0) : Theme.Brush(0x70, 0x70, 0x70), FontSize = 12, Margin = new Thickness(8, 18, 8, 18)
        };

        var header = new DockPanel { Margin = new Thickness(12, 8, 6, 4), Background = Brushes.Transparent };
        var close = HeaderButton("", L.T("Close the shelf"), () => { _files.Clear(); Persist(); Close(); });
        var clear = HeaderButton("", L.T("Clear"), ClearAnimated);
        var dockLeft = HeaderButton("", L.T("Pin to the left"), () => Dock(left: true, animate: true));
        var dockRight = HeaderButton("", L.T("Pin to the right"), () => Dock(left: false, animate: true));
        foreach (var b in new[] { close, clear, dockRight, dockLeft }) { DockPanel.SetDock(b, System.Windows.Controls.Dock.Right); header.Children.Add(b); }
        header.Children.Add(new TextBlock { Text = L.T("Shelf"), FontWeight = FontWeights.SemiBold, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_empty);
        stack.Children.Add(new ScrollViewer { Content = _items, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(6, 0, 6, 8) });

        _panel = new Border
        {
            Margin = new Thickness(12),
            Background = dark ? Theme.Brush(0x2A, 0x2A, 0x2A, 0xF5) : Theme.Brush(0xF8, 0xF8, 0xF8, 0xF7),
            BorderBrush = dark ? Theme.Brush(255, 255, 255, 26) : Theme.Brush(0, 0, 0, 24),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 3, Opacity = dark ? 0.5 : 0.2, Color = Colors.Black }
        };
        Content = _panel;

        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _dragCandidate == null ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (_dragCandidate != null) return;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Add(files);
            // Link: the shelf keeps links to files, the files themselves stay in place
            e.Effects = DragDropEffects.Link;
            e.Handled = true;
        };
        Loaded += (_, _) =>
        {
            Dock(left: false, animate: false);
            Motion.Entrance(_panel, 0, 0, 360, 48); // slides in from the right edge
        };
        // the shelf slides back out towards its edge before it closes
        Closing += (_, e) =>
        {
            if (_closeAnimated || !Motion.Enabled) return;
            e.Cancel = true;
            _closeAnimated = true;
            IsHitTestVisible = false;
            if (Motion.Translate(_panel) is { } t) Motion.To(t, TranslateTransform.XProperty, _left ? -48 : 48, 220, Motion.EaseInOut);
            Motion.FromTo(_panel, OpacityProperty, _panel.Opacity, 0, 220, Motion.EaseInOut, 0, Close);
        };
    }

    Button HeaderButton(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = Theme.IconFont, FontSize = 11 }, Width = 26, Height = 24, Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = tip, Cursor = Cursors.Hand,
            Foreground = Theme.IsDark ? Theme.Brush(0xC0, 0xC0, 0xC0) : Theme.Brush(0x50, 0x50, 0x50)
        };
        b.Click += (_, _) => click();
        return b;
    }

    void Dock(bool left, bool animate)
    {
        var (x, y) = Native.CursorPos();
        var (work, scale) = Native.MonitorAt(x, y);
        UpdateLayout();
        _left = left;
        var targetLeft = left ? work.Left / scale : work.Right / scale - ActualWidth;
        var targetTop = (work.Top + (work.Bottom - work.Top) / 2) / scale - ActualHeight / 2;
        if (!animate || !Motion.Enabled)
        {
            BeginAnimation(LeftProperty, null); BeginAnimation(TopProperty, null);
            Left = targetLeft; Top = targetTop;
            return;
        }
        // glide across the screen to the chosen edge
        Motion.FromTo(this, LeftProperty, Left, targetLeft, 420, Motion.EaseInOut);
        Motion.FromTo(this, TopProperty, Top, targetTop, 420, Motion.EaseInOut);
    }

    public static void AddFiles(IEnumerable<string> files)
    {
        if (_instance == null || !_instance.IsLoaded)
        {
            _instance = new ShelfWindow();
            _instance.Closed += (_, _) => _instance = null;
            _instance.Show();
        }
        _instance.Add(files);
        _instance.Activate();
    }

    public static void Toggle()
    {
        if (_instance != null) { _instance.Close(); return; }
        AddFiles(AppSettings.Current.ShelfItems.Where(f => File.Exists(f) || Directory.Exists(f)).ToList());
    }

    public static void RestoreIfNeeded()
    {
        var saved = AppSettings.Current.ShelfItems.Where(f => File.Exists(f) || Directory.Exists(f)).ToList();
        if (saved.Count > 0) AddFiles(saved);
    }

    void Add(IEnumerable<string> files)
    {
        foreach (var f in files)
            if (!_files.Contains(f, StringComparer.OrdinalIgnoreCase)) _files.Add(f);
        Refresh();
    }

    void Persist()
    {
        AppSettings.Current.ShelfItems = _files.ToList();
        AppSettings.Save();
    }

    /// <summary>Every tile fades away together, then the shelf is emptied.</summary>
    void ClearAnimated()
    {
        if (_items.Children.Count == 0) { _files.Clear(); Refresh(); return; }
        _items.IsHitTestVisible = false;
        Motion.FromTo(_items, OpacityProperty, 1, 0, 180, Motion.EaseInOut, 0, () =>
        {
            _files.Clear();
            Refresh();
            _items.IsHitTestVisible = true;
            _items.Opacity = 1;
        });
    }

    void Refresh()
    {
        _files.RemoveAll(f => !File.Exists(f) && !Directory.Exists(f));
        _items.Children.Clear();
        var wasEmpty = _empty.Visibility == Visibility.Visible;
        _empty.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_files.Count == 0 && !wasEmpty) Motion.Entrance(_empty, 0, 6);
        var fg = Theme.IsDark ? Theme.Brush(0xE8, 0xE8, 0xE8) : Theme.Brush(0x25, 0x25, 0x25);
        var added = 0;
        foreach (var f in _files)
        {
            var tile = new StackPanel { Width = 96, Margin = new Thickness(3), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = f };
            var thumb = new Image { Source = ShellThumbs.Get(f, 96), Width = 64, Height = 64, Stretch = Stretch.Uniform, Margin = new Thickness(0, 4, 0, 4) };
            tile.Children.Add(thumb);
            // the tile rises a little and its thumbnail grows under the mouse
            Motion.HoverLift(tile, 2);
            tile.MouseEnter += (_, _) => { if (Motion.Scale(thumb) is { } s) Motion.ScaleTo(s, 1.08, 200, Motion.Spring); };
            tile.MouseLeave += (_, _) => { if (Motion.Scale(thumb) is { } s) Motion.ScaleTo(s, 1, 220); };
            tile.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(f.TrimEnd('\\')), FontSize = 11, Foreground = fg, TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 30, TextWrapping = TextWrapping.Wrap
            });
            var path = f;
            tile.MouseLeftButtonDown += (_, e) =>
            {
                _dragStart = e.GetPosition(this);
                _dragCandidate = path;
                if (e.ClickCount == 2) { _dragCandidate = null; Integration.Open(path); }
            };
            tile.MouseMove += (_, e) =>
            {
                if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;
                if ((e.GetPosition(this) - _dragStart).Length < 6) return;
                // with Ctrl held, drag every file on the shelf, otherwise just one
                var drag = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? _files.ToArray() : new[] { _dragCandidate };
                var list = new StringCollection(); list.AddRange(drag);
                var data = new DataObject(); data.SetFileDropList(list);
                // copy or link only: a file must not silently "move away" from its folder via the shelf
                try { DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy | DragDropEffects.Link); }
                finally { _dragCandidate = null; }
                Refresh();
            };
            tile.MouseLeftButtonUp += (_, _) => _dragCandidate = null;
            tile.ContextMenu = new ContextMenu();
            var open = new MenuItem { Header = L.T("Open") }; open.Click += (_, _) => Integration.Open(path);
            var reveal = new MenuItem { Header = L.T("Show in folder") }; reveal.Click += (_, _) => Integration.RevealInExplorer(path);
            var wheel = new MenuItem { Header = L.T("Convert…") }; wheel.Click += (_, _) => WheelWindow.ShowForFiles(new[] { path });
            var remove = new MenuItem { Header = L.T("Remove from the shelf") }; remove.Click += (_, _) => Motion.PopOut(tile, () => { _files.Remove(path); Refresh(); });
            tile.ContextMenu.Items.Add(open); tile.ContextMenu.Items.Add(reveal); tile.ContextMenu.Items.Add(wheel);
            tile.ContextMenu.Items.Add(new Separator()); tile.ContextMenu.Items.Add(remove);
            _items.Children.Add(tile);
            // files that just arrived pop in one after another
            if (_shown.Add(path)) Motion.Pop(tile, 0.6, added++ * 45);
        }
        _shown.IntersectWith(_files);
        Persist();
    }
}
