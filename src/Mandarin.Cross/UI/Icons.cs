using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Material.Icons;

namespace Mandarin.UI;

/// <summary>
/// Icons. The shared code and the Windows app name icons by Segoe Fluent Icons code points, which exist only on
/// Windows 10/11; here every one of them is drawn with the matching Material Design icon (Apache 2.0, via Material.Icons).
/// </summary>
public static class Icons
{
    static readonly Dictionary<char, MaterialIconKind> BySegoe = new()
    {
        [''] = MaterialIconKind.ChevronDown, [''] = MaterialIconKind.ChevronUp, [''] = MaterialIconKind.PencilOutline,
        [''] = MaterialIconKind.Close, [''] = MaterialIconKind.CogOutline, [''] = MaterialIconKind.VideoOutline,
        [''] = MaterialIconKind.PinOutline, [''] = MaterialIconKind.Merge, [''] = MaterialIconKind.CameraOutline,
        [''] = MaterialIconKind.ArrowRight, [''] = MaterialIconKind.ArrowLeft, [''] = MaterialIconKind.ShareVariantOutline,
        [''] = MaterialIconKind.Minus, [''] = MaterialIconKind.SquareOutline, [''] = MaterialIconKind.Check,
        [''] = MaterialIconKind.ArrowCollapse, [''] = MaterialIconKind.DeleteOutline, [''] = MaterialIconKind.VolumeOff,
        [''] = MaterialIconKind.Console, [''] = MaterialIconKind.VolumeHigh, [''] = MaterialIconKind.Play,
        [''] = MaterialIconKind.Pause, [''] = MaterialIconKind.ChevronLeft, [''] = MaterialIconKind.ChevronRight,
        [''] = MaterialIconKind.PaletteOutline, [''] = MaterialIconKind.InformationOutline, [''] = MaterialIconKind.ContentCut,
        [''] = MaterialIconKind.TuneVariant, [''] = MaterialIconKind.Undo, [''] = MaterialIconKind.Crop,
        [''] = MaterialIconKind.RotateRight, [''] = MaterialIconKind.PackageVariantClosed, [''] = MaterialIconKind.AlertOutline,
        [''] = MaterialIconKind.FileOutline, [''] = MaterialIconKind.Marker, [''] = MaterialIconKind.ClosedCaptionOutline,
        [''] = MaterialIconKind.Presentation, [''] = MaterialIconKind.Speaker, [''] = MaterialIconKind.Table,
        [''] = MaterialIconKind.HomeOutline, [''] = MaterialIconKind.History, [''] = MaterialIconKind.BookOpenPageVariantOutline,
        [''] = MaterialIconKind.SkipPrevious, [''] = MaterialIconKind.SkipNext, [''] = MaterialIconKind.TrayArrowUp,
        [''] = MaterialIconKind.FileDocumentOutline, [''] = MaterialIconKind.ViewGridOutline, [''] = MaterialIconKind.FlipHorizontal,
        [''] = MaterialIconKind.CursorDefaultOutline, [''] = MaterialIconKind.SelectAll, [''] = MaterialIconKind.FolderOutline,
        [''] = MaterialIconKind.CallSplit, [''] = MaterialIconKind.ContentCopy, [''] = MaterialIconKind.SortAlphabeticalAscending,
        [''] = MaterialIconKind.FormatText, [''] = MaterialIconKind.MusicNote, [''] = MaterialIconKind.FolderMoveOutline,
        [''] = MaterialIconKind.TagOutline, [''] = MaterialIconKind.FormatListBulleted, [''] = MaterialIconKind.ImageOutline,
        [''] = MaterialIconKind.InformationOutline, [''] = MaterialIconKind.Numeric1CircleOutline, [''] = MaterialIconKind.Waveform,
        [''] = MaterialIconKind.CircleOutline, [''] = MaterialIconKind.FilePdfBox, [''] = MaterialIconKind.Speedometer,
        [''] = MaterialIconKind.EyeOffOutline, [''] = MaterialIconKind.Pen, [''] = MaterialIconKind.ViewDashboardOutline,
    };

    static readonly Dictionary<MaterialIconKind, Geometry> Cache = new();

    public static Geometry Geometry(MaterialIconKind kind)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(kind, out var g))
                Cache[kind] = g = StreamGeometry.Parse(MaterialIconDataProvider.GetData(kind));
            return g;
        }
    }

    public static MaterialIconKind KindOf(string glyph) =>
        glyph.Length > 0 && BySegoe.TryGetValue(glyph[0], out var k) ? k : MaterialIconKind.FileOutline;

    /// <summary>An icon control of the given size; the color follows the text color unless one is given.</summary>
    public static Control Make(string glyph, double size, IBrush? brush = null) => Make(KindOf(glyph), size, brush);

    public static Control Make(MaterialIconKind kind, double size, IBrush? brush = null)
    {
        var icon = new PathIcon { Data = Geometry(kind), Width = size, Height = size };
        if (brush != null) icon.Foreground = brush;
        return icon;
    }

    /// <summary>A plain path (for drawing inside a canvas, where the color is set explicitly).</summary>
    public static Avalonia.Controls.Shapes.Path MakePath(string glyph, double size, IBrush brush) => new()
    {
        Data = Geometry(KindOf(glyph)), Fill = brush, Width = size, Height = size, Stretch = Stretch.Uniform
    };
}
