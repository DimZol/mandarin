using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Mandarin.Core;

namespace Mandarin.Engines;

public sealed record MediaInfo(
    double Duration, int Width, int Height, double Fps, string? VideoCodec, string? AudioCodec,
    int AudioChannels, int SampleRate, long Bitrate, Dictionary<string, string> Tags, bool HasVideo, bool HasAudio)
{
    public double AspectRatio => Height == 0 ? 16.0 / 9 : (double)Width / Height;
}

/// <summary>ffmpeg/ffprobe wrapper: lookup, runs with progress, media probing.</summary>
public static class Ffmpeg
{
    static string? _ffmpeg, _ffprobe;

    public static string? FfmpegPath => _ffmpeg ??= Locate("ffmpeg.exe");
    public static string? FfprobePath => _ffprobe ??= Locate("ffprobe.exe");
    public static bool Available => FfmpegPath != null && FfprobePath != null;

    public static void ResetCache() { _ffmpeg = null; _ffprobe = null; }

    static string? Locate(string exe)
    {
        var custom = AppSettings.Current.FfmpegPath;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(custom))
        {
            candidates.Add(Directory.Exists(custom) ? Path.Combine(custom, exe) : Path.Combine(Path.GetDirectoryName(custom) ?? "", exe));
            candidates.Add(Path.Combine(custom, "bin", exe));
        }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, exe));
        candidates.Add(Path.Combine(AppSettings.Folder, "ffmpeg", "bin", exe));

        // absolute PATH entries only: relative ones (".") would point at the current folder with foreign files
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var d = dir.Trim('"');
            if (Path.IsPathFullyQualified(d)) candidates.Add(Path.Combine(d, exe));
        }

        // a winget install of Gyan.FFmpeg does not always reach the PATH of this process
        var wingetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        try
        {
            if (Directory.Exists(wingetRoot))
                foreach (var pkg in Directory.GetDirectories(wingetRoot, "*FFmpeg*"))
                    candidates.AddRange(Directory.GetFiles(pkg, exe, SearchOption.AllDirectories));
        }
        catch { /* no access - skip */ }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", exe));
        candidates.Add(@"C:\ffmpeg\bin\" + exe);

        return candidates.FirstOrDefault(File.Exists);
    }

    public static void EnsureAvailable()
    {
        if (!Available)
            throw new UserFacingException(
                L.T("Video and audio need FFmpeg. Install it with \"winget install Gyan.FFmpeg\" or set its path in Mandarin settings."));
    }

    static readonly Dictionary<string, MediaInfo> ProbeCache = new();

    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        EnsureAvailable();
        var key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
        lock (ProbeCache) if (ProbeCache.TryGetValue(key, out var cached)) return cached;

        var (code, stdout, stderr) = await RunCaptureAsync(FfprobePath!,
            new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }, ct);
        if (code != 0) throw new UserFacingException(L.T("Could not read the media file: ") + LastLine(stderr));

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        double duration = 0; long bitrate = 0;
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("format", out var fmt))
        {
            duration = ParseD(fmt, "duration");
            bitrate = (long)ParseD(fmt, "bit_rate");
            if (fmt.TryGetProperty("tags", out var t))
                foreach (var p in t.EnumerateObject()) tags[p.Name] = p.Value.ToString();
        }

        int w = 0, h = 0, ch = 0, sr = 0; double fps = 0; string? vc = null, ac = null;
        bool hasV = false, hasA = false;
        if (root.TryGetProperty("streams", out var streams))
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.GetProperty("codec_type").GetString();
                if (type == "video" && !hasV)
                {
                    var attached = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                    if (attached) continue;
                    hasV = true;
                    vc = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                    w = s.TryGetProperty("width", out var ww) ? ww.GetInt32() : 0;
                    h = s.TryGetProperty("height", out var hh) ? hh.GetInt32() : 0;
                    fps = ParseRate(s.TryGetProperty("avg_frame_rate", out var fr) ? fr.GetString() : null);
                    if (fps <= 0) fps = ParseRate(s.TryGetProperty("r_frame_rate", out var rr) ? rr.GetString() : null);
                    if (Rotation(s) is 90 or 270 or -90 or -270) (w, h) = (h, w);
                    if (duration <= 0) duration = ParseD(s, "duration");
                }
                else if (type == "audio" && !hasA)
                {
                    hasA = true;
                    ac = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                    ch = s.TryGetProperty("channels", out var c) ? c.GetInt32() : 0;
                    sr = (int)ParseD(s, "sample_rate");
                    if (duration <= 0) duration = ParseD(s, "duration");
                }
            }

        var info = new MediaInfo(duration, w, h, fps <= 0 ? 30 : fps, vc, ac, ch, sr, bitrate, tags, hasV, hasA);
        lock (ProbeCache) ProbeCache[key] = info;
        return info;
    }

    static int Rotation(JsonElement stream)
    {
        if (stream.TryGetProperty("tags", out var t) && t.TryGetProperty("rotate", out var r) &&
            int.TryParse(r.GetString(), out var deg)) return deg;
        if (stream.TryGetProperty("side_data_list", out var sdl))
            foreach (var sd in sdl.EnumerateArray())
                if (sd.TryGetProperty("rotation", out var rot) && rot.TryGetInt32(out var deg2)) return deg2;
        return 0;
    }

    static double ParseD(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    static double ParseRate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var parts = s.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0) return a / b;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
    }

    /// <summary>Runs ffmpeg and parses progress. duration is the output length in seconds.</summary>
    public static async Task RunAsync(IEnumerable<string> args, double duration, Action<double>? progress, CancellationToken ct,
        string? outputToCleanOnFail = null)
    {
        EnsureAvailable();
        // -n: ffmpeg never overwrites existing files (result names are unique anyway)
        var all = new List<string> { "-hide_banner", "-nostdin", "-n", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };
        // on failure only a file that did not exist before the run may be deleted
        if (outputToCleanOnFail != null && (File.Exists(outputToCleanOnFail) || Directory.Exists(outputToCleanOnFail)))
            outputToCleanOnFail = null;
        all.AddRange(args);

        var psi = new ProcessStartInfo(FfmpegPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in all) psi.ArgumentList.Add(a);
        Log.Info("ffmpeg " + string.Join(' ', all.Select(Quote)));

        using var proc = new Process { StartInfo = psi };
        var err = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null || progress == null || duration <= 0) return;
            if (e.Data.StartsWith("out_time_us=") || e.Data.StartsWith("out_time_ms="))
            {
                if (long.TryParse(e.Data[(e.Data.IndexOf('=') + 1)..], out var us) && us > 0)
                    progress(Math.Clamp(us / 1_000_000.0 / duration, 0, 1));
            }
        };
        var sw = Stopwatch.StartNew();
        // do not lower the priority: under CPU load Windows wakes such threads only every ~4 s
        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();

        await using (ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } }))
            await proc.WaitForExitAsync(CancellationToken.None);
        Log.Info($"ffmpeg exit {proc.ExitCode} in {sw.ElapsedMilliseconds} ms (process: {(proc.ExitTime - proc.StartTime).TotalMilliseconds:0} ms)");

        if (ct.IsCancellationRequested)
        {
            OutputPaths.TryDelete(outputToCleanOnFail);
            ct.ThrowIfCancellationRequested();
        }
        string errors; lock (err) errors = err.ToString();
        // with -n and an existing file ffmpeg 8 exits with code 0 - treat that as an error
        if (errors.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException(L.T("The output file already exists - overwriting is not allowed"));
        if (proc.ExitCode != 0)
        {
            OutputPaths.TryDelete(outputToCleanOnFail);
            throw new UserFacingException("FFmpeg: " + LastLine(errors));
        }
    }

    public static async Task<(int code, string stdout, string stderr)> RunCaptureAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var o = proc.StandardOutput.ReadToEndAsync(ct);
        var e = proc.StandardError.ReadToEndAsync(ct);
        await using (ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } }))
            await proc.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return (proc.ExitCode, await o, await e);
    }

    /// <summary>Raw PCM peaks (mono, s16le) for drawing a waveform.</summary>
    public static async Task<float[]> ReadPeaksAsync(string path, int bins, CancellationToken ct)
    {
        EnsureAvailable();
        const int rate = 4000;
        var psi = new ProcessStartInfo(FfmpegPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", path, "-vn", "-ac", "1", "-ar", rate.ToString(), "-f", "s16le", "-" })
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        _ = proc.StandardError.ReadToEndAsync(ct);
        using var ms = new MemoryStream();
        await proc.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        await proc.WaitForExitAsync(ct);
        var bytes = ms.GetBuffer();
        var samples = (int)(ms.Length / 2);
        var peaks = new float[bins];
        if (samples == 0) return peaks;
        for (var b = 0; b < bins; b++)
        {
            var from = (int)((long)b * samples / bins);
            var to = Math.Max(from + 1, (int)((long)(b + 1) * samples / bins));
            var max = 0;
            for (var i = from; i < to && i < samples; i++)
            {
                var v = Math.Abs((int)BitConverter.ToInt16(bytes, i * 2));
                if (v > max) max = v;
            }
            peaks[b] = max / 32768f;
        }
        return peaks;
    }

    public static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Time(double seconds) => Num(Math.Max(0, seconds));

    static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? L.T("unknown error") : string.Join(" ", lines.TakeLast(2));
    }
}
