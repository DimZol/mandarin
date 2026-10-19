using System.IO;

namespace Mandarin.Core;

public static class Log
{
    static readonly object Gate = new();
    static string FilePath => Path.Combine(AppSettings.Folder, "mandarin.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Folder);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never crash the app
        }
    }
}
