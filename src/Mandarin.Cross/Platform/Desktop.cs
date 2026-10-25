using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Mandarin.Core;
using Microsoft.Win32;

namespace Mandarin.Platform;

/// <summary>
/// The desktop around Mandarin on Linux and Windows 7/8: opening files, showing them in the file manager, a terminal,
/// starting with the session and the entries in file manager menus ("Open with" on Linux, "Send to" on Windows).
/// </summary>
public static class Desktop
{
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, OsInfo.Exe("Mandarin"));

    static void Start(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }

    /// <summary>Full path of a program from PATH (null if there is none).</summary>
    public static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                     .Concat(OsInfo.IsWindows ? Array.Empty<string>() : new[] { "/usr/bin", "/usr/local/bin", "/bin" }))
        {
            var d = dir.Trim('"');
            if (!Path.IsPathFullyQualified(d)) continue;
            var p = Path.Combine(d, OsInfo.Exe(name));
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static void Open(string path)
    {
        try
        {
            if (OsInfo.IsWindows) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            else Start(Which("xdg-open") ?? "xdg-open", path);
        }
        catch (Exception ex) { Log.Error("Open", ex); }
    }

    /// <summary>Shows the file selected in the file manager (or opens its folder when the file manager cannot select).</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (OsInfo.IsWindows)
            {
                var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                Process.Start(new ProcessStartInfo(explorer, $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
                return;
            }
            // the freedesktop file manager interface: Nautilus, Dolphin, Nemo, Caja, Thunar and others
            if (Which("gdbus") is { } gdbus)
            {
                var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
                var psi = new ProcessStartInfo(gdbus) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var a in new[] { "call", "--session", "--dest", "org.freedesktop.FileManager1", "--object-path", "/org/freedesktop/FileManager1",
                             "--method", "org.freedesktop.FileManager1.ShowItems", $"['{uri.Replace("'", "%27")}']", "" })
                    psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                if (p.WaitForExit(3000) && p.ExitCode == 0) return;
            }
            Open(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!);
        }
        catch (Exception ex) { Log.Error("Reveal", ex); }
    }

    /// <summary>A terminal in the folder.</summary>
    public static void Terminal(string dir)
    {
        if (OsInfo.IsWindows)
        {
            // full paths: a program with the same name in the current folder must not run instead of the system one
            var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var exe = File.Exists(ps) ? ps : Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = true })?.Dispose();
            return;
        }
        (string name, string[] args)[] terminals =
        {
            ("x-terminal-emulator", Array.Empty<string>()), ("gnome-terminal", new[] { $"--working-directory={dir}" }),
            ("konsole", new[] { "--workdir", dir }), ("xfce4-terminal", new[] { $"--working-directory={dir}" }),
            ("mate-terminal", new[] { $"--working-directory={dir}" }), ("tilix", new[] { $"--working-directory={dir}" }),
            ("kgx", new[] { $"--working-directory={dir}" }), ("ptyxis", new[] { $"--working-directory={dir}" }),
            ("kitty", new[] { "--directory", dir }), ("alacritty", new[] { "--working-directory", dir }),
            ("wezterm", new[] { "start", "--cwd", dir }), ("lxterminal", new[] { $"--working-directory={dir}" }), ("xterm", Array.Empty<string>())
        };
        foreach (var (name, args) in terminals)
        {
            if (Which(name) is not { } exe) continue;
            var psi = new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
            return;
        }
        throw new UserFacingException(L.T("No terminal was found"));
    }

    // ---------- starting with the session ----------

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static string AutostartFile => Path.Combine(ConfigHome, "autostart", "mandarin.desktop");
    static string ConfigHome => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    public static void SetAutostart(bool enable)
    {
        try
        {
            if (OsInfo.IsWindows)
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (enable) key.SetValue("Mandarin", $"\"{ExePath}\" --background");
                else key.DeleteValue("Mandarin", false);
                return;
            }
            if (!enable) { if (File.Exists(AutostartFile)) File.Delete(AutostartFile); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
            File.WriteAllText(AutostartFile, DesktopEntry("--background", autostart: true), new UTF8Encoding(false));
        }
        catch (Exception ex) { Log.Error("Autostart", ex); }
    }

    // ---------- file manager menus ----------

    static string SendToLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Mandarin - convert.lnk");
    static string ApplicationsFile => Path.Combine(DataHome, "applications", "mandarin.desktop");
    static string IconFile => Path.Combine(DataHome, "icons", "hicolor", "256x256", "apps", "mandarin.png");

    /// <summary>"Open with → Mandarin" (Linux) or "Send to → Mandarin" (Windows) is installed.</summary>
    public static bool MenuEntryInstalled => File.Exists(OsInfo.IsWindows ? SendToLink : ApplicationsFile);

    static readonly string[] MimeTypes =
    {
        "image/jpeg", "image/png", "image/webp", "image/heic", "image/heif", "image/tiff", "image/avif", "image/bmp", "image/gif", "image/svg+xml",
        "image/x-icon", "image/vnd.adobe.photoshop", "image/x-dcraw", "video/mp4", "video/quicktime", "video/x-matroska", "video/webm", "video/x-msvideo",
        "video/x-ms-wmv", "video/x-flv", "video/mpeg", "video/3gpp", "video/mp2t", "audio/mpeg", "audio/mp4", "audio/x-m4a", "audio/wav", "audio/x-wav",
        "audio/flac", "audio/ogg", "audio/opus", "audio/x-aiff", "audio/x-ms-wma", "audio/aac", "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/msword", "application/vnd.oasis.opendocument.text",
        "application/rtf", "text/markdown", "text/plain", "text/html", "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.ms-powerpoint", "application/vnd.oasis.opendocument.presentation", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.ms-excel", "application/vnd.oasis.opendocument.spreadsheet", "text/csv", "text/tab-separated-values", "application/epub+zip",
        "application/x-subrip", "text/vtt", "application/zip", "application/x-tar", "application/gzip", "application/x-compressed-tar", "application/vnd.rar",
        "application/x-7z-compressed", "application/x-bzip2", "application/x-xz", "inode/directory"
    };

    static string DesktopEntry(string args, bool autostart = false)
    {
        var exe = ExePath.Contains(' ') ? $"\"{ExePath}\"" : ExePath;
        var sb = new StringBuilder();
        sb.AppendLine("[Desktop Entry]");
        sb.AppendLine("Type=Application");
        sb.AppendLine("Name=Mandarin");
        sb.AppendLine($"Comment={L.T("Offline file converter")}");
        sb.AppendLine($"Exec={exe} {args}");
        sb.AppendLine($"Icon={(File.Exists(IconFile) ? IconFile : "mandarin")}");
        sb.AppendLine("Terminal=false");
        sb.AppendLine("Categories=Utility;Graphics;AudioVideo;Office;");
        if (autostart) sb.AppendLine("X-GNOME-Autostart-enabled=true");
        else sb.AppendLine("MimeType=" + string.Join(";", MimeTypes) + ";");
        return sb.ToString();
    }

    public static void SetMenuEntry(bool enable)
    {
        try
        {
            if (OsInfo.IsWindows)
            {
                if (!enable) { if (File.Exists(SendToLink)) File.Delete(SendToLink); return; }
                var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic lnk = shell.CreateShortcut(SendToLink);
                lnk.TargetPath = ExePath;
                lnk.Arguments = "--wheel";
                lnk.IconLocation = ExePath + ",0";
                lnk.Description = L.T("Open the Mandarin conversion wheel");
                lnk.Save();
                Marshal.FinalReleaseComObject(lnk);
                Marshal.FinalReleaseComObject(shell);
                return;
            }
            if (!enable)
            {
                if (File.Exists(ApplicationsFile)) File.Delete(ApplicationsFile);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(IconFile)!);
            using (var icon = typeof(Desktop).Assembly.GetManifestResourceStream("Mandarin.Assets.Mandarin.png"))
            using (var fs = File.Create(IconFile)) icon?.CopyTo(fs);
            Directory.CreateDirectory(Path.GetDirectoryName(ApplicationsFile)!);
            // %F: the selected files; Mandarin opens the wheel for them
            File.WriteAllText(ApplicationsFile, DesktopEntry("%F"), new UTF8Encoding(false));
            if (Which("update-desktop-database") is { } update) Start(update, Path.GetDirectoryName(ApplicationsFile)!);
        }
        catch (Exception ex) { Log.Error("Menu entry", ex); }
    }

    // ---------- the pointer ----------

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);

    /// <summary>Pointer position on the screen in physical pixels, if the system tells it (under KDE Wayland - the last one read).</summary>
    public static (int x, int y)? CursorPos()
    {
        try
        {
            if (OsInfo.IsWindows) return GetCursorPos(out var p) ? (p.X, p.Y) : null;
            return X11.IsWayland ? KWin.LastCursor : X11.PointerPosition();
        }
        catch { return null; }
    }

    /// <summary>Pointer position on the screen in physical pixels; under KDE Wayland it is asked from KWin.</summary>
    public static async Task<(int x, int y)?> CursorPosAsync() => KWin.Available ? await KWin.CursorAsync() : CursorPos();
}
