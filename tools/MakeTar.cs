// Packs a published Linux build into .tar.gz, keeping the executable bit of the program and its native libraries
// (a zip made on Windows loses it). Usage: dotnet run tools/MakeTar.cs -- <folder> <archive.tar.gz> <top folder name>
using System.Formats.Tar;
using System.IO.Compression;

var (src, dst, top) = (Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), args[2]);
var exec = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
           UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
var plain = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

using var fs = File.Create(dst);
using var gz = new GZipStream(fs, CompressionLevel.SmallestSize);
using var tar = new TarWriter(gz, TarEntryFormat.Pax);
tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top + "/") { Mode = exec });
foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories).Order())
    tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, $"{top}/{Path.GetRelativePath(src, dir).Replace('\\', '/')}/") { Mode = exec });
foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).Order())
{
    var rel = Path.GetRelativePath(src, file).Replace('\\', '/');
    var name = Path.GetFileName(file);
    var isExec = name == "Mandarin" || name.EndsWith(".so") || name.Contains(".so.") || name == "createdump";
    using var data = File.OpenRead(file);
    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{top}/{rel}")
    {
        Mode = isExec ? exec : plain, DataStream = data, ModificationTime = File.GetLastWriteTimeUtc(file)
    });
}
Console.WriteLine($"Archive: {dst}");
