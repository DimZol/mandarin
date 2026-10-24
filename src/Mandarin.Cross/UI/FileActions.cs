using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Mandarin.Core;
using Mandarin.Engines;
using Mandarin.Platform;

namespace Mandarin.UI;

/// <summary>File actions (not conversion): clipboard, move, path, terminal, folder, ZIP.</summary>
public static class FileActions
{
    /// <summary>Any open window: clipboard and dialogs need a top level.</summary>
    static TopLevel? Host()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime d) return null;
        return d.Windows.FirstOrDefault(w => w.IsActive) ?? d.Windows.FirstOrDefault(w => w.IsVisible && w is not WheelWindow) ?? HomeWindow.Hidden();
    }

    public static async void Copy(IReadOnlyList<string> files)
    {
        try
        {
            var host = Host() ?? throw new UserFacingException(L.T("The clipboard is unavailable"));
            var items = new List<IStorageItem>();
            foreach (var f in files)
            {
                IStorageItem? item = Directory.Exists(f) ? await host.StorageProvider.TryGetFolderFromPathAsync(f) : await host.StorageProvider.TryGetFileFromPathAsync(f);
                if (item != null) items.Add(item);
            }
            if (host.Clipboard == null || items.Count == 0) throw new UserFacingException(L.T("The clipboard is unavailable"));
            await host.Clipboard.SetFilesAsync(items);
            Hud.ShowInfo(L.T("Copied to the clipboard"), L.F("{0} - paste into any folder (Ctrl+V)", Catalog.Describe(files)), "");
        }
        catch (Exception ex) { Hud.ShowError(L.T("Copy to the clipboard"), ex is UserFacingException ? ex.Message : JobManager.Describe(ex)); }
    }

    public static async void CopyPath(IReadOnlyList<string> files)
    {
        var host = Host();
        if (host?.Clipboard == null) return;
        await host.Clipboard.SetTextAsync(string.Join(Environment.NewLine, files));
        Hud.ShowInfo(L.T("Path copied"), files.Count == 1 ? files[0] : L.F("Paths: {0}", files.Count), "");
    }

    public static void Reveal(IReadOnlyList<string> files) => Desktop.Reveal(files[0]);

    public static async void Move(IReadOnlyList<string> files)
    {
        var host = Host();
        if (host == null) return;
        var start = await host.StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(files[0]))!);
        var picked = await host.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = L.F("Move to: {0}", Catalog.Describe(files)), SuggestedStartLocation = start, AllowMultiple = false
        });
        var target = picked.FirstOrDefault()?.TryGetLocalPath();
        if (target == null) return;
        var comparison = OsInfo.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        JobManager.RunBatch(L.F("Moving to \"{0}\"", Path.GetFileName(target)), "", files, (f, ctx) =>
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(f));
            var dest = Path.Combine(target, name);
            if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(dest)), comparison))
                return Task.CompletedTask;
            // the name is taken - never overwrite, pick a free one
            if (File.Exists(dest) || Directory.Exists(dest))
                dest = Directory.Exists(f)
                    ? OutputPaths.UniqueFolder(target, name)
                    : OutputPaths.UniqueFile(target, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
            if (Directory.Exists(f))
            {
                if (Path.GetFullPath(dest).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)) + Path.DirectorySeparatorChar, comparison))
                    throw new UserFacingException(L.T("A folder cannot be moved into itself"));
                try { Directory.Move(f, dest); }
                catch (IOException)
                {
                    // another disk: copy, check, then remove the original
                    CopyDirectory(f, dest);
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
        try { Desktop.Terminal(Directory.Exists(first) ? first : Path.GetDirectoryName(first)!); }
        catch (Exception ex) { Hud.ShowError(L.T("Open in Terminal"), ex is UserFacingException ? ex.Message : JobManager.Describe(ex)); }
    }

    public static void Zip(IReadOnlyList<string> files)
    {
        if (files.Count == 1)
            JobManager.Run($"{Path.GetFileName(Path.TrimEndingDirectorySeparator(files[0]))} → ZIP", "", files, ctx => ArchiveEngine.ConvertAsync(files[0], "zip", ctx));
        else
            JobManager.Run(L.F("Packing into ZIP ({0})", files.Count), "", files, ctx => ArchiveEngine.PackManyAsync(files, ctx));
    }
}
