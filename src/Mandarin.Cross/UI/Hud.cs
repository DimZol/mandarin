using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mandarin.Core;
using Mandarin.Platform;

namespace Mandarin.UI;

/// <summary>Progress cards in the bottom-right corner: what is converting, whether it is done, where the result is.</summary>
public sealed class Hud : Window
{
    static Hud? _instance;
    readonly StackPanel _stack = new() { VerticalAlignment = VerticalAlignment.Bottom };
    readonly Dictionary<Job, Border> _cards = new();

    Hud()
    {
        Title = L.T("Mandarin - tasks");
#if AVALONIA12
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
#else
        SystemDecorations = SystemDecorations.None;
#endif
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        TransparencyBackgroundFallback = Brushes.Transparent;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        CanResize = false;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        FontFamily = Look.UiFont;
        RenderOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);
        Content = new Border { Padding = new Thickness(14), Child = _stack };
        SizeChanged += (_, _) => Reposition();
    }

    public static void Init()
    {
        JobManager.JobStarted += job => Get().AddCard(job);
        JobManager.JobFinished += job => Get().Finished(job);
    }

    static Hud Get()
    {
        if (_instance == null)
        {
            _instance = new Hud();
            _instance.Show();
        }
        else if (!_instance.IsVisible) _instance.Show();
        return _instance;
    }

    void Reposition()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) return;
        if (Desktop.CursorPos() is { } c && Screens.ScreenFromPoint(new PixelPoint(c.x, c.y)) is { } under) screen = under;
        var work = screen.WorkingArea;
        var scale = screen.Scaling;
        var w = (int)Math.Round(Bounds.Width * scale);
        var h = (int)Math.Round(Bounds.Height * scale);
        Position = new PixelPoint(work.Right - w - 4, work.Bottom - h - 4);
    }

    public static void ShowError(string title, string message)
    {
        var job = new Job { Title = title, Glyph = "", Inputs = Array.Empty<string>() };
        job.State = JobState.Failed;
        job.Error = message;
        job.Detail = message;
        Get().AddCard(job);
        Get().Finished(job);
    }

    public static void ShowInfo(string title, string message, string glyph = "")
    {
        var job = new Job { Title = title, Glyph = glyph, Inputs = Array.Empty<string>() };
        job.State = JobState.Done;
        job.Detail = message;
        Get().AddCard(job);
        Get().Finished(job, playSound: false);
    }

    void AddCard(Job job)
    {
        var dark = Look.IsDark;
        var fg = dark ? Look.Brush(0xF2, 0xF2, 0xF2) : Look.Brush(0x1F, 0x1F, 0x1F);
        var sub = dark ? Look.Brush(0xB0, 0xB0, 0xB0) : Look.Brush(0x5F, 0x5F, 0x5F);

        var icon = new Grid { Width = 44, Height = 44, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        var glyphBox = new Border
        {
            CornerRadius = new CornerRadius(10), Background = Look.Brush(Look.Accent),
            Child = new Viewbox { Width = 22, Height = 22, Child = Icons.Make(job.Glyph, 22, Look.OnAccent) }
        };
        icon.Children.Add(glyphBox);
        if (job.Inputs.Count > 0)
            _ = LoadThumb(job.Inputs[0], icon);

        var title = new TextBlock { Text = job.Title, FontWeight = FontWeight.SemiBold, FontSize = 13.5, Foreground = fg, TextTrimming = TextTrimming.CharacterEllipsis };
        var detail = new TextBlock { FontSize = 12, Foreground = sub, TextWrapping = TextWrapping.Wrap, MaxHeight = 64, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        var bar = new ProgressBar
        {
            Height = 4, MinHeight = 4, Margin = new Thickness(0, 8, 0, 2), Minimum = 0, Maximum = 100, Foreground = Look.Brush(Look.Accent),
            Background = dark ? Look.Brush(255, 255, 255, 30) : Look.Brush(0, 0, 0, 20)
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        var close = new Button
        {
            Content = Icons.Make("", 12, sub), Width = 26, Height = 26, Padding = new Thickness(0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Top, Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(close, L.T("Hide"));

        var textCol = new StackPanel { Children = { title, detail, bar, buttons } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(textCol, 1); Grid.SetColumn(close, 2);
        grid.Children.Add(icon); grid.Children.Add(textCol); grid.Children.Add(close);

        var card = new Border
        {
            Background = dark ? Look.Brush(0x2B, 0x2B, 0x2B, 0xF7) : Look.Brush(0xFF, 0xFF, 0xFF, 0xFA),
            BorderBrush = dark ? Look.Brush(255, 255, 255, 24) : Look.Brush(0, 0, 0, 22),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 8, 12),
            Margin = new Thickness(0, 8, 0, 0), Child = grid, Tag = job,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, OffsetY = 3, Color = Color.FromArgb((byte)(dark ? 110 : 45), 0, 0, 0) })
        };

        var wasFinished = false;
        var initial = true;
        void Refresh()
        {
            detail.Text = job.Detail;
            detail.IsVisible = !string.IsNullOrEmpty(job.Detail);
            bar.IsVisible = !job.IsFinished;
            bar.IsIndeterminate = job.IsIndeterminate;
            if (!job.IsIndeterminate) Motion.SmoothValue(bar, job.Percent);
            if (job.State == JobState.Failed) detail.Foreground = Look.Brush(Color.FromRgb(0xE8, 0x4B, 0x3C));
            if (job.IsFinished && !wasFinished)
            {
                wasFinished = true;
                if (job.State == JobState.Done) AddBadge(icon, "", Look.Accent);
                else if (job.State == JobState.Failed)
                {
                    AddBadge(icon, "", Color.FromRgb(0xE8, 0x4B, 0x3C));
                    if (!initial) Motion.Shake(card);
                }
            }
            buttons.Children.Clear();
            if (job.IsRunning)
                buttons.Children.Add(MakeButton(L.T("Cancel"), () => job.Cancel.Cancel(), false));
            else if (job.HasOutputs)
            {
                var first = job.Outputs[0];
                buttons.Children.Add(MakeButton(Directory.Exists(first) ? L.T("Open folder") : L.T("Open"), () => Desktop.Open(first), true));
                buttons.Children.Add(MakeButton(L.T("Show in folder"), () => Desktop.Reveal(first), false));
            }
            buttons.IsVisible = buttons.Children.Count > 0;
        }

        PropertyChangedEventHandler handler = (_, _) => Dispatcher.UIThread.Post(Refresh);
        job.PropertyChanged += handler;
        job.Outputs.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        close.Click += (_, _) => RemoveCard(job);
        Refresh();
        initial = false;

        _cards[job] = card;
        _stack.Children.Add(card);
        while (_stack.Children.Count > 5)
        {
            var oldest = _stack.Children.OfType<Border>().FirstOrDefault(b => b.Tag is Job { IsFinished: true });
            if (oldest == null) break;
            RemoveCard((Job)oldest.Tag!);
        }
        Motion.Entrance(card, 0, 0, 340, 56);
        if (!IsVisible) Show();
        Dispatcher.UIThread.Post(Reposition, DispatcherPriority.Loaded);
    }

    static async Task LoadThumb(string path, Grid icon)
    {
        var bmp = await Thumbs.GetAsync(path, 96);
        if (bmp == null) return;
        icon.Children.Insert(1, new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Background = Look.Brush(0x80, 0x80, 0x80, 0x20), Child = new Image { Source = bmp, Stretch = Stretch.Uniform } });
        icon.Children[0].IsVisible = false;
    }

    static void AddBadge(Grid icon, string glyph, Color color)
    {
        var badge = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Look.Brush(color),
            BorderBrush = Look.IsDark ? Look.Brush(0x2B, 0x2B, 0x2B) : Brushes.White, BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -5, -5),
            Child = Icons.Make(glyph, 9, color == Look.Accent ? Look.OnAccent : Brushes.White)
        };
        icon.Children.Add(badge);
        Motion.Pop(badge, 0.2, 0, 420);
        Motion.ScaleTo(icon, 0.86, 1, 380, Motion.Spring);
    }

    static Button MakeButton(string text, Action onClick, bool accent)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(0, 0, 8, 0), FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand), MinHeight = 0 };
        if (accent) b.Classes.Add("accent");
        b.Click += (_, _) => onClick();
        return b;
    }

    [DllImport("user32.dll")] static extern bool MessageBeep(uint type);

    static void Sound(bool error)
    {
        try
        {
            if (OsInfo.IsWindows) MessageBeep(error ? 0x10u : 0x40u);
            else if (Desktop.Which("canberra-gtk-play") is { } play)
                Process.Start(new ProcessStartInfo(play) { ArgumentList = { "-i", error ? "dialog-error" : "complete" }, UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch { /* no sound */ }
    }

    void Finished(Job job, bool playSound = true)
    {
        if (!_cards.ContainsKey(job)) return;
        if (playSound && AppSettings.Current.PlaySound && job.State is JobState.Failed or JobState.Done) Sound(job.State == JobState.Failed);
        if (job.State == JobState.Done && AppSettings.Current.RevealAfterConvert && job.Outputs.Count > 0) Desktop.Reveal(job.Outputs[0]);
        var delay = job.State == JobState.Failed ? 20 : 7;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_cards.TryGetValue(job, out var c) && c.IsPointerOver) { timer.Start(); return; }
            RemoveCard(job);
        };
        timer.Start();
    }

    void RemoveCard(Job job)
    {
        if (!_cards.Remove(job, out var card)) return;
        card.IsHitTestVisible = false;
        void Detach()
        {
            _stack.Children.Remove(card);
            if (_stack.Children.Count == 0) Hide();
            else Dispatcher.UIThread.Post(Reposition, DispatcherPriority.Loaded);
        }
        if (!Motion.Enabled) { Detach(); return; }
        Motion.MoveX(card, 0, 60, 200, Motion.EaseInOut);
        Motion.Opacity(card, card.Opacity, 0, 200, Motion.EaseInOut, 0, Detach);
    }
}
