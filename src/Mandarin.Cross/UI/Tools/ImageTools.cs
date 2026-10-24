using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ImageMagick;
using ImageMagick.Drawing;
using Mandarin.Core;
using Mandarin.Engines;
using Color = Avalonia.Media.Color;
using Orientation = Avalonia.Layout.Orientation;

namespace Mandarin.UI.Tools;

static class Preview
{
    public static Border Host(Control child) => new()
    {
        Child = child, CornerRadius = new CornerRadius(8), ClipToBounds = true, Padding = new Thickness(8),
        Background = Look.IsDark ? Look.Brush(0, 0, 0, 60) : Look.Brush(0, 0, 0, 12)
    };

    public static readonly (string name, double? aspect)[] Aspects =
    {
        (L.T("Free"), null), (L.T("Original"), -1), ("1:1", 1), ("4:3", 4.0 / 3), ("3:2", 1.5), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16), ("4:5", 0.8), ("3:4", 0.75)
    };
}

/// <summary>Off-screen drawing at any resolution (results of the background, collage and annotation tools).</summary>
static class Offscreen
{
    /// <summary>Draws into a bitmap of the given size and returns straight (not premultiplied) BGRA pixels.</summary>
    public static byte[] Bgra(int width, int height, Action<DrawingContext> draw)
    {
        using var rtb = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext()) draw(ctx);
        return Pixels(rtb);
    }

    public static byte[] Pixels(Bitmap bmp)
    {
        int width = bmp.PixelSize.Width, height = bmp.PixelSize.Height;
        var buf = new byte[width * height * 4];
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { bmp.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), buf.Length, width * 4); }
        finally { handle.Free(); }
        // premultiplied → straight
        for (var i = 0; i < buf.Length; i += 4)
        {
            var a = buf[i + 3];
            if (a is 0 or 255) continue;
            buf[i] = (byte)Math.Min(255, buf[i] * 255 / a); buf[i + 1] = (byte)Math.Min(255, buf[i + 1] * 255 / a); buf[i + 2] = (byte)Math.Min(255, buf[i + 2] * 255 / a);
        }
        return buf;
    }

    public static RenderTargetBitmap Bitmap(int width, int height, Action<DrawingContext> draw)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(Math.Max(1, width), Math.Max(1, height)), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext()) draw(ctx);
        return rtb;
    }
}

// ======================= Crop =======================

public sealed class CropWindow : ToolWindow
{
    readonly IReadOnlyList<string> _files;
    readonly CropOverlay _overlay = new();
    readonly Image _image = new() { Stretch = Stretch.Fill };
    readonly Grid _surface = new() { UseLayoutRounding = false };
    int _fullW, _fullH, _rotation;
    Bitmap? _bmp;
    readonly TextBox _outW = new() { Width = 80 }, _outH = new() { Width = 80 };
    readonly TextBlock _info = new() { Opacity = 0.75, Margin = new Thickness(0, 6, 0, 0) };
    bool _flipH;

    public CropWindow(IReadOnlyList<string> files) : base(L.T("Crop"), Catalog.Describe(files), 1100, 760)
    {
        _files = files;
        _surface.Children.Add(_image);
        _surface.Children.Add(_overlay);
        ScreenUnits.SharpWhenEnlarged(_image);
        var viewbox = new Viewbox { Child = _surface, Stretch = Stretch.Uniform };

        var side = new StackPanel { Width = 280, Margin = new Thickness(16, 0, 0, 0) };
        side.Children.Add(Ui.Section(L.T("Aspect ratio"), 0));
        var aspectPanel = new WrapPanel();
        foreach (var (name, aspect) in Preview.Aspects)
        {
            var b = new Button { Content = name, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 4, 10, 5) };
            b.Click += (_, _) => _overlay.SetAspect(aspect == -1 ? _overlay.ContentWidth / _overlay.ContentHeight : aspect);
            aspectPanel.Children.Add(b);
        }
        side.Children.Add(aspectPanel);
        side.Children.Add(Ui.Section(L.T("Rotation")));
        var rot = new StackPanel { Orientation = Orientation.Horizontal };
        rot.Children.Add(Ui.IconButton("", L.T("Rotate left"), () => Rotate(-90), 36).Also(b => b.RenderTransform = new ScaleTransform(-1, 1)));
        rot.Children.Add(Ui.IconButton("", L.T("Rotate right"), () => Rotate(90), 36));
        rot.Children.Add(Ui.IconButton("", L.T("Flip"), () => { _flipH = !_flipH; _image.RenderTransform = new ScaleTransform(_flipH ? -1 : 1, 1); }, 36));
        side.Children.Add(rot);
        side.Children.Add(Ui.Section(L.T("Exact output size")));
        side.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _outW, new TextBlock { Text = " × ", VerticalAlignment = VerticalAlignment.Center }, _outH, new TextBlock { Text = " px", VerticalAlignment = VerticalAlignment.Center } }
        });
        side.Children.Add(Ui.Note(L.T("Optional. If both values are set, the frame takes this aspect ratio and the result gets exactly this size.")));
        void SizeChanged()
        {
            if (int.TryParse(_outW.Text, out var w) && int.TryParse(_outH.Text, out var h) && w > 0 && h > 0)
                _overlay.SetAspect((double)w / h);
        }
        _outW.LostFocus += (_, _) => SizeChanged();
        _outH.LostFocus += (_, _) => SizeChanged();
        side.Children.Add(_info);
        if (files.Count > 1)
            side.Children.Add(Ui.Note(L.F("The frame applies to all {0} images. With a fixed aspect ratio each one is cropped around its center.", files.Count)));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var host = Preview.Host(viewbox);
        Grid.SetColumn(side, 1);
        grid.Children.Add(host); grid.Children.Add(side);
        Body.Content = grid;

        _overlay.Changed += UpdateInfo;
        AddCancel();
        AddButton(L.T("Crop"), Save, true);
        Opened += async (_, _) =>
        {
            try
            {
                (_bmp, _fullW, _fullH) = await Task.Run(() => Thumbs.Preview(files[0], 1400));
                ShowBitmap();
            }
            catch (Exception ex) { await ShowMessage(ex.Message); Close(); }
        };
    }

    void ShowBitmap()
    {
        if (_bmp == null) return;
        int w = _bmp.PixelSize.Width, h = _bmp.PixelSize.Height;
        var rotated = _rotation is 90 or 270;
        _image.Source = _bmp;
        _image.RenderTransformOrigin = RelativePoint.Center;
        // the preview turns around its center; the overlay works in the rotated frame
        var cw = rotated ? h : w; var ch = rotated ? w : h;
        _image.Width = w; _image.Height = h;
        _image.HorizontalAlignment = HorizontalAlignment.Center; _image.VerticalAlignment = VerticalAlignment.Center;
        var t = new TransformGroup();
        if (_flipH) t.Children.Add(new ScaleTransform(-1, 1));
        t.Children.Add(new RotateTransform(_rotation));
        _image.RenderTransform = t;
        _surface.Width = cw; _surface.Height = ch;
        _overlay.SetContentSize(cw, ch);
        UpdateInfo();
    }

    void Rotate(int deg)
    {
        _rotation = ((_rotation + deg) % 360 + 360) % 360;
        ShowBitmap();
    }

    (double fx, double fy, double fw, double fh) Normalized()
    {
        var r = _overlay.Selection;
        return (r.X / _overlay.ContentWidth, r.Y / _overlay.ContentHeight, r.Width / _overlay.ContentWidth, r.Height / _overlay.ContentHeight);
    }

    void UpdateInfo()
    {
        if (_overlay.ContentWidth <= 0) return;
        var (_, _, fw, fh) = Normalized();
        var rotated = _rotation is 90 or 270;
        var w = (rotated ? _fullH : _fullW) * fw; var h = (rotated ? _fullW : _fullH) * fh;
        _info.Text = L.F("Selected: {0:0} × {1:0} px", w, h);
    }

    void Save()
    {
        if (_overlay.ContentWidth <= 0) return;
        var (fx, fy, fw, fh) = Normalized();
        var aspect = _overlay.Aspect;
        var rotation = _rotation;
        var flip = _flipH;
        int? outW = int.TryParse(_outW.Text, out var ow) && ow > 0 ? ow : null;
        int? outH = int.TryParse(_outH.Text, out var oh) && oh > 0 ? oh : null;
        var multi = _files.Count > 1;
        JobManager.RunBatch(L.F("Crop ({0})", _files.Count), "", _files, (f, ctx) => Task.Run(() =>
        {
            using var img = ImageEngine.Load(f);
            if (flip) img.Flop();
            if (rotation != 0) img.Rotate(rotation);
            int x, y, w, h;
            if (multi && aspect is { } a)
            {
                w = (int)img.Width; h = (int)Math.Round(w / a);
                if (h > img.Height) { h = (int)img.Height; w = (int)Math.Round(h * a); }
                x = ((int)img.Width - w) / 2; y = ((int)img.Height - h) / 2;
            }
            else
            {
                x = (int)Math.Round(fx * img.Width); y = (int)Math.Round(fy * img.Height);
                w = (int)Math.Round(fw * img.Width); h = (int)Math.Round(fh * img.Height);
            }
            w = Math.Max(1, Math.Min(w, (int)img.Width - x)); h = Math.Max(1, Math.Min(h, (int)img.Height - y));
            img.Crop(new MagickGeometry(x, y, (uint)w, (uint)h));
            img.ResetPage();
            if (outW is { } tw && outH is { } th) img.Resize(new MagickGeometry((uint)tw, (uint)th) { IgnoreAspectRatio = true });
            else if (outW is { } tw2) img.Resize(new MagickGeometry((uint)tw2, 0));
            else if (outH is { } th2) img.Resize(new MagickGeometry(0, (uint)th2));
            var ext = ImageEngine.OutputExtFor(f);
            var output = OutputPaths.File(f, ext, L.T(" (cropped)"));
            ImageEngine.PrepareForTarget(img, ext);
            ImageEngine.Write(img, output, ext);
            ctx.AddOutput(output);
        }));
        Close();
    }
}

// ======================= Background =======================

public sealed class BackgroundOptions
{
    public int Kind; // 0 color, 1 gradient, 2 image, 3 blurred photo
    public Color Color1 = Color.FromRgb(0xFF, 0xE3, 0xC8), Color2 = Color.FromRgb(0xF2, 0x6B, 0x1D);
    public double Angle = 135;
    public string? Image;
    public double? Aspect;
    public double Padding = 0.08, Radius = 0.03;
    public bool Shadow = true;
}

public sealed class BackgroundWindow : ToolWindow
{
    readonly IReadOnlyList<string> _files;
    readonly BackgroundOptions _o = new();
    readonly Image _preview = new() { Stretch = Stretch.Uniform };
    Bitmap? _photo, _blurred, _bgImage;

    public BackgroundWindow(IReadOnlyList<string> files) : base(L.T("Add a background"), Catalog.Describe(files), 1100, 760)
    {
        _files = files;
        ScreenUnits.SharpWhenEnlarged(_preview);
        var side = new StackPanel { Width = 310, Margin = new Thickness(16, 0, 18, 0) };
        side.Children.Add(Ui.Section(L.T("Background"), 0));
        var color1 = Ui.ColorPicker(_o.Color1, c => { _o.Color1 = c; Render(); }, new[]
        {
            Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xF3, 0xF3, 0xF3), Color.FromRgb(0x1E, 0x1E, 0x1E), Color.FromRgb(0xFF, 0xE3, 0xC8),
            Color.FromRgb(0xDC, 0xEB, 0xFF), Color.FromRgb(0xE6, 0xF4, 0xEA), Color.FromRgb(0xF2, 0x6B, 0x1D), Color.FromRgb(0x6C, 0x5C, 0xE7)
        });
        var color2 = Ui.ColorPicker(_o.Color2, c => { _o.Color2 = c; Render(); });
        var (angleRow, _) = Ui.LabeledSlider(L.T("Angle"), 0, 360, _o.Angle, "0'°'", v => { _o.Angle = v; Render(); }, 15);
        var pickImage = new Button { Content = L.T("Choose an image…"), Padding = new Thickness(12, 5, 12, 6), Margin = new Thickness(0, 4, 0, 0) };
        pickImage.Click += async (_, _) =>
        {
            if (await Pick.Image(this) is not { } path) return;
            _o.Image = path;
            _bgImage = (await Task.Run(() => Thumbs.Preview(path, 1600))).bmp;
            Render();
        };
        var color2Label = Ui.Section(L.T("Second color"), 6);
        void Vis()
        {
            color1.IsVisible = _o.Kind is 0 or 1;
            color2.IsVisible = color2Label.IsVisible = angleRow.IsVisible = _o.Kind == 1;
            pickImage.IsVisible = _o.Kind == 2;
        }
        side.Children.Add(Ui.Segmented(new[] { L.T("Color"), L.T("Gradient"), L.T("Image"), L.T("Blurred photo") }, 0, i => { _o.Kind = i; Vis(); Render(); }));
        side.Children.Add(color1); side.Children.Add(color2Label); side.Children.Add(color2); side.Children.Add(angleRow); side.Children.Add(pickImage);
        Vis();

        side.Children.Add(Ui.Section(L.T("Aspect ratio")));
        var aspects = new (string, double?)[] { (L.T("Same as the photo"), null), ("1:1", 1), ("4:5", 0.8), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16), ("4:3", 4.0 / 3) };
        side.Children.Add(Ui.Segmented(aspects.Select(a => a.Item1).ToArray(), 0, i => { _o.Aspect = aspects[i].Item2; Render(); }));
        side.Children.Add(Ui.Section(L.T("Style")));
        side.Children.Add(Ui.LabeledSlider(L.T("Padding"), 0, 40, _o.Padding * 100, "0'%'", v => { _o.Padding = v / 100; Render(); }).row);
        side.Children.Add(Ui.LabeledSlider(L.T("Corner radius"), 0, 25, _o.Radius * 100, "0'%'", v => { _o.Radius = v / 100; Render(); }).row);
        var shadow = new CheckBox { Content = L.T("Shadow"), IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
        shadow.IsCheckedChanged += (_, _) => { _o.Shadow = shadow.IsChecked == true; Render(); };
        side.Children.Add(shadow);
        if (files.Count > 1) side.Children.Add(Ui.Note(L.F("The settings apply to all {0} images.", files.Count)));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var scroll = new ScrollViewer { Content = side, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(Preview.Host(_preview)); grid.Children.Add(scroll);
        Body.Content = grid;
        AddCancel();
        AddButton(L.T("Save"), Save, true);
        Opened += async (_, _) =>
        {
            try
            {
                _photo = (await Task.Run(() => Thumbs.Preview(files[0], 1200))).bmp;
                _blurred = await Task.Run(() => Blurred(files[0], 600));
                Render();
            }
            catch (Exception ex) { await ShowMessage(ex.Message); Close(); }
        };
    }

    public static Bitmap Blurred(string file, int maxSide)
    {
        using var img = ImageEngine.Load(file);
        img.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide));
        img.Blur(0, maxSide / 25.0);
        img.Modulate(new Percentage(105), new Percentage(120), new Percentage(100));
        using var px = img.GetPixelsUnsafe();
        return Thumbs.FromBgra(px.ToByteArray(PixelMapping.BGRA)!, (int)img.Width, (int)img.Height);
    }

    void Render()
    {
        if (_photo == null) return;
        var (cw, ch) = Size(_photo.PixelSize.Width, _photo.PixelSize.Height, _o);
        var scale = Math.Min(1, 1100.0 / Math.Max(cw, ch));
        var photo = _photo; var o = _o; var blurred = _blurred; var bg = _bgImage;
        _preview.Source = Offscreen.Bitmap((int)(cw * scale), (int)(ch * scale), dc =>
        {
            using (dc.PushTransform(Matrix.CreateScale(scale, scale))) Compose(dc, photo, photo.PixelSize.Width, photo.PixelSize.Height, o, blurred, bg);
        });
    }

    public static (double cw, double ch) Size(double pw, double ph, BackgroundOptions o)
    {
        var pad = o.Padding * Math.Max(pw, ph);
        double iw = pw + 2 * pad, ih = ph + 2 * pad;
        if (o.Aspect is not { } a) return (iw, ih);
        return iw / ih > a ? (iw, iw / a) : (ih * a, ih);
    }

    /// <summary>"Photo on a background" scene in photo pixel coordinates.</summary>
    public static void Compose(DrawingContext dc, Bitmap photo, double pw, double ph, BackgroundOptions o, Bitmap? blurred, Bitmap? bgImage)
    {
        var pad = o.Padding * Math.Max(pw, ph);
        var (cw, ch) = Size(pw, ph, o);
        var canvas = new Rect(0, 0, cw, ch);
        var photoRect = new Rect((cw - pw) / 2, (ch - ph) / 2, pw, ph);
        var radius = o.Radius * Math.Min(pw, ph);

        switch (o.Kind)
        {
            case 1:
                var rad = o.Angle * Math.PI / 180;
                var vx = Math.Cos(rad) * 0.5; var vy = Math.Sin(rad) * 0.5;
                dc.DrawRectangle(new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0.5 - vx, 0.5 - vy, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5 + vx, 0.5 + vy, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(o.Color1, 0), new GradientStop(o.Color2, 1) }
                }, null, canvas);
                break;
            case 2 when bgImage != null:
                DrawCover(dc, bgImage, canvas);
                break;
            case 3 when blurred != null:
                DrawCover(dc, blurred, canvas);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), null, canvas);
                break;
            default:
                dc.DrawRectangle(new SolidColorBrush(o.Color1), null, canvas);
                break;
        }
        if (o.Shadow && pad > 0)
        {
            var spread = Math.Max(pw, ph) * 0.03;
            for (var i = 8; i >= 1; i--)
            {
                var g = spread * i / 8;
                var r = new Rect(photoRect.X - g * 0.5, photoRect.Y - g * 0.2 + spread * 0.5, photoRect.Width + g, photoRect.Height + g);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(9 + (8 - i) * 2), 0, 0, 0)), null, r, radius + g / 2, radius + g / 2);
            }
        }
        using (dc.PushClip(new RoundedRect(photoRect, radius)))
            dc.DrawImage(photo, new Rect(0, 0, photo.PixelSize.Width, photo.PixelSize.Height), photoRect);
    }

    /// <summary>Fills the rectangle with the picture, cropping around the center.</summary>
    static void DrawCover(DrawingContext dc, Bitmap bmp, Rect target)
    {
        double bw = bmp.PixelSize.Width, bh = bmp.PixelSize.Height;
        var scale = Math.Max(target.Width / bw, target.Height / bh);
        double sw = target.Width / scale, sh = target.Height / scale;
        using (dc.PushClip(target))
            dc.DrawImage(bmp, new Rect((bw - sw) / 2, (bh - sh) / 2, sw, sh), target);
    }

    void Save()
    {
        var o = _o;
        var bgPath = o.Image;
        JobManager.RunBatch(L.F("Background ({0})", _files.Count), "", _files, async (f, ctx) =>
        {
            var (photo, _, _) = await Task.Run(() => Thumbs.Preview(f, 4096));
            var blurred = o.Kind == 3 ? await Task.Run(() => Blurred(f, 800)) : null;
            var bg = o.Kind == 2 && bgPath != null ? (await Task.Run(() => Thumbs.Preview(bgPath, 4096))).bmp : null;
            var (px, w, h) = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var (cw, ch) = Size(photo.PixelSize.Width, photo.PixelSize.Height, o);
                int iw = (int)Math.Round(cw), ih = (int)Math.Round(ch);
                var buf = Offscreen.Bgra(iw, ih, dc => Compose(dc, photo, photo.PixelSize.Width, photo.PixelSize.Height, o, blurred, bg));
                return (buf, iw, ih);
            });
            await Task.Run(() =>
            {
                var ext = ImageEngine.OutputExtFor(f);
                var output = OutputPaths.File(f, ext, L.T(" (background)"));
                ImageEngine.SaveBgra(px, w, h, output, f);
                ctx.AddOutput(output);
            });
        });
        Close();
    }
}

// ======================= Redact areas and text =======================

public sealed class RedactImageWindow : ToolWindow
{
    readonly string _file;
    readonly RegionsOverlay _overlay = new();
    readonly Image _image = new() { Stretch = Stretch.Fill };
    readonly Grid _surface = new() { UseLayoutRounding = false };
    Bitmap? _bmp;
    int _fullW, _fullH;
    int _style = 3; // 0 black, 1 white, 2 color, 3 blur, 4 pixelate
    Color _color = Ui.Palette[6];
    readonly TextBlock _status = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    public RedactImageWindow(string file) : base(L.T("Redact areas and text"), Path.GetFileName(file), 1100, 760)
    {
        _file = file;
        _surface.Children.Add(_image);
        _surface.Children.Add(_overlay);
        ScreenUnits.SharpWhenEnlarged(_image);
        var side = new StackPanel { Width = 290, Margin = new Thickness(16, 0, 0, 0) };
        side.Children.Add(Ui.Section(L.T("Find automatically"), 0));
        var text = new Button { Content = L.T("Text"), Padding = new Thickness(14, 5, 14, 6), IsEnabled = WinRt.OcrAvailable };
        text.Click += async (_, _) => await DetectText();
        side.Children.Add(text);
        side.Children.Add(Ui.Note(WinRt.OcrAvailable
            ? L.T("Text is found by Tesseract, without the internet. Areas can also be added by hand - drag around them with the mouse.")
            : L.T("Drag around the areas to hide with the mouse. Finding text automatically needs Tesseract OCR.")));
        side.Children.Add(_status);
        side.Children.Add(Ui.Section(L.T("How to redact")));
        side.Children.Add(Ui.Segmented(new[] { L.T("Black"), L.T("White"), L.T("Color fill"), L.T("Blur"), L.T("Pixelate") }, 3, i => _style = i));
        side.Children.Add(Ui.ColorPicker(_color, c => _color = c));
        side.Children.Add(Ui.Section(L.T("Areas")));
        side.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new Button { Content = L.T("Delete selected"), Padding = new Thickness(10, 4, 10, 5), Margin = new Thickness(0, 0, 8, 0) }.Also(b => b.Click += (_, _) => _overlay.RemoveSelected()),
                new Button { Content = L.T("Clear"), Padding = new Thickness(10, 4, 10, 5) }.Also(b => b.Click += (_, _) => _overlay.Clear())
            }
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var host = Preview.Host(new Viewbox { Child = _surface });
        Grid.SetColumn(side, 1);
        grid.Children.Add(host); grid.Children.Add(side);
        Body.Content = grid;
        _overlay.RegionsChanged += () => _status.Text = L.F("Areas: {0}", _overlay.Regions.Count);
        AddCancel();
        AddButton(L.T("Save a copy"), Save, true);
        Opened += async (_, _) =>
        {
            try
            {
                (_bmp, _fullW, _fullH) = await Task.Run(() => Thumbs.Preview(file, 1800));
                _image.Source = _bmp;
                _surface.Width = _bmp.PixelSize.Width; _surface.Height = _bmp.PixelSize.Height;
                _overlay.SetContentSize(_bmp.PixelSize.Width, _bmp.PixelSize.Height);
            }
            catch (Exception ex) { await ShowMessage(ex.Message); Close(); }
        };
    }

    async Task DetectText()
    {
        if (_bmp == null) return;
        _status.Text = L.T("Looking for text…");
        try
        {
            var found = 0;
            foreach (var line in await WinRt.RecognizeAsync(Look.Png(_bmp)))
            {
                var pad = line.H * 0.15;
                if (ClipToImage(new Rect(line.X - pad, line.Y - pad, line.W + pad * 2, line.H + pad * 2)) is { } r) { _overlay.Add(r); found++; }
            }
            _status.Text = found == 0 ? L.T("No text found") : L.F("Text lines found: {0}", found);
        }
        catch (Exception ex) { _status.Text = ex is UserFacingException ? ex.Message : L.T("Recognition error: ") + ex.Message; }
    }

    Rect? ClipToImage(Rect r)
    {
        var c = r.Intersect(new Rect(0, 0, _overlay.ContentWidth, _overlay.ContentHeight));
        return c.Width < 2 || c.Height < 2 ? null : c;
    }

    void Save()
    {
        if (_bmp == null) return;
        var sx = (double)_fullW / _bmp.PixelSize.Width; var sy = (double)_fullH / _bmp.PixelSize.Height;
        var rects = _overlay.Regions.Select(r => new Rect(r.Rect.X * sx, r.Rect.Y * sy, r.Rect.Width * sx, r.Rect.Height * sy)).ToList();
        if (rects.Count == 0) { _status.Text = L.T("Add at least one area"); return; }
        var style = _style; var color = _color; var file = _file;
        JobManager.Run(L.F("{0} - redaction", Path.GetFileName(file)), "", new[] { file }, ctx => Task.Run(() =>
        {
            using var img = ImageEngine.Load(file);
            ApplyRedaction(img, rects, style, color);
            var ext = ImageEngine.OutputExtFor(file);
            var output = OutputPaths.File(file, ext, L.T(" (redacted)"));
            ImageEngine.StripMetadata(img);
            ImageEngine.ApplyQuality(img, ext);
            ImageEngine.Write(img, output, ext);
            ctx.AddOutput(output);
        }));
        Close();
    }

    public static void ApplyRedaction(MagickImage img, IEnumerable<Rect> rects, int style, Color color)
    {
        foreach (var r in rects)
        {
            var x = (int)Math.Max(0, r.X); var y = (int)Math.Max(0, r.Y);
            var w = (int)Math.Min(img.Width - x, r.Width); var h = (int)Math.Min(img.Height - y, r.Height);
            if (w < 2 || h < 2) continue;
            var geo = new MagickGeometry(x, y, (uint)w, (uint)h);
            if (style is 3 or 4)
            {
                using var part = (MagickImage)img.Clone();
                part.Crop(geo);
                part.ResetPage();
                if (style == 3)
                {
                    part.Resize(new Percentage(12.5));
                    part.Blur(0, 3);
                    part.Resize(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });
                    part.Blur(0, Math.Max(2, Math.Min(w, h) / 12.0));
                }
                else
                {
                    var block = Math.Max(6, Math.Min(w, h) / 8);
                    part.Sample(new MagickGeometry((uint)Math.Max(1, w / block), (uint)Math.Max(1, h / block)) { IgnoreAspectRatio = true });
                    part.Sample(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });
                }
                img.Composite(part, x, y, CompositeOperator.Over);
            }
            else
            {
                var c = style switch { 0 => MagickColors.Black, 1 => MagickColors.White, _ => new MagickColor(color.R, color.G, color.B) };
                new Drawables().FillColor(c).Rectangle(x, y, x + w, y + h).Draw(img);
            }
        }
    }
}

// ======================= Collage =======================

public sealed class CollageWindow : ToolWindow
{
    readonly ObservableCollection<string> _files;
    readonly ListBox _list = new() { Height = 170 };
    readonly Image _preview = new() { Stretch = Stretch.Uniform };
    readonly Dictionary<string, Bitmap> _thumbs = new();
    int _layout; double _gap = 2, _radius = 2; Color _bg = Colors.White; int _width = 2048; bool _png;

    public CollageWindow(IReadOnlyList<string> files) : base(L.T("Collage"), Catalog.Describe(files), 1150, 780)
    {
        _files = new ObservableCollection<string>(files);
        _list.ItemsSource = _files;
        _list.ItemTemplate = new FuncDataTemplate<string>((s, _) => new TextBlock { Text = Path.GetFileName(s ?? "") });
        _list.SelectedIndex = 0;

        var side = new StackPanel { Width = 320, Margin = new Thickness(16, 0, 18, 0) };
        side.Children.Add(Ui.Section(L.T("Layout"), 0));
        side.Children.Add(Ui.Segmented(new[] { L.T("Grid"), L.T("Row"), L.T("Column"), L.T("Featured photo") }, 0, i => { _layout = i; Render(); }));
        side.Children.Add(Ui.Section(L.T("Order")));
        var listRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var btns = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
        btns.Children.Add(Ui.IconButton("", L.T("Up"), () => Move(-1)));
        btns.Children.Add(Ui.IconButton("", L.T("Down"), () => Move(1)));
        Grid.SetColumn(btns, 1);
        listRow.Children.Add(_list); listRow.Children.Add(btns);
        side.Children.Add(listRow);
        side.Children.Add(Ui.Section(L.T("Style")));
        side.Children.Add(Ui.LabeledSlider(L.T("Spacing"), 0, 8, _gap, "0.#'%'", v => { _gap = v; Render(); }).row);
        side.Children.Add(Ui.LabeledSlider(L.T("Corner radius"), 0, 10, _radius, "0.#'%'", v => { _radius = v; Render(); }).row);
        side.Children.Add(Ui.ColorPicker(_bg, c => { _bg = c; Render(); }, new[]
        {
            Colors.White, Color.FromRgb(0xF3, 0xF3, 0xF3), Color.FromRgb(0x1E, 0x1E, 0x1E), Colors.Black,
            Color.FromRgb(0xFF, 0xE3, 0xC8), Color.FromRgb(0xF2, 0x6B, 0x1D), Color.FromRgb(0xDC, 0xEB, 0xFF)
        }));
        side.Children.Add(Ui.Section(L.T("Result")));
        var widths = new[] { 1080, 1600, 2048, 3000, 4000 };
        side.Children.Add(Ui.Segmented(widths.Select(w => $"{w}px").ToArray(), 2, i => _width = widths[i]));
        side.Children.Add(Ui.Segmented(new[] { "JPG", "PNG" }, 0, i => _png = i == 1));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var scroll = new ScrollViewer { Content = side, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(Preview.Host(_preview)); grid.Children.Add(scroll);
        Body.Content = grid;
        AddCancel();
        AddButton(L.T("Create collage"), Save, true);

        Opened += async (_, _) =>
        {
            foreach (var f in _files.ToList())
            {
                try { _thumbs[f] = (await Task.Run(() => Thumbs.Preview(f, 700))).bmp; }
                catch { _files.Remove(f); }
            }
            Render();
        };
    }

    void Move(int d)
    {
        var i = _list.SelectedIndex; var j = i + d;
        if (i < 0 || j < 0 || j >= _files.Count) return;
        _files.Move(i, j); _list.SelectedIndex = j; Render();
    }

    void Render()
    {
        var images = _files.Where(_thumbs.ContainsKey).Select(f => _thumbs[f]).ToList();
        if (images.Count == 0) return;
        var cells = Layout(images, 1000, _layout, _gap / 100, out var h);
        var radius = _radius / 100 * 1000; var bg = _bg;
        _preview.Source = Offscreen.Bitmap(1000, (int)Math.Max(1, h), dc => Draw(dc, images, cells, 1000, h, radius, bg));
    }

    public static List<Rect> Layout(IReadOnlyList<Bitmap> imgs, double width, int layout, double gapF, out double height)
    {
        var gap = gapF * width;
        var cells = new List<Rect>();
        var aspects = imgs.Select(i => (double)i.PixelSize.Width / i.PixelSize.Height).ToList();
        var n = imgs.Count;
        switch (layout)
        {
            case 1:
            {
                var h = (width - (n + 1) * gap) / aspects.Sum();
                var x = gap;
                foreach (var a in aspects) { cells.Add(new Rect(x, gap, h * a, h)); x += h * a + gap; }
                height = h + 2 * gap;
                break;
            }
            case 2:
            {
                var w = width - 2 * gap; var y = gap;
                foreach (var a in aspects) { cells.Add(new Rect(gap, y, w, w / a)); y += w / a + gap; }
                height = y;
                break;
            }
            case 3 when n > 1:
            {
                var leftW = (width - 3 * gap) * 0.64;
                var rightW = width - 3 * gap - leftW;
                var leftH = Math.Max(leftW / aspects[0], (n - 1) * 60.0);
                cells.Add(new Rect(gap, gap, leftW, leftH));
                var ch = (leftH - (n - 2) * gap) / (n - 1);
                for (var i = 1; i < n; i++) cells.Add(new Rect(2 * gap + leftW, gap + (i - 1) * (ch + gap), rightW, ch));
                height = leftH + 2 * gap;
                break;
            }
            default:
            {
                var cols = (int)Math.Ceiling(Math.Sqrt(n));
                var rows = (int)Math.Ceiling((double)n / cols);
                var cellAspect = Math.Clamp(aspects.Average(), 0.6, 1.8);
                var cw = (width - (cols + 1) * gap) / cols;
                var chh = cw / cellAspect;
                for (var i = 0; i < n; i++)
                {
                    var r = i / cols; var c = i % cols;
                    var inRow = r == rows - 1 ? n - r * cols : cols;
                    var offset = (cols - inRow) * (cw + gap) / 2;
                    cells.Add(new Rect(gap + offset + c * (cw + gap), gap + r * (chh + gap), cw, chh));
                }
                height = rows * chh + (rows + 1) * gap;
                break;
            }
        }
        return cells;
    }

    public static void Draw(DrawingContext dc, IReadOnlyList<Bitmap> imgs, List<Rect> cells, double width, double height, double radius, Color bg)
    {
        dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, width, height));
        for (var i = 0; i < imgs.Count; i++)
        {
            var cell = cells[i];
            double bw = imgs[i].PixelSize.Width, bh = imgs[i].PixelSize.Height;
            var scale = Math.Max(cell.Width / bw, cell.Height / bh);
            double sw = cell.Width / scale, sh = cell.Height / scale;
            using (dc.PushClip(new RoundedRect(cell, radius)))
                dc.DrawImage(imgs[i], new Rect((bw - sw) / 2, (bh - sh) / 2, sw, sh), cell);
        }
    }

    void Save()
    {
        var files = _files.ToList();
        int layout = _layout, width = _width; double gap = _gap / 100, radius = _radius / 100; var bg = _bg; var png = _png;
        JobManager.Run(L.F("Collage of {0} photos", files.Count), "", files, async ctx =>
        {
            var perImage = Math.Clamp(width * 2 / Math.Max(1, (int)Math.Ceiling(Math.Sqrt(files.Count))), 600, 3000);
            var images = new List<Bitmap>();
            foreach (var f in files) images.Add((await Task.Run(() => Thumbs.Preview(f, perImage))).bmp);
            var (px, w, h) = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var cells = Layout(images, width, layout, gap, out var hh);
                var ih = (int)Math.Round(hh);
                return (Offscreen.Bgra(width, ih, dc => Draw(dc, images, cells, width, hh, radius * width, bg)), width, ih);
            });
            await Task.Run(() =>
            {
                var output = OutputPaths.UniqueFile(OutputPaths.DirectoryFor(files[0]), L.T("Collage"), png ? "png" : "jpg");
                ImageEngine.SaveBgra(px, w, h, output);
                ctx.AddOutput(output);
            });
        });
        Close();
    }
}
