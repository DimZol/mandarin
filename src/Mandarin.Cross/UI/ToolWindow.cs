using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Mandarin.Core;

namespace Mandarin.UI;

/// <summary>Base tool window: header, content, buttons at the bottom.</summary>
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
        FontFamily = Look.UiFont;
        Icon = App.WindowIcon;
        Background = Look.WindowBackground;

        var header = new StackPanel { Margin = new Thickness(24, 18, 24, 10) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold });
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

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Opened += (_, _) =>
        {
            Motion.Entrance(header, 0, 10);
            if (Body.Content is StackPanel form) Motion.Stagger(form, 22, 60, 10);
            else if (Body.Content is Visual body) Motion.Entrance(body, 60, 14);
            Motion.Entrance(FooterBar, 160, 8);
        };
    }

    protected Button AddButton(string text, Action onClick, bool primary = false)
    {
        var b = new Button { Content = text, MinWidth = 110, Padding = new Thickness(16, 6, 16, 7), Margin = new Thickness(8, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        if (primary)
        {
            b.Classes.Add("accent");
            b.IsDefault = true;
        }
        b.Click += (_, _) => onClick();
        Motion.Press(b);
        Footer.Children.Add(b);
        return b;
    }

    protected void AddCancel() => AddButton(L.T("Cancel"), Close);

    protected void AddFooterLeft(Control element)
    {
        DockPanel.SetDock(element, Dock.Left);
        FooterBar.Children.Add(element);
    }

    /// <summary>A message box over this window.</summary>
    public Task ShowMessage(string text) => Ui.Message(this, text);
}

/// <summary>Small element factories so all tool windows look alike.</summary>
public static class Ui
{
    public static TextBlock Section(string text, double top = 14) =>
        new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 13.5, Margin = new Thickness(0, top, 0, 6) };

    public static TextBlock Note(string text) =>
        new() { Text = text, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    public static WrapPanel Segmented(string[] options, int selected, Action<int> onChange, string? group = null)
    {
        group ??= Guid.NewGuid().ToString("N");
        var panel = new WrapPanel();
        for (var i = 0; i < options.Length; i++)
        {
            var index = i;
            var rb = new RadioButton
            {
                Content = options[i], GroupName = group, IsChecked = i == selected, Margin = new Thickness(0, 2, 16, 2), VerticalContentAlignment = VerticalAlignment.Center
            };
            rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true) onChange(index); };
            panel.Children.Add(rb);
        }
        return panel;
    }

    public static (Grid row, Slider slider) LabeledSlider(string label, double min, double max, double value, string format,
        Action<double>? onChange = null, double tick = 0)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1), ColumnDefinitions = new ColumnDefinitions("130,*,56") };
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center };
        if (tick > 0) { slider.TickFrequency = tick; slider.IsSnapToTickEnabled = true; }
        var val = new TextBlock { Text = value.ToString(format, CultureInfo.CurrentCulture), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Opacity = 0.8 };
        slider.ValueChanged += (_, e) =>
        {
            val.Text = e.NewValue.ToString(format, CultureInfo.CurrentCulture);
            onChange?.Invoke(e.NewValue);
        };
        // double-click on the label resets the slider
        name.DoubleTapped += (_, _) => slider.Value = value;
        Grid.SetColumn(slider, 1); Grid.SetColumn(val, 2);
        grid.Children.Add(name); grid.Children.Add(slider); grid.Children.Add(val);
        return (grid, slider);
    }

    public static Grid Field(string label, Control input, double labelWidth = 130)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4), ColumnDefinitions = new ColumnDefinitions($"{labelWidth.ToString(CultureInfo.InvariantCulture)},*") };
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(input, 1);
        grid.Children.Add(name); grid.Children.Add(input);
        return grid;
    }

    public static Border Card(Control child, Thickness? padding = null) => new()
    {
        Child = child, CornerRadius = new CornerRadius(8), Padding = padding ?? new Thickness(14),
        Background = Look.IsDark ? Look.Brush(255, 255, 255, 10) : Look.Brush(255, 255, 255, 180),
        BorderBrush = Look.IsDark ? Look.Brush(255, 255, 255, 18) : Look.Brush(0, 0, 0, 18), BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 4, 0, 4)
    };

    public static Button IconButton(string glyph, string tip, Action click, double size = 32)
    {
        var b = new Button
        {
            Content = Icons.Make(glyph, size * 0.5), Width = size, Height = size, Padding = new Thickness(0), Margin = new Thickness(2),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        Motion.Press(b);
        return b;
    }

    public static ToggleButton ToolToggle(string glyph, string tip, List<ToggleButton> all, Action onSelect)
    {
        var t = new ToggleButton
        {
            Content = Icons.Make(glyph, 17), Width = 38, Height = 34, Padding = new Thickness(0), Margin = new Thickness(2),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(t, tip);
        all.Add(t);
        t.IsCheckedChanged += (_, _) =>
        {
            if (t.IsChecked == true)
            {
                foreach (var o in all) if (o != t) o.IsChecked = false;
                onSelect();
            }
            else if (all.All(o => o.IsChecked != true)) t.IsChecked = true;
        };
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
        var ring = Look.Brush(Look.AccentDark);
        var plain = Look.Brush(0x80, 0x80, 0x80, 0x80);
        void Mark(Border? chosen)
        {
            foreach (var s in swatches) { s.BorderThickness = new Thickness(1); s.BorderBrush = plain; }
            if (chosen != null) { chosen.BorderThickness = new Thickness(3); chosen.BorderBrush = ring; }
        }
        foreach (var c in palette ?? Palette)
        {
            var color = c;
            var sw = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(2), Cursor = new Cursor(StandardCursorType.Hand),
                Background = new SolidColorBrush(color)
            };
            ToolTip.SetTip(sw, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
            sw.PointerReleased += (_, _) =>
            {
                Mark(sw);
                Motion.ScaleTo(sw, 0.8, 1, 300, Motion.Spring);
                onPick(color);
            };
            sw.PointerEntered += (_, _) => Motion.ScaleTo(sw, Motion.CurrentScale(sw), 1.15, 180, Motion.Spring);
            sw.PointerExited += (_, _) => Motion.ScaleTo(sw, Motion.CurrentScale(sw), 1, 200);
            swatches.Add(sw);
            panel.Children.Add(sw);
        }
        Mark(swatches.FirstOrDefault(s => s.Background is SolidColorBrush b && b.Color == initial));
        var custom = new Button { Content = "…", Width = 28, Height = 26, Padding = new Thickness(0), Margin = new Thickness(4, 2, 2, 2), HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(custom, L.T("Another color"));
        custom.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(custom) is not Window owner) return;
            if (await ColorDialog.Pick(owner, initial) is { } c)
            {
                Mark(null);
                onPick(c);
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

    /// <summary>A simple message box.</summary>
    public static async Task Message(Window? owner, string text)
    {
        var w = new Window
        {
            Title = "Mandarin", Width = 420, SizeToContent = SizeToContent.Height, CanResize = false, Icon = App.WindowIcon,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen, FontFamily = Look.UiFont
        };
        var ok = new Button { Content = "OK", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => w.Close();
        w.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 16,
            Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, ok }
        };
        if (owner != null) await w.ShowDialog(owner);
        else w.Show();
    }

    /// <summary>A yes/no question.</summary>
    public static async Task<bool> Ask(Window? owner, string text)
    {
        var w = new Window
        {
            Title = "Mandarin", Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, Icon = App.WindowIcon,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen, FontFamily = Look.UiFont
        };
        var answer = false;
        var yes = new Button { Content = L.T("Yes"), MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        yes.Classes.Add("accent");
        var no = new Button { Content = L.T("No"), MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        yes.Click += (_, _) => { answer = true; w.Close(); };
        no.Click += (_, _) => w.Close();
        w.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 16,
            Children =
            {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { yes, no } }
            }
        };
        if (owner != null) await w.ShowDialog(owner);
        else
        {
            var tcs = new TaskCompletionSource();
            w.Closed += (_, _) => tcs.TrySetResult();
            w.Show();
            await tcs.Task;
        }
        return answer;
    }

    public static Image Picture(Bitmap? source, double w, double h) => new() { Source = source, Width = w, Height = h, Stretch = Stretch.Uniform };
}

/// <summary>Picking any color: hue, saturation and brightness sliders and a hex field.</summary>
public static class ColorDialog
{
    public static async Task<Color?> Pick(Window owner, Color initial)
    {
        var hsv = initial.ToHsv();
        double h = hsv.H, s = hsv.S, v = hsv.V;
        Color? result = null;
        var preview = new Border { Height = 46, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 10) };
        var hex = new TextBox { Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
        var updating = false;
        Color Current() => HsvColor.ToRgb(h, s, v);
        void Show()
        {
            preview.Background = new SolidColorBrush(Current());
            updating = true;
            hex.Text = Look.Hex(Current());
            updating = false;
        }
        var (hueRow, hue) = Ui.LabeledSlider(L.T("Hue"), 0, 359, h, "0", x => { h = x; Show(); });
        var (satRow, sat) = Ui.LabeledSlider(L.T("Saturation"), 0, 100, s * 100, "0", x => { s = x / 100; Show(); });
        var (valRow, val) = Ui.LabeledSlider(L.T("Brightness"), 0, 100, v * 100, "0", x => { v = x / 100; Show(); });
        hex.TextChanged += (_, _) =>
        {
            if (updating || !Look.TryParseHex(hex.Text?.Trim().ToUpperInvariant(), out var c)) return;
            var hs = c.ToHsv();
            h = hs.H; s = hs.S; v = hs.V;
            preview.Background = new SolidColorBrush(c);
            hue.Value = h; sat.Value = s * 100; val.Value = v * 100;
        };
        var w = new Window
        {
            Title = L.T("Another color"), Width = 380, SizeToContent = SizeToContent.Height, CanResize = false, Icon = App.WindowIcon,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = Look.UiFont
        };
        var ok = new Button { Content = "OK", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = L.T("Cancel"), MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        ok.Click += (_, _) => { result = Current(); w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                preview, hueRow, satRow, valRow, Ui.Field("HEX", hex, 130),
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 14, 0, 0), Children = { ok, cancel } }
            }
        };
        Show();
        await w.ShowDialog(owner);
        return result;
    }
}

public static class Extensions
{
    public static T Also<T>(this T obj, Action<T> action)
    {
        action(obj);
        return obj;
    }
}
