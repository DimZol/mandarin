using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Mandarin.Core;
using Mandarin.Engines;
using Mandarin.Interop;
using Microsoft.Win32;

namespace Mandarin.UI;

/// <summary>File actions (not conversion): share, shelf, clipboard, move, path, terminal.</summary>
public static class FileActions
{
    public static void Share(IReadOnlyList<string> files) => ShareUi.Share(files);

    public static void Float(IReadOnlyList<string> files) => ShelfWindow.AddFiles(files);

    public static void Copy(IReadOnlyList<string> files)
    {
        var list = new StringCollection();
        list.AddRange(files.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        // "copy", not "cut": pasting in Explorer creates a copy
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        Clipboard.SetDataObject(data, true);
        Hud.ShowInfo(L.T("Copied to the clipboard"), L.F("{0} - paste into any folder (Ctrl+V)", Catalog.Describe(files)), "");
    }

    public static void CopyPath(IReadOnlyList<string> files)
    {
        Clipboard.SetText(string.Join(Environment.NewLine, files));
        Hud.ShowInfo(L.T("Path copied"), files.Count == 1 ? files[0] : L.F("Paths: {0}", files.Count), "");
    }

    public static void Move(IReadOnlyList<string> files)
    {
        var dialog = new OpenFolderDialog
        {
            Title = L.F("Move to: {0}", Catalog.Describe(files)),
            InitialDirectory = Path.GetDirectoryName(files[0].TrimEnd('\\'))
        };
        if (dialog.ShowDialog() != true) return;
        var target = dialog.FolderName;
        JobManager.RunBatch(L.F("Moving to \"{0}\"", Path.GetFileName(target)), "", files, (f, ctx) =>
        {
            var name = Path.GetFileName(f.TrimEnd('\\'));
            var dest = Path.Combine(target, name);
            if (string.Equals(Path.GetFullPath(f).TrimEnd('\\'), Path.GetFullPath(dest).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;
            // the name is taken - never overwrite, pick a free one
            if (File.Exists(dest) || Directory.Exists(dest))
                dest = Directory.Exists(f)
                    ? OutputPaths.UniqueFolder(target, name)
                    : OutputPaths.UniqueFile(target, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
            if (Directory.Exists(f))
            {
                if (Path.GetFullPath(dest).StartsWith(Path.GetFullPath(f).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new UserFacingException(L.T("A folder cannot be moved into itself"));
                if (string.Equals(Path.GetPathRoot(Path.GetFullPath(f)), Path.GetPathRoot(Path.GetFullPath(dest)), StringComparison.OrdinalIgnoreCase))
                    Directory.Move(f, dest);
                else
                {
                    CopyDirectory(f, dest);
                    // delete the original only after making sure the copy is complete
                    var (srcCount, srcBytes) = Measure(f);
                    var (dstCount, dstBytes) = Measure(dest);
                    if (srcCount != dstCount || srcBytes != dstBytes)
                        throw new UserFacingException(L.T("The folder copy does not match the original - the original was left in place"));
                    Directory.Delete(f, true);
                }
            }
            else File.Move(f, dest);
            ctx.AddOutput(dest);
            return Task.CompletedTask;
        });
    }

    static (int count, long bytes) Measure(string dir)
    {
        var files = new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories);
        return (files.Length, files.Sum(f => f.Length));
    }

    static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src)) File.Copy(file, Path.Combine(dst, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(src)) CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    public static void Terminal(IReadOnlyList<string> files)
    {
        var first = files[0];
        var dir = Directory.Exists(first) ? first : Path.GetDirectoryName(first)!;
        // full paths: a program with the same name in the current folder must not run instead of the system one
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        try
        {
            if (!File.Exists(wt)) throw new FileNotFoundException();
            Process.Start(new ProcessStartInfo(wt) { ArgumentList = { "-d", dir }, UseShellExecute = false });
        }
        catch
        {
            Process.Start(new ProcessStartInfo(ps) { WorkingDirectory = dir, UseShellExecute = false });
        }
    }

    public static void Zip(IReadOnlyList<string> files)
    {
        if (files.Count == 1)
            JobManager.Run($"{Path.GetFileName(files[0].TrimEnd('\\'))} → ZIP", "", files, ctx => ArchiveEngine.ConvertAsync(files[0], "zip", ctx));
        else
            JobManager.Run(L.F("Packing into ZIP ({0})", files.Count), "", files, ctx => ArchiveEngine.PackManyAsync(files, ctx));
    }
}
