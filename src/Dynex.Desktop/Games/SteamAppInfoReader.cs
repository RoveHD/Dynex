using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Dynex.Desktop.Games;

/// <summary>Only reads app IDs and common/type from Steam's local v39-v41 cache.</summary>
internal static class SteamAppInfoReader
{
    internal const int MaxFileBytes = 128 * 1024 * 1024;
    private const int MaxEntryBytes = 4 * 1024 * 1024;
    private const int MaxEntries = 200_000;
    private const int MaxTableBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static IReadOnlyDictionary<uint, string> ReadTypes(string path)
    {
        try
        {
            // Do not prevent Steam updating/replacing its own cache. Publish a
            // snapshot only when its bounded read completed without a file change.
            var modified = File.GetLastWriteTimeUtc(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length is < 12 or > MaxFileBytes) return new Dictionary<uint, string>();
            var bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
            if (file.Length != bytes.Length || File.GetLastWriteTimeUtc(path) != modified)
                return new Dictionary<uint, string>();
            return Parse(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException or OverflowException)
        { return new Dictionary<uint, string>(); }
    }

    internal static IReadOnlyDictionary<uint, string> Parse(ReadOnlySpan<byte> bytes)
    {
        try { return ParseCore(bytes); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
        { return new Dictionary<uint, string>(); }
    }

    private static Dictionary<uint, string> ParseCore(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 12 or > MaxFileBytes) throw new InvalidDataException();
        var cursor = new Cursor(bytes);
        var magic = cursor.UInt32();
        if (magic is not (0x07564427 or 0x07564428 or 0x07564429) || cursor.UInt32() != 1)
            throw new InvalidDataException();
        var version = (int)(magic & 255);
        string[]? strings = null;
        var entriesEnd = bytes.Length;
        if (version == 41)
        {
            var tableOffset = cursor.Int64();
            if (tableOffset < 20 || tableOffset > bytes.Length - 4 || bytes.Length - tableOffset > MaxTableBytes)
                throw new InvalidDataException();
            entriesEnd = checked((int)tableOffset);
            var table = new Cursor(bytes[entriesEnd..]);
            var count = table.UInt32();
            if (count > MaxEntries || count > table.Remaining) throw new InvalidDataException();
            strings = new string[count];
            for (var i = 0; i < count; i++) strings[i] = table.String(4096);
            if (table.Remaining != 0) throw new InvalidDataException();
        }

        var types = new Dictionary<uint, string>();
        var seenIds = new HashSet<uint>();
        var entries = 0;
        while (true)
        {
            if (cursor.Position + 4 > entriesEnd) throw new InvalidDataException();
            var id = cursor.UInt32();
            if (id == 0)
            {
                if (cursor.Position != entriesEnd) throw new InvalidDataException();
                return types;
            }
            if (++entries > MaxEntries || cursor.Position + 4 > entriesEnd) throw new InvalidDataException();
            if (!seenIds.Add(id)) types[id] = "Unknown";
            var size = cursor.UInt32();
            var metadataBytes = version >= 40 ? 60 : 40;
            if (size < metadataBytes + 1 || size > MaxEntryBytes || size > entriesEnd - cursor.Position)
                throw new InvalidDataException();
            var entry = cursor.Take(checked((int)size));
            var data = entry[metadataBytes..];
            // v40/v41 store a hash of this exact binary blob (including pooled
            // key indices). Reject a torn/corrupt entry instead of guessing type.
            if (version >= 40 && !SHA1.HashData(data).AsSpan().SequenceEqual(entry.Slice(40, 20))) continue;
            try
            {
                var reader = new TypeReader(data, strings);
                var type = reader.Read();
                if (type is not null)
                {
                    // Duplicated IDs are not a basis for positive classification.
                    if (types.ContainsKey(id)) types[id] = "Unknown";
                    else types.Add(id, type);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { /* Other structurally bounded records remain independently usable. */ }
        }
    }

    private ref struct Cursor(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        internal int Position { get; private set; }
        internal int Remaining => _bytes.Length - Position;
        internal byte Byte() => Take(1)[0];
        internal uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        internal long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        internal ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > Remaining) throw new InvalidDataException();
            var value = _bytes.Slice(Position, count);
            Position += count;
            return value;
        }
        internal ReadOnlySpan<byte> CString(int maxBytes)
        {
            var inspected = _bytes.Slice(Position, Math.Min(Remaining, maxBytes + 1));
            var count = inspected.IndexOf((byte)0);
            if (count < 0) throw new InvalidDataException();
            var value = Take(count);
            Take(1);
            return value;
        }
        internal string String(int maxBytes) => Utf8.GetString(CString(maxBytes));
    }

    private ref struct TypeReader(ReadOnlySpan<byte> bytes, string[]? strings)
    {
        private Cursor _cursor = new(bytes);
        private readonly string[]? _strings = strings;
        private int _nodes;
        private string? _type;
        private bool _rootSeen;

        internal string? Read()
        {
            ReadBlock(0, 0);
            if (_cursor.Remaining != 0 || !_rootSeen) throw new InvalidDataException();
            return _type;
        }

        private void ReadBlock(int depth, int scope)
        {
            if (depth > 64) throw new InvalidDataException();
            while (true)
            {
                var tag = _cursor.Byte();
                if (tag is 8 or 11) return;
                if (++_nodes > 100_000) throw new InvalidDataException();
                var name = Key();
                if (tag == 0)
                {
                    var next = -1;
                    if (scope == 0 && name.Equals("appinfo", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_rootSeen) throw new InvalidDataException();
                        _rootSeen = true;
                        next = 1;
                    }
                    else if (scope == 1 && name.Equals("common", StringComparison.OrdinalIgnoreCase)) next = 2;
                    ReadBlock(depth + 1, next);
                }
                else if (tag == 1)
                {
                    if (scope == 2 && name.Equals("type", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_type is not null) throw new InvalidDataException();
                        _type = _cursor.String(64);
                    }
                    else _cursor.CString(65536);
                }
                else if (tag is 2 or 3 or 4 or 6) _cursor.Take(4);
                else if (tag is 7 or 10) _cursor.Take(8);
                // SteamKit likewise treats WideString as unsupported. New or
                // unknown value encodings never become an inferred Game.
                else throw new InvalidDataException();
            }
        }

        private string Key()
        {
            if (_strings is null) return _cursor.String(4096);
            var index = _cursor.UInt32();
            if (index >= _strings.Length) throw new InvalidDataException();
            return _strings[index];
        }
    }
}
