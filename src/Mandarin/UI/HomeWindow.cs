using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Mandarin.Core;
using Mandarin.Engines;
using Mandarin.Interop;
using Mandarin.UI.Tools;
using Microsoft.Win32;

namespace Mandarin.UI;

/// <summary>Main window: drop zone, gesture help, formats, history and settings.</summary>
public sealed class HomeWindow : Window
{
    static HomeWindow? _instance;
    ContentControl _page = new();
    ListBox _nav = new();
    readonly Dictionary<string, Func<UIElement>> _pages;
    bool _restyling;

    HomeWindow()
    {
        Title = "Mandarin";
        Width = 1040; Height = 720;
        MinWidth = 820; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Theme.UiFont;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Mandarin;component/Assets/Mandarin.png"));

        _pages = new()
        {
            ["home"] = HomePage,
            ["formats"] = FormatsPage,
            ["history"] = HistoryPage,
            ["settings"] = SettingsPage,
            ["about"] = AboutPage
        };

        Build();
        SourceInitialized += (_, _) => ThemeResources.ApplyChrome(this);
        Closed += (_, _) => _instance = null;
        _nav.SelectedIndex = 0;
    }

    /// <summary>Navigation and page host; built again when the palette or the accent changes.</summary>
    void Build()
    {
        _nav = new ListBox();
        _page = new ContentControl();
        var logo = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 18, 16, 18) };
        logo.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Mandarin;component/Assets/Mandarin.png")), Width = 30, Height = 30 });
        logo.Children.Add(new TextBlock { Text = "Mandarin", FontSize = 18, FontWeight = FontWeights.SemiBold, FontFamily = Theme.DisplayFont, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

        foreach (var (key, glyph, text) in new[]
                 {
                     ("home", "", L.T("Home")), ("formats", "", L.T("Formats and tools")), ("history", "", L.T("History")),
                     ("settings", "", L.T("Settings")), ("about", "", L.T("About"))
                 })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6, 4, 6) };
            var navIcon = new TextBlock { Text = glyph, FontFamily = Theme.IconFont, FontSize = 16, Width = 28, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(navIcon);
            row.Children.Add(new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var navItem = new ListBoxItem { Content = row, Tag = key, Margin = new Thickness(6, 1, 6, 1) };
            // the row nudges right under the mouse; the icon bounces when the page is chosen
            navItem.MouseEnter += (_, _) => { if (Motion.Translate(row) is { } t) Motion.To(t, TranslateTransform.XProperty, 3, 160); };
            navItem.MouseLeave += (_, _) => { if (Motion.Translate(row) is { } t) Motion.To(t, TranslateTransform.XProperty, 0, 220); };
            navItem.Selected += (_, _) => { if (IsLoaded && Motion.Scale(navIcon) is { } s) Motion.ScaleFromTo(s, 0.7, 1, 380, Motion.Spring); };
            _nav.Items.Add(navItem);
        }
        _nav.BorderThickness = new Thickness(0);
        _nav.Background = Brushes.Transparent;
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

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var pageHost = new Border
        {
            CornerRadius = new CornerRadius(8, 0, 0, 0), Child = _page,
            Background = Theme.IsDark ? Theme.Brush(255, 255, 255, 8) : Theme.Brush(255, 255, 255, 150),
            BorderBrush = Theme.IsDark ? Theme.Brush(255, 255, 255, 14) : Theme.Brush(0, 0, 0, 14), BorderThickness = new Thickness(1, 1, 0, 0)
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
        var offset = (w._page.Content as ScrollViewer)?.VerticalOffset ?? 0;
        w._restyling = true;
        w.Build();
        w.Select(key);
        w._restyling = false;
        ThemeResources.ApplyChrome(w);
        if (w._page.Content is ScrollViewer sv)
            w.Dispatcher.BeginInvoke(() => sv.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Window for previews/screenshots that is not registered as the main one.</summary>
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

    void Select(string key)
    {
        foreach (ListBoxItem item in _nav.Items)
            if ((string)item.Tag == key) { _nav.SelectedItem = item; break; }
    }

    void Navigate(string key)
    {
        var page = _pages[key]();
        _page.Content = new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(36, 28, 36, 32), Child = page },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        // the first page comes in with the window itself; later pages build up block by block
        if (IsLoaded && !_restyling && page is Panel panel) Motion.Stagger(panel, 26, 0, 14);
    }

    public static void PickFilesAndShowWheel()
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = L.T("Files to convert") };
        if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0) WheelWindow.ShowForFiles(dlg.FileNames);
    }

    static TextBlock H1(string text) => new() { Text = text, FontSize = 28, FontWeight = FontWeights.SemiBold, FontFamily = Theme.DisplayFont, Margin = new Thickness(0, 0, 0, 4) };
    static TextBlock Lead(string text) => new() { Text = text, FontSize = 14, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) };
    static TextBlock H2(string text) => new() { Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 10) };

    // ---------------- Home ----------------

    UIElement HomePage()
    {
        var stack = new StackPanel();
        stack.Children.Add(H1(L.T("A converter without extra clicks")));
        stack.Children.Add(Lead(L.T("Start dragging a file in Explorer and tap a key - the wheel appears. Drop the file on an option and the result appears next to the original. Everything works offline.")));

        // drop zone
        var dropText = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var arrow = new TextBlock { Text = "\uE898", FontFamily = Theme.IconFont, FontSize = 34, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Theme.Brush(Theme.Accent) };
        dropText.Children.Add(arrow);
        dropText.Children.Add(new TextBlock { Text = L.T("Drop files or folders here"), FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 4) });
        dropText.Children.Add(new TextBlock { Text = L.T("the wheel opens with every option"), Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center });
        var pick = new Button { Content = L.T("Choose files…"), Padding = new Thickness(16, 6, 16, 7), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        pick.Click += (_, _) => PickFilesAndShowWheel();
        dropText.Children.Add(pick);
        var tint = new SolidColorBrush(Color.FromArgb(14, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B));
        var dashed = new Rectangle
        {
            Stroke = Theme.Brush(Theme.Accent), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 6, 4 }, RadiusX = 14, RadiusY = 14,
            Fill = tint
        };
        var drop = new Grid { Height = 210, AllowDrop = true, Background = Brushes.Transparent };
        drop.Children.Add(dashed);
        drop.Children.Add(dropText);
        drop.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };

        // the zone brightens under the mouse; while a file is dragged over it the border marches
        // and the arrow bobs, inviting the drop
        drop.MouseEnter += (_, _) => Motion.ColorTo(tint, Color.FromArgb(26, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B), 160);
        drop.MouseLeave += (_, _) => Motion.ColorTo(tint, Color.FromArgb(14, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B), 240);
        void Excite(bool on)
        {
            Motion.ColorTo(tint, Color.FromArgb((byte)(on ? 40 : 14), Theme.Accent.R, Theme.Accent.G, Theme.Accent.B), 180);
            Motion.To(dashed, Shape.StrokeThicknessProperty, on ? 2.5 : 1.5, 180);
            if (!Motion.Enabled) return;
            if (on)
            {
                dashed.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, -10, TimeSpan.FromMilliseconds(600)) { RepeatBehavior = RepeatBehavior.Forever });
                if (Motion.Translate(arrow) is { } t)
                    t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -7, TimeSpan.FromMilliseconds(420))
                        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.EaseInOut });
            }
            else
            {
                dashed.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                if (Motion.Translate(arrow) is { } t) Motion.To(t, TranslateTransform.YProperty, 0, 200);
            }
        }
        drop.DragEnter += (_, _) => Excite(true);
        drop.DragLeave += (_, _) => Excite(false);
        drop.Drop += (_, e) =>
        {
            Excite(false);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                e.Effects = DragDropEffects.Copy;
                var (x, y) = Native.CursorPos();
                Dispatcher.BeginInvoke(() => WheelWindow.ShowForFiles(files, WheelMode.Convert, x, y));
            }
        };
        stack.Children.Add(drop);

        // keys
        stack.Children.Add(H2(L.T("Three wheels, three keys")));
        var keys = new UniformGrid { Columns = 3 };
        keys.Children.Add(KeyCard(AppSettings.Current.ConvertKey, L.T("Convert"), L.T("Images, video, audio, PDF, documents, spreadsheets, EPUB, subtitles, archives.")));
        keys.Children.Add(KeyCard(AppSettings.Current.ToolsKey, L.T("Tools"), L.T("Compress, trim, crop, metadata, split, join, redact, background and more.")));
        keys.Children.Add(KeyCard(AppSettings.Current.ActionsKey, L.T("Actions"), L.T("Share, shelf, copy, move, path, terminal, ZIP.")));
        stack.Children.Add(keys);
        stack.Children.Add(Ui.Note(L.T("Keys can be changed in settings. The wheel stays open until you release the file; release it in the center or press Esc to cancel.")));

        // component status
        stack.Children.Add(H2(L.T("Status")));
        stack.Children.Add(StatusRow(AppSettings.Current.DragWheelEnabled, L.T("Wheel while dragging"), AppSettings.Current.DragWheelEnabled ? L.T("On") : L.T("Off - turn it on in settings or in the tray icon menu")));
        stack.Children.Add(StatusRow(Ffmpeg.Available, L.T("FFmpeg for video and audio"), Ffmpeg.Available ? Ffmpeg.FfmpegPath! : L.T("Not found - click \"Install\" in settings")));
        var native = OfficeEngine.Native(OfficeApp.Word) || OfficeEngine.Native(OfficeApp.PowerPoint);
        stack.Children.Add(StatusRow(native, L.T("Documents, spreadsheets, presentations"), EngineSummary() +
            (native ? "" : L.T(". Presentations and DOC are unavailable - install LibreOffice in settings"))));
        var autostart = new CheckBox
        {
            Content = L.T("Start Mandarin with Windows so the wheel is always available"), IsChecked = AppSettings.Current.StartWithWindows,
            Margin = new Thickness(0, 14, 0, 0)
        };
        autostart.Checked += (_, _) => { AppSettings.Current.StartWithWindows = true; Integration.SetAutostart(true); AppSettings.Save(); };
        autostart.Unchecked += (_, _) => { AppSettings.Current.StartWithWindows = false; Integration.SetAutostart(false); AppSettings.Save(); };
        stack.Children.Add(autostart);
        return stack;
    }

    /// <summary>Which engine currently handles documents, spreadsheets and presentations.</summary>
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
            Background = Theme.IsDark ? Theme.Brush(0x3A, 0x3A, 0x3A) : Theme.Brush(0xFF, 0xFF, 0xFF),
            BorderBrush = Theme.IsDark ? Theme.Brush(0x50, 0x50, 0x50) : Theme.Brush(0xD0, 0xD0, 0xD0), BorderThickness = new Thickness(1, 1, 1, 3),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = DragGesture.KeySymbol(key), FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = DragGesture.KeyLabel(key).ToLowerInvariant(), FontSize = 10, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center }
                }
            }
        };
        s.Children.Add(cap);
        s.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        s.Children.Add(new TextBlock { Text = text, Opacity = 0.72, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
        var card = Ui.Card(s, new Thickness(16)).Also(b => b.Margin = new Thickness(0, 0, 10, 0));
        // the card rises under the mouse and its key cap goes down, as if pressed
        Motion.HoverLift(card);
        void Press(bool down)
        {
            var thickness = down ? new Thickness(1, 1, 1, 1) : new Thickness(1, 1, 1, 3);
            if (Motion.Translate(cap) is { } t) Motion.To(t, TranslateTransform.YProperty, down ? 2 : 0, down ? 110 : 260, down ? Motion.EaseOut : Motion.Spring);
            if (!Motion.Enabled) { cap.BeginAnimation(Border.BorderThicknessProperty, null); cap.BorderThickness = thickness; return; }
            cap.BeginAnimation(Border.BorderThicknessProperty, new ThicknessAnimation(thickness, TimeSpan.FromMilliseconds(down ? 110 : 220)) { EasingFunction = Motion.EaseOut });
        }
        card.MouseEnter += (_, _) => Press(true);
        card.MouseLeave += (_, _) => Press(false);
        return card;
    }

    static Grid StatusRow(bool ok, string title, string detail)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock
        {
            Text = ok ? "" : "", FontFamily = Theme.IconFont, FontSize = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0),
            Foreground = ok ? Theme.Brush(0x2E, 0xA0, 0x43) : Theme.Brush(0xE0, 0x8A, 0x00)
        });
        var t = new StackPanel();
        t.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        t.Children.Add(new TextBlock { Text = detail, Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        return g;
    }

    // ---------------- Formats ----------------

    UIElement FormatsPage()
    {
        var stack = new StackPanel();
        stack.Children.Add(H1(L.T("Formats and tools")));
        stack.Children.Add(Lead(L.T("Everything Mandarin can do. Originals are never changed - results are saved next to them under a new name.")));
        var cats = new[]
        {
            (FileCategory.Image, "JPG, PNG, WebP, HEIC, TIFF, AVIF, BMP, GIF, SVG, ICO, PSD, RAW", L.T("Compress, metadata, photo editing, annotations, background, crop, redact faces and text, rotate, PDF from photos, collage")),
            (FileCategory.Video, "MP4, MOV, MKV, WebM, AVI, WMV, M4V, FLV, MPG, 3GP, TS", L.T("Compress, metadata, mute, trim, crop, speed, frames to PNG, split, redact an area, join")),
            (FileCategory.Audio, "MP3, M4A, WAV, FLAC, OGG, Opus, AIFF, WMA, AAC", L.T("Compress, tags, loudness (LUFS), waveform video, trim with silence removal, mono/stereo, bleep, join")),
            (FileCategory.Pdf, "PDF", L.T("Compress, properties, split, reorder/rotate/delete pages, merge; scanned text via Windows OCR")),
            (FileCategory.Document, "DOCX, DOC, ODT, RTF, Markdown, TXT, HTML", L.T("Exact layout via Microsoft Word or LibreOffice; without them, the built-in converters")),
            (FileCategory.Slides, "PPTX, PPT, ODP", L.T("Via Microsoft PowerPoint or LibreOffice (engine in settings)")),
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
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = FileKinds.CategoryGlyph(cat), FontFamily = Theme.IconFont, FontSize = 18, Foreground = Theme.Brush(Theme.Accent), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = FileKinds.CategoryName(cat), FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            s.Children.Add(head);
            s.Children.Add(new TextBlock { Text = L.T("Opens: ") + inputs, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            var targets = string.Join("  ", Formats.AllTargets(cat).Select(Formats.Label));
            s.Children.Add(new TextBlock { Text = L.T("Saves: ") + targets, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            if (tools.Length > 0) s.Children.Add(new TextBlock { Text = tools, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            wrap.Children.Add(Ui.Card(s).Also(b => b.Margin = new Thickness(0, 0, 10, 10)).Also(b => Motion.HoverLift(b)));
        }
        stack.Children.Add(wrap);
        stack.Children.Add(H2(L.T("Actions for any files")));
        stack.Children.Add(Lead(L.T("Share (the Windows Share sheet: Nearby sharing, mail, apps), Shelf - a floating panel for files, Copy to the clipboard, Move to a folder, Copy path, Open in Terminal, Pack into ZIP.")));
        return stack;
    }

    // ---------------- History ----------------

    UIElement HistoryPage()
    {
        var stack = new StackPanel();
        stack.Children.Add(H1(L.T("History")));
        stack.Children.Add(Lead(L.T("Tasks of the current session.")));
        if (JobManager.Jobs.Count == 0)
        {
            stack.Children.Add(new TextBlock { Text = L.T("Nothing yet. Drag a file and press Shift."), Opacity = 0.6 });
            return stack;
        }
        foreach (var job in JobManager.Jobs.ToList())
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = job.Glyph, FontFamily = Theme.IconFont, FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush(Theme.Accent) });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = job.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = $"{job.Started:HH:mm} · {job.StateText} · {job.Detail}", FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            if (job.Outputs.Count > 0)
            {
                var btns = new StackPanel { Orientation = Orientation.Horizontal };
                var first = job.Outputs[0];
                btns.Children.Add(new Button { Content = L.T("Open"), Padding = new Thickness(10, 4, 10, 5), Margin = new Thickness(6, 0, 0, 0) }.Also(b => b.Click += (_, _) => Integration.Open(first)));
                btns.Children.Add(new Button { Content = L.T("In folder"), Padding = new Thickness(10, 4, 10, 5), Margin = new Thickness(6, 0, 0, 0) }.Also(b => b.Click += (_, _) => Integration.RevealInExplorer(first)));
                Grid.SetColumn(btns, 2);
                row.Children.Add(btns);
            }
            stack.Children.Add(Ui.Card(row, new Thickness(12, 10, 12, 10)).Also(b => Motion.HoverLift(b, 2)));
        }
        return stack;
    }

    // ---------------- Settings ----------------

    UIElement SettingsPage()
    {
        var s = AppSettings.Current;
        var stack = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(H1(L.T("Settings")));

        void Save() => AppSettings.Save();

        // --- appearance
        stack.Children.Add(H2(L.T("Appearance")));
        stack.Children.Add(AccentPicker());
        stack.Children.Add(PalettePicker());

        // --- gesture
        stack.Children.Add(H2(L.T("Wheel")));
        var enabled = new CheckBox { Content = L.T("Show the wheel while dragging files"), IsChecked = s.DragWheelEnabled };
        enabled.Checked += (_, _) => { s.DragWheelEnabled = true; Save(); };
        enabled.Unchecked += (_, _) => { s.DragWheelEnabled = false; Save(); };
        stack.Children.Add(enabled);
        var keyNames = Enum.GetValues<TriggerKey>().ToList();
        ComboBox KeyBox(TriggerKey current, Action<TriggerKey> set)
        {
            var cb = new ComboBox { ItemsSource = keyNames.Select(k => k == TriggerKey.None ? L.T("Off") : DragGesture.KeyLabel(k)).ToList(), SelectedIndex = keyNames.IndexOf(current), Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
            cb.SelectionChanged += (_, _) => { set(keyNames[cb.SelectedIndex]); Save(); };
            return cb;
        }
        stack.Children.Add(Ui.Field(L.T("Convert"), KeyBox(s.ConvertKey, k => s.ConvertKey = k), 200));
        stack.Children.Add(Ui.Field(L.T("Tools"), KeyBox(s.ToolsKey, k => s.ToolsKey = k), 200));
        stack.Children.Add(Ui.Field(L.T("Actions"), KeyBox(s.ActionsKey, k => s.ActionsKey = k), 200));
        stack.Children.Add(Ui.LabeledSlider(L.T("Wheel size"), 260, 440, s.WheelSize, "0", v => { s.WheelSize = (int)v; Save(); }, 20).row);

        // --- output
        stack.Children.Add(H2(L.T("Where to save")));
        var folderLabel = new TextBlock { Text = s.CustomOutputFolder ?? L.T("not selected"), Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var chooseFolder = new Button { Content = L.T("Choose a folder…"), Padding = new Thickness(12, 4, 12, 5), IsEnabled = s.Output == OutputLocation.CustomFolder };
        chooseFolder.Click += (_, _) =>
        {
            var d = new OpenFolderDialog { Title = L.T("Output folder") };
            if (d.ShowDialog(this) == true) { s.CustomOutputFolder = d.FolderName; folderLabel.Text = d.FolderName; Save(); }
        };
        stack.Children.Add(Ui.Segmented(new[] { L.T("Next to the original"), L.T("To a chosen folder") }, (int)s.Output, i =>
        {
            s.Output = (OutputLocation)i; chooseFolder.IsEnabled = i == 1;
            if (i == 1 && string.IsNullOrEmpty(s.CustomOutputFolder)) chooseFolder.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Save();
        }));
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        folderRow.Children.Add(chooseFolder); folderRow.Children.Add(folderLabel);
        stack.Children.Add(folderRow);
        stack.Children.Add(Check(L.T("Show the result in Explorer after converting"), s.RevealAfterConvert, v => s.RevealAfterConvert = v));
        stack.Children.Add(Check(L.T("Play a sound when done"), s.PlaySound, v => s.PlaySound = v));

        // --- quality
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
        stack.Children.Add(Check(L.T("Recognize text in scans (Windows OCR)"), s.OcrScannedPdf, v => s.OcrScannedPdf = v));

        stack.Children.Add(H2(L.T("Documents, spreadsheets, presentations")));
        var engineStatus = new TextBlock { Opacity = 0.75, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Text = L.T("Now: ") + EngineSummary() };
        stack.Children.Add(Ui.Segmented(new[] { L.T("Auto: Office → LibreOffice → built-in"), "Microsoft Office", "LibreOffice", L.T("Built-in only") },
            (int)s.DocumentEngine, i => { s.DocumentEngine = (DocumentEngine)i; Save(); engineStatus.Text = L.T("Now: ") + EngineSummary(); }));
        stack.Children.Add(engineStatus);
        stack.Children.Add(Ui.Note(
            L.F("Microsoft Office: {0}. ", OfficeEngine.WordInstalled || OfficeEngine.ExcelInstalled || OfficeEngine.PowerPointInstalled
                ? string.Join(", ", new[] { OfficeEngine.WordInstalled ? "Word" : null, OfficeEngine.ExcelInstalled ? "Excel" : null, OfficeEngine.PowerPointInstalled ? "PowerPoint" : null }.Where(x => x != null))
                : L.T("not installed")) +
            L.F("LibreOffice: {0}. ", OfficeEngine.LibreInstalled ? L.T("installed") : L.T("not installed")) +
            L.T("The built-in converters keep text, headings, lists, tables and images accurately, but not the page layout; presentations and DOC need Office or LibreOffice.")));
        if (!OfficeEngine.LibreInstalled)
        {
            var loStatus = new TextBlock { Opacity = 0.75, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            var installLo = new Button { Content = L.T("Install LibreOffice (free, about 1.5 GB)"), Padding = new Thickness(12, 5, 12, 6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            installLo.Click += (_, _) =>
            {
                var drive = new DriveInfo(System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))!);
                var freeGb = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
                if (freeGb < 4 && MessageBox.Show(this, L.F("Drive {0} has {1:0.0} GB free, and LibreOffice takes about 1.5 GB. Install anyway?", drive.Name, freeGb),
                        "Mandarin", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                try
                {
                    var winget = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
                    Process.Start(new ProcessStartInfo(winget, "install --id TheDocumentFoundation.LibreOffice -e --accept-source-agreements") { UseShellExecute = true });
                    loStatus.Text = L.T("Installation started in a separate window. When it finishes, Mandarin picks up LibreOffice automatically.");
                }
                catch (Exception ex) { loStatus.Text = L.T("Could not start winget: ") + ex.Message; }
            };
            stack.Children.Add(installLo);
            stack.Children.Add(loStatus);
        }

        stack.Children.Add(H2(L.T("Video and audio")));
        stack.Children.Add(Ui.LabeledSlider(L.T("Video quality (CRF)"), 14, 32, s.VideoCrf, "0", v => { s.VideoCrf = (int)v; Save(); }, 1).row);
        stack.Children.Add(Ui.Note(L.T("Lower means better quality and a bigger file. 18–23 is visually lossless.")));
        var presets = new[] { "veryfast", "fast", "medium", "slow" };
        stack.Children.Add(Ui.Field(L.T("Encoding speed"), Ui.Segmented(new[] { L.T("Very fast"), L.T("Fast"), L.T("Balanced"), L.T("Slow, smaller") }, Math.Max(0, Array.IndexOf(presets, s.VideoPreset)), i => { s.VideoPreset = presets[i]; Save(); }), 200));
        var ffPath = new TextBox { Text = s.FfmpegPath ?? "", Width = 360 };
        ffPath.LostFocus += (_, _) => { s.FfmpegPath = string.IsNullOrWhiteSpace(ffPath.Text) ? null : ffPath.Text.Trim(); Save(); };
        var ffRow = new StackPanel { Orientation = Orientation.Horizontal };
        ffRow.Children.Add(ffPath);
        var browse = new Button { Content = "…", Width = 34, Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var d = new OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe" };
            if (d.ShowDialog(this) == true) { ffPath.Text = d.FileName; s.FfmpegPath = d.FileName; Save(); }
        };
        ffRow.Children.Add(browse);
        stack.Children.Add(Ui.Field(L.T("FFmpeg path"), ffRow, 200));
        var ffStatus = new TextBlock { Text = Ffmpeg.Available ? L.T("Found: ") + Ffmpeg.FfmpegPath : L.T("FFmpeg was not found"), Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        stack.Children.Add(ffStatus);
        if (!Ffmpeg.Available)
        {
            var install = new Button { Content = L.T("Install FFmpeg with winget"), Padding = new Thickness(12, 5, 12, 6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            install.Click += (_, _) =>
            {
                try
                {
                    var winget = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
                    Process.Start(new ProcessStartInfo(winget, "install --id Gyan.FFmpeg -e --accept-source-agreements") { UseShellExecute = true });
                    ffStatus.Text = L.T("Installation started in a separate window. Restart Mandarin when it finishes.");
                }
                catch (Exception ex) { ffStatus.Text = L.T("Could not start winget: ") + ex.Message; }
            };
            stack.Children.Add(install);
        }

        stack.Children.Add(H2(L.T("Windows integration")));
        stack.Children.Add(Check(L.T("Start with Windows (in the tray)"), s.StartWithWindows, v => { s.StartWithWindows = v; Integration.SetAutostart(v); }));
        stack.Children.Add(Check(L.T("\"Send to → Mandarin\" entry in the Explorer menu"), Integration.SendToInstalled, v => Integration.SetSendTo(v)));
        var languageNote = new TextBlock { Opacity = 0.75, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(Ui.Field(L.T("Language"), Ui.Segmented(new[] { L.T("System"), L.NativeName("en"), L.NativeName("ru") }, (int)s.Language, i =>
        {
            s.Language = (AppLanguage)i; Save();
            languageNote.Text = L.T("Restart Mandarin to apply the language.");
        }), 200));
        stack.Children.Add(languageNote);
        var openLog = new Button { Content = L.T("Open the log and settings folder"), Padding = new Thickness(12, 5, 12, 6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        openLog.Click += (_, _) => { Directory.CreateDirectory(AppSettings.Folder); Integration.Open(AppSettings.Folder); };
        stack.Children.Add(openLog);
        return stack;

        CheckBox Check(string text, bool value, Action<bool> set)
        {
            var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 6, 0, 0) };
            c.Checked += (_, _) => { set(true); Save(); };
            c.Unchecked += (_, _) => { set(false); Save(); };
            return c;
        }
    }

    // ---------------- Appearance ----------------

    static void ApplyLook()
    {
        AppSettings.Save();
        App.ApplyTheme();
    }

    static UIElement AccentPicker()
    {
        var s = AppSettings.Current;
        var panel = new StackPanel();
        var on = new CheckBox { Content = L.T("Accent color in the interface"), IsChecked = s.AccentOn };
        on.Checked += (_, _) => { s.AccentOn = true; ApplyLook(); };
        on.Unchecked += (_, _) => { s.AccentOn = false; ApplyLook(); };
        panel.Children.Add(on);

        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Opacity = s.AccentOn ? 1 : 0.55 };
        Theme.TryParseHex(s.AccentColor, out var current);
        var ring = Theme.IsDark ? Theme.Brush(0xFF, 0xFF, 0xFF) : Theme.Brush(0, 0, 0);
        foreach (var (hex, name) in Theme.Accents)
        {
            Theme.TryParseHex(hex, out var c);
            var chosen = s.AccentOn && c == current;
            var sw = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
                Background = Theme.Brush(c), BorderBrush = chosen ? ring : Theme.Brush(0x80, 0x80, 0x80, 0x60), BorderThickness = new Thickness(chosen ? 2 : 1),
                ToolTip = $"{L.T(name)} · {hex}",
                Child = chosen ? new TextBlock
                {
                    Text = "", FontFamily = Theme.IconFont, FontSize = 13, Foreground = Theme.Brush(Theme.TextOn(c)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                } : null
            };
            sw.MouseLeftButtonUp += (_, _) => { s.AccentColor = hex; s.AccentOn = true; ApplyLook(); };
            sw.MouseEnter += (_, _) => { if (Motion.Scale(sw) is { } st) Motion.ScaleTo(st, 1.12, 180, Motion.Spring); };
            sw.MouseLeave += (_, _) => { if (Motion.Scale(sw) is { } st) Motion.ScaleTo(st, 1, 200); };
            row.Children.Add(sw);
        }
        var isCustom = !Theme.Accents.Any(a => string.Equals(a.hex, s.AccentColor, StringComparison.OrdinalIgnoreCase));
        var custom = new Button
        {
            Content = isCustom ? L.F("Custom: {0}", s.AccentColor.ToUpperInvariant()) : L.T("Custom color…"),
            Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(4, 0, 0, 8), VerticalAlignment = VerticalAlignment.Top
        };
        if (isCustom && s.AccentOn)
            custom.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Theme.Brush(current), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = L.F("Custom: {0}", s.AccentColor.ToUpperInvariant()) }
                }
            };
        custom.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B) };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            s.AccentColor = Theme.Hex(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
            s.AccentOn = true;
            ApplyLook();
        };
        row.Children.Add(custom);
        panel.Children.Add(row);
        panel.Children.Add(Ui.Note(L.T("The accent highlights main buttons, check boxes, sliders, selected items and the wheel option under the cursor. With the accent off the palette's own neutral colors remain.")));
        return panel;
    }

    static UIElement PalettePicker()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(Ui.Section(L.T("Interface palette")));
        foreach (var (title, ids) in Theme.Groups)
        {
            panel.Children.Add(new TextBlock { Text = L.T(title), Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 6, 0, 6) });
            var wrap = new WrapPanel();
            foreach (var id in ids) wrap.Children.Add(PaletteTile(id));
            panel.Children.Add(wrap);
        }
        panel.Children.Add(Ui.Note(L.T("The palette applies at once and is saved. \"Windows\" keeps the system look with Mica, \"Auto\" follows the Windows theme and switches with it. Images and conversion results are not affected.")));
        return panel;
    }

    /// <summary>Colors of a palette miniature.</summary>
    sealed record Look(Color Bg, Color Side, Color Panel, Color Line, Color Text, Color Muted, Color Accent);

    static Look LookOf(Theme.Palette p)
    {
        var accent = Theme.ChosenAccent is { } a ? p.Dark ? a : Theme.Mix(a, Colors.Black, 0.12) : p.Accent;
        return new Look(p.Bg, p.Bg2, p.Panel, p.Line, p.Text, p.Muted, accent);
    }

    static Look WindowsLook(bool dark)
    {
        var accent = Theme.ChosenAccent ?? SystemColors.AccentColor;
        return dark
            ? new Look(Color.FromRgb(0x20, 0x20, 0x20), Color.FromRgb(0x1C, 0x1C, 0x1C), Color.FromRgb(0x2B, 0x2B, 0x2B), Color.FromRgb(0x3A, 0x3A, 0x3A), Colors.White, Color.FromRgb(0x9D, 0x9D, 0x9D), accent)
            : new Look(Color.FromRgb(0xF3, 0xF3, 0xF3), Color.FromRgb(0xEB, 0xEB, 0xEB), Colors.White, Color.FromRgb(0xE0, 0xE0, 0xE0), Color.FromRgb(0x1A, 0x1A, 0x1A), Color.FromRgb(0x61, 0x61, 0x61), accent);
    }

    const double PreviewW = 160, PreviewH = 92;

    /// <summary>A miniature window in the palette colors: a sidebar, a card with a progress bar and two buttons.</summary>
    static FrameworkElement Miniature(Look l)
    {
        static Brush B(Color c) => Theme.Brush(c);
        var side = new StackPanel { Background = B(l.Side), Width = 34 };
        for (var i = 0; i < 3; i++)
            side.Children.Add(new Border
            {
                Height = 5, Width = 18, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(0, i == 0 ? 12 : 6, 0, 0),
                Background = B(i == 0 ? l.Accent : l.Muted), Opacity = i == 0 ? 1 : 0.45
            });
        var bar = new Grid { Height = 4, Margin = new Thickness(0, 6, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
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
                    new TextBlock { Text = "Aa", FontWeight = FontWeights.SemiBold, FontSize = 11, Foreground = B(l.Text) },
                    new Border { Height = 3, Width = 52, HorizontalAlignment = HorizontalAlignment.Left, Background = B(l.Muted), Opacity = 0.7, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 3, 0, 0) },
                    bar
                }
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 7, 0, 0) };
        buttons.Children.Add(new Border { Width = 30, Height = 12, CornerRadius = new CornerRadius(3), Background = B(l.Line), Margin = new Thickness(0, 0, 5, 0) });
        buttons.Children.Add(new Border { Width = 36, Height = 12, CornerRadius = new CornerRadius(3), Background = B(l.Accent) });
        var main = new StackPanel { Margin = new Thickness(9, 9, 9, 0), Children = { card, buttons } };
        var grid = new Grid { Width = PreviewW, Height = PreviewH, Background = B(l.Bg) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(main, 1);
        grid.Children.Add(side);
        grid.Children.Add(main);
        return grid;
    }

    /// <summary>Two palettes in one miniature: the left half dark, the right half light.</summary>
    static FrameworkElement Split(Look dark, Look light)
    {
        var right = Miniature(light);
        right.Clip = new RectangleGeometry(new Rect(PreviewW / 2, 0, PreviewW / 2, PreviewH));
        return new Grid { Children = { Miniature(dark), right } };
    }

    static Border PaletteTile(string id)
    {
        var chosen = Theme.ChosenId == id;
        var p = Theme.Find(id);
        var (name, note) = id switch
        {
            Theme.WindowsId => ("Windows", L.T("system colors and Mica, by default")),
            Theme.AutoId => (L.T("Auto"), L.T("as in the system: dark - Gray, light - Light gray")),
            _ => (L.T(p!.Name), L.T(p.Note))
        };
        var preview = id switch
        {
            Theme.WindowsId => Miniature(WindowsLook(Theme.SystemIsDark())),
            Theme.AutoId => Split(LookOf(Theme.Find("gray")!), LookOf(Theme.Find("light")!)),
            _ => Miniature(LookOf(p!))
        };
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 8, 0, 0) };
        caption.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
        if (chosen) caption.Children.Add(new TextBlock { Text = "", FontFamily = Theme.IconFont, FontSize = 12, Margin = new Thickness(6, 2, 0, 0), Foreground = Theme.Brush(Theme.Accent) });
        var content = new StackPanel { Width = PreviewW };
        content.Children.Add(new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = preview, BorderBrush = Theme.Brush(0x80, 0x80, 0x80, 0x50), BorderThickness = new Thickness(1) });
        content.Children.Add(caption);
        content.Children.Add(new TextBlock { Text = note, FontSize = 11.5, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 1, 0, 0) });
        var tile = new Border
        {
            Child = content, Padding = new Thickness(8), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand,
            Background = Theme.IsDark ? Theme.Brush(255, 255, 255, 8) : Theme.Brush(255, 255, 255, 170),
            BorderBrush = chosen ? Theme.Brush(Theme.Accent) : Theme.IsDark ? Theme.Brush(255, 255, 255, 16) : Theme.Brush(0, 0, 0, 16),
            BorderThickness = new Thickness(chosen ? 2 : 1), ToolTip = chosen ? null : L.F("Choose the \"{0}\" palette", name)
        };
        tile.MouseLeftButtonUp += (_, _) =>
        {
            if (Theme.ChosenId == id) return;
            AppSettings.Current.Palette = id;
            ApplyLook();
        };
        Motion.HoverLift(tile, 2);
        return tile;
    }


    // ---------------- About ----------------

    UIElement AboutPage()
    {
        var stack = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        head.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Mandarin;component/Assets/Mandarin.png")), Width = 72, Height = 72 });
        var titles = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Mandarin", FontSize = 28, FontWeight = FontWeights.SemiBold, FontFamily = Theme.DisplayFont });
        titles.Children.Add(new TextBlock { Text = L.F("Version {0} · offline file converter for Windows 11", typeof(HomeWindow).Assembly.GetName().Version?.ToString(3)), Opacity = 0.75 });
        head.Children.Add(titles);
        stack.Children.Add(head);
        stack.Children.Add(H2(L.T("Your files stay on your computer")));
        stack.Children.Add(Lead(L.T("Online converters make you upload a copy of your file to someone else's server. Mandarin does everything locally: images with ImageMagick, video and audio with FFmpeg, PDF with built-in Windows features, PdfPig and PDFsharp, documents with Microsoft Office or LibreOffice (if installed) or its own converters. Text and face recognition use built-in Windows features. Nothing is sent to the internet.")));
        stack.Children.Add(H2(L.T("Safety")));
        stack.Children.Add(Lead(L.T("Originals are never overwritten: results always get a new name. Dropping on the wheel copies, it never moves. Archives are extracted only into a new folder. Macros in Office documents are disabled during conversion.")));
        return stack;
    }
}
