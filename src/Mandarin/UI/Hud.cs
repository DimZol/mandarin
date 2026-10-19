using System.ComponentModel;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Mandarin.Core;
using Mandarin.Interop;

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
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        FontFamily = Theme.UiFont;
        Content = new Border { Padding = new Thickness(14), Child = _stack };
        SourceInitialized += (_, _) => Native.MakeNoActivate(new WindowInteropHelper(this).Handle);
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
        var (x, y) = Native.CursorPos();
        var (work, scale) = Native.MonitorAt(x, y);
        Left = work.Right / scale - ActualWidth - 4;
        Top = work.Bottom / scale - ActualHeight - 4;
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

    public static void ShowInfo(string title, string message, string glyph = "")
    {
        var job = new Job { Title = title, Glyph = glyph, Inputs = Array.Empty<string>() };
        job.State = JobState.Done;
        job.Detail = message;
        Get().AddCard(job);
        Get().Finished(job, playSound: false);
    }

    void AddCard(Job job)
    {
        var dark = Theme.IsDark;
        var fg = dark ? Theme.Brush(0xF2, 0xF2, 0xF2) : Theme.Brush(0x1F, 0x1F, 0x1F);
        var sub = dark ? Theme.Brush(0xB0, 0xB0, 0xB0) : Theme.Brush(0x5F, 0x5F, 0x5F);

        var icon = new Grid { Width = 44, Height = 44, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        var thumb = job.Inputs.Count > 0 ? ShellThumbs.Get(job.Inputs[0], 96) : null;
        if (thumb != null) icon.Children.Add(new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = new Image { Source = thumb, Stretch = Stretch.Uniform } });
        else icon.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10), Background = Theme.Brush(Theme.Accent),
            Child = new TextBlock { Text = job.Glyph, FontFamily = Theme.IconFont, FontSize = 20, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });

        var title = new TextBlock { Text = job.Title, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Foreground = fg, TextTrimming = TextTrimming.CharacterEllipsis };
        var detail = new TextBlock { FontSize = 12, Foreground = sub, TextWrapping = TextWrapping.Wrap, MaxHeight = 64, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        var bar = new ProgressBar { Height = 4, Margin = new Thickness(0, 8, 0, 2), Minimum = 0, Maximum = 100, Foreground = Theme.Brush(Theme.Accent), BorderThickness = new Thickness(0),
            Background = dark ? Theme.Brush(255, 255, 255, 30) : Theme.Brush(0, 0, 0, 20) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        var close = new Button
        {
            Content = new TextBlock { Text = "", FontFamily = Theme.IconFont, FontSize = 10 }, Width = 26, Height = 26, Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = sub, VerticalAlignment = VerticalAlignment.Top,
            ToolTip = L.T("Hide"), Cursor = Cursors.Hand
        };

        var textCol = new StackPanel();
        textCol.Children.Add(title);
        textCol.Children.Add(detail);
        textCol.Children.Add(bar);
        textCol.Children.Add(buttons);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(textCol, 1); Grid.SetColumn(close, 2);
        grid.Children.Add(icon); grid.Children.Add(textCol); grid.Children.Add(close);

        var card = new Border
        {
            Background = dark ? Theme.Brush(0x2B, 0x2B, 0x2B, 0xF7) : Theme.Brush(0xFF, 0xFF, 0xFF, 0xFA),
            BorderBrush = dark ? Theme.Brush(255, 255, 255, 24) : Theme.Brush(0, 0, 0, 22),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 8, 12),
            Margin = new Thickness(0, 8, 0, 0), Child = grid,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = dark ? 0.5 : 0.18, Color = Colors.Black },
            Opacity = 0, Tag = job
        };

        void Refresh()
        {
            detail.Text = job.Detail;
            detail.Visibility = string.IsNullOrEmpty(job.Detail) ? Visibility.Collapsed : Visibility.Visible;
            bar.Visibility = job.IsFinished ? Visibility.Collapsed : Visibility.Visible;
            bar.IsIndeterminate = job.IsIndeterminate;
            if (!job.IsIndeterminate) bar.Value = job.Percent;
            if (job.State == JobState.Failed) detail.Foreground = Theme.Brush(0xE8, 0x4B, 0x3C);
            buttons.Children.Clear();
            if (job.IsRunning)
                buttons.Children.Add(MakeButton(L.T("Cancel"), () => job.Cancel.Cancel(), false));
            else if (job.HasOutputs)
            {
                var first = job.Outputs[0];
                buttons.Children.Add(MakeButton(Directory.Exists(first) ? L.T("Open folder") : L.T("Open"), () => Integration.Open(first), true));
                buttons.Children.Add(MakeButton(L.T("Show in folder"), () => Integration.RevealInExplorer(first), false));
            }
            buttons.Visibility = buttons.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        PropertyChangedEventHandler handler = (_, _) => Dispatcher.BeginInvoke(Refresh);
        job.PropertyChanged += handler;
        job.Outputs.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(Refresh);
        close.Click += (_, _) => RemoveCard(job);
        Refresh();

        _cards[job] = card;
        _stack.Children.Add(card);
        while (_stack.Children.Count > 5)
        {
            var oldest = _stack.Children.OfType<Border>().FirstOrDefault(b => b.Tag is Job { IsFinished: true });
            if (oldest == null) break;
            RemoveCard((Job)oldest.Tag);
        }
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        if (!IsVisible) Show();
        Reposition();
    }

    Button MakeButton(string text, Action onClick, bool accent)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(0, 0, 8, 0), FontSize = 12,
            Cursor = Cursors.Hand, MinHeight = 0
        };
        if (accent) { b.Background = Theme.Brush(Theme.Accent); b.Foreground = Brushes.White; b.BorderBrush = Theme.Brush(Theme.AccentDark); }
        b.Click += (_, _) => onClick();
        return b;
    }

    void Finished(Job job, bool playSound = true)
    {
        if (!_cards.ContainsKey(job)) return;
        if (playSound && AppSettings.Current.PlaySound)
        {
            if (job.State == JobState.Failed) SystemSounds.Hand.Play();
            else if (job.State == JobState.Done) SystemSounds.Asterisk.Play();
        }
        if (job.State == JobState.Done && AppSettings.Current.RevealAfterConvert && job.Outputs.Count > 0)
            Integration.RevealInExplorer(job.Outputs[0]);
        var delay = job.State == JobState.Failed ? 20 : 7;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_cards.TryGetValue(job, out var c) && c.IsMouseOver) { timer.Start(); return; }
            RemoveCard(job);
        };
        timer.Start();
    }

    void RemoveCard(Job job)
    {
        if (!_cards.Remove(job, out var card)) return;
        var anim = new DoubleAnimation(card.Opacity, 0, TimeSpan.FromMilliseconds(140));
        anim.Completed += (_, _) =>
        {
            _stack.Children.Remove(card);
            if (_stack.Children.Count == 0) Hide();
            else Reposition();
        };
        card.BeginAnimation(OpacityProperty, anim);
    }
}
