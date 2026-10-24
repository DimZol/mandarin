using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mandarin.Core;
using Mandarin.Engines;

namespace Mandarin.UI.Tools;

/// <summary>
/// Preview without a media player: frames are decoded by FFmpeg (a single frame when seeking, a paced stream when
/// playing), the sound is played by ffplay when it is installed next to FFmpeg.
/// </summary>
public sealed class FfPreview : IDisposable
{
    readonly string _file;
    readonly int _width;
    Process? _video, _audio;
    CancellationTokenSource? _frameCts, _playCts;
    readonly Stopwatch _clock = new();
    double _startAt;

    public double Speed { get; set; } = 1;
    /// <summary>Fragments muted in the preview (bleep).</summary>
    public IReadOnlyList<(double start, double end)>? Muted { get; set; }
    public bool Playing { get; private set; }
    public double Now => _startAt + _clock.Elapsed.TotalSeconds * Speed;
    public event Action<Bitmap>? Frame;

    public FfPreview(string file, int width)
    {
        _file = file;
        _width = Math.Max(16, width);
    }

    static string? FfplayPath
    {
        get
        {
            if (Ffmpeg.FfmpegPath is not { } ff) return null;
            var p = Path.Combine(Path.GetDirectoryName(ff)!, OsInfo.Exe("ffplay"));
            return File.Exists(p) ? p : Platform.Desktop.Which("ffplay");
        }
    }

    public static bool CanPlaySound => FfplayPath != null;

    static ProcessStartInfo Psi(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Shows the frame at a moment (the latest request wins).</summary>
    public async Task ShowFrameAsync(double t)
    {
        if (Ffmpeg.FfmpegPath is not { } ff) return;
        _frameCts?.Cancel();
        var cts = _frameCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(30, cts.Token);
            var png = await Task.Run(async () =>
            {
                using var p = Process.Start(Psi(ff, new[] { "-hide_banner", "-loglevel", "error", "-ss", Ffmpeg.Time(t), "-i", _file, "-frames:v", "1",
                    "-vf", $"scale={_width}:-2", "-f", "image2pipe", "-vcodec", "png", "-" }))!;
                _ = p.StandardError.ReadToEndAsync();
                using var ms = new MemoryStream();
                await p.StandardOutput.BaseStream.CopyToAsync(ms, cts.Token);
                p.WaitForExit(5000);
                return ms.ToArray();
            }, cts.Token);
            if (cts.IsCancellationRequested || png.Length == 0) return;
            Frame?.Invoke(new Bitmap(new MemoryStream(png)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Preview frame", ex); }
    }

    public void Play(double from, bool video)
    {
        Stop();
        _startAt = from;
        Playing = true;
        _clock.Restart();
        var cts = _playCts = new CancellationTokenSource();
        if (video && Ffmpeg.FfmpegPath is { } ff) _ = Task.Run(() => PumpFrames(ff, from, cts.Token));
        if (FfplayPath is { } play)
        {
            var args = new List<string> { "-nodisp", "-autoexit", "-loglevel", "quiet", "-ss", Ffmpeg.Time(from) };
            var filters = new List<string>();
            if (Math.Abs(Speed - 1) > 0.001) filters.Add(MediaEngine.AtempoChain(Speed));
            if (Muted is { Count: > 0 } m)
                filters.Add("volume=enable='" + string.Join("+", m.Select(r => $"between(t,{Ffmpeg.Num(r.start)},{Ffmpeg.Num(r.end)})")) + "':volume=0");
            if (filters.Count > 0) { args.Add("-af"); args.Add(string.Join(",", filters)); }
            args.Add(_file);
            try { _audio = Process.Start(Psi(play, args)); }
            catch (Exception ex) { Log.Error("ffplay", ex); }
        }
    }

    /// <summary>Frames as a JPEG stream at 12 per second of the video, shown on time; the pipe paces FFmpeg.</summary>
    void PumpFrames(string ff, double from, CancellationToken ct)
    {
        const double fps = 12;
        try
        {
            _video = Process.Start(Psi(ff, new[] { "-hide_banner", "-loglevel", "error", "-ss", Ffmpeg.Time(from), "-i", _file, "-an",
                "-vf", $"fps={fps.ToString(CultureInfo.InvariantCulture)},scale={_width}:-2", "-f", "image2pipe", "-vcodec", "mjpeg", "-q:v", "5", "-" }))!;
            _ = _video.StandardError.ReadToEndAsync();
            var stream = _video.StandardOutput.BaseStream;
            var frame = new MemoryStream();
            int prev = -1, b, index = 0;
            var inFrame = false;
            while (!ct.IsCancellationRequested && (b = stream.ReadByte()) >= 0)
            {
                if (!inFrame)
                {
                    if (prev == 0xFF && b == 0xD8) { inFrame = true; frame.SetLength(0); frame.WriteByte(0xFF); frame.WriteByte(0xD8); }
                    prev = b;
                    continue;
                }
                frame.WriteByte((byte)b);
                if (prev == 0xFF && b == 0xD9)
                {
                    inFrame = false;
                    var due = index++ / fps / Speed;
                    var wait = due - _clock.Elapsed.TotalSeconds;
                    if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(Math.Min(wait, 1)));
                    if (ct.IsCancellationRequested) break;
                    var bytes = frame.ToArray();
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        try { Frame?.Invoke(new Bitmap(new MemoryStream(bytes))); } catch { /* broken frame */ }
                    });
                }
                prev = b;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Error("Preview stream", ex); }
    }

    public void Stop()
    {
        if (Playing) _startAt = Now;
        Playing = false;
        _clock.Reset();
        _playCts?.Cancel();
        foreach (var p in new[] { _video, _audio })
            try { if (p is { HasExited: false }) p.Kill(true); } catch { /* already exited */ }
        _video = _audio = null;
    }

    public void SeekTo(double t)
    {
        _startAt = t;
        if (Playing) _clock.Restart();
    }

    public void Dispose()
    {
        Stop();
        _frameCts?.Cancel();
    }
}

public abstract class MediaToolBase : ToolWindow
{
    protected readonly string File;
    protected readonly Image Frame = new() { Stretch = Stretch.Fill };
    protected readonly Timeline Timeline = new();
    protected readonly Grid VideoSurface = new() { UseLayoutRounding = false };
    protected readonly Viewbox VideoBox = new() { Stretch = Stretch.Uniform };
    protected MediaInfo Info = null!;
    protected bool IsVideo;
    FfPreview? _preview;
    double _position;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontFamily = new FontFamily("Cascadia Mono, DejaVu Sans Mono, Consolas, monospace") };
    readonly Button _play;
    protected readonly StackPanel ModePanel = new() { Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBlock _loading = new() { Text = L.T("Loading…"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };

    protected double Duration => Info?.Duration ?? 0;
    protected double Now => _preview is { Playing: true } p ? Math.Min(p.Now, Duration) : _position;
    protected bool Playing => _preview?.Playing == true;
    protected FfPreview? Player => _preview;

    protected MediaToolBase(string title, string file, double width = 1100, double height = 780) : base(title, Path.GetFileName(file), width, height)
    {
        File = file;
        VideoSurface.Children.Add(Frame);
        VideoBox.Child = VideoSurface;

        _play = Ui.IconButton("", L.T("Play / pause (Space)"), TogglePlay, 40);
        var transport = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        transport.Children.Add(Ui.IconButton("", L.T("Previous frame (←)"), () => Step(-1)));
        transport.Children.Add(_play);
        transport.Children.Add(Ui.IconButton("", L.T("Next frame (→)"), () => Step(1)));
        transport.Children.Add(_time);
        if (!FfPreview.CanPlaySound)
            transport.Children.Add(new TextBlock { Text = L.T("(no sound in the preview: ffplay was not found)"), Opacity = 0.55, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) });

        var videoHost = new Border
        {
            CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = new SolidColorBrush(Color.FromArgb(Look.IsDark ? (byte)120 : (byte)230, 0, 0, 0)),
            Child = new Grid { Children = { VideoBox, _loading } }
        };

        var dock = new DockPanel();
        var bottom = new StackPanel { Children = { Timeline, transport, ModePanel } };
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(videoHost);
        Body.Content = dock;
        Timeline.Margin = new Thickness(0, 10, 0, 0);

        Timeline.Seek += SeekTo;
        _tick.Tick += (_, _) => OnTick();
        AddHandler(KeyDownEvent, (_, e) => OnKey(e), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Closed += (_, _) =>
        {
            _tick.Stop();
            _preview?.Dispose();
        };
        Opened += async (_, _) => await InitAsync();
    }

    async Task InitAsync()
    {
        try
        {
            Info = await Ffmpeg.ProbeAsync(File, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await ShowMessage(ex.Message);
            Close();
            return;
        }
        IsVideo = Info.HasVideo;
        Timeline.Duration = Math.Max(0.01, Info.Duration);
        Timeline.Out = Timeline.Duration;
        _preview = new FfPreview(File, Math.Min(960, Math.Max(2, Info.Width)));
        _preview.Frame += bmp =>
        {
            Frame.Source = bmp;
            _loading.IsVisible = false;
        };
        if (IsVideo)
        {
            VideoSurface.Width = Math.Max(2, Info.Width);
            VideoSurface.Height = Math.Max(2, Info.Height);
            _ = _preview.ShowFrameAsync(0);
        }
        else
        {
            VideoBox.IsVisible = false;
            _loading.Text = "♪  " + Path.GetFileName(File);
            Timeline.Height = 180;
        }
        OnInfoReady();
        UpdateTime();
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

    protected virtual void OnInfoReady() { }
    protected virtual void OnPeaksReady() { }
    protected virtual void OnTickExtra() { }

    void OnTick()
    {
        if (Playing && Now >= Duration - 0.01) { Pause(); _position = Duration; }
        Timeline.Position = Now;
        Timeline.Refresh();
        UpdateTime();
        OnTickExtra();
    }

    void UpdateTime() => _time.Text = $"{Ui.Time(Now)} / {Ui.Time(Duration)}";

    protected void SeekTo(double t)
    {
        t = Math.Clamp(t, 0, Duration);
        var wasPlaying = Playing;
        if (wasPlaying) _preview?.Stop();
        _position = t;
        Timeline.Position = t;
        Timeline.Refresh();
        UpdateTime();
        if (wasPlaying) Play();
        else if (IsVideo) _ = _preview?.ShowFrameAsync(t);
        OnTickExtra();
    }

    protected void TogglePlay()
    {
        if (Playing) Pause();
        else Play();
    }

    protected virtual void Play()
    {
        if (_preview == null) return;
        if (_position >= Duration - 0.05) _position = 0;
        _preview.Play(_position, IsVideo);
        _play.Content = Icons.Make("", 20);
        _tick.Start();
    }

    protected void Pause()
    {
        if (_preview == null) return;
        _position = Now;
        _preview.Stop();
        _play.Content = Icons.Make("", 20);
        _tick.Stop();
        if (IsVideo) _ = _preview.ShowFrameAsync(_position);
        Timeline.Position = _position;
        Timeline.Refresh();
        UpdateTime();
    }

    void Step(int frames)
    {
        Pause();
        var fps = Info?.Fps > 0 ? Info.Fps : 30;
        SeekTo(Now + frames / fps);
    }

    protected virtual void OnKey(KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); e.Handled = true; break;
            case Key.Left: if (e.KeyModifiers == KeyModifiers.Shift) SeekTo(Now - 1); else Step(-1); e.Handled = true; break;
            case Key.Right: if (e.KeyModifiers == KeyModifiers.Shift) SeekTo(Now + 1); else Step(1); e.Handled = true; break;
            case Key.Delete: Timeline.DeleteSelected(); e.Handled = true; break;
        }
    }

    protected static Button TextButton(string text, Action click, string? tip = null)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 5, 12, 6), Margin = new Thickness(0, 0, 8, 6) };
        if (tip != null) ToolTip.SetTip(b, tip);
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
                fast.IsCheckedChanged += (_, _) => _fastTrim = fast.IsChecked == true;
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
                var (sliderRow, slider) = Ui.LabeledSlider(L.T("Speed"), 0.25, 4, 1, "0.##'×'", v =>
                {
                    _speed = v;
                    if (Player != null) Player.Speed = v;
                    UpdateStatus();
                }, 0.05);
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
    }

    protected override void Play()
    {
        if (_mode == MediaEditMode.Trim && (Now < Timeline.In - 0.05 || Now >= Timeline.Out - 0.05)) SeekTo(Timeline.In);
        if (Player != null && _mode == MediaEditMode.Bleep) Player.Muted = Timeline.Ranges.ToList();
        base.Play();
    }

    protected override void OnKey(KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        if (_mode == MediaEditMode.Trim && e.Key == Key.I) { Timeline.In = Math.Min(Now, Timeline.Out - 0.05); UpdateStatus(); Timeline.Refresh(); e.Handled = true; return; }
        if (_mode == MediaEditMode.Trim && e.Key == Key.O) { Timeline.Out = Math.Max(Now, Timeline.In + 0.05); UpdateStatus(); Timeline.Refresh(); e.Handled = true; return; }
        if (_mode is MediaEditMode.Split or MediaEditMode.Snapshot && e.Key == Key.S) { AddMarker(); e.Handled = true; return; }
        base.OnKey(e);
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
            row.Children.Add(Ui.ColorPicker(_color, c => _color = c, new[] { Colors.Black, Colors.White, Color.FromRgb(0x80, 0x80, 0x80), Look.Accent }));
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
