using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Mandarin.Interop;

namespace Mandarin.UI;

/// <summary>Base tool window: header, content, buttons at the bottom. Mica and the Windows 11 dark theme.</summary>
public class ToolWindow : Window
{
    protected readonly ContentControl Body = new();
    protected readonly StackPanel Footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    protected readonly DockPanel FooterBar = new() { LastChildFill = false };
    protected readonly TextBlock Subtitle;

    public ToolWindow(string title, string subtitle, double width = 520, double? height = null)
    {
        Title = title + " — Mandarin";
        Width = width;
        if (height is { } h) Height = h; else SizeToContent = SizeToContent.Height;
        MinWidth = Math.Min(width, 420);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Theme.UiFont;
        ShowInTaskbar = true;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Mandarin;component/Assets/Mandarin.png"));

        var header = new StackPanel { Margin = new Thickness(24, 18, 24, 10) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = Theme.DisplayFont });
        Subtitle = new TextBlock { Text = subtitle, FontSize = 12.5, Opacity = 0.72, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        header.Children.Add(Subtitle);

        FooterBar.Margin = new Thickness(24, 10, 24, 18);
        DockPanel.SetDock(Footer, Dock.Right);
        FooterBar.Children.Add(Footer);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(FooterBar, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(FooterBar);
        Body.Margin = new Thickness(24, 4, 24, 4);
        root.Children.Add(Body);
        Content = root;

        SourceInitialized += (_, _) => Native.ApplyWindowChrome(new WindowInteropHelper(this).Handle, Theme.IsDark);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    protected Button AddButton(string text, Action onClick, bool primary = false)
    {
        var b = new Button { Content = text, MinWidth = 110, Padding = new Thickness(16, 6, 16, 7), Margin = new Thickness(8, 0, 0, 0) };
        if (primary)
        {
            b.Background = Theme.Brush(Theme.Accent);
            b.Foreground = Brushes.White;
            b.BorderBrush = Theme.Brush(Theme.AccentDark);
            b.IsDefault = true;
        }
        b.Click += (_, _) => onClick();
        Footer.Children.Add(b);
        return b;
    }

    protected void AddCancel() => AddButton(L.T("Cancel"), Close);

    protected void AddFooterLeft(UIElement element)
    {
        DockPanel.SetDock(element, Dock.Left);
        FooterBar.Children.Add(element);
    }
}

/// <summary>Small element factories so all tool windows look alike.</summary>
public static class Ui
{
    public static TextBlock Section(string text, double top = 14) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Margin = new Thickness(0, top, 0, 6) };

    public static TextBlock Note(string text) =>
        new() { Text = text, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    /// <summary>Radio buttons styled as a segmented group.</summary>
    public static WrapPanel Segmented(string[] options, int selected, Action<int> onChange, string? group = null)
    {
        group ??= Guid.NewGuid().ToString("N");
        var panel = new WrapPanel();
        for (var i = 0; i < options.Length; i++)
        {
            var index = i;
            var rb = new RadioButton
            {
                Content = options[i], GroupName = group, IsChecked = i == selected, Margin = new Thickness(0, 2, 16, 2),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            rb.Checked += (_, _) => onChange(index);
            panel.Children.Add(rb);
        }
        return panel;
    }

    public static (Grid row, Slider slider) LabeledSlider(string label, double min, double max, double value, string format,
        Action<double>? onChange = null, double tick = 0)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
        if (tick > 0) { slider.TickFrequency = tick; slider.IsSnapToTickEnabled = true; }
        var val = new TextBlock { Text = value.ToString(format, CultureInfo.CurrentCulture), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Opacity = 0.8 };
        slider.ValueChanged += (_, e) =>
        {
            val.Text = e.NewValue.ToString(format, CultureInfo.CurrentCulture);
            onChange?.Invoke(e.NewValue);
        };
        // double-click on the label resets the slider
        name.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) slider.Value = value; };
        Grid.SetColumn(slider, 1); Grid.SetColumn(val, 2);
        grid.Children.Add(name); grid.Children.Add(slider); grid.Children.Add(val);
        return (grid, slider);
    }

    public static Grid Field(string label, UIElement input, double labelWidth = 130)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn((FrameworkElement)input, 1);
        grid.Children.Add(name); grid.Children.Add(input);
        return grid;
    }

    public static Border Card(UIElement child, Thickness? padding = null) => new()
    {
        Child = child, CornerRadius = new CornerRadius(8), Padding = padding ?? new Thickness(14),
        Background = Theme.IsDark ? Theme.Brush(255, 255, 255, 10) : Theme.Brush(255, 255, 255, 180),
        BorderBrush = Theme.IsDark ? Theme.Brush(255, 255, 255, 18) : Theme.Brush(0, 0, 0, 18), BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 4, 0, 4)
    };

    public static Button IconButton(string glyph, string tip, Action click, double size = 32)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = Theme.IconFont, FontSize = 14 }, Width = size, Height = size,
            Padding = new Thickness(0), ToolTip = tip, Margin = new Thickness(2)
        };
        b.Click += (_, _) => click();
        return b;
    }

    public static ToggleButton ToolToggle(string glyph, string tip, string group, List<ToggleButton> all, Action onSelect)
    {
        var t = new ToggleButton
        {
            Content = new TextBlock { Text = glyph, FontFamily = Theme.IconFont, FontSize = 15 }, Width = 36, Height = 34,
            Padding = new Thickness(0), ToolTip = tip, Margin = new Thickness(2), Tag = group
        };
        all.Add(t);
        t.Checked += (_, _) =>
        {
            foreach (var o in all) if (o != t) o.IsChecked = false;
            onSelect();
        };
        t.Unchecked += (_, _) => { if (all.All(o => o.IsChecked != true)) t.IsChecked = true; };
        return t;
    }

    public static readonly Color[] Palette =
    {
        Color.FromRgb(0xF2, 0x6B, 0x1D), Color.FromRgb(0xE5, 0x39, 0x35), Color.FromRgb(0xFD, 0xD8, 0x35), Color.FromRgb(0x43, 0xA0, 0x47),
        Color.FromRgb(0x1E, 0x88, 0xE5), Color.FromRgb(0x8E, 0x24, 0xAA), Color.FromRgb(0x21, 0x21, 0x21), Color.FromRgb(0xFF, 0xFF, 0xFF)
    };

    public static WrapPanel ColorPicker(Color initial, Action<Color> onPick, Color[]? palette = null)
    {
        var panel = new WrapPanel();
        var swatches = new List<Border>();
        foreach (var c in palette ?? Palette)
        {
            var color = c;
            var sw = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(2), Cursor = Cursors.Hand,
                Background = new SolidColorBrush(color), BorderThickness = new Thickness(color == initial ? 3 : 1),
                BorderBrush = color == initial ? Theme.Brush(Theme.AccentDark) : Theme.Brush(0x80, 0x80, 0x80, 0x80), ToolTip = $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            };
            sw.MouseLeftButtonUp += (_, _) =>
            {
                foreach (var s in swatches) { s.BorderThickness = new Thickness(1); s.BorderBrush = Theme.Brush(0x80, 0x80, 0x80, 0x80); }
                sw.BorderThickness = new Thickness(3); sw.BorderBrush = Theme.Brush(Theme.AccentDark);
                onPick(color);
            };
            swatches.Add(sw);
            panel.Children.Add(sw);
        }
        var custom = new Button { Content = "…", Width = 28, Height = 26, Padding = new Thickness(0), Margin = new Thickness(4, 2, 2, 2), ToolTip = L.T("Another color") };
        custom.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(initial.R, initial.G, initial.B) };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                foreach (var s in swatches) { s.BorderThickness = new Thickness(1); s.BorderBrush = Theme.Brush(0x80, 0x80, 0x80, 0x80); }
                onPick(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
            }
        };
        panel.Children.Add(custom);
        return panel;
    }

    public static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

    public static string Time(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.ff");
    }
}
