using System.IO;

namespace Mandarin.Core;

/// <summary>Picks result names: next to the original (or in the folder from settings), never overwriting.</summary>
public static class OutputPaths
{
    public static string DirectoryFor(string input)
    {
        var s = AppSettings.Current;
        if (s.Output == OutputLocation.CustomFolder && !string.IsNullOrWhiteSpace(s.CustomOutputFolder))
        {
            Directory.CreateDirectory(s.CustomOutputFolder);
            return s.CustomOutputFolder;
        }
        var full = Path.GetFullPath(input).TrimEnd('\\', '/');
        return Path.GetDirectoryName(full) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }

    /// <summary>Free file name: "photo.png", then "photo (1).png" and so on.</summary>
    public static string File(string input, string newExt, string? suffix = null) =>
        UniqueFile(DirectoryFor(input), FileKinds.BaseName(input) + (suffix ?? ""), newExt);

    // names already handed out to parallel tasks: two conversions never get the same path
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase);

    static bool Taken(string path) =>
        System.IO.File.Exists(path) || Directory.Exists(path) || Reserved.Contains(path);

    public static string UniqueFile(string dir, string baseName, string ext)
    {
        baseName = Sanitize(baseName);
        ext = ext.TrimStart('.');
        var dotExt = ext.Length == 0 ? "" : "." + ext; // files without an extension get no dangling dot
        lock (Reserved)
        {
            var candidate = Path.Combine(dir, baseName + dotExt);
            for (var i = 1; Taken(candidate); i++)
                candidate = Path.Combine(dir, $"{baseName} ({i}){dotExt}");
            Reserved.Add(candidate);
            return candidate;
        }
    }

    /// <summary>Free name for a new folder next to the original.</summary>
    public static string Folder(string input, string? suffix = null) =>
        UniqueFolder(DirectoryFor(input), FileKinds.BaseName(input) + (suffix ?? ""));

    public static string UniqueFolder(string dir, string name)
    {
        name = Sanitize(name);
        lock (Reserved)
        {
            var candidate = Path.Combine(dir, name);
            for (var i = 1; Taken(candidate); i++)
                candidate = Path.Combine(dir, $"{name} ({i})");
            Reserved.Add(candidate);
            return candidate;
        }
    }

    public static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        // Windows drops trailing dots and spaces, while ".." and device names are dangerous
        name = name.Trim().TrimEnd('.', ' ');
        if (name.Length == 0) return "file";
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            name = "_" + name;
        return name;
    }

    /// <summary>Temporary folder for a task; the caller deletes it.</summary>
    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Mandarin", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        catch
        {
            // temporary files are removed by the next cleanup
        }
    }

    public static void CleanupOldTemp()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "Mandarin");
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.GetDirectories(root))
                if (Directory.GetCreationTime(d) < DateTime.Now.AddDays(-1)) TryDelete(d);
        }
        catch
        {
            // not critical
        }
    }

    public static string HumanSize(long bytes)
    {
        string[] u = { L.T("B"), L.T("KB"), L.T("MB"), L.T("GB"), L.T("TB") };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} {u[0]}" : $"{v:0.#} {u[i]}";
    }
}
