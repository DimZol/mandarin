#if !NET7_0_OR_GREATER
using System.Text;

// .NET 6 (the Windows 7 build) has no System.Formats.Tar: a small implementation of the part the archive engine uses.
// Writing: POSIX ustar headers, PAX records for long names. Reading: ustar, PAX and GNU long names.
namespace System.Formats.Tar;

public enum TarEntryFormat { Unknown, V7, Ustar, Pax, Gnu }

public enum TarEntryType : byte
{
    V7RegularFile = 0, RegularFile = (byte)'0', HardLink = (byte)'1', SymbolicLink = (byte)'2', CharacterDevice = (byte)'3',
    BlockDevice = (byte)'4', Directory = (byte)'5', Fifo = (byte)'6', ContiguousFile = (byte)'7', ExtendedAttributes = (byte)'x',
    GlobalExtendedAttributes = (byte)'g', LongPath = (byte)'L', LongLink = (byte)'K'
}

public class TarEntry
{
    public TarEntry(TarEntryType type, string name) { EntryType = type; Name = name; }
    public TarEntryType EntryType { get; }
    public string Name { get; }
    public Stream? DataStream { get; internal set; }
    public long Length { get; internal set; }
}

public sealed class PaxTarEntry : TarEntry
{
    public PaxTarEntry(TarEntryType type, string name) : base(type, name) { }
}

public sealed class TarWriter : IDisposable
{
    readonly Stream _stream;
    readonly bool _leaveOpen;

    public TarWriter(Stream stream, TarEntryFormat format, bool leaveOpen)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    public void WriteEntry(TarEntry entry) => Write(entry.Name, entry.EntryType, 0, DateTime.UtcNow, null);

    public void WriteEntry(string fileName, string entryName)
    {
        var info = new FileInfo(fileName);
        using var fs = info.OpenRead();
        Write(entryName, TarEntryType.RegularFile, info.Length, info.LastWriteTimeUtc, fs);
    }

    void Write(string name, TarEntryType type, long size, DateTime modified, Stream? data)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > 100 || nameBytes.Any(b => b > 127))
        {
            // PAX record "<len> path=<name>\n", the length counts itself
            var body = $" path={name}\n";
            var len = Encoding.UTF8.GetByteCount(body);
            var total = len + len.ToString().Length;
            if ((total).ToString().Length != len.ToString().Length) total++;
            var record = Encoding.UTF8.GetBytes(total + body);
            Header("PaxHeaders/" + Truncate(name), TarEntryType.ExtendedAttributes, record.Length, modified);
            _stream.Write(record);
            Pad(record.Length);
        }
        Header(Truncate(name), type, size, modified);
        if (data != null)
        {
            data.CopyTo(_stream);
            Pad(size);
        }
    }

    static string Truncate(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name)
        {
            if (ch > 127) continue;
            if (sb.Length >= 99) break;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    void Header(string name, TarEntryType type, long size, DateTime modified)
    {
        var h = new byte[512];
        void Put(int offset, int length, string value)
        {
            var b = Encoding.ASCII.GetBytes(value);
            Array.Copy(b, 0, h, offset, Math.Min(b.Length, length));
        }
        void Octal(int offset, int length, long value) => Put(offset, length, Convert.ToString(value, 8).PadLeft(length - 1, '0'));
        Put(0, 100, name);
        Octal(100, 8, type == TarEntryType.Directory ? 0x1ED : 0x1A4); // 755 / 644
        Octal(108, 8, 0);
        Octal(116, 8, 0);
        Octal(124, 12, size);
        Octal(136, 12, new DateTimeOffset(modified).ToUnixTimeSeconds());
        for (var i = 148; i < 156; i++) h[i] = (byte)' ';
        h[156] = (byte)type;
        Put(257, 6, "ustar");
        Put(263, 2, "00");
        var sum = h.Sum(b => b);
        Put(148, 8, Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ");
        _stream.Write(h);
    }

    void Pad(long size)
    {
        var rest = (int)(size % 512);
        if (rest != 0) _stream.Write(new byte[512 - rest]);
    }

    public void Dispose()
    {
        _stream.Write(new byte[1024]); // two empty blocks end the archive
        _stream.Flush();
        if (!_leaveOpen) _stream.Dispose();
    }
}

public sealed class TarReader : IDisposable
{
    readonly Stream _stream;
    long _remaining;

    public TarReader(Stream stream) => _stream = stream;

    public TarEntry? GetNextEntry()
    {
        Skip(_remaining);
        _remaining = 0;
        string? longName = null;
        while (true)
        {
            var h = new byte[512];
            if (!ReadExactly(h)) return null;
            if (h.All(b => b == 0)) return null;
            var name = Text(h, 0, 100);
            var prefix = Text(h, 345, 155);
            if (Text(h, 257, 5) == "ustar" && prefix.Length > 0) name = prefix + "/" + name;
            var size = Octal(h, 124, 12);
            var type = (TarEntryType)h[156];
            if (type is TarEntryType.ExtendedAttributes or TarEntryType.LongPath or TarEntryType.GlobalExtendedAttributes or TarEntryType.LongLink)
            {
                var data = new byte[size];
                ReadExactly(data);
                Skip(PadOf(size));
                if (type == TarEntryType.LongPath) longName = Encoding.UTF8.GetString(data).TrimEnd('\0');
                else if (type == TarEntryType.ExtendedAttributes)
                    foreach (var rec in Encoding.UTF8.GetString(data).Split('\n'))
                    {
                        var eq = rec.IndexOf(" path=", StringComparison.Ordinal);
                        if (eq >= 0) longName = rec[(eq + 6)..];
                    }
                continue;
            }
            var entry = new TarEntry(type, longName ?? name) { Length = size };
            if (size > 0) entry.DataStream = new Slice(_stream, size);
            _remaining = size + PadOf(size);
            // whatever the caller does not read is skipped before the next header
            if (entry.DataStream is Slice s) s.Consumed += n => _remaining -= n;
            return entry;
        }
    }

    static long PadOf(long size) => size % 512 == 0 ? 0 : 512 - size % 512;

    static string Text(byte[] h, int offset, int length)
    {
        var end = Array.IndexOf(h, (byte)0, offset, length);
        return Encoding.UTF8.GetString(h, offset, (end < 0 ? offset + length : end) - offset);
    }

    static long Octal(byte[] h, int offset, int length)
    {
        if ((h[offset] & 0x80) != 0) // GNU base-256
        {
            long v = h[offset] & 0x7F;
            for (var i = 1; i < length; i++) v = (v << 8) | h[offset + i];
            return v;
        }
        var s = Text(h, offset, length).Trim(' ', '\0');
        return s.Length == 0 ? 0 : Convert.ToInt64(s, 8);
    }

    bool ReadExactly(byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = _stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    void Skip(long count)
    {
        var buffer = new byte[8192];
        while (count > 0)
        {
            var n = _stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n == 0) return;
            count -= n;
        }
    }

    public void Dispose() { }

    /// <summary>The data of one entry.</summary>
    sealed class Slice : Stream
    {
        readonly Stream _inner;
        long _left;
        public event Action<long>? Consumed;
        public Slice(Stream inner, long length) { _inner = inner; _left = length; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0) return 0;
            var n = _inner.Read(buffer, offset, (int)Math.Min(count, _left));
            _left -= n;
            Consumed?.Invoke(n);
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
#endif
