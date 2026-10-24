using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using Mandarin.Core;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Mandarin.Engines;

/// <summary>ZIP, TAR, GZIP (tar.gz), plus reading RAR, 7z, bz2, xz.</summary>
public static class ArchiveEngine
{
    public static string OutExt(string target) => target == "gzip" ? "tar.gz" : target;

    /// <summary>Converts an archive or packs a folder/files.</summary>
    public static async Task ConvertAsync(string input, string target, JobContext ctx)
    {
        var isFolder = Directory.Exists(input);
        var outExt = target == "gzip" && !isFolder && FileKinds.Classify(input) != FileCategory.Archive ? FileKinds.Ext(input) + ".gz" : OutExt(target);
        var output = OutputPaths.File(input, outExt);

        if (isFolder)
        {
            await Task.Run(() => Pack(input, output, target, includeBaseFolder: true, ctx), ctx.Token);
        }
        else if (FileKinds.Classify(input) != FileCategory.Archive)
        {
            // single file
            if (target == "gzip")
            {
                await using var src = File.OpenRead(input);
                await using var dst = new FileStream(output, FileMode.CreateNew);
                await using var gz = new GZipStream(dst, CompressionLevel.SmallestSize);
                await src.CopyToAsync(gz, ctx.Token);
            }
            else
            {
                var temp = OutputPaths.TempDir();
                try
                {
                    File.Copy(input, Path.Combine(temp, Path.GetFileName(input)));
                    await Task.Run(() => Pack(temp, output, target, includeBaseFolder: false, ctx), ctx.Token);
                }
                finally { OutputPaths.TryDelete(temp); }
            }
        }
        else
        {
            var temp = OutputPaths.TempDir();
            try
            {
                ctx.Detail(L.T("Extracting…"));
                await Task.Run(() => ExtractTo(input, temp, ctx), ctx.Token);
                ctx.Detail(L.T("Packing…"));
                await Task.Run(() => Pack(temp, output, target, includeBaseFolder: false, ctx), ctx.Token);
            }
            finally { OutputPaths.TryDelete(temp); }
        }
        ctx.AddOutput(output);
    }

    /// <summary>Packs several files/folders into one ZIP (action for a selection).</summary>
    public static async Task PackManyAsync(IReadOnlyList<string> items, JobContext ctx)
    {
        var output = OutputPaths.UniqueFile(OutputPaths.DirectoryFor(items[0]), L.T("Archive"), "zip");
        await Task.Run(() =>
        {
            try
            {
                using var fs = new FileStream(output, FileMode.CreateNew);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < items.Count; i++)
                {
                    ctx.Report((double)i / items.Count);
                    // identical names from different folders must not overwrite each other on extraction
                    var name = Path.GetFileName(items[i].TrimEnd('\\'));
                    var unique = name;
                    for (var k = 2; !used.Add(unique); k++) unique = $"{Path.GetFileNameWithoutExtension(name)} ({k}){Path.GetExtension(name)}";
                    if (Directory.Exists(items[i])) AddFolder(zip, items[i], unique, ctx, output);
                    else zip.CreateEntryFromFile(items[i], unique, CompressionLevel.Optimal);
                }
            }
            catch
            {
                OutputPaths.TryDelete(output);
                throw;
            }
        }, ctx.Token);
        ctx.AddOutput(output);
    }

    static void AddFolder(ZipArchive zip, string folder, string prefix, JobContext ctx, string output)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            ctx.Token.ThrowIfCancellationRequested();
            // an archive saved inside the folder being packed must not include itself
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(folder, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, prefix.Length > 0 ? $"{prefix}/{rel}" : rel, CompressionLevel.Optimal);
        }
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories))
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                var rel = Path.GetRelativePath(folder, dir).Replace('\\', '/');
                zip.CreateEntry((prefix.Length > 0 ? $"{prefix}/{rel}" : rel) + "/");
            }
    }

    static void Pack(string folder, string output, string target, bool includeBaseFolder, JobContext ctx)
    {
        var prefix = includeBaseFolder ? Path.GetFileName(folder.TrimEnd('\\')) : "";
        if (target is not ("zip" or "tar" or "gzip")) throw new UserFacingException(L.F("Cannot create a {0} archive", target.ToUpperInvariant()));
        // CreateNew: an existing file is never overwritten and never deleted on failure
        var fs = new FileStream(output, FileMode.CreateNew);
        try
        {
            switch (target)
            {
                case "zip":
                    using (fs)
                    using (var zip = new ZipArchive(fs, ZipArchiveMode.Create)) AddFolder(zip, folder, prefix, ctx, output);
                    break;
                case "tar":
                    using (fs) WriteTar(fs, folder, prefix, ctx, output);
                    break;
                case "gzip":
                    using (fs)
                    using (var gz = new GZipStream(fs, CompressionLevel.SmallestSize))
                        WriteTar(gz, folder, prefix, ctx, output);
                    break;
                default:
                    throw new UserFacingException(L.F("Cannot create a {0} archive", target.ToUpperInvariant()));
            }
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
    }

    static void WriteTar(Stream stream, string folder, string prefix, JobContext ctx, string output)
    {
        using var tar = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
        {
            ctx.Token.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFullPath(entry), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(folder, entry).Replace('\\', '/');
            var name = prefix.Length > 0 ? $"{prefix}/{rel}" : rel;
            if (Directory.Exists(entry)) tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name + "/"));
            else tar.WriteEntry(entry, name);
        }
    }

    /// <summary>Extracts next to the archive into a new folder.</summary>
    public static async Task ExtractAsync(string input, JobContext ctx)
    {
        var folder = OutputPaths.Folder(input);
        if (Directory.Exists(folder)) throw new UserFacingException(L.T("The extraction folder already exists"));
        Directory.CreateDirectory(folder);
        try
        {
            await Task.Run(() => ExtractTo(input, folder, ctx), ctx.Token);
        }
        catch
        {
            OutputPaths.TryDelete(folder);
            throw;
        }
        // exactly one folder inside - avoid needless nesting
        var entries = Directory.GetFileSystemEntries(folder);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
        {
            var inner = entries[0];
            var target = OutputPaths.UniqueFolder(Path.GetDirectoryName(folder)!, Path.GetFileName(inner));
            var tmp = folder + ".tmp-" + Guid.NewGuid().ToString("N")[..6];
            Directory.Move(inner, tmp);
            Directory.Delete(folder);
            Directory.Move(tmp, target);
            folder = target;
        }
        ctx.AddOutput(folder);
    }

    /// <summary>
    /// Safe extraction: paths cannot leave the target folder, only new files are created,
    /// and the total size is limited by free disk space (protection against archive bombs).
    /// </summary>
    public static void ExtractTo(string input, string destination, JobContext ctx)
    {
        var sep = Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(destination).TrimEnd(sep) + sep;
        string Safe(string entryName)
        {
            var full = Path.GetFullPath(Path.Combine(root, entryName.Replace('\\', '/').Replace('/', sep).TrimStart(sep)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.Length == root.Length)
                throw new UserFacingException(L.T("The archive contains unsafe paths - extraction stopped"));
            return full;
        }

        // always leave at least 1 GB free on the disk
        var budget = Math.Max(0, new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace - (1L << 30));
        void CheckDeclared(long total)
        {
            if (total > budget)
                throw new UserFacingException(L.F("Extraction needs {0}, but less is free (keeping a 1 GB reserve)", OutputPaths.HumanSize(total)));
        }
        void Write(Stream source, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var outFs = new FileStream(path, FileMode.CreateNew);
            var buffer = new byte[81920];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                ctx.Token.ThrowIfCancellationRequested();
                budget -= read;
                if (budget < 0) throw new UserFacingException(L.T("The extracted data does not fit on the disk - extraction stopped"));
                outFs.Write(buffer, 0, read);
            }
        }
        void ExtractTar(Stream s)
        {
            using var reader = new TarReader(s);
            TarEntry? e;
            while ((e = reader.GetNextEntry()) != null)
            {
                ctx.Token.ThrowIfCancellationRequested();
                var path = Safe(e.Name);
                if (e.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(path); continue; }
                // links and devices are never created - regular files only
                if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile)) continue;
                if (e.DataStream == null) { Write(Stream.Null, path); continue; }
                Write(e.DataStream, path);
            }
        }

        var ext = FileKinds.Ext(input);
        if (ext == "zip")
        {
            using var zip = ZipFile.OpenRead(input);
            CheckDeclared(zip.Entries.Sum(e => e.Length));
            var total = Math.Max(1, zip.Entries.Count);
            var i = 0;
            foreach (var e in zip.Entries)
            {
                ctx.Token.ThrowIfCancellationRequested();
                ctx.Report((double)i++ / total);
                var path = Safe(e.FullName);
                if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                using var es = e.Open();
                Write(es, path);
            }
            return;
        }
        if (ext is "tar" or "tar.gz" or "tgz")
        {
            using var fs = File.OpenRead(input);
            using Stream s = ext == "tar" ? fs : new GZipStream(fs, CompressionMode.Decompress);
            ExtractTar(s);
            return;
        }
        if (ext == "gz")
        {
            // single compressed file
            var path = Safe(Path.GetFileNameWithoutExtension(input));
            using var fs = File.OpenRead(input);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            Write(gz, path);
            return;
        }

        // RAR, 7z, bz2, xz and the rest go through SharpCompress
        using var archive = ArchiveFactory.OpenArchive(input);
        var entries = archive.Entries.Where(en => !en.IsDirectory).ToList();
        CheckDeclared(entries.Sum(e => Math.Max(0, e.Size)));
        var innerTarOnly = ext is "bz2" or "xz" && entries.Count == 1 && FileKinds.Ext(entries[0].Key ?? "") == "tar";
        var n = 0;
        foreach (var e in entries)
        {
            ctx.Token.ThrowIfCancellationRequested();
            ctx.Report((double)n++ / Math.Max(1, entries.Count));
            if (e.IsEncrypted) throw new UserFacingException(L.T("The archive is password protected"));
            using var es = e.OpenEntryStream();
            // .tar.bz2 / .tar.xz: the inner tar is extracted as a stream without being saved to disk
            if (innerTarOnly) ExtractTar(es);
            else Write(es, Safe(e.Key ?? $"file{n}"));
        }
    }
}