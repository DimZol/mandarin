using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Mandarin.Core;
using Mandarin.Engines;
using Mandarin.Interop;
using Microsoft.Win32;

namespace Mandarin.UI.Tools;

// ======================= Image compression =======================

public sealed class ImageCompressDialog : ToolWindow
{
    public ImageCompressDialog(IReadOnlyList<string> files) : base(L.T("Compress images"), Catalog.Describe(files), 500)
    {
        var mode = ImageEngine.CompressMode.Preset;
        var preset = 1;
        var percent = 50.0;
        var targetKb = 500.0;
        int? maxSide = null;
        var strip = false;
        var format = 0; // 0 - as is, 1 JPG, 2 WEBP, 3 AVIF

        var total = files.Sum(f => new FileInfo(f).Length);
        var stack = new StackPanel();
        stack.Children.Add(Ui.Note(L.F("Now: {0}. Originals stay unchanged - compressed copies appear next to them.", OutputPaths.HumanSize(total))));
        stack.Children.Add(Ui.Section(L.T("Method")));

        var presetPanel = Ui.Segmented(new[] { L.T("High quality"), L.T("Balanced"), L.T("Smallest size") }, 1, i => preset = i);
        var (percentRow, _) = Ui.LabeledSlider(L.T("Reduce by"), 10, 90, 50, "0'%'", v => percent = v, 5);
        var sizeBox = new TextBox { Text = "500", Width = 90 };
        sizeBox.TextChanged += (_, _) => { if (double.TryParse(sizeBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v)) targetKb = v; };
        var sizeRow = Ui.Field(L.T("At most, KB"), sizeBox);

        void Show()
        {
            presetPanel.Visibility = mode == ImageEngine.CompressMode.Preset ? Visibility.Visible : Visibility.Collapsed;
            percentRow.Visibility = mode == ImageEngine.CompressMode.Percent ? Visibility.Visible : Visibility.Collapsed;
            sizeRow.Visibility = mode == ImageEngine.CompressMode.TargetSize ? Visibility.Visible : Visibility.Collapsed;
        }
        stack.Children.Add(Ui.Segmented(new[] { L.T("Preset"), L.T("Percent"), L.T("File size") }, 0, i => { mode = (ImageEngine.CompressMode)i; Show(); }));
        stack.Children.Add(presetPanel);
        stack.Children.Add(percentRow);
        stack.Children.Add(sizeRow);
        Show();

        stack.Children.Add(Ui.Section(L.T("Dimensions")));
        var resize = new ComboBox { ItemsSource = new[] { L.T("Keep"), L.T("Up to 3840 px"), L.T("Up to 2560 px"), L.T("Up to 1920 px"), L.T("Up to 1280 px"), L.T("Up to 1024 px"), L.T("Up to 800 px") }, SelectedIndex = 0 };
        resize.SelectionChanged += (_, _) => maxSide = resize.SelectedIndex switch { 1 => 3840, 2 => 2560, 3 => 1920, 4 => 1280, 5 => 1024, 6 => 800, _ => null };
        stack.Children.Add(Ui.Field(L.T("Long side"), resize));

        stack.Children.Add(Ui.Section(L.T("Format")));
        stack.Children.Add(Ui.Segmented(new[] { L.T("Same as the original"), "JPG", "WEBP", "AVIF" }, 0, i => format = i));
        var stripBox = new CheckBox { Content = L.T("Remove metadata (EXIF, GPS)"), Margin = new Thickness(0, 10, 0, 0) };
        stripBox.Checked += (_, _) => strip = true; stripBox.Unchecked += (_, _) => strip = false;
        stack.Children.Add(stripBox);
        Body.Content = stack;

        AddCancel();
        AddButton(L.T("Compress"), () =>
        {
            var opts = new ImageEngine.CompressOptions(mode, preset, (int)percent, (long)(targetKb * 1024), maxSide, strip);
            JobManager.RunBatch(L.F("Compressing images ({0})", files.Count), "", files, (f, ctx) => Task.Run(() =>
            {
                var ext = format switch { 1 => "jpg", 2 => "webp", 3 => "avif", _ => ImageEngine.OutputExtFor(f) };
                if (ext is "heic" or "bmp" or "ico") ext = "jpg"; // these formats cannot be shrunk by tuning quality
                var output = OutputPaths.File(f, ext, L.T(" (compressed)"));
                var before = new FileInfo(f).Length;
                var after = ImageEngine.Compress(f, output, opts, ctx.Token);
                ctx.AddOutput(output);
                if (files.Count == 1) ctx.Detail($"{OutputPaths.HumanSize(before)} → {OutputPaths.HumanSize(after)} (−{Math.Max(0, 100 - after * 100 / Math.Max(1, before))}%)");
            }, ctx.Token));
            Close();
        }, true);
    }
}

// ======================= Photo metadata =======================

public sealed class ImageMetadataDialog : ToolWindow
{
    public ImageMetadataDialog(IReadOnlyList<string> files) : base(L.T("Photo metadata"), Catalog.Describe(files), 520)
    {
        var single = files.Count == 1;
        var meta = single ? SafeRead(files[0]) : new ImageEngine.PhotoMeta();
        TextBox Box(string? v) => new() { Text = v ?? "" };
        var make = Box(meta.Make); var model = Box(meta.Model); var artist = Box(meta.Artist); var copyright = Box(meta.Copyright);
        var desc = Box(meta.Description); var date = Box(meta.DateTaken);
        var lat = Box(meta.Latitude?.ToString("0.######", CultureInfo.InvariantCulture));
        var lon = Box(meta.Longitude?.ToString("0.######", CultureInfo.InvariantCulture));

        var stack = new StackPanel();
        if (!single) stack.Children.Add(Ui.Note(L.T("Filled-in fields are written to all selected photos; empty ones are removed.")));
        stack.Children.Add(Ui.Section(L.T("Camera"), 6));
        stack.Children.Add(Ui.Field(L.T("Make"), make));
        stack.Children.Add(Ui.Field(L.T("Model"), model));
        stack.Children.Add(Ui.Field(L.T("Date taken"), date));
        stack.Children.Add(Ui.Section(L.T("Author")));
        stack.Children.Add(Ui.Field(L.T("Author"), artist));
        stack.Children.Add(Ui.Field(L.T("Copyright"), copyright));
        stack.Children.Add(Ui.Field(L.T("Description"), desc));
        stack.Children.Add(Ui.Section(L.T("Location")));
        stack.Children.Add(Ui.Field(L.T("Latitude"), lat));
        stack.Children.Add(Ui.Field(L.T("Longitude"), lon));
        stack.Children.Add(Ui.Note(L.T("For example: 55.751244 and 37.618423. Clear both fields to remove the geotag.")));
        Body.Content = stack;

        AddFooterLeft(new Button
        {
            Content = L.T("Remove all metadata"), Padding = new Thickness(14, 6, 14, 7)
        }.Also(b => b.Click += (_, _) =>
        {
            JobManager.RunBatch(L.F("Removing metadata ({0})", files.Count), "", files, (f, ctx) => Task.Run(() =>
            {
                var output = OutputPaths.File(f, ImageEngine.OutputExtFor(f), L.T(" (no metadata)"));
                ImageEngine.RemoveMeta(f, output);
                ctx.AddOutput(output);
            }));
            Close();
        }));
        AddCancel();
        AddButton(L.T("Save"), () =>
        {
            double? P(string s) => double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            var m = new ImageEngine.PhotoMeta
            {
                Make = make.Text, Model = model.Text, Artist = artist.Text, Copyright = copyright.Text,
                Description = desc.Text, DateTaken = date.Text, Latitude = P(lat.Text), Longitude = P(lon.Text)
            };
            JobManager.RunBatch(L.F("Writing metadata ({0})", files.Count), "", files, (f, ctx) => Task.Run(() =>
            {
                var output = OutputPaths.File(f, ImageEngine.OutputExtFor(f), L.T(" (metadata)"));
                ImageEngine.WriteMeta(f, output, m);
                ctx.AddOutput(output);
            }));
            Close();
        }, true);
    }

    static ImageEngine.PhotoMeta SafeRead(string f)
    {
        try { return ImageEngine.ReadMeta(f); }
        catch { return new ImageEngine.PhotoMeta(); }
    }
}

// ======================= Video compression =======================

public sealed class VideoCompressDialog : ToolWindow
{
    public VideoCompressDialog(IReadOnlyList<string> files) : base(L.T("Compress video"), Catalog.Describe(files), 500)
    {
        var quality = 1; var res = 0; var useTarget = false; var targetMb = 25.0;
        var total = files.Sum(f => new FileInfo(f).Length);
        var stack = new StackPanel();
        stack.Children.Add(Ui.Note(L.F("Now: {0}. Originals are kept.", OutputPaths.HumanSize(total))));
        stack.Children.Add(Ui.Section(L.T("Quality")));
        stack.Children.Add(Ui.Segmented(new[] { L.T("High"), L.T("Balanced"), L.T("Small file") }, 1, i => quality = i));
        stack.Children.Add(Ui.Section(L.T("Resolution")));
        stack.Children.Add(Ui.Segmented(new[] { L.T("As is"), "1080p", "720p", "480p" }, 0, i => res = i));
        stack.Children.Add(Ui.Section(L.T("Exact size")));
        var box = new TextBox { Text = "25", Width = 80, IsEnabled = false };
        box.TextChanged += (_, _) => { if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v)) targetMb = v; };
        var check = new CheckBox { Content = L.T("Fit into a size (MB):"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        check.Checked += (_, _) => { useTarget = true; box.IsEnabled = true; };
        check.Unchecked += (_, _) => { useTarget = false; box.IsEnabled = false; };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(check); row.Children.Add(box);
        stack.Children.Add(row);
        stack.Children.Add(Ui.Note(L.T("Handy for messengers and mail with attachment size limits.")));
        Body.Content = stack;
        AddCancel();
        AddButton(L.T("Compress"), () =>
        {
            var crf = quality switch { 0 => 21, 1 => 25, _ => 30 };
            int? maxH = res switch { 1 => 1080, 2 => 720, 3 => 480, _ => null };
            var opts = new MediaEngine.VideoCompressOptions(crf, maxH, useTarget ? (long)(targetMb * 1024 * 1024) : null, quality == 2 ? 96 : 128);
            JobManager.RunBatch(L.F("Compressing video ({0})", files.Count), "", files, (f, ctx) => MediaEngine.CompressVideoAsync(f, opts, ctx));
            Close();
        }, true);
    }
}

// ======================= Audio compression =======================

public sealed class AudioCompressDialog : ToolWindow
{
    public AudioCompressDialog(IReadOnlyList<string> files) : base(L.T("Compress audio"), Catalog.Describe(files), 480)
    {
        var kbps = 128; string? fmt = null; var mono = false;
        var stack = new StackPanel();
        stack.Children.Add(Ui.Section(L.T("Bitrate"), 4));
        var rates = new[] { 64, 96, 128, 160, 192 };
        stack.Children.Add(Ui.Segmented(rates.Select(r => L.F("{0} kbps", r)).ToArray(), 2, i => kbps = rates[i]));
        stack.Children.Add(Ui.Note(L.T("64–96 for speech and podcasts, 128–160 for good-quality music.")));
        stack.Children.Add(Ui.Section(L.T("Format")));
        stack.Children.Add(Ui.Segmented(new[] { L.T("Same as the original"), "MP3", "M4A (AAC)", "OPUS" }, 0, i => fmt = i switch { 1 => "mp3", 2 => "m4a", 3 => "opus", _ => null }));
        var monoBox = new CheckBox { Content = L.T("Make it mono (half the size again)"), Margin = new Thickness(0, 10, 0, 0) };
        monoBox.Checked += (_, _) => mono = true; monoBox.Unchecked += (_, _) => mono = false;
        stack.Children.Add(monoBox);
        Body.Content = stack;
        AddCancel();
        AddButton(L.T("Compress"), () =>
        {
            JobManager.RunBatch(L.F("Compressing audio ({0})", files.Count), "", files, (f, ctx) => MediaEngine.CompressAudioAsync(f, kbps, fmt, mono, ctx));
            Close();
        }, true);
    }
}

// ======================= Loudness =======================

public sealed class NormalizeDialog : ToolWindow
{
    public NormalizeDialog(IReadOnlyList<string> files) : base(L.T("Normalize loudness"), Catalog.Describe(files), 480)
    {
        var target = -14.0;
        var stack = new StackPanel();
        stack.Children.Add(Ui.Section(L.T("Target"), 4));
        var targets = new[] { -14.0, -16.0, -19.0, -23.0 };
        stack.Children.Add(Ui.Segmented(new[] { L.T("Music, streaming (−14 LUFS)"), L.T("Podcast (−16 LUFS)"), L.T("Voice, quieter (−19 LUFS)"), L.T("Broadcast, EBU R128 (−23 LUFS)") }, 0, i => target = targets[i]));
        stack.Children.Add(Ui.Note(L.T("Loudness is measured over the whole recording and normalized without distortion; peaks are limited to −1.5 dBTP.")));
        Body.Content = stack;
        AddCancel();
        AddButton(L.T("Normalize"), () =>
        {
            JobManager.RunBatch(L.F("Loudness ({0})", files.Count), "", files, (f, ctx) => MediaEngine.NormalizeAsync(f, target, ctx));
            Close();
        }, true);
    }
}

// ======================= Channels =======================

public sealed class ChannelsDialog : ToolWindow
{
    public ChannelsDialog(IReadOnlyList<string> files) : base(L.T("Mono or stereo"), Catalog.Describe(files), 440)
    {
        var mono = true;
        var stack = new StackPanel();
        stack.Children.Add(Ui.Segmented(new[] { L.T("To mono"), L.T("To stereo") }, 0, i => mono = i == 0));
        stack.Children.Add(Ui.Note(L.T("Mono → stereo duplicates the same sound in both channels.")));
        Body.Content = stack;
        AddCancel();
        AddButton(L.T("Convert"), () =>
        {
            JobManager.RunBatch(mono ? L.T("To mono") : L.T("To stereo"), "", files, (f, ctx) => MediaEngine.ChannelsAsync(f, mono, ctx));
            Close();
        }, true);
    }
}

// ======================= Visualizer =======================

public sealed class VisualizeDialog : ToolWindow
{
    public VisualizeDialog(string file) : base(L.T("Video with a waveform"), Path.GetFileName(file), 480)
    {
        var shape = MediaEngine.VisualShape.Landscape;
        string? image = null;
        var stack = new StackPanel();
        stack.Children.Add(Ui.Section(L.T("Frame shape"), 4));
        stack.Children.Add(Ui.Segmented(new[] { L.T("Landscape 16:9"), L.T("Portrait 9:16"), L.T("Square 1:1") }, 0, i => shape = (MediaEngine.VisualShape)i));
        stack.Children.Add(Ui.Section(L.T("Background")));
        var imgLabel = new TextBlock { Text = L.T("Black background"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var pick = new Button { Content = L.T("Choose an image…"), Padding = new Thickness(12, 5, 12, 6) };
        var clear = new Button { Content = L.T("No image"), Padding = new Thickness(12, 5, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        pick.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = L.T("Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.heic|All files|*.*") };
            if (dlg.ShowDialog(this) == true) { image = dlg.FileName; imgLabel.Text = Path.GetFileName(image); }
        };
        clear.Click += (_, _) => { image = null; imgLabel.Text = L.T("Black background"); };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(pick); row.Children.Add(clear); row.Children.Add(imgLabel);
        stack.Children.Add(row);
        stack.Children.Add(Ui.Note(L.T("An orange waveform over the background. The result is an MP4 for social networks.")));
        Body.Content = stack;
        AddCancel();
        AddButton(L.T("Create video"), () =>
        {
            JobManager.Run(L.F("{0} → MP4 with a waveform", Path.GetFileName(file)), "", new[] { file }, ctx => MediaEngine.VisualizeAsync(file, shape, image, ctx));
            Close();
        }, true);
    }
}

// ======================= Video/audio metadata =======================

public sealed class MediaMetadataDialog : ToolWindow
{
    readonly Dictionary<string, TextBox> _boxes = new();

    public MediaMetadataDialog(IReadOnlyList<string> files, MediaInfo? info) : base(L.T("Metadata"), Catalog.Describe(files), 520)
    {
        var isAudio = FileKinds.Classify(files[0]) == FileCategory.Audio;
        var fields = isAudio
            ? new[] { ("title", L.T("Title")), ("artist", L.T("Artist")), ("album", L.T("Album")), ("album_artist", L.T("Album artist")), ("track", L.T("Track number")), ("date", L.T("Year")), ("genre", L.T("Genre")), ("comment", L.T("Comment")) }
            : new[] { ("title", L.T("Title")), ("artist", L.T("Author")), ("date", L.T("Date")), ("description", L.T("Description")), ("comment", L.T("Comment")), ("location", L.T("Location (ISO 6709)")) };
        var stack = new StackPanel();
        if (files.Count > 1) stack.Children.Add(Ui.Note(L.T("Filled-in fields are written to all selected files; empty ones stay unchanged.")));
        foreach (var (key, label) in fields)
        {
            var value = "";
            if (files.Count == 1 && info != null)
                foreach (var k in new[] { key, key.ToUpperInvariant(), key == "location" ? "com.apple.quicktime.location.ISO6709" : key })
                    if (info.Tags.TryGetValue(k, out var v)) { value = v; break; }
            var box = new TextBox { Text = value };
            _boxes[key] = box;
            stack.Children.Add(Ui.Field(label, box, 160));
        }
        if (!isAudio) stack.Children.Add(Ui.Note(L.T("Location, for example: +55.7512+037.6184/")));
        Body.Content = stack;

        AddFooterLeft(new Button { Content = L.T("Remove all metadata"), Padding = new Thickness(14, 6, 14, 7) }.Also(b => b.Click += (_, _) =>
        {
            JobManager.RunBatch(L.F("Removing metadata ({0})", files.Count), "", files, (f, ctx) => MediaEngine.WriteMetadataAsync(f, new(), true, ctx));
            Close();
        }));
        AddCancel();
        AddButton(L.T("Save"), () =>
        {
            var tags = _boxes.Where(kv => files.Count == 1 || kv.Value.Text.Trim().Length > 0)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Text.Trim());
            JobManager.RunBatch(L.F("Metadata ({0})", files.Count), "", files, (f, ctx) => MediaEngine.WriteMetadataAsync(f, tags, false, ctx));
            Close();
        }, true);
    }
}

// ======================= PDF compression =======================

public sealed class PdfCompressDialog : ToolWindow
{
    public PdfCompressDialog(IReadOnlyList<string> files) : base(L.T("Compress PDF"), Catalog.Describe(files), 500)
    {
        var level = 1;
        var stack = new StackPanel();
        stack.Children.Add(Ui.Note(L.F("Now: {0}. Text stays selectable (except in the last mode).", OutputPaths.HumanSize(files.Sum(f => new FileInfo(f).Length)))));
        stack.Children.Add(Ui.Section(L.T("Compression level")));
        stack.Children.Add(Ui.Segmented(new[] { L.T("Gentle - for print"), L.T("Recommended - for sending"), L.T("Strong - for screens"), L.T("Maximum - pages as images") }, 1, i => level = i));
        Body.Content = stack;
        AddCancel();
        AddButton(L.T("Compress"), () =>
        {
            JobManager.RunBatch(L.F("Compressing PDFs ({0})", files.Count), "", files, async (f, ctx) =>
            {
                var output = OutputPaths.File(f, "pdf", L.T(" (compressed)"));
                var before = new FileInfo(f).Length;
                var after = await PdfEngine.CompressAsync(f, output, level, ctx);
                ctx.AddOutput(output);
                if (files.Count == 1)
                    ctx.Detail(after >= before ? L.T("The file is already optimal - it could not be reduced") : $"{OutputPaths.HumanSize(before)} → {OutputPaths.HumanSize(after)}");
            });
            Close();
        }, true);
    }
}

// ======================= PDF metadata =======================

public sealed class PdfMetadataDialog : ToolWindow
{
    public PdfMetadataDialog(IReadOnlyList<string> files) : base(L.T("PDF properties"), Catalog.Describe(files), 520)
    {
        PdfEngine.PdfMeta meta;
        try { meta = files.Count == 1 ? PdfEngine.ReadMeta(files[0]) : new PdfEngine.PdfMeta("", "", "", "", ""); }
        catch (Exception ex) { meta = new PdfEngine.PdfMeta("", "", "", "", ""); Subtitle.Text = ex.Message; }
        var title = new TextBox { Text = meta.Title }; var author = new TextBox { Text = meta.Author };
        var subject = new TextBox { Text = meta.Subject }; var keywords = new TextBox { Text = meta.Keywords };
        var creator = new TextBox { Text = meta.Creator };
        var stack = new StackPanel();
        stack.Children.Add(Ui.Field(L.T("Title"), title));
        stack.Children.Add(Ui.Field(L.T("Author"), author));
        stack.Children.Add(Ui.Field(L.T("Subject"), subject));
        stack.Children.Add(Ui.Field(L.T("Keywords"), keywords));
        stack.Children.Add(Ui.Field(L.T("Creator"), creator));
        Body.Content = stack;
        AddFooterLeft(new Button { Content = L.T("Remove all metadata"), Padding = new Thickness(14, 6, 14, 7) }.Also(b => b.Click += (_, _) =>
        {
            JobManager.RunBatch(L.F("Removing PDF metadata ({0})", files.Count), "", files, (f, ctx) => Task.Run(() =>
            {
                var output = OutputPaths.File(f, "pdf", L.T(" (no metadata)"));
                PdfEngine.WriteMeta(f, output, null);
                ctx.AddOutput(output);
            }));
            Close();
        }));
        AddCancel();
        AddButton(L.T("Save"), () =>
        {
            var m = new PdfEngine.PdfMeta(title.Text, author.Text, subject.Text, keywords.Text, creator.Text);
            JobManager.RunBatch(L.F("PDF properties ({0})", files.Count), "", files, (f, ctx) => Task.Run(() =>
            {
                var output = OutputPaths.File(f, "pdf", L.T(" (properties)"));
                PdfEngine.WriteMeta(f, output, m);
                ctx.AddOutput(output);
            }));
            Close();
        }, true);
    }
}

// ======================= File order =======================

/// <summary>Choosing the file order before joining/merging.</summary>
public sealed class OrderDialog : ToolWindow
{
    readonly ObservableCollection<string> _files;
    readonly ListBox _list = new() { Height = 300 };

    public OrderDialog(string title, IReadOnlyList<string> files, string actionText, Action<IReadOnlyList<string>> onOk) : base(title, L.T("Put the files in the desired order"), 540)
    {
        _files = new ObservableCollection<string>(files);
        _list.ItemsSource = _files;
        _list.ItemTemplate = new DataTemplate { VisualTree = MakeTemplate() };
        _list.SelectedIndex = 0;

        var buttons = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(Ui.IconButton("", L.T("Up"), () => Move(-1)));
        buttons.Children.Add(Ui.IconButton("", L.T("Down"), () => Move(1)));
        buttons.Children.Add(Ui.IconButton("", L.T("By name"), () => Sort()));
        buttons.Children.Add(Ui.IconButton("", L.T("Remove"), () => { if (_list.SelectedItem is string s && _files.Count > 2) _files.Remove(s); }));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(_list); grid.Children.Add(buttons);
        Body.Content = grid;
        AddCancel();
        AddButton(actionText, () => { onOk(_files.ToList()); Close(); }, true);
    }

    static FrameworkElementFactory MakeTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new FileNameConverter() });
        text.SetValue(TextBlock.MarginProperty, new Thickness(4, 6, 4, 6));
        panel.AppendChild(text);
        return panel;
    }

    sealed class FileNameConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => Path.GetFileName(value as string ?? "");
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    void Move(int delta)
    {
        var i = _list.SelectedIndex;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _files.Count) return;
        _files.Move(i, j);
        _list.SelectedIndex = j;
    }

    void Sort()
    {
        var sorted = _files.OrderBy(f => Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase).ToList();
        _files.Clear();
        foreach (var f in sorted) _files.Add(f);
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
