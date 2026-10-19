using System.IO;
using ImageMagick;
using Mandarin.Core;
using Mandarin.Engines;
using Mandarin.UI.Tools;

namespace Mandarin.UI;

/// <summary>Launches tools from the wheel: a dialog or an immediate task (no clicks).</summary>
public static class ToolLauncher
{
    static void Show(ToolWindow w)
    {
        w.Show();
        w.Activate();
    }

    static bool NeedFfmpeg()
    {
        if (Ffmpeg.Available) return true;
        Hud.ShowError(L.T("FFmpeg is required"), L.T("Install it with \"winget install Gyan.FFmpeg\" or set its path in Mandarin settings."));
        return false;
    }

    // ---------- Images ----------

    public static void ImageCompress(IReadOnlyList<string> f) => Show(new ImageCompressDialog(f));
    public static void ImageMetadata(IReadOnlyList<string> f) => Show(new ImageMetadataDialog(f));
    public static void PhotoEditor(IReadOnlyList<string> f) => Show(new PhotoEditorWindow(f[0]));
    public static void Annotate(IReadOnlyList<string> f) => Show(new AnnotateWindow(f[0]));
    public static void AddBackground(IReadOnlyList<string> f) => Show(new BackgroundWindow(f));
    public static void Crop(IReadOnlyList<string> f) => Show(new CropWindow(f));
    public static void RedactImage(IReadOnlyList<string> f) => Show(new RedactImageWindow(f[0]));
    public static void Collage(IReadOnlyList<string> f) => Show(new CollageWindow(f));

    public static void RotateImages(IReadOnlyList<string> files) =>
        JobManager.RunBatch(L.F("Rotate ({0})", files.Count), "", files, (f, ctx) => Task.Run(() =>
        {
            using var img = ImageEngine.Load(f);
            img.Rotate(90);
            var ext = ImageEngine.OutputExtFor(f);
            var output = OutputPaths.File(f, ext, L.T(" (rotated)"));
            ImageEngine.PrepareForTarget(img, ext);
            ImageEngine.Write(img, output, ext);
            ctx.AddOutput(output);
        }));

    public static void ImagesToPdf(IReadOnlyList<string> files) =>
        JobManager.Run(L.F("PDF from {0} images", files.Count), "", files, ctx => Task.Run(() =>
        {
            var output = OutputPaths.UniqueFile(OutputPaths.DirectoryFor(files[0]), L.T("Images"), "pdf");
            PdfEngine.ImagesToPdf(files, output, ctx);
            ctx.AddOutput(output);
        }));

    // ---------- Video ----------

    public static void VideoCompress(IReadOnlyList<string> f) { if (NeedFfmpeg()) Show(new VideoCompressDialog(f)); }

    public static async void MediaMetadata(IReadOnlyList<string> f)
    {
        if (!NeedFfmpeg()) return;
        MediaInfo? info = null;
        try { if (f.Count == 1) info = await Ffmpeg.ProbeAsync(f[0]); } catch { /* show empty fields */ }
        Show(new MediaMetadataDialog(f, info));
    }

    public static void Mute(IReadOnlyList<string> files)
    {
        if (!NeedFfmpeg()) return;
        JobManager.RunBatch(L.F("Mute ({0})", files.Count), "", files, MediaEngine.MuteAsync);
    }

    public static void TrimVideo(IReadOnlyList<string> f) { if (NeedFfmpeg()) Show(new MediaEditorWindow(f[0], MediaEditMode.Trim)); }
    public static void CropVideo(IReadOnlyList<string> f) { if (NeedFfmpeg()) Show(new VideoOverlayWindow(f[0], false)); }
    public static void Speed(IReadOnlyList<string> f) { if (NeedFfmpeg()) Show(new MediaEditorWindow(f[0], MediaEditMode.Speed)); }
    public static void Snapshot(IReadOnlyList<string> f) { if (NeedFfmpeg()) Show(new MediaEditorWindow(f[0], MediaEditMode.Snapshot)); }
    public static void SplitVideo(IReadOnlyList<string> f) { if (NeedFfmpeg()) Show(new MediaEditorWindow(f[0], MediaEditMode.Split)); }
    public static void RedactVideo(IReadOnlyList<string> f) { if (NeedFfmpeg()) Show(new VideoOverlayWindow(f[0], true)); }

    public static void JoinVideos(IReadOnlyList<string> files)
    {
        if (!NeedFfmpeg()) return;
        Show(new OrderDialog(L.T("Join videos"), files, L.T("Join"), ordered =>
            JobManager.Run(L.F("Joining {0} videos", ordered.Count), "", ordered, ctx => MediaEngine.JoinVideosAsync(ordered, ctx))));
    }

    // ---------- Audio ----------

    public static void AudioCompress(IReadOnlyList<string> f) { if (NeedFfmpeg()) Show(new AudioCompressDialog(f)); }
    public static void Normalize(IReadOnlyList<string> f) { if (NeedFfmpeg()) Show(new NormalizeDialog(f)); }
    public static void Visualize(IReadOnlyList<string> f) { if (NeedFfmpeg()) Show(new VisualizeDialog(f[0])); }
    public static void TrimAudio(IReadOnlyList<string> f) { if (NeedFfmpeg()) Show(new MediaEditorWindow(f[0], MediaEditMode.Trim)); }
    public static void Channels(IReadOnlyList<string> f) { if (NeedFfmpeg()) Show(new ChannelsDialog(f)); }
    public static void Bleep(IReadOnlyList<string> f) { if (NeedFfmpeg()) Show(new MediaEditorWindow(f[0], MediaEditMode.Bleep)); }

    public static void JoinAudio(IReadOnlyList<string> files)
    {
        if (!NeedFfmpeg()) return;
        Show(new OrderDialog(L.T("Join audio"), files, L.T("Join"), ordered =>
            JobManager.Run(L.F("Joining {0} audio files", ordered.Count), "", ordered, ctx => MediaEngine.JoinAudioAsync(ordered, ctx))));
    }

    // ---------- PDF ----------

    public static void PdfCompress(IReadOnlyList<string> f) => Show(new PdfCompressDialog(f));
    public static void PdfMetadata(IReadOnlyList<string> f) => Show(new PdfMetadataDialog(f));
    public static void PdfOrganize(IReadOnlyList<string> f) => Show(new PdfOrganizeWindow(f[0]));

    public static void PdfSplit(IReadOnlyList<string> files) =>
        JobManager.RunBatch(L.F("Splitting PDFs ({0})", files.Count), "", files, (f, ctx) => Task.Run(() => ctx.AddOutput(PdfEngine.Split(f, ctx))));

    public static void PdfMerge(IReadOnlyList<string> files) =>
        Show(new OrderDialog(L.T("Merge PDFs"), files, L.T("Merge"), ordered =>
            JobManager.Run(L.F("Merging {0} PDFs", ordered.Count), "", ordered, ctx => Task.Run(() =>
            {
                var output = OutputPaths.UniqueFile(OutputPaths.DirectoryFor(ordered[0]), L.T("Merged document"), "pdf");
                PdfEngine.Merge(ordered, output, ctx);
                ctx.AddOutput(output);
            }))));

    // ---------- Archives ----------

    public static void Extract(IReadOnlyList<string> files) =>
        JobManager.RunBatch(L.F("Extracting ({0})", files.Count), "", files, ArchiveEngine.ExtractAsync);
}
