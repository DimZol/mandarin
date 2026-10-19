using System.Globalization;
using System.IO;
using System.Text;
using Mandarin.Core;

namespace Mandarin.Engines;

/// <summary>Video and audio: conversion and tools on top of ffmpeg.</summary>
public static class MediaEngine
{
    static string N(double v) => Ffmpeg.Num(v);

    // ---------- Conversion ----------

    public static async Task ConvertAsync(string input, string target, JobContext ctx)
    {
        target = target.ToLowerInvariant();
        var info = await Ffmpeg.ProbeAsync(input, ctx.Token);
        var output = OutputPaths.File(input, target);
        var args = new List<string> { "-i", input };
        var isAudioTarget = target is "mp3" or "m4a" or "wav" or "flac" or "ogg" or "opus" or "aiff" or "wma" or "aac";

        if (isAudioTarget)
        {
            if (!info.HasAudio) throw new UserFacingException(L.T("The file has no audio track"));
            args.AddRange(new[] { "-vn", "-map", "0:a:0", "-map_metadata", "0" });
            args.AddRange(AudioCodecArgs(target));
        }
        else if (target == "gif")
        {
            if (!info.HasVideo) throw new UserFacingException(L.T("The file has no video"));
            var width = Math.Min(info.Width > 0 ? info.Width : 640, 720);
            var fps = Math.Min(15, Math.Max(1, (int)Math.Round(info.Fps)));
            args.AddRange(new[]
            {
                "-filter_complex",
                $"[0:v]fps={fps},scale={width}:-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a",
                "-loop", "0"
            });
        }
        else
        {
            if (!info.HasVideo) throw new UserFacingException(L.T("The file has no video"));
            args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?", "-map_metadata", "0" });
            var canCopy = target is "mp4" or "mov" or "mkv" &&
                          info.VideoCodec is "h264" or "hevc" &&
                          (info.AudioCodec is null or "aac" or "mp3" || (target == "mkv" && info.AudioCodec is "opus" or "vorbis" or "flac"));
            if (canCopy)
            {
                args.AddRange(new[] { "-c", "copy" });
                if (info.VideoCodec == "hevc" && target is "mp4" or "mov") args.AddRange(new[] { "-tag:v", "hvc1" });
            }
            else
            {
                // yuv420p needs even dimensions (GIFs and phone videos can be odd)
                if (info.Width % 2 == 1 || info.Height % 2 == 1) args.AddRange(new[] { "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2" });
                args.AddRange(VideoCodecArgs(target));
            }
            if (target is "mp4" or "mov") args.AddRange(new[] { "-movflags", "+faststart" });
        }
        args.Add(output);
        await Ffmpeg.RunAsync(args, info.Duration, ctx.Report, ctx.Token, output);
        ctx.AddOutput(output);
    }

    public static string[] AudioCodecArgs(string target, int? kbps = null) => target switch
    {
        "mp3" => kbps is { } k1 ? new[] { "-c:a", "libmp3lame", "-b:a", $"{k1}k" } : new[] { "-c:a", "libmp3lame", "-q:a", "2" },
        "m4a" or "aac" => new[] { "-c:a", "aac", "-b:a", $"{kbps ?? 256}k" },
        "wav" => new[] { "-c:a", "pcm_s16le" },
        "flac" => new[] { "-c:a", "flac", "-compression_level", "8" },
        "ogg" => kbps is { } k2 ? new[] { "-c:a", "libvorbis", "-b:a", $"{k2}k" } : new[] { "-c:a", "libvorbis", "-q:a", "6" },
        "opus" => new[] { "-c:a", "libopus", "-b:a", $"{kbps ?? 160}k", "-vbr", "on" },
        "aiff" or "aif" => new[] { "-c:a", "pcm_s16be" },
        "wma" => new[] { "-c:a", "wmav2", "-b:a", $"{kbps ?? 192}k" },
        _ => new[] { "-c:a", "aac", "-b:a", $"{kbps ?? 192}k" }
    };

    public static string[] VideoCodecArgs(string target, int? crf = null, int? audioKbps = null, string? preset = null)
    {
        var s = AppSettings.Current;
        var q = crf ?? s.VideoCrf;
        preset ??= s.VideoPreset;
        return target switch
        {
            "webm" => new[] { "-c:v", "libvpx-vp9", "-crf", (q + 12).ToString(), "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "2", "-pix_fmt", "yuv420p",
                              "-c:a", "libopus", "-b:a", $"{audioKbps ?? 128}k" },
            "avi" => new[] { "-c:v", "mpeg4", "-q:v", "3", "-vtag", "xvid", "-c:a", "libmp3lame", "-q:a", "3" },
            "wmv" => new[] { "-c:v", "wmv2", "-q:v", "3", "-c:a", "wmav2", "-b:a", $"{audioKbps ?? 192}k" },
            _ => new[] { "-c:v", "libx264", "-preset", preset, "-crf", q.ToString(), "-pix_fmt", "yuv420p",
                         "-c:a", "aac", "-b:a", $"{audioKbps ?? 192}k" }
        };
    }

    /// <summary>Output extension for tools: the source format if it can be encoded, otherwise MP4.</summary>
    public static string VideoOutExt(string input) =>
        FileKinds.Ext(input) is var e && e is "mp4" or "mov" or "mkv" or "webm" or "avi" or "wmv" ? e : "mp4";

    public static string AudioOutExt(string input) =>
        FileKinds.Ext(input) is var e && e is "mp3" or "m4a" or "wav" or "flac" or "ogg" or "opus" or "aiff" or "wma" ? e : e == "aif" ? "aiff" : "m4a";

    static string[] EncodeFor(string outExt, int? crf = null) =>
        outExt is "mp3" or "m4a" or "wav" or "flac" or "ogg" or "opus" or "aiff" or "wma" ? AudioCodecArgs(outExt) : VideoCodecArgs(outExt, crf);

    static void FastStart(List<string> args, string output)
    {
        if (FileKinds.Ext(output) is "mp4" or "mov" or "m4a") args.AddRange(new[] { "-movflags", "+faststart" });
    }

    // ---------- Video tools ----------

    public sealed record VideoCompressOptions(int Crf, int? MaxHeight, long? TargetBytes, int AudioKbps);

    public static async Task CompressVideoAsync(string input, VideoCompressOptions o, JobContext ctx)
    {
        var info = await Ffmpeg.ProbeAsync(input, ctx.Token);
        var ext = VideoOutExt(input);
        if (ext is "avi" or "wmv") ext = "mp4";
        var output = OutputPaths.File(input, ext, L.T(" (compressed)"));
        var args = new List<string> { "-i", input, "-map", "0:v:0", "-map", "0:a:0?", "-map_metadata", "0" };
        if (o.MaxHeight is { } mh && info.Height > mh)
            args.AddRange(new[] { "-vf", $"scale=-2:{mh}:flags=lanczos" });

        if (o.TargetBytes is { } target && info.Duration > 0)
        {
            var totalKbps = target * 8 / 1000.0 / info.Duration * 0.96;
            var videoKbps = Math.Max(80, totalKbps - o.AudioKbps);
            if (ext == "webm")
                args.AddRange(new[] { "-c:v", "libvpx-vp9", "-b:v", $"{(int)videoKbps}k", "-row-mt", "1", "-c:a", "libopus", "-b:a", $"{o.AudioKbps}k" });
            else
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-b:v", $"{(int)videoKbps}k", "-maxrate", $"{(int)(videoKbps * 1.3)}k",
                    "-bufsize", $"{(int)(videoKbps * 2)}k", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", $"{o.AudioKbps}k" });
        }
        else args.AddRange(VideoCodecArgs(ext, o.Crf, o.AudioKbps, "medium"));
        FastStart(args, output);
        args.Add(output);
        await Ffmpeg.RunAsync(args, info.Duration, ctx.Report, ctx.Token, output);
        ctx.AddOutput(output);
    }

    public static async Task MuteAsync(string input, JobContext ctx)
    {
        var info = await Ffmpeg.ProbeAsync(input, ctx.Token);
        var ext = VideoOutExt(input);
        var output = OutputPaths.File(input, ext, L.T(" (no sound)"));
        var args = new List<string> { "-i", input, "-map", "0:v:0", "-c", "copy", "-an", "-map_metadata", "0" };
        FastStart(args, output);
        args.Add(output);
        await Ffmpeg.RunAsync(args, info.Duration, ctx.Report, ctx.Token, output);
        ctx.AddOutput(output);
    }

    public static async Task TrimAsync(string input, double start, double end, bool exact, JobContext ctx, string? output = null)
    {
        var info = await Ffmpeg.ProbeAsync(input, ctx.Token);
        var ext = info.HasVideo ? VideoOutExt(input) : AudioOutExt(input);
        output ??= OutputPaths.File(input, ext, L.T(" (trimmed)"));
        var args = new List<string>();
        if (exact)
        {
            args.AddRange(new[] { "-ss", N(start), "-i", input, "-t", N(end - start), "-map", "0:v:0?", "-map", "0:a:0?", "-map_metadata", "0" });
            args.AddRange(EncodeFor(FileKinds.Ext(output)));
        }
        else
        {
            args.AddRange(new[] { "-ss", N(start), "-i", input, "-t", N(end - start), "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero" });
        }
        FastStart(args, output);
        args.Add(output);
        await Ffmpeg.RunAsync(args, end - start, ctx.Report, ctx.Token, output);
        ctx.AddOutput(output);
    }

    public static async Task SplitAsync(string input, IReadOnlyList<double> points, JobContext ctx)
    {
        var info = await Ffmpeg.ProbeAsync(input, ctx.Token);
        var bounds = new List<double> { 0 };
        bounds.AddRange(points.Where(p => p > 0.05 && p < info.Duration - 0.05).OrderBy(p => p));
        bounds.Add(info.Duration);
        var folder = OutputPaths.Folder(input, L.T(" - parts"));
        Directory.CreateDirectory(folder);
        var ext = info.HasVideo ? VideoOutExt(input) : AudioOutExt(input);
        for (var i = 0; i < bounds.Count - 1; i++)
        {
            ctx.Detail(L.F("Part {0} of {1}", i + 1, bounds.Count - 1));
            var part = Path.Combine(folder, $"{FileKinds.BaseName(input)} {i + 1:00}.{ext}");
            var segStart = bounds[i];
            var segEnd = bounds[i + 1];
            var args = new List<string> { "-ss", N(segStart), "-i", input, "-t", N(segEnd - segStart), "-map", "0:v:0?", "-map", "0:a:0?" };
            args.AddRange(EncodeFor(ext));
            FastStart(args, part);
            args.Add(part);
            var idx = i;
            await Ffmpeg.RunAsync(args, segEnd - segStart,
                p => ctx.Report((idx + p) / (bounds.Count - 1)), ctx.Token, part);
        }
        ctx.AddOutput(folder);
    }

    public sealed record CropRect(int X, int Y, int W, int H);

    public static async Task CropVideoAsync(string input, CropRect r, JobContext ctx)
    {
        var info = await Ffmpeg.ProbeAsync(input, ctx.Token);
        var ext = VideoOutExt(input);
        var output = OutputPaths.File(input, ext, L.T(" (cropped)"));
        int w = r.W / 2 * 2, h = r.H / 2 * 2;
        var args = new List<string> { "-i", input, "-map", "0:v:0", "-map", "0:a:0?", "-map_metadata", "0",
            "-vf", $"crop={w}:{h}:{r.X}:{r.Y}" };
        args.AddRange(VideoCodecArgs(ext));
        FastStart(args, output);
        args.Add(output);
        await Ffmpeg.RunAsync(args, info.Duration, ctx.Report, ctx.Token, output);
        ctx.AddOutput(output);
    }

    public static string AtempoChain(double speed)
    {
        var parts = new List<string>();
        var s = speed;
        while (s > 2.0) { parts.Add("atempo=2.0"); s /= 2.0; }
        while (s < 0.5) { parts.Add("atempo=0.5"); s /= 0.5; }
        parts.Add("atempo=" + N(s));
        return string.Join(",", parts);
    }

    public static async Task SpeedAsync(string input, double speed, JobContext ctx)
    {
        var info = await Ffmpeg.ProbeAsync(input, ctx.Token);
        var ext = info.HasVideo ? VideoOutExt(input) : AudioOutExt(input);
        var label = speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";
        var output = OutputPaths.File(input, ext, $" ({label})");
        var args = new List<string> { "-i", input };
        if (info.HasVideo && info.HasAudio)
            args.AddRange(new[] { "-filter_complex", $"[0:v]setpts=PTS/{N(speed)}[v];[0:a]{AtempoChain(speed)}[a]", "-map", "[v]", "-map", "[a]" });
        else if (info.HasVideo)
            args.AddRange(new[] { "-filter:v", $"setpts=PTS/{N(speed)}", "-an" });
        else
            args.AddRange(new[] { "-filter:a", AtempoChain(speed), "-vn" });
        args.AddRange(EncodeFor(ext));
        FastStart(args, output);
        args.Add(output);
        await Ffmpeg.RunAsync(args, info.Duration / speed, ctx.Report, ctx.Token, output);
        ctx.AddOutput(output);
    }

    public static async Task SnapshotsAsync(string input, IReadOnlyList<double> times, JobContext ctx)
    {
        if (times.Count == 0) return;
        var single = times.Count == 1;
        var folder = single ? OutputPaths.DirectoryFor(input) : OutputPaths.Folder(input, L.T(" - frames"));
        Directory.CreateDirectory(folder);
        for (var i = 0; i < times.Count; i++)
        {
            ctx.Report((double)i / times.Count);
            var ts = TimeSpan.FromSeconds(times[i]);
            var name = $"{FileKinds.BaseName(input)} {ts:hh\\-mm\\-ss\\.fff}";
            var output = OutputPaths.UniqueFile(folder, name, "png");
            await Ffmpeg.RunAsync(new[] { "-ss", N(times[i]), "-i", input, "-frames:v", "1", "-update", "1", output }, 0, null, ctx.Token, output);
            if (single) ctx.AddOutput(output);
        }
        if (!single) ctx.AddOutput(folder);
    }

    public sealed record RedactRegion(CropRect Rect, double Start, double End);

    public static async Task RedactVideoAsync(string input, IReadOnlyList<RedactRegion> regions, bool blur, string colorHex, JobContext ctx)
    {
        var info = await Ffmpeg.ProbeAsync(input, ctx.Token);
        var ext = VideoOutExt(input);
        var output = OutputPaths.File(input, ext, L.T(" (redacted)"));
        var sb = new StringBuilder();
        var last = "0:v";
        for (var i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            // at least 8×8 and inside the frame: otherwise ffmpeg crop/boxblur fail
            int x0 = Math.Clamp(r.Rect.X, 0, Math.Max(0, info.Width - 8)), y0 = Math.Clamp(r.Rect.Y, 0, Math.Max(0, info.Height - 8));
            int w = Math.Clamp(r.Rect.W, 8, Math.Max(8, info.Width - x0)) / 2 * 2, h = Math.Clamp(r.Rect.H, 8, Math.Max(8, info.Height - y0)) / 2 * 2;
            r = r with { Rect = new CropRect(x0, y0, w, h) };
            var enable = $"between(t\\,{N(r.Start)}\\,{N(r.End)})";
            if (blur)
            {
                var radius = Math.Clamp(Math.Min(w, h) / 6, 1, Math.Max(1, Math.Min(w, h) / 2 - 1));
                sb.Append($"[{last}]split[base{i}][src{i}];[src{i}]crop={w}:{h}:{r.Rect.X}:{r.Rect.Y},boxblur={radius}:3[blr{i}];");
                sb.Append($"[base{i}][blr{i}]overlay={r.Rect.X}:{r.Rect.Y}:enable='{enable}'[v{i}];");
            }
            else
            {
                sb.Append($"[{last}]drawbox=x={r.Rect.X}:y={r.Rect.Y}:w={w}:h={h}:color=0x{colorHex}@1:t=fill:enable='{enable}'[v{i}];");
            }
            last = $"v{i}";
        }
        var graph = sb.ToString().TrimEnd(';');
        var args = new List<string> { "-i", input, "-filter_complex", graph, "-map", $"[{last}]", "-map", "0:a:0?", "-map_metadata", "0" };
        args.AddRange(VideoCodecArgs(ext));
        FastStart(args, output);
        args.Add(output);
        await Ffmpeg.RunAsync(args, info.Duration, ctx.Report, ctx.Token, output);
        ctx.AddOutput(output);
    }

    public static async Task JoinVideosAsync(IReadOnlyList<string> inputs, JobContext ctx)
    {
        if (inputs.Count < 2) throw new UserFacingException(L.T("Select at least two videos"));
        var infos = new List<MediaInfo>();
        foreach (var i in inputs) infos.Add(await Ffmpeg.ProbeAsync(i, ctx.Token));
        var w = infos[0].Width / 2 * 2; var h = infos[0].Height / 2 * 2;
        var fps = N(Math.Round(infos[0].Fps, 3));
        var output = OutputPaths.UniqueFile(OutputPaths.DirectoryFor(inputs[0]), L.T("Joined video"), "mp4");
        var args = new List<string>();
        foreach (var i in inputs) args.AddRange(new[] { "-i", i });
        var sb = new StringBuilder();
        for (var i = 0; i < inputs.Count; i++)
        {
            sb.Append($"[{i}:v]scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps},format=yuv420p[v{i}];");
            if (infos[i].HasAudio)
                sb.Append($"[{i}:a]aformat=sample_rates=48000:channel_layouts=stereo[a{i}];");
            else
                sb.Append($"anullsrc=r=48000:cl=stereo,atrim=0:{N(infos[i].Duration)}[a{i}];");
        }
        for (var i = 0; i < inputs.Count; i++) sb.Append($"[v{i}][a{i}]");
        sb.Append($"concat=n={inputs.Count}:v=1:a=1[v][a]");
        args.AddRange(new[] { "-filter_complex", sb.ToString(), "-map", "[v]", "-map", "[a]" });
        args.AddRange(VideoCodecArgs("mp4"));
        args.AddRange(new[] { "-movflags", "+faststart", output });
        await Ffmpeg.RunAsync(args, infos.Sum(x => x.Duration), ctx.Report, ctx.Token, output);
        ctx.AddOutput(output);
    }

    public static async Task JoinAudioAsync(IReadOnlyList<string> inputs, JobContext ctx)
    {
        if (inputs.Count < 2) throw new UserFacingException(L.T("Select at least two audio files"));
        var infos = new List<MediaInfo>();
        foreach (var i in inputs) infos.Add(await Ffmpeg.ProbeAsync(i, ctx.Token));
        var ext = AudioOutExt(inputs[0]);
        var output = OutputPaths.UniqueFile(OutputPaths.DirectoryFor(inputs[0]), L.T("Joined audio"), ext);
        var args = new List<string>();
        foreach (var i in inputs) args.AddRange(new[] { "-i", i });
        var sb = new StringBuilder();
        for (var i = 0; i < inputs.Count; i++) sb.Append($"[{i}:a]aformat=sample_rates=48000:channel_layouts=stereo[a{i}];");
        for (var i = 0; i < inputs.Count; i++) sb.Append($"[a{i}]");
        sb.Append($"concat=n={inputs.Count}:v=0:a=1[a]");
        args.AddRange(new[] { "-filter_complex", sb.ToString(), "-map", "[a]" });
        args.AddRange(AudioCodecArgs(ext));
        args.Add(output);
        await Ffmpeg.RunAsync(args, infos.Sum(x => x.Duration), ctx.Report, ctx.Token, output);
        ctx.AddOutput(output);
    }

    // ---------- Metadata ----------

    public static async Task WriteMetadataAsync(string input, Dictionary<string, string> tags, bool removeAll, JobContext ctx)
    {
        var info = await Ffmpeg.ProbeAsync(input, ctx.Token);
        var ext = FileKinds.Ext(input);
        var output = OutputPaths.File(input, ext, removeAll ? L.T(" (no metadata)") : L.T(" (metadata)"));
        var args = new List<string> { "-i", input, "-map", "0", "-c", "copy" };
        if (removeAll) args.AddRange(new[] { "-map_metadata", "-1", "-map_chapters", "-1" });
        else
        {
            args.AddRange(new[] { "-map_metadata", "0" });
            foreach (var (k, v) in tags) args.AddRange(new[] { "-metadata", $"{k}={v}" });
        }
        if (ext is "mp4" or "mov" or "m4a" or "m4v") args.AddRange(new[] { "-movflags", "+faststart+use_metadata_tags" });
        args.Add(output);
        await Ffmpeg.RunAsync(args, info.Duration, ctx.Report, ctx.Token, output);
        ctx.AddOutput(output);
    }

    // ---------- Audio tools ----------

    public static async Task CompressAudioAsync(string input, int kbps, string? targetExt, bool mono, JobContext ctx)
    {
        var info = await Ffmpeg.ProbeAsync(input, ctx.Token);
        var ext = targetExt ?? AudioOutExt(input);
        if (ext is "wav" or "flac" or "aiff") ext = "m4a"; // uncompressed formats can only shrink by changing the codec
        var output = OutputPaths.File(input, ext, L.T(" (compressed)"));
        var args = new List<string> { "-i", input, "-vn", "-map", "0:a:0", "-map_metadata", "0" };
        if (mono) args.AddRange(new[] { "-ac", "1" });
        args.AddRange(AudioCodecArgs(ext, kbps));
        args.Add(output);
        await Ffmpeg.RunAsync(args, info.Duration, ctx.Report, ctx.Token, output);
        ctx.AddOutput(output);
    }

    public static async Task NormalizeAsync(string input, double targetLufs, JobContext ctx)
    {
        var info = await Ffmpeg.ProbeAsync(input, ctx.Token);
        var isVideo = info.HasVideo;
        var ext = isVideo ? VideoOutExt(input) : AudioOutExt(input);
        var output = OutputPaths.File(input, ext, L.T(" (loudness)"));

        // pass 1: measurement
        ctx.Detail(L.T("Measuring loudness…"));
        var (_, _, measure) = await Ffmpeg.RunCaptureAsync(Ffmpeg.FfmpegPath!, new[]
        {
            "-hide_banner", "-nostdin", "-i", input, "-vn", "-af", $"loudnorm=I={N(targetLufs)}:TP=-1.5:LRA=11:print_format=json", "-f", "null", "-"
        }, ctx.Token);
        string Get(string key)
        {
            var idx = measure.LastIndexOf($"\"{key}\"", StringComparison.Ordinal);
            if (idx < 0) return "0";
            var colon = measure.IndexOf(':', idx);
            var q1 = measure.IndexOf('"', colon + 1);
            var q2 = measure.IndexOf('"', q1 + 1);
            return measure[(q1 + 1)..q2].Trim();
        }
        var measured = new[] { "input_i", "input_tp", "input_lra", "input_thresh", "target_offset" }.Select(Get).ToArray();
        // silence yields "-inf" - then the second pass runs without measurements (plain single-pass loudnorm)
        var valid = measured.All(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d));
        var filter = valid
            ? $"loudnorm=I={N(targetLufs)}:TP=-1.5:LRA=11:measured_I={measured[0]}:measured_TP={measured[1]}:" +
              $"measured_LRA={measured[2]}:measured_thresh={measured[3]}:offset={measured[4]}:linear=true"
            : $"loudnorm=I={N(targetLufs)}:TP=-1.5:LRA=11";
        ctx.Detail(L.T("Normalizing loudness…"));
        var args = new List<string> { "-i", input, "-map_metadata", "0", "-af", filter, "-ar", Math.Max(44100, info.SampleRate).ToString() };
        if (isVideo) args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0", "-c:v", "copy" });
        args.AddRange(isVideo ? (ext == "webm" ? new[] { "-c:a", "libopus", "-b:a", "160k" } : new[] { "-c:a", "aac", "-b:a", "192k" }) : AudioCodecArgs(ext));
        FastStart(args, output);
        args.Add(output);
        await Ffmpeg.RunAsync(args, info.Duration, ctx.Report, ctx.Token, output);
        ctx.AddOutput(output);
    }

    public static async Task ChannelsAsync(string input, bool toMono, JobContext ctx)
    {
        var info = await Ffmpeg.ProbeAsync(input, ctx.Token);
        var ext = AudioOutExt(input);
        var output = OutputPaths.File(input, ext, toMono ? L.T(" (mono)") : L.T(" (stereo)"));
        var args = new List<string> { "-i", input, "-vn", "-map", "0:a:0", "-map_metadata", "0" };
        if (toMono) args.AddRange(new[] { "-ac", "1" });
        else args.AddRange(info.AudioChannels == 1 ? new[] { "-af", "pan=stereo|c0=c0|c1=c0" } : new[] { "-ac", "2" });
        args.AddRange(AudioCodecArgs(ext));
        args.Add(output);
        await Ffmpeg.RunAsync(args, info.Duration, ctx.Report, ctx.Token, output);
        ctx.AddOutput(output);
    }

    public enum VisualShape { Landscape, Portrait, Square }

    public static async Task VisualizeAsync(string input, VisualShape shape, string? backgroundImage, JobContext ctx)
    {
        var info = await Ffmpeg.ProbeAsync(input, ctx.Token);
        var (w, h) = shape switch { VisualShape.Portrait => (1080, 1920), VisualShape.Square => (1080, 1080), _ => (1920, 1080) };
        var waveH = shape == VisualShape.Portrait ? 600 : h / 2;
        var output = OutputPaths.File(input, "mp4", L.T(" (visualizer)"));
        var args = new List<string>();
        string graph;
        if (backgroundImage != null)
        {
            args.AddRange(new[] { "-loop", "1", "-i", backgroundImage, "-i", input });
            graph = $"[0:v]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},format=yuv420p[bg];" +
                    $"[1:a]showwaves=s={w}x{waveH}:mode=cline:rate=30:colors=0xFF7A1A,format=rgba,colorkey=0x000000:0.1:0.0[wv];" +
                    $"[bg][wv]overlay=0:(H-h)/2:shortest=1,format=yuv420p[v]";
            args.AddRange(new[] { "-filter_complex", graph, "-map", "[v]", "-map", "1:a:0" });
        }
        else
        {
            args.AddRange(new[] { "-i", input });
            graph = $"[0:a]showwaves=s={w}x{waveH}:mode=cline:rate=30:colors=0xFF7A1A,pad={w}:{h}:0:(oh-ih)/2:black,format=yuv420p[v]";
            args.AddRange(new[] { "-filter_complex", graph, "-map", "[v]", "-map", "0:a:0" });
        }
        args.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "22", "-tune", "animation", "-r", "30",
            "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", output });
        await Ffmpeg.RunAsync(args, info.Duration, ctx.Report, ctx.Token, output);
        ctx.AddOutput(output);
    }

    public static async Task BleepAsync(string input, IReadOnlyList<(double start, double end)> ranges, JobContext ctx)
    {
        if (ranges.Count == 0) throw new UserFacingException(L.T("Mark at least one fragment"));
        var info = await Ffmpeg.ProbeAsync(input, ctx.Token);
        var isVideo = info.HasVideo;
        var ext = isVideo ? VideoOutExt(input) : AudioOutExt(input);
        var output = OutputPaths.File(input, ext, L.T(" (bleep)"));
        var cond = string.Join("+", ranges.Select(r => $"between(t\\,{N(r.start)}\\,{N(r.end)})"));
        var sr = info.SampleRate > 0 ? info.SampleRate : 44100;
        var graph = $"[0:a]volume=volume=0:enable='{cond}'[muted];" +
                    $"aevalsrc=exprs='0.35*sin(2*PI*1000*t)*gt({cond}\\,0)':s={sr}:d={N(info.Duration)}[beep];" +
                    $"[muted][beep]amix=inputs=2:duration=first:normalize=0[a]";
        var args = new List<string> { "-i", input, "-filter_complex", graph, "-map", "[a]", "-map_metadata", "0" };
        if (isVideo) args.InsertRange(4, new[] { "-map", "0:v:0", "-c:v", "copy" });
        args.AddRange(isVideo ? (ext == "webm" ? new[] { "-c:a", "libopus", "-b:a", "160k" } : new[] { "-c:a", "aac", "-b:a", "192k" }) : AudioCodecArgs(ext));
        FastStart(args, output);
        args.Add(output);
        await Ffmpeg.RunAsync(args, info.Duration, ctx.Report, ctx.Token, output);
        ctx.AddOutput(output);
    }

    /// <summary>Finds silence at the start and end from waveform peaks.</summary>
    public static (double start, double end) DetectSilenceBounds(float[] peaks, double duration, float threshold = 0.02f)
    {
        if (peaks.Length == 0) return (0, duration);
        var first = Array.FindIndex(peaks, p => p > threshold);
        var last = Array.FindLastIndex(peaks, p => p > threshold);
        if (first < 0) return (0, duration);
        var per = duration / peaks.Length;
        var pad = 0.15;
        return (Math.Max(0, first * per - pad), Math.Min(duration, (last + 1) * per + pad));
    }

    /// <summary>Light preview copy for the player when Windows cannot play the source.</summary>
    public static async Task<string> MakePreviewProxyAsync(string input, bool video, CancellationToken ct, Action<double>? progress = null)
    {
        var info = await Ffmpeg.ProbeAsync(input, ct);
        var dir = OutputPaths.TempDir();
        var output = Path.Combine(dir, video ? "preview.mp4" : "preview.m4a");
        var args = new List<string> { "-i", input };
        if (video)
            args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?", "-vf", "scale=-2:'min(720,ih)'", "-c:v", "libx264", "-preset", "veryfast", "-crf", "26",
                "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart" });
        else
            args.AddRange(new[] { "-vn", "-map", "0:a:0", "-c:a", "aac", "-b:a", "160k" });
        args.Add(output);
        await Ffmpeg.RunAsync(args, info.Duration, progress, ct, output);
        return output;
    }
}
