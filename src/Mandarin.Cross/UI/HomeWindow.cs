using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mandarin.Core;
using Mandarin.Engines;
using Mandarin.Platform;

namespace Mandarin.UI;

/// <summary>Main window: drop zone, gesture help, formats, history and settings.</summary>
public sealed class HomeWindow : Window
{
    static HomeWindow? _instance, _hidden;
    ContentControl _page = new();
    ListBox _nav = new();
    readonly Dictionary<string, Func<Control>> _pages;
    bool _restyling;

    HomeWindow()
    {
        Title = "Mandarin";
        Width = 1040; Height = 720;
        MinWidth = 820; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Look.UiFont;
        Icon = App.WindowIcon;

        _pages = new()
        {
            ["home"] = HomePage,
            ["formats"] = FormatsPage,
            ["history"] = HistoryPage,
            ["settings"] = SettingsPage,
            ["about"] = AboutPage
        };
        Build();
        Closed += (_, _) => { if (_instance == this) _instance = null; };
        _nav.SelectedIndex = 0;
    }

    /// <summary>A window that is never shown: a top level for the clipboard and dialogs when no window is open.</summary>
    public static TopLevel Hidden() => _instance ?? (_hidden ??= new HomeWindow());

    public static HomeWindow CreatePreview(string page)
    {
        var w = new HomeWindow();
        w.Select(page);
        return w;
    }

    public static void ShowSingle() => ShowSingle("home");

    public static void ShowSingle(string page)
    {
        _instance ??= new HomeWindow();
        _instance.Show();
        if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
        _instance.Activate();
        _instance.Select(page);
    }

    static Bitmap? Logo()
    {
        using var s = typeof(HomeWindow).Assembly.GetManifestResourceStream("Mandarin.Assets.Mandarin.png");
        return s == null ? null : new Bitmap(s);
    }

    void Build()
    {
        Background = Look.WindowBackground;
        _nav = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        _page = new ContentControl();
        var logo = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 18, 16, 18) };
        logo.Children.Add(new Image { Source = Logo(), Width = 30, Height = 30 });
        logo.Children.Add(new TextBlock { Text = "Mandarin", FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

        foreach (var (key, glyph, text) in new[]
                 {
                     ("home", "", L.T("Home")), ("formats", "", L.T("Formats and tools")), ("history", "", L.T("History")),
                     ("settings", "", L.T("Settings")), ("about", "", L.T("About"))
                 })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6, 4, 6), Spacing = 12 };
            row.Children.Add(Icons.Make(glyph, 18));
            row.Children.Add(new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var item = new ListBoxItem { Content = row, Tag = key, Margin = new Thickness(6, 1, 6, 1), CornerRadius = new CornerRadius(6) };
            item.PointerEntered += (_, _) => Motion.MoveX(row, Motion.Xf(row).move.X, 3, 160);
            item.PointerExited += (_, _) => Motion.MoveX(row, Motion.Xf(row).move.X, 0, 220);
            _nav.Items.Add(item);
        }
        _nav.SelectionChanged += (_, _) => { if (_nav.SelectedItem is ListBoxItem { Tag: string k }) Navigate(k); };

        var side = new DockPanel { Width = 250 };
        DockPanel.SetDock(logo, Dock.Top);
        side.Children.Add(logo);
        var footer = new TextBlock
        {
            Text = L.T("All files are processed\non this computer."), FontSize = 11.5, Opacity = 0.6, Margin = new Thickness(18, 0, 16, 18), TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        side.Children.Add(footer);
        side.Children.Add(_nav);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var pageHost = new Border
        {
            CornerRadius = new CornerRadius(8, 0, 0, 0), Child = _page,
            Background = Look.IsDark ? Look.Brush(255, 255, 255, 8) : Look.Brush(255, 255, 255, 150),
            BorderBrush = Look.IsDark ? Look.Brush(255, 255, 255, 14) : Look.Brush(0, 0, 0, 14), BorderThickness = new Thickness(1, 1, 0, 0)
        };
        Grid.SetColumn(pageHost, 1);
        grid.Children.Add(side);
        grid.Children.Add(pageHost);
        Content = grid;
    }

    /// <summary>Repaints the open main window with the new palette, keeping the page and its scroll position.</summary>
    public static void Restyle()
    {
        if (_instance is not { } w) return;
        var key = (w._nav.SelectedItem as ListBoxItem)?.Tag as string ?? "home";
        var offset = (w._page.Content as ScrollViewer)?.Offset ?? default;
        w._restyling = true;
        w.Build();
        w.Select(key);
        w._restyling = false;
        if (w._page.Content is ScrollViewer sv) Dispatcher.UIThread.Post(() => sv.Offset = offset, DispatcherPriority.Loaded);
    }

    void Select(string key)
    {
        foreach (var item in _nav.Items.OfType<ListBoxItem>())
            if ((string)item.Tag! == key) { _nav.SelectedItem = item; break; }
    }

    void Navigate(string key)
    {
        var page = _pages[key]();
        _page.Content = new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(36, 28, 36, 32), Child = page },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        if (IsVisible && !_restyling && page is Panel panel) Motion.Stagger(panel, 26, 0, 14);
    }

    public static async Task PickFilesAndShowWheel()
    {
        var host = Hidden();
        var files = await host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = L.T("Files to convert") });
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToList();
        if (paths.Count > 0) WheelWindow.ShowForFiles(paths);
    }

    static TextBlock H1(string text) => new() { Text = text, FontSize = 28, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
    static TextBlock Lead(string text) => new() { Text = text, FontSize = 14, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) };
    static TextBlock H2(string text) => new() { Text = text, FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 22, 0, 10) };

    // ---------------- Home ----------------

    Control HomePage()
    {
        var stack = new StackPanel();
        stack.Children.Add(H1(L.T("A converter without extra clicks")));
        stack.Children.Add(Lead(App.DragGesture?.Works == true
            ? L.T("Start dragging a file in Explorer and tap a key - the wheel appears. Drop the file on an option and the result appears next to the original. Everything works offline.")
            : L.T("Drop files here or open them with Mandarin from the file manager - the wheel appears. Choose an option and the result appears next to the original. Everything works offline.")));

        var accent = Look.Accent;
        var dropText = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var arrow = Icons.Make("", 38, Look.Brush(accent));
        arrow.HorizontalAlignment = HorizontalAlignment.Center;
        dropText.Children.Add(arrow);
        dropText.Children.Add(new TextBlock { Text = L.T("Drop files or folders here"), FontSize = 16, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 4) });
        dropText.Children.Add(new TextBlock { Text = L.T("the wheel opens with every option"), Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center });
        var pick = new Button { Content = L.T("Choose files…"), Padding = new Thickness(16, 6, 16, 7), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        pick.Click += (_, _) => _ = PickFilesAndShowWheel();
        dropText.Children.Add(pick);
        var tint = new SolidColorBrush(Color.FromArgb(14, accent.R, accent.G, accent.B));
        var dashed = new Rectangle
        {
            Stroke = Look.Brush(accent), StrokeThickness = 1.5, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 6, 4 }, RadiusX = 14, RadiusY = 14, Fill = tint
        };
        var drop = new Grid { Height = 210, Background = Brushes.Transparent };
        drop.Children.Add(dashed);
        drop.Children.Add(dropText);
        DragDrop.SetAllowDrop(drop, true);
        drop.PointerEntered += (_, _) => Motion.ColorTo(tint, Color.FromArgb(26, accent.R, accent.G, accent.B), 160);
        drop.PointerExited += (_, _) => Motion.ColorTo(tint, Color.FromArgb(14, accent.R, accent.G, accent.B), 240);
        void Excite(bool on)
        {
            Motion.ColorTo(tint, Color.FromArgb((byte)(on ? 40 : 14), accent.R, accent.G, accent.B), 180);
            dashed.StrokeThickness = on ? 2.5 : 1.5;
            Motion.MoveY(arrow, Motion.CurrentY(arrow), on ? -6 : 0, 220, Motion.Spring);
        }
        drop.AddHandler(DragDrop.DragEnterEvent, (_, e) => { Excite(true); e.DragEffects = DragDropEffects.Copy; });
        drop.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.TryGetFiles() != null ? DragDropEffects.Copy : DragDropEffects.None);
        drop.AddHandler(DragDrop.DragLeaveEvent, (_, _) => Excite(false));
        drop.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            Excite(false);
            var files = e.DataTransfer.TryGetFiles()?.Select(i => i.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToList();
            if (files is { Count: > 0 })
            {
                e.DragEffects = DragDropEffects.Copy;
                Dispatcher.UIThread.Post(() => WheelWindow.ShowForFiles(files));
            }
        });
        stack.Children.Add(drop);

        stack.Children.Add(H2(L.T("Three wheels, three keys")));
        var keys = new UniformGrid { Columns = 3 };
        keys.Children.Add(KeyCard(AppSettings.Current.ConvertKey, L.T("Convert"), L.T("Images, video, audio, PDF, documents, spreadsheets, EPUB, subtitles, archives.")));
        keys.Children.Add(KeyCard(AppSettings.Current.ToolsKey, L.T("Tools"), L.T("Compress, trim, crop, metadata, split, join, redact, background and more.")));
        keys.Children.Add(KeyCard(AppSettings.Current.ActionsKey, L.T("Actions"), L.T("Copy, move, path, terminal, folder, ZIP.")));
        stack.Children.Add(keys);
        stack.Children.Add(Ui.Note(App.DragGesture?.Works == true
            ? L.T("Keys can be changed in settings. The wheel stays open until you release the file; release it in the center or press Esc to cancel.")
            : X11.IsWayland
                ? L.T("The drag gesture needs an X11 session (under Wayland the system does not let programs see the keys). In the wheel opened from the window or the file manager the same keys switch the modes.")
                : L.T("In the wheel the same keys switch the modes; Esc closes it.")));

        stack.Children.Add(H2(L.T("Status")));
        var gestureWorks = App.DragGesture?.Works == true;
        stack.Children.Add(StatusRow(gestureWorks && AppSettings.Current.DragWheelEnabled, L.T("Wheel while dragging"),
            !gestureWorks ? (X11.IsWayland ? L.T("Unavailable under Wayland - drop files on this window or use \"Open with → Mandarin\"") : L.T("Unavailable on this system"))
            : AppSettings.Current.DragWheelEnabled ? L.T("On") : L.T("Off - turn it on in settings or in the tray icon menu")));
        stack.Children.Add(StatusRow(Ffmpeg.Available, L.T("FFmpeg for video and audio"), Ffmpeg.Available ? Ffmpeg.FfmpegPath! : L.T("Not found.") + " " + OsInfo.FfmpegHint));
        var native = OfficeEngine.Native(OfficeApp.Word) || OfficeEngine.Native(OfficeApp.PowerPoint);
        stack.Children.Add(StatusRow(native, L.T("Documents, spreadsheets, presentations"), EngineSummary() +
            (native ? "" : ". " + LibreHint())));
        var printer = HtmlEngine.ChromiumPath != null || OfficeEngine.LibreInstalled;
        stack.Children.Add(StatusRow(printer, L.T("PDF from documents"), HtmlEngine.ChromiumPath is { } c ? c : OfficeEngine.LibreInstalled ? "LibreOffice" : L.T("Needs LibreOffice or a Chromium-based browser")));
        stack.Children.Add(StatusRow(WinRt.OcrAvailable, L.T("Text recognition (OCR)"), WinRt.TesseractPath ?? L.T("Tesseract was not found - scanned PDFs are converted without text")));
        var autostart = new CheckBox
        {
            Content = OsInfo.IsWindows ? L.T("Start Mandarin with Windows so the wheel is always available") : L.T("Start Mandarin with the session so the wheel is always available"),
            IsChecked = AppSettings.Current.StartWithWindows, Margin = new Thickness(0, 14, 0, 0)
        };
        autostart.IsCheckedChanged += (_, _) =>
        {
            AppSettings.Current.StartWithWindows = autostart.IsChecked == true;
            Desktop.SetAutostart(autostart.IsChecked == true);
            AppSettings.Save();
        };
        stack.Children.Add(autostart);
        return stack;
    }

    static string LibreHint() => OsInfo.IsLinux
        ? L.T("Presentations and DOC need LibreOffice: install it with your package manager (for example \"sudo apt install libreoffice\")")
        : L.T("Presentations and DOC need Microsoft Office or LibreOffice (LibreOffice 24.8 is the last version for Windows 7 and 8)");

    static string EngineSummary()
    {
        static string Name(Backend b, string office) => b switch
        {
            Backend.MicrosoftOffice => office,
            Backend.LibreOffice => "LibreOffice",
            _ => L.T("built-in converters")
        };
        var slides = OfficeEngine.For(OfficeApp.PowerPoint) == Backend.BuiltIn ? L.T("unavailable") : Name(OfficeEngine.For(OfficeApp.PowerPoint), "Microsoft PowerPoint");
        return L.F("documents - {0}, ", Name(OfficeEngine.For(OfficeApp.Word), "Microsoft Word")) +
               L.F("spreadsheets - {0}, presentations - {1}", Name(OfficeEngine.For(OfficeApp.Excel), "Microsoft Excel"), slides);
    }

    static Border KeyCard(TriggerKey key, string title, string text)
    {
        var s = new StackPanel();
        var cap = new Border
        {
            Width = 64, Height = 56, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left,
            Background = Look.IsDark ? Look.Brush(0x3A, 0x3A, 0x3A) : Look.Brush(0xFF, 0xFF, 0xFF),
            BorderBrush = Look.IsDark ? Look.Brush(0x50, 0x50, 0x50) : Look.Brush(0xD0, 0xD0, 0xD0), BorderThickness = new Thickness(1, 1, 1, 3),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = Gesture.KeySymbol(key), FontSize = 16, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = Gesture.KeyLabel(key).ToLowerInvariant(), FontSize = 10, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center }
                }
            }
        };
        s.Children.Add(cap);
        s.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        s.Children.Add(new TextBlock { Text = text, Opacity = 0.72, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
        var card = Ui.Card(s, new Thickness(16)).Also(b => b.Margin = new Thickness(0, 0, 10, 0));
        Motion.HoverLift(card);
        card.PointerEntered += (_, _) => { cap.BorderThickness = new Thickness(1); Motion.MoveY(cap, Motion.CurrentY(cap), 2, 110); };
        card.PointerExited += (_, _) => { cap.BorderThickness = new Thickness(1, 1, 1, 3); Motion.MoveY(cap, Motion.CurrentY(cap), 0, 260, Motion.Spring); };
        return card;
    }

    static Grid StatusRow(bool ok, string title, string detail)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4), ColumnDefinitions = new ColumnDefinitions("30,*") };
        var icon = Icons.Make(ok ? "" : "", 17, ok ? Look.Brush(Color.FromRgb(0x2E, 0xA0, 0x43)) : Look.Brush(Color.FromRgb(0xE0, 0x8A, 0x00)));
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        g.Children.Add(icon);
        var t = new StackPanel();
        t.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        t.Children.Add(new TextBlock { Text = detail, Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        return g;
    }

    // ---------------- Formats ----------------

    Control FormatsPage()
    {
        var stack = new StackPanel();
        stack.Children.Add(H1(L.T("Formats and tools")));
        stack.Children.Add(Lead(L.T("Everything Mandarin can do. Originals are never changed - results are saved next to them under a new name.")));
        var cats = new[]
        {
            (FileCategory.Image, "JPG, PNG, WebP, HEIC, TIFF, AVIF, BMP, GIF, SVG, ICO, PSD, RAW", L.T("Compress, metadata, photo editing, annotations, background, crop, redact areas and text, rotate, PDF from photos, collage")),
            (FileCategory.Video, "MP4, MOV, MKV, WebM, AVI, WMV, M4V, FLV, MPG, 3GP, TS", L.T("Compress, metadata, mute, trim, crop, speed, frames to PNG, split, redact an area, join")),
            (FileCategory.Audio, "MP3, M4A, WAV, FLAC, OGG, Opus, AIFF, WMA, AAC", L.T("Compress, tags, loudness (LUFS), waveform video, trim with silence removal, mono/stereo, bleep, join")),
            (FileCategory.Pdf, "PDF", L.T("Compress, properties, split, reorder/rotate/delete pages, merge; scanned text via Tesseract")),
            (FileCategory.Document, "DOCX, DOC, ODT, RTF, Markdown, TXT, HTML", L.T("Exact layout via LibreOffice; without it, the built-in converters")),
            (FileCategory.Slides, "PPTX, PPT, ODP", L.T("Via LibreOffice (engine in settings)")),
            (FileCategory.Spreadsheet, "XLSX, XLS, ODS, CSV, TSV", ""),
            (FileCategory.Epub, "EPUB", ""),
            (FileCategory.Subtitle, "SRT, VTT", ""),
            (FileCategory.Archive, "ZIP, TAR, GZIP, RAR, 7z, BZ2, XZ", L.T("Extraction protected against dangerous paths")),
            (FileCategory.Folder, L.T("Folders"), "")
        };
        var wrap = new UniformGrid { Columns = 2 };
        foreach (var (cat, inputs, tools) in cats)
        {
            var s = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            head.Children.Add(Icons.Make(FileKinds.CategoryGlyph(cat), 20, Look.Brush(Look.Accent)));
            head.Children.Add(new TextBlock { Text = FileKinds.CategoryName(cat), FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            s.Children.Add(head);
            s.Children.Add(new TextBlock { Text = L.T("Opens: ") + inputs, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            var targets = string.Join("  ", Formats.AllTargets(cat).Select(Formats.Label));
            s.Children.Add(new TextBlock { Text = L.T("Saves: ") + targets, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            if (tools.Length > 0) s.Children.Add(new TextBlock { Text = tools, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            wrap.Children.Add(Ui.Card(s).Also(b => b.Margin = new Thickness(0, 0, 10, 10)).Also(b => Motion.HoverLift(b)));
        }
        stack.Children.Add(wrap);
        stack.Children.Add(H2(L.T("Actions for any files")));
        stack.Children.Add(Lead(L.T("Copy to the clipboard, Move to a folder, Copy path, Open in Terminal, Show in folder, Pack into ZIP.")));
        return stack;
    }

    // ---------------- History ----------------

    Control HistoryPage()
    {
        var stack = new StackPanel();
        stack.Children.Add(H1(L.T("History")));
        stack.Children.Add(Lead(L.T("Tasks of the current session.")));
        if (JobManager.Jobs.Count == 0)
        {
            stack.Children.Add(new TextBlock { Text = L.T("Nothing yet. Drop a file on the Home page."), Opacity = 0.6 });
            return stack;
        }
        foreach (var job in JobManager.Jobs.ToList())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*,Auto") };
            var icon = Icons.Make(job.Glyph, 20, Look.Brush(Look.Accent));
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(icon);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = job.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = $"{job.Started:HH:mm} · {job.StateText} · {job.Detail}", FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            if (job.Outputs.Count > 0)
            {
                var btns = new StackPanel { Orientation = Orientation.Horizontal };
                var first = job.Outputs[0];
                btns.Children.Add(new Button { Content = L.T("Open"), Padding = new Thickness(10, 4, 10, 5), Margin = new Thickness(6, 0, 0, 0) }.Also(b => b.Click += (_, _) => Desktop.Open(first)));
                btns.Children.Add(new Button { Content = L.T("In folder"), Padding = new Thickness(10, 4, 10, 5), Margin = new Thickness(6, 0, 0, 0) }.Also(b => b.Click += (_, _) => Desktop.Reveal(first)));
                Grid.SetColumn(btns, 2);
                row.Children.Add(btns);
            }
            stack.Children.Add(Ui.Card(row, new Thickness(12, 10, 12, 10)).Also(b => Motion.HoverLift(b, 2)));
        }
        return stack;
    }

    // ---------------- Settings ----------------

    Control SettingsPage()
    {
        var s = AppSettings.Current;
        var stack = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(H1(L.T("Settings")));

        void Save() => AppSettings.Save();

        stack.Children.Add(H2(L.T("Appearance")));
        stack.Children.Add(AccentPicker());
        stack.Children.Add(PalettePicker());

        stack.Children.Add(H2(L.T("Wheel")));
        var enabled = new CheckBox { Content = L.T("Show the wheel while dragging files"), IsChecked = s.DragWheelEnabled, IsEnabled = App.DragGesture?.Works == true };
        enabled.IsCheckedChanged += (_, _) => { s.DragWheelEnabled = enabled.IsChecked == true; Save(); };
        stack.Children.Add(enabled);
        if (App.DragGesture?.Works != true)
            stack.Children.Add(Ui.Note(X11.IsWayland ? L.T("Unavailable under Wayland - drop files on this window or use \"Open with → Mandarin\"") : L.T("Unavailable on this system")));
        var keyNames = Enum.GetValues(typeof(TriggerKey)).Cast<TriggerKey>().ToList();
        ComboBox KeyBox(TriggerKey current, Action<TriggerKey> set)
        {
            var cb = new ComboBox { ItemsSource = keyNames.Select(k => k == TriggerKey.None ? L.T("Off") : Gesture.KeyLabel(k)).ToList(), SelectedIndex = keyNames.IndexOf(current), Width = 180 };
            cb.SelectionChanged += (_, _) => { if (cb.SelectedIndex >= 0) { set(keyNames[cb.SelectedIndex]); Save(); } };
            return cb;
        }
        stack.Children.Add(Ui.Field(L.T("Convert"), KeyBox(s.ConvertKey, k => s.ConvertKey = k), 200));
        stack.Children.Add(Ui.Field(L.T("Tools"), KeyBox(s.ToolsKey, k => s.ToolsKey = k), 200));
        stack.Children.Add(Ui.Field(L.T("Actions"), KeyBox(s.ActionsKey, k => s.ActionsKey = k), 200));
        stack.Children.Add(Ui.LabeledSlider(L.T("Wheel size"), 260, 440, s.WheelSize, "0", v => { s.WheelSize = (int)v; Save(); }, 20).row);

        stack.Children.Add(H2(L.T("Where to save")));
        var folderLabel = new TextBlock { Text = s.CustomOutputFolder ?? L.T("not selected"), Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var chooseFolder = new Button { Content = L.T("Choose a folder…"), Padding = new Thickness(12, 4, 12, 5), IsEnabled = s.Output == OutputLocation.CustomFolder };
        async Task Choose()
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("Output folder") });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path) { s.CustomOutputFolder = path; folderLabel.Text = path; Save(); }
        }
        chooseFolder.Click += (_, _) => _ = Choose();
        stack.Children.Add(Ui.Segmented(new[] { L.T("Next to the original"), L.T("To a chosen folder") }, (int)s.Output, i =>
        {
            s.Output = (OutputLocation)i; chooseFolder.IsEnabled = i == 1;
            if (i == 1 && string.IsNullOrEmpty(s.CustomOutputFolder)) _ = Choose();
            Save();
        }));
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { chooseFolder, folderLabel } });
        stack.Children.Add(Check(L.T("Show the result in the file manager after converting"), s.RevealAfterConvert, v => s.RevealAfterConvert = v));
        stack.Children.Add(Check(L.T("Play a sound when done"), s.PlaySound, v => s.PlaySound = v));

        stack.Children.Add(H2(L.T("Images")));
        stack.Children.Add(Check(L.T("Keep metadata (EXIF) when converting"), s.KeepMetadata, v => s.KeepMetadata = v));
        stack.Children.Add(Ui.LabeledSlider(L.T("JPG quality"), 50, 100, s.JpegQuality, "0", v => { s.JpegQuality = (int)v; Save(); }, 1).row);
        stack.Children.Add(Ui.LabeledSlider(L.T("WebP quality"), 50, 100, s.WebpQuality, "0", v => { s.WebpQuality = (int)v; Save(); }, 1).row);
        stack.Children.Add(Ui.LabeledSlider(L.T("AVIF quality"), 30, 100, s.AvifQuality, "0", v => { s.AvifQuality = (int)v; Save(); }, 1).row);
        stack.Children.Add(Ui.LabeledSlider(L.T("HEIC quality"), 30, 100, s.HeicQuality, "0", v => { s.HeicQuality = (int)v; Save(); }, 1).row);

        stack.Children.Add(H2(L.T("PDF and documents")));
        var dpis = new[] { 150, 200, 300, 400 };
        stack.Children.Add(Ui.Field(L.T("Pages to images"), Ui.Segmented(dpis.Select(d => $"{d} DPI").ToArray(), Math.Max(0, Array.IndexOf(dpis, s.PdfImageDpi)), i => { s.PdfImageDpi = dpis[i]; Save(); }), 200));
        stack.Children.Add(Ui.Field("PDF → EPUB", Ui.Segmented(new[] { L.T("Reflowable text"), L.T("Keep the page layout") }, (int)s.PdfToEpub, i => { s.PdfToEpub = (PdfEpubMode)i; Save(); }), 200));
        stack.Children.Add(Check(L.T("Recognize text in scans (Tesseract)"), s.OcrScannedPdf, v => s.OcrScannedPdf = v));

        stack.Children.Add(H2(L.T("Documents, spreadsheets, presentations")));
        var engineStatus = new TextBlock { Opacity = 0.75, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Text = L.T("Now: ") + EngineSummary() };
        // Microsoft Office exists only on Windows
        var engines = OsInfo.IsWindows
            ? new[] { DocumentEngine.Auto, DocumentEngine.MicrosoftOffice, DocumentEngine.LibreOffice, DocumentEngine.BuiltIn }
            : new[] { DocumentEngine.Auto, DocumentEngine.LibreOffice, DocumentEngine.BuiltIn };
        var engineNames = engines.Select(e => e switch
        {
            DocumentEngine.Auto => OsInfo.IsWindows ? L.T("Auto: Office → LibreOffice → built-in") : L.T("Auto: LibreOffice → built-in"),
            DocumentEngine.MicrosoftOffice => "Microsoft Office",
            DocumentEngine.LibreOffice => "LibreOffice",
            _ => L.T("Built-in only")
        }).ToArray();
        stack.Children.Add(Ui.Segmented(engineNames, Math.Max(0, Array.IndexOf(engines, s.DocumentEngine)), i =>
        {
            s.DocumentEngine = engines[i]; Save(); engineStatus.Text = L.T("Now: ") + EngineSummary();
        }));
        stack.Children.Add(engineStatus);
        stack.Children.Add(Ui.Note(
            (OsInfo.IsWindows
                ? L.F("Microsoft Office: {0}. ", OfficeEngine.WordInstalled || OfficeEngine.ExcelInstalled || OfficeEngine.PowerPointInstalled
                    ? string.Join(", ", new[] { OfficeEngine.WordInstalled ? "Word" : null, OfficeEngine.ExcelInstalled ? "Excel" : null, OfficeEngine.PowerPointInstalled ? "PowerPoint" : null }.Where(x => x != null))
                    : L.T("not installed"))
                : "") +
            L.F("LibreOffice: {0}. ", OfficeEngine.LibreInstalled ? L.T("installed") : L.T("not installed")) +
            L.T("The built-in converters keep text, headings, lists, tables and images accurately, but not the page layout; presentations and DOC need Office or LibreOffice.")));
        if (!OfficeEngine.LibreInstalled) stack.Children.Add(Ui.Note(LibreHint()));

        stack.Children.Add(H2(L.T("Video and audio")));
        stack.Children.Add(Ui.LabeledSlider(L.T("Video quality (CRF)"), 14, 32, s.VideoCrf, "0", v => { s.VideoCrf = (int)v; Save(); }, 1).row);
        stack.Children.Add(Ui.Note(L.T("Lower means better quality and a bigger file. 18–23 is visually lossless.")));
        var presets = new[] { "veryfast", "fast", "medium", "slow" };
        stack.Children.Add(Ui.Field(L.T("Encoding speed"), Ui.Segmented(new[] { L.T("Very fast"), L.T("Fast"), L.T("Balanced"), L.T("Slow, smaller") }, Math.Max(0, Array.IndexOf(presets, s.VideoPreset)), i => { s.VideoPreset = presets[i]; Save(); }), 200));
        var ffPath = new TextBox { Text = s.FfmpegPath ?? "", Width = 360 };
        ffPath.LostFocus += (_, _) => { s.FfmpegPath = string.IsNullOrWhiteSpace(ffPath.Text) ? null : ffPath.Text.Trim(); Save(); };
        var browse = new Button { Content = "…", Width = 34, Margin = new Thickness(6, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        browse.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "FFmpeg" });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } p) { ffPath.Text = p; s.FfmpegPath = p; Save(); }
        };
        stack.Children.Add(Ui.Field(L.T("FFmpeg path"), new StackPanel { Orientation = Orientation.Horizontal, Children = { ffPath, browse } }, 200));
        stack.Children.Add(new TextBlock
        {
            Text = Ffmpeg.Available ? L.T("Found: ") + Ffmpeg.FfmpegPath : L.T("FFmpeg was not found") + ". " + OsInfo.FfmpegHint, Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap
        });

        stack.Children.Add(H2(OsInfo.IsWindows ? L.T("Windows integration") : L.T("Desktop integration")));
        stack.Children.Add(Check(OsInfo.IsWindows ? L.T("Start with Windows (in the tray)") : L.T("Start with the session (in the tray)"), s.StartWithWindows, v => { s.StartWithWindows = v; Desktop.SetAutostart(v); }));
        stack.Children.Add(Check(OsInfo.IsWindows ? L.T("\"Send to → Mandarin\" entry in the Explorer menu") : L.T("\"Open with → Mandarin\" in the file manager"),
            Desktop.MenuEntryInstalled, v => Desktop.SetMenuEntry(v)));
        var languageNote = new TextBlock { Opacity = 0.75, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(Ui.Field(L.T("Language"), Ui.Segmented(new[] { L.T("System"), L.NativeName("en"), L.NativeName("ru") }, (int)s.Language, i =>
        {
            s.Language = (AppLanguage)i; Save();
            languageNote.Text = L.T("Restart Mandarin to apply the language.");
        }), 200));
        stack.Children.Add(languageNote);
        var openLog = new Button { Content = L.T("Open the log and settings folder"), Padding = new Thickness(12, 5, 12, 6), Margin = new Thickness(0, 14, 0, 0) };
        openLog.Click += (_, _) => { Directory.CreateDirectory(AppSettings.Folder); Desktop.Open(AppSettings.Folder); };
        stack.Children.Add(openLog);
        return stack;

        CheckBox Check(string text, bool value, Action<bool> set)
        {
            var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 6, 0, 0) };
            c.IsCheckedChanged += (_, _) => { set(c.IsChecked == true); Save(); };
            return c;
        }
    }

    // ---------------- Appearance ----------------

    static void ApplyLook()
    {
        AppSettings.Save();
        Dispatcher.UIThread.Post(App.ApplyTheme);
    }

    static Control AccentPicker()
    {
        var s = AppSettings.Current;
        var panel = new StackPanel();
        var on = new CheckBox { Content = L.T("Accent color in the interface"), IsChecked = s.AccentOn };
        on.IsCheckedChanged += (_, _) => { s.AccentOn = on.IsChecked == true; ApplyLook(); };
        panel.Children.Add(on);

        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Opacity = s.AccentOn ? 1 : 0.55 };
        Look.TryParseHex(s.AccentColor, out var current);
        var ring = Look.IsDark ? Look.Brush(0xFF, 0xFF, 0xFF) : Look.Brush(0, 0, 0);
        foreach (var (hex, name) in Look.Accents)
        {
            Look.TryParseHex(hex, out var c);
            var chosen = s.AccentOn && c == current;
            var sw = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 8, 8), Cursor = new Cursor(StandardCursorType.Hand),
                Background = Look.Brush(c), BorderBrush = chosen ? ring : Look.Brush(0x80, 0x80, 0x80, 0x60), BorderThickness = new Thickness(chosen ? 2 : 1),
                Child = chosen ? Icons.Make("", 15, Look.Brush(Look.TextOn(c))).Also(i => { i.HorizontalAlignment = HorizontalAlignment.Center; i.VerticalAlignment = VerticalAlignment.Center; }) : null
            };
            ToolTip.SetTip(sw, $"{L.T(name)} · {hex}");
            sw.PointerReleased += (_, _) => { s.AccentColor = hex; s.AccentOn = true; ApplyLook(); };
            sw.PointerEntered += (_, _) => Motion.ScaleTo(sw, Motion.CurrentScale(sw), 1.12, 180, Motion.Spring);
            sw.PointerExited += (_, _) => Motion.ScaleTo(sw, Motion.CurrentScale(sw), 1, 200);
            row.Children.Add(sw);
        }
        var isCustom = !Look.Accents.Any(a => string.Equals(a.hex, s.AccentColor, StringComparison.OrdinalIgnoreCase));
        var custom = new Button { Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(4, 0, 0, 8), VerticalAlignment = VerticalAlignment.Top };
        custom.Content = isCustom && s.AccentOn
            ? new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Look.Brush(current), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = L.F("Custom: {0}", s.AccentColor.ToUpperInvariant()) }
                }
            }
            : L.T("Custom color…");
        custom.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(custom) is not Window owner) return;
            if (await ColorDialog.Pick(owner, current) is not { } c) return;
            s.AccentColor = Look.Hex(c);
            s.AccentOn = true;
            ApplyLook();
        };
        row.Children.Add(custom);
        panel.Children.Add(row);
        panel.Children.Add(Ui.Note(L.T("The accent highlights main buttons, check boxes, sliders, selected items and the wheel option under the cursor. With the accent off the palette's own neutral colors remain.")));
        return panel;
    }

    static Control PalettePicker()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(Ui.Section(L.T("Interface palette")));
        foreach (var (title, ids) in Look.Groups)
        {
            panel.Children.Add(new TextBlock { Text = L.T(title), Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 6, 0, 6) });
            var wrap = new WrapPanel();
            foreach (var id in ids) wrap.Children.Add(PaletteTile(id));
            panel.Children.Add(wrap);
        }
        panel.Children.Add(Ui.Note(L.F("The palette applies at once and is saved. \"{0}\" keeps the system look, \"Auto\" follows the system theme and switches with it. Images and conversion results are not affected.", Look.SystemPaletteName)));
        return panel;
    }

    sealed record MiniColors(Color Bg, Color Side, Color Panel, Color Line, Color Text, Color Muted, Color Accent);

    static MiniColors LookOf(Look.Palette p)
    {
        var accent = Look.ChosenAccent is { } a ? p.Dark ? a : Look.Mix(a, Colors.Black, 0.12) : p.Accent;
        return new MiniColors(p.Bg, p.Bg2, p.Panel, p.Line, p.Text, p.Muted, accent);
    }

    static MiniColors SystemLook(bool dark)
    {
        var accent = Look.ChosenAccent ?? Look.Accent;
        return dark
            ? new MiniColors(Color.FromRgb(0x20, 0x20, 0x20), Color.FromRgb(0x1C, 0x1C, 0x1C), Color.FromRgb(0x2B, 0x2B, 0x2B), Color.FromRgb(0x3A, 0x3A, 0x3A), Colors.White, Color.FromRgb(0x9D, 0x9D, 0x9D), accent)
            : new MiniColors(Color.FromRgb(0xF3, 0xF3, 0xF3), Color.FromRgb(0xEB, 0xEB, 0xEB), Colors.White, Color.FromRgb(0xE0, 0xE0, 0xE0), Color.FromRgb(0x1A, 0x1A, 0x1A), Color.FromRgb(0x61, 0x61, 0x61), accent);
    }

    const double PreviewW = 160, PreviewH = 92;

    static Control Miniature(MiniColors l)
    {
        static IBrush B(Color c) => Look.Brush(c);
        var side = new StackPanel { Background = B(l.Side), Width = 34 };
        for (var i = 0; i < 3; i++)
            side.Children.Add(new Border
            {
                Height = 5, Width = 18, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(0, i == 0 ? 12 : 6, 0, 0),
                Background = B(i == 0 ? l.Accent : l.Muted), Opacity = i == 0 ? 1 : 0.45
            });
        var bar = new Grid { Height = 4, Margin = new Thickness(0, 6, 0, 0), ColumnDefinitions = new ColumnDefinitions("3*,2*") };
        var track = new Border { Background = B(l.Line), CornerRadius = new CornerRadius(2) };
        Grid.SetColumnSpan(track, 2);
        bar.Children.Add(track);
        bar.Children.Add(new Border { Background = B(l.Accent), CornerRadius = new CornerRadius(2) });
        var card = new Border
        {
            Background = B(l.Panel), BorderBrush = B(l.Line), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 5, 7, 7),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Aa", FontWeight = FontWeight.SemiBold, FontSize = 11, Foreground = B(l.Text) },
                    new Border { Height = 3, Width = 52, HorizontalAlignment = HorizontalAlignment.Left, Background = B(l.Muted), Opacity = 0.7, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 3, 0, 0) },
                    bar
                }
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 7, 0, 0) };
        buttons.Children.Add(new Border { Width = 30, Height = 12, CornerRadius = new CornerRadius(3), Background = B(l.Line), Margin = new Thickness(0, 0, 5, 0) });
        buttons.Children.Add(new Border { Width = 36, Height = 12, CornerRadius = new CornerRadius(3), Background = B(l.Accent) });
        var main = new StackPanel { Margin = new Thickness(9, 9, 9, 0), Children = { card, buttons } };
        var grid = new Grid { Width = PreviewW, Height = PreviewH, Background = B(l.Bg), ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(main, 1);
        grid.Children.Add(side);
        grid.Children.Add(main);
        return grid;
    }

    static Control Split(MiniColors dark, MiniColors light)
    {
        var right = Miniature(light);
        right.Clip = new RectangleGeometry(new Rect(PreviewW / 2, 0, PreviewW / 2, PreviewH));
        return new Grid { Children = { Miniature(dark), right } };
    }

    static Border PaletteTile(string id)
    {
        var chosen = Look.ChosenId == id;
        var p = Look.Find(id);
        var (name, note) = id switch
        {
            Look.WindowsId => (Look.SystemPaletteName, L.T("system colors, by default")),
            Look.AutoId => (L.T("Auto"), L.T("as in the system: dark - Gray, light - Light gray")),
            _ => (L.T(p!.Name), L.T(p.Note))
        };
        var preview = id switch
        {
            Look.WindowsId => Miniature(SystemLook(Look.SystemIsDark())),
            Look.AutoId => Split(LookOf(Look.Find("gray")!), LookOf(Look.Find("light")!)),
            _ => Miniature(LookOf(p!))
        };
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 8, 0, 0), Spacing = 6 };
        caption.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold });
        if (chosen) caption.Children.Add(Icons.Make("", 14, Look.Brush(Look.Accent)));
        var content = new StackPanel { Width = PreviewW };
        content.Children.Add(new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = preview, BorderBrush = Look.Brush(0x80, 0x80, 0x80, 0x50), BorderThickness = new Thickness(1) });
        content.Children.Add(caption);
        content.Children.Add(new TextBlock { Text = note, FontSize = 11.5, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 1, 0, 0) });
        var tile = new Border
        {
            Child = content, Padding = new Thickness(8), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(10), Cursor = new Cursor(StandardCursorType.Hand),
            Background = Look.IsDark ? Look.Brush(255, 255, 255, 8) : Look.Brush(255, 255, 255, 170),
            BorderBrush = chosen ? Look.Brush(Look.Accent) : Look.IsDark ? Look.Brush(255, 255, 255, 16) : Look.Brush(0, 0, 0, 16),
            BorderThickness = new Thickness(chosen ? 2 : 1)
        };
        if (!chosen) ToolTip.SetTip(tile, L.F("Choose the \"{0}\" palette", name));
        tile.PointerReleased += (_, _) =>
        {
            if (Look.ChosenId == id) return;
            AppSettings.Current.Palette = id;
            ApplyLook();
        };
        Motion.HoverLift(tile, 2);
        return tile;
    }

    // ---------------- About ----------------

    Control AboutPage()
    {
        var stack = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        head.Children.Add(new Image { Source = Logo(), Width = 72, Height = 72 });
        var titles = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Mandarin", FontSize = 28, FontWeight = FontWeight.SemiBold });
        titles.Children.Add(new TextBlock
        {
            Text = L.F(OsInfo.IsLinux ? "Version {0} · offline file converter for Linux" : "Version {0} · offline file converter for Windows 7 and 8",
                typeof(HomeWindow).Assembly.GetName().Version?.ToString(3)),
            Opacity = 0.75
        });
        head.Children.Add(titles);
        stack.Children.Add(head);
        stack.Children.Add(H2(L.T("Your files stay on your computer")));
        stack.Children.Add(Lead(L.T("Online converters make you upload a copy of your file to someone else's server. Mandarin does everything locally: images with ImageMagick, video and audio with FFmpeg, PDF with PDFium, PdfPig and PDFsharp, documents with LibreOffice (if installed) or its own converters, scanned text with Tesseract (if installed). Nothing is sent to the internet.")));
        stack.Children.Add(H2(L.T("Safety")));
        stack.Children.Add(Lead(L.T("Originals are never overwritten: results always get a new name. Dropping on the wheel copies, it never moves. Archives are extracted only into a new folder. Macros in Office documents are disabled during conversion.")));
        return stack;
    }
}
