using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mandarin.Core;
using Mandarin.Engines;

namespace Mandarin.UI.Tools;

/// <summary>Shared by media editors: player, proxy for unsupported formats, timeline, transport.</summary>
public abstract class MediaToolBase : ToolWindow
{
    protected readonly string File;
    protected readonly MediaElement Player = new()
    {
        LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, ScrubbingEnabled = true, Stretch = Stretch.Fill, Volume = 0.85
    };
    protected readonly Timeline Timeline = new();
    protected readonly Grid VideoSurface = new();
    protected readonly Viewbox VideoBox = new() { Stretch = Stretch.Uniform };
    protected MediaInfo Info = null!;
    protected bool IsVideo;
    protected bool Playing;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    readonly Button _play;
    protected readonly StackPanel ModePanel = new() { Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBlock _loading = new() { Text = L.T("Loading…"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
    string? _proxyDir;
    bool _triedProxy;

    protected double Duration => Info?.Duration ?? 0;
    protected double Now => Player.Position.TotalSeconds;

    protected MediaToolBase(string title, string file, double width = 1100, double height = 780) : base(title, Path.GetFileName(file), width, height)
    {
        File = file;
        ResizeMode = ResizeMode.CanResize;
        VideoSurface.Children.Add(Player);
        VideoBox.Child = VideoSurface;

        _play = Ui.IconButton("", L.T("Play / pause (Space)"), TogglePlay, 40);
        var transport = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        transport.Children.Add(Ui.IconButton("", L.T("Previous frame (←)"), () => Step(-1)));
        transport.Children.Add(_play);
        transport.Children.Add(Ui.IconButton("", L.T("Next frame (→)"), () => Step(1)));
        transport.Children.Add(_time);

        var videoHost = new Border
        {
            CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = Theme.Brush(0, 0, 0, Theme.IsDark ? (byte)120 : (byte)230),
            Child = new Grid { Children = { VideoBox, _loading } }
        };

        var dock = new DockPanel();
        var bottom = new StackPanel();
        bottom.Children.Add(Timeline);
        bottom.Children.Add(transport);
        bottom.Children.Add(ModePanel);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(videoHost);
        Body.Content = dock;
        Timeline.Margin = new Thickness(0, 10, 0, 0);

        Timeline.Seek += t => SeekTo(t);
        _tick.Tick += (_, _) => OnTick();
        Player.MediaOpened += (_, _) => { _loading.Visibility = Visibility.Collapsed; SeekTo(0.001); };
        Player.MediaFailed += async (_, e) => await FallbackToProxy(e.ErrorException);
        PreviewKeyDown += OnKey;
        Closed += (_, _) =>
        {
            _tick.Stop();
            Player.Close();
            if (_proxyDir != null) OutputPaths.TryDelete(_proxyDir);
        };
        Loaded += async (_, _) => await InitAsync();
    }

    async Task InitAsync()
    {
        try
        {
            Info = await Ffmpeg.ProbeAsync(File, CancellationToken.None);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Mandarin", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        IsVideo = Info.HasVideo;
        Timeline.Duration = Math.Max(0.01, Info.Duration);
        Timeline.Out = Timeline.Duration;
        if (IsVideo)
        {
            VideoSurface.Width = Math.Max(2, Info.Width);
            VideoSurface.Height = Math.Max(2, Info.Height);
        }
        else
        {
            VideoBox.Visibility = Visibility.Collapsed;
            _loading.Text = "♪  " + Path.GetFileName(File);
            Timeline.Height = 180;
        }
        OnInfoReady();
        Player.Source = new Uri(File);
        Player.Pause();
        _tick.Start();
        if (Info.HasAudio)
        {
            try
            {
                Timeline.Peaks = await Ffmpeg.ReadPeaksAsync(File, 3000, CancellationToken.None);
                Timeline.Refresh();
                OnPeaksReady();
            }
            catch { /* the waveform is optional */ }
        }
    }

    async Task FallbackToProxy(Exception? ex)
    {
        if (_triedProxy)
        {
            _loading.Text = L.T("Preview is unavailable, but editing works");
            return;
        }
        _triedProxy = true;
        _loading.Visibility = Visibility.Visible;
        _loading.Text = L.T("Preparing the preview…");
        try
        {
            var proxy = await MediaEngine.MakePreviewProxyAsync(File, IsVideo, CancellationToken.None,
                p => Dispatcher.BeginInvoke(() => _loading.Text = L.F("Preparing the preview… {0:P0}", p)));
            _proxyDir = Path.GetDirectoryName(proxy);
            Player.Source = new Uri(proxy);
            Player.Pause();
        }
        catch (Exception e2)
        {
            Log.Error("Preview proxy", e2);
            _loading.Text = L.T("Preview is unavailable, but editing works");
        }
    }

    protected virtual void OnInfoReady() { }
    protected virtual void OnPeaksReady() { }
    protected virtual void OnTickExtra() { }

    void OnTick()
    {
        Timeline.Position = Now;
        Timeline.Refresh();
        _time.Text = $"{Ui.Time(Now)} / {Ui.Time(Duration)}";
        OnTickExtra();
    }

    protected void SeekTo(double t)
    {
        Player.Position = TimeSpan.FromSeconds(Math.Clamp(t, 0, Duration));
        Timeline.Position = t;
        Timeline.Refresh();
    }

    protected void TogglePlay()
    {
        if (Playing) Pause();
        else Play();
    }

    protected virtual void Play()
    {
        Player.Play();
        Playing = true;
        ((TextBlock)_play.Content).Text = "";
    }

    protected void Pause()
    {
        Player.Pause();
        Playing = false;
        ((TextBlock)_play.Content).Text = "";
    }

    void Step(int frames)
    {
        Pause();
        var fps = Info?.Fps > 0 ? Info.Fps : 30;
        SeekTo(Now + frames / fps);
    }

    protected virtual void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); e.Handled = true; break;
            case Key.Left: if (Keyboard.Modifiers == ModifierKeys.Shift) SeekTo(Now - 1); else Step(-1); e.Handled = true; break;
            case Key.Right: if (Keyboard.Modifiers == ModifierKeys.Shift) SeekTo(Now + 1); else Step(1); e.Handled = true; break;
            case Key.Delete: Timeline.DeleteSelected(); e.Handled = true; break;
        }
    }

    protected static Button TextButton(string text, Action click, string? tip = null)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 5, 12, 6), Margin = new Thickness(0, 0, 8, 0), ToolTip = tip };
        b.Click += (_, _) => click();
        return b;
    }
}

public enum MediaEditMode { Trim, Split, Snapshot, Speed, Bleep }

/// <summary>Trim, split, frame snapshots, speed and bleep on a shared timeline.</summary>
public sealed class MediaEditorWindow : MediaToolBase
{
    readonly MediaEditMode _mode;
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8, Margin = new Thickness(4, 0, 0, 0) };
    bool _fastTrim;
    double _speed = 1;

    static string TitleFor(MediaEditMode m) => m switch
    {
        MediaEditMode.Trim => L.T("Trim"),
        MediaEditMode.Split => L.T("Split into parts"),
        MediaEditMode.Snapshot => L.T("Save frames"),
        MediaEditMode.Speed => L.T("Playback speed"),
        _ => L.T("Bleep fragments")
    };

    public MediaEditorWindow(string file, MediaEditMode mode) : base(TitleFor(mode), file)
    {
        _mode = mode;
        Timeline.EditMode = mode switch
        {
            MediaEditMode.Trim => Timeline.Mode.Range,
            MediaEditMode.Split or MediaEditMode.Snapshot => Timeline.Mode.Markers,
            MediaEditMode.Bleep => Timeline.Mode.Ranges,
            _ => Timeline.Mode.Plain
        };
        Timeline.Changed += UpdateStatus;
        BuildModePanel();
        AddCancel();
        AddButton(mode switch
        {
            MediaEditMode.Trim => L.T("Save the fragment"),
            MediaEditMode.Split => L.T("Split"),
            MediaEditMode.Snapshot => L.T("Save frames"),
            MediaEditMode.Speed => L.T("Save"),
            _ => L.T("Save with bleeps")
        }, Save, true);
    }

    void BuildModePanel()
    {
        var row = new WrapPanel();
        switch (_mode)
        {
            case MediaEditMode.Trim:
                row.Children.Add(TextButton(L.T("Start here"), () => { Timeline.In = Math.Min(Now, Timeline.Out - 0.05); UpdateStatus(); Timeline.Refresh(); }, L.T("Key I")));
                row.Children.Add(TextButton(L.T("End here"), () => { Timeline.Out = Math.Max(Now, Timeline.In + 0.05); UpdateStatus(); Timeline.Refresh(); }, L.T("Key O")));
                row.Children.Add(TextButton(L.T("Remove silence at the ends"), TrimSilence, L.T("Pauses inside the recording are kept")));
                var fast = new CheckBox { Content = L.T("Fast, without re-encoding (at keyframes)"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) };
                fast.Checked += (_, _) => _fastTrim = true; fast.Unchecked += (_, _) => _fastTrim = false;
                row.Children.Add(fast);
                break;
            case MediaEditMode.Split:
                row.Children.Add(TextButton(L.T("Cut here"), AddMarker, L.T("Key S")));
                row.Children.Add(TextButton(L.T("Delete selected"), () => Timeline.DeleteSelected(), "Delete"));
                break;
            case MediaEditMode.Snapshot:
                row.Children.Add(TextButton(L.T("Snapshot of this frame"), AddMarker, L.T("Key S")));
                row.Children.Add(TextButton(L.T("Delete selected"), () => Timeline.DeleteSelected(), "Delete"));
                break;
            case MediaEditMode.Speed:
                var (sliderRow, slider) = Ui.LabeledSlider(L.T("Speed"), 0.25, 4, 1, "0.##'×'", v => { _speed = v; Player.SpeedRatio = v; UpdateStatus(); }, 0.05);
                sliderRow.Width = 520;
                row.Children.Add(sliderRow);
                foreach (var s in new[] { 0.5, 0.75, 1, 1.25, 1.5, 2 })
                    row.Children.Add(TextButton(s.ToString("0.##", CultureInfo.CurrentCulture) + "×", () => slider.Value = s));
                break;
            case MediaEditMode.Bleep:
                row.Children.Add(TextButton(L.T("Mark 1 second here"), () =>
                {
                    Timeline.Ranges.Add((Now, Math.Min(Duration, Now + 1)));
                    UpdateStatus(); Timeline.Refresh();
                }));
                row.Children.Add(TextButton(L.T("Delete selected"), () => Timeline.DeleteSelected(), "Delete"));
                break;
        }
        row.Children.Add(_status);
        ModePanel.Children.Add(row);
        ModePanel.Children.Add(Ui.Note(_mode switch
        {
            MediaEditMode.Trim => L.T("Drag the orange handles on the timeline. Space plays, ←/→ steps by frame, Shift+←/→ by second."),
            MediaEditMode.Split => L.T("Place cut points - every part is saved as a separate file in a new folder."),
            MediaEditMode.Snapshot => L.T("Find the frames you need and mark them. Frames are saved as full-resolution PNG."),
            MediaEditMode.Speed => L.T("The sound is sped up or slowed down without changing its pitch."),
            _ => L.T("Drag across the waveform to mark a fragment. Marked parts are replaced with a bleep. In the preview they are muted.")
        }));
    }

    protected override void OnInfoReady()
    {
        if (_mode == MediaEditMode.Snapshot && !IsVideo) Close();
        UpdateStatus();
    }

    protected override void OnPeaksReady() => UpdateStatus();

    protected override void OnTickExtra()
    {
        if (!Playing) return;
        if (_mode == MediaEditMode.Trim && Now >= Timeline.Out) { Pause(); SeekTo(Timeline.Out); }
        if (_mode == MediaEditMode.Bleep)
        {
            var t = Now;
            Player.IsMuted = Timeline.Ranges.Any(r => t >= r.start && t <= r.end);
        }
    }

    protected override void Play()
    {
        if (_mode == MediaEditMode.Trim && (Now < Timeline.In - 0.05 || Now >= Timeline.Out - 0.05)) SeekTo(Timeline.In);
        base.Play();
    }

    protected override void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (_mode == MediaEditMode.Trim && e.Key == Key.I) { Timeline.In = Math.Min(Now, Timeline.Out - 0.05); UpdateStatus(); e.Handled = true; return; }
        if (_mode == MediaEditMode.Trim && e.Key == Key.O) { Timeline.Out = Math.Max(Now, Timeline.In + 0.05); UpdateStatus(); e.Handled = true; return; }
        if (_mode is MediaEditMode.Split or MediaEditMode.Snapshot && e.Key == Key.S) { AddMarker(); e.Handled = true; return; }
        base.OnKey(sender, e);
    }

    void AddMarker()
    {
        var t = Now;
        if (Timeline.Markers.Any(m => Math.Abs(m - t) < 0.02)) return;
        Timeline.Markers.Add(t);
        Timeline.Markers.Sort();
        Timeline.Refresh();
        UpdateStatus();
    }

    void TrimSilence()
    {
        if (Timeline.Peaks == null) { _status.Text = L.T("The waveform is still loading…"); return; }
        var (s, e) = MediaEngine.DetectSilenceBounds(Timeline.Peaks, Duration);
        Timeline.In = s; Timeline.Out = e;
        Timeline.Refresh();
        UpdateStatus();
    }

    void UpdateStatus()
    {
        _status.Text = _mode switch
        {
            MediaEditMode.Trim => L.F("Fragment: {0} - {1}  ({2})", Ui.Time(Timeline.In), Ui.Time(Timeline.Out), Ui.Time(Timeline.Out - Timeline.In)),
            MediaEditMode.Split => L.F("Parts: {0}", Timeline.Markers.Count(m => m > 0.05 && m < Duration - 0.05) + 1),
            MediaEditMode.Snapshot => L.F("Frames: {0}", Timeline.Markers.Count),
            MediaEditMode.Speed => L.F("New duration: {0}", Ui.Time(Duration / _speed)),
            _ => L.F("Fragments: {0}", Timeline.Ranges.Count)
        };
    }

    void Save()
    {
        var file = File;
        var name = Path.GetFileName(file);
        switch (_mode)
        {
            case MediaEditMode.Trim:
                var (a, b, exact) = (Timeline.In, Timeline.Out, !_fastTrim || !IsVideo);
                JobManager.Run(L.F("{0} - trim", name), "", new[] { file }, ctx => MediaEngine.TrimAsync(file, a, b, exact, ctx));
                break;
            case MediaEditMode.Split:
                if (Timeline.Markers.Count == 0) { _status.Text = L.T("Add at least one cut"); return; }
                var pts = Timeline.Markers.ToList();
                JobManager.Run(L.F("{0} - split", name), "", new[] { file }, ctx => MediaEngine.SplitAsync(file, pts, ctx));
                break;
            case MediaEditMode.Snapshot:
                var times = Timeline.Markers.Count > 0 ? Timeline.Markers.ToList() : new List<double> { Now };
                JobManager.Run(L.F("{0} - frames", name), "", new[] { file }, ctx => MediaEngine.SnapshotsAsync(file, times, ctx));
                break;
            case MediaEditMode.Speed:
                if (Math.Abs(_speed - 1) < 0.001) { Close(); return; }
                var speed = _speed;
                JobManager.Run($"{name} — {speed.ToString("0.##", CultureInfo.CurrentCulture)}×", "", new[] { file }, ctx => MediaEngine.SpeedAsync(file, speed, ctx));
                break;
            case MediaEditMode.Bleep:
                if (Timeline.Ranges.Count == 0) { _status.Text = L.T("Mark at least one fragment"); return; }
                var ranges = Timeline.Ranges.ToList();
                JobManager.Run(L.F("{0} - bleep", name), "", new[] { file }, ctx => MediaEngine.BleepAsync(file, ranges, ctx));
                break;
        }
        Close();
    }
}

/// <summary>Video cropping and area redaction - frames drawn over the video.</summary>
public sealed class VideoOverlayWindow : MediaToolBase
{
    readonly bool _redact;
    readonly CropOverlay _crop = new();
    readonly RegionsOverlay _regions = new();
    readonly TextBlock _status = new() { Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
    bool _blur = true;
    Color _color = Colors.Black;

    public VideoOverlayWindow(string file, bool redact) : base(redact ? L.T("Redact an area in the video") : L.T("Crop the video"), file)
    {
        _redact = redact;
        Timeline.EditMode = Timeline.Mode.Plain;
        var row = new WrapPanel();
        if (redact)
        {
            VideoSurface.Children.Add(_regions);
            _regions.DefaultTime = () => (0, double.MaxValue);
            row.Children.Add(TextButton(L.T("Start = now"), () => SetTime(true)));
            row.Children.Add(TextButton(L.T("End = now"), () => SetTime(false)));
            row.Children.Add(TextButton(L.T("Whole video"), () => { if (_regions.Selected is { } r) { r.Start = 0; r.End = double.MaxValue; Refresh(); } }));
            row.Children.Add(TextButton(L.T("Delete area"), () => _regions.RemoveSelected()));
            row.Children.Add(Ui.Segmented(new[] { L.T("Blur"), L.T("Fill") }, 0, i => _blur = i == 0));
            row.Children.Add(Ui.ColorPicker(_color, c => _color = c, new[] { Colors.Black, Colors.White, Color.FromRgb(0x80, 0x80, 0x80), Theme.Accent }));
            _regions.SelectionChanged += Refresh;
            _regions.RegionsChanged += Refresh;
            ModePanel.Children.Add(row);
            ModePanel.Children.Add(_status);
            ModePanel.Children.Add(Ui.Note(L.T("Drag around an area right on the frame. The selected area can get its own start and end time.")));
        }
        else
        {
            VideoSurface.Children.Add(_crop);
            foreach (var (name, aspect) in Preview.Aspects)
            {
                var a = aspect;
                row.Children.Add(TextButton(name, () => _crop.SetAspect(a == -1 ? _crop.ContentWidth / _crop.ContentHeight : a)));
            }
            _crop.Changed += Refresh;
            ModePanel.Children.Add(row);
            ModePanel.Children.Add(_status);
            ModePanel.Children.Add(Ui.Note(L.T("Drag the frame or pick an aspect ratio. The sound is kept.")));
        }
        AddCancel();
        AddButton(redact ? L.T("Save a copy") : L.T("Crop video"), Save, true);
    }

    protected override void OnInfoReady()
    {
        if (!IsVideo) { Close(); return; }
        _crop.SetContentSize(Info.Width, Info.Height);
        _regions.SetContentSize(Info.Width, Info.Height);
        Refresh();
    }

    protected override void OnTickExtra()
    {
        if (_redact) _regions.ShowAt(Now);
    }

    void SetTime(bool start)
    {
        if (_regions.Selected is not { } r) { _status.Text = L.T("Select an area first"); return; }
        if (start) r.Start = Math.Min(Now, r.End - 0.05);
        else r.End = Math.Max(Now, r.Start + 0.05);
        Refresh();
    }

    void Refresh()
    {
        if (_redact)
        {
            var sel = _regions.Selected;
            _status.Text = sel == null
                ? L.F("Areas: {0}", _regions.Regions.Count)
                : L.F("Areas: {0}. Selected: {1} - {2}", _regions.Regions.Count, Ui.Time(sel.Start), sel.End >= double.MaxValue / 2 ? L.T("to the end") : Ui.Time(sel.End));
        }
        else
        {
            var r = _crop.Selection;
            _status.Text = L.F("Frame: {0} × {1} px", (int)r.Width / 2 * 2, (int)r.Height / 2 * 2);
        }
    }

    void Save()
    {
        var file = File;
        var name = Path.GetFileName(file);
        if (_redact)
        {
            if (_regions.Regions.Count == 0) { _status.Text = L.T("Add at least one area"); return; }
            var regs = _regions.Regions.Select(r => new MediaEngine.RedactRegion(
                new MediaEngine.CropRect((int)r.Rect.X, (int)r.Rect.Y, (int)r.Rect.Width, (int)r.Rect.Height),
                r.Start, Math.Min(r.End, Duration + 1))).ToList();
            var blur = _blur; var hex = Ui.Hex(_color);
            JobManager.Run(L.F("{0} - redaction", name), "", new[] { file }, ctx => MediaEngine.RedactVideoAsync(file, regs, blur, hex, ctx));
        }
        else
        {
            var r = _crop.Selection;
            var rect = new MediaEngine.CropRect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
            JobManager.Run(L.F("{0} - crop", name), "", new[] { file }, ctx => MediaEngine.CropVideoAsync(file, rect, ctx));
        }
        Close();
    }
}
