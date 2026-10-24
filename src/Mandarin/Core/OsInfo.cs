namespace Mandarin.Core;

/// <summary>Which system Mandarin runs on: Windows 10/11 (the main build), Windows 7/8 or Linux (the cross-platform build).</summary>
public static class OsInfo
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>Windows 7 and 8: no winget, WinRT, Share sheet or WebView2.</summary>
    public static bool IsLegacyWindows => OperatingSystem.IsWindows() && !OperatingSystem.IsWindowsVersionAtLeast(10);

    /// <summary>Executable file name of a program: "ffmpeg.exe" on Windows, "ffmpeg" elsewhere.</summary>
    public static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>How to get FFmpeg on this system.</summary>
    public static string FfmpegHint => IsLinux
        ? L.T("Install it with your package manager (for example \"sudo apt install ffmpeg\") or set its path in Mandarin settings.")
        : IsLegacyWindows
            ? L.T("Download a Windows build from ffmpeg.org, unpack it and set its folder in Mandarin settings.")
            : L.T("Install it with \"winget install Gyan.FFmpeg\" or set its path in Mandarin settings.");
}
