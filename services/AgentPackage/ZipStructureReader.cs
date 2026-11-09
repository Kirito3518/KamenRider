using System.Buffers.Binary;
using System.Text;

namespace KamenRaider.AgentPackage;

internal static class ZipStructureReader
{
    internal sealed record Entry(string Name, ushort Method, uint Crc, int CompressedLength,
        int Length, int DataOffset, uint Attributes);

    private sealed record Central(string Name, byte[] RawName, ushort Flags, ushort Method, ushort Version,
        uint Crc, int CompressedLength, int Length, int LocalOffset, uint Attributes, Extra Extra);

    private sealed record Extra(uint? UnixModified, uint? UnixAccessed, uint? UnixCreated,
        long? NtfsModified, long? NtfsAccessed, long? NtfsCreated);

    internal static IReadOnlyList<Entry> Read(byte[] bytes)
    {
        var data = bytes.AsSpan();
        if (data.Length < 22) throw Fail();
        var end = -1;
        for (var i = data.Length - 22; i >= Math.Max(0, data.Length - 65557); i--)
        {
            if (U32(data, i) == 0x06054b50 && i + 22 + U16(data, i + 20) == data.Length)
            {
                end = i;
                break;
            }
        }
        if (end < 0 || U16(data, end + 4) != 0 || U16(data, end + 6) != 0) throw Fail();
        var diskEntries = U16(data, end + 8);
        var count = U16(data, end + 10);
        var centralLength = U32(data, end + 12);
        var centralOffset = U32(data, end + 16);
        if (count == ushort.MaxValue || diskEntries != count || centralLength == uint.MaxValue || centralOffset == uint.MaxValue)
            throw Fail();
        if (count > 5000) throw Limit();
        if ((long)centralOffset + centralLength != end) throw Fail();

        var central = new List<Central>(count);
        var pos = checked((int)centralOffset);
        long expanded = 0;
        for (var index = 0; index < count; index++)
        {
            Need(data, pos, 46);
            if (U32(data, pos) != 0x02014b50) throw Fail();
            var version = U16(data, pos + 6);
            var flags = U16(data, pos + 8);
            var method = U16(data, pos + 10);
            var crc = U32(data, pos + 16);
            var compressed = U32(data, pos + 20);
            var length = U32(data, pos + 24);
            var nameLength = U16(data, pos + 28);
            var extraLength = U16(data, pos + 30);
            var commentLength = U16(data, pos + 32);
            var disk = U16(data, pos + 34);
            var attributes = U32(data, pos + 38);
            var local = U32(data, pos + 42);
            if (version > 20 || (flags & ~0x080e) != 0 || method is not (0 or 8) || (method == 0 && (flags & 6) != 0)
                || disk != 0 || compressed == uint.MaxValue || length == uint.MaxValue || local == uint.MaxValue)
                throw Fail();
            Need(data, pos + 46, nameLength + extraLength + commentLength);
            var rawName = data.Slice(pos + 46, nameLength).ToArray();
            if (rawName.Any(c => c > 126 || c < 32)) throw new PackageValidationException("path", "ZIP name is not printable ASCII.");
            var name = Encoding.ASCII.GetString(rawName);
            var extra = ReadExtra(data.Slice(pos + 46 + nameLength, extraLength), true, rawName);
            expanded += length;
            if (expanded > 524288000) throw Limit();
            if (compressed > int.MaxValue || length > int.MaxValue || local > int.MaxValue) throw Fail();
            central.Add(new Central(name, rawName, flags, method, version, crc, (int)compressed,
                (int)length, (int)local, attributes, extra));
            pos += 46 + nameLength + extraLength + commentLength;
        }
        if (pos != end) throw Fail();

        central.Sort((a, b) => a.LocalOffset.CompareTo(b.LocalOffset));
        var entries = new List<Entry>(count);
        for (var index = 0; index < count; index++)
        {
            var c = central[index];
            var next = index + 1 == count ? (int)centralOffset : central[index + 1].LocalOffset;
            if (index == 0 && c.LocalOffset != 0) throw Fail();
            Need(data, c.LocalOffset, 30);
            pos = c.LocalOffset;
            if (U32(data, pos) != 0x04034b50 || U16(data, pos + 4) != c.Version
                || U16(data, pos + 6) != c.Flags || U16(data, pos + 8) != c.Method) throw Fail();
            var crc = U32(data, pos + 14);
            var compressed = U32(data, pos + 18);
            var length = U32(data, pos + 22);
            var nameLength = U16(data, pos + 26);
            var extraLength = U16(data, pos + 28);
            Need(data, pos + 30, nameLength + extraLength);
            if (!data.Slice(pos + 30, nameLength).SequenceEqual(c.RawName)) throw Fail();
            var extra = ReadExtra(data.Slice(pos + 30 + nameLength, extraLength), false, c.RawName);
            if (extra.UnixModified.HasValue && c.Extra.UnixModified.HasValue && extra.UnixModified != c.Extra.UnixModified
                || extra.UnixAccessed.HasValue && c.Extra.UnixAccessed.HasValue && extra.UnixAccessed != c.Extra.UnixAccessed
                || extra.UnixCreated.HasValue && c.Extra.UnixCreated.HasValue && extra.UnixCreated != c.Extra.UnixCreated
                || extra.NtfsModified.HasValue && c.Extra.NtfsModified.HasValue && extra.NtfsModified != c.Extra.NtfsModified
                || extra.NtfsAccessed.HasValue && c.Extra.NtfsAccessed.HasValue && extra.NtfsAccessed != c.Extra.NtfsAccessed
                || extra.NtfsCreated.HasValue && c.Extra.NtfsCreated.HasValue && extra.NtfsCreated != c.Extra.NtfsCreated
                || !TimesAgree(extra.UnixModified, c.Extra.NtfsModified) || !TimesAgree(c.Extra.UnixModified, extra.NtfsModified)
                || !TimesAgree(extra.UnixAccessed, c.Extra.NtfsAccessed) || !TimesAgree(c.Extra.UnixAccessed, extra.NtfsAccessed)
                || !TimesAgree(extra.UnixCreated, c.Extra.NtfsCreated) || !TimesAgree(c.Extra.UnixCreated, extra.NtfsCreated))
                throw Fail();
            var descriptor = (c.Flags & 8) != 0;
            if (descriptor)
            {
                if (crc != 0 && crc != c.Crc || compressed != 0 && compressed != c.CompressedLength
                    || length != 0 && length != c.Length) throw Fail();
            }
            else if (crc != c.Crc || compressed != c.CompressedLength || length != c.Length) throw Fail();
            var start = checked(pos + 30 + nameLength + extraLength);
            var after = (long)start + c.CompressedLength;
            if (after > next || after > centralOffset) throw Fail();
            if (descriptor)
            {
                var d = (int)after;
                if (next - d == 16)
                {
                    if (U32(data, d) != 0x08074b50) throw Fail();
                    d += 4;
                }
                else if (next - d != 12) throw Fail();
                Need(data, d, 12);
                if (U32(data, d) != c.Crc || U32(data, d + 4) != c.CompressedLength || U32(data, d + 8) != c.Length) throw Fail();
            }
            else if (after != next) throw Fail();
            entries.Add(new Entry(c.Name, c.Method, c.Crc, c.CompressedLength, c.Length, start, c.Attributes));
        }
        if (count == 0 && centralOffset != 0) throw Fail();
        return entries;
    }

    private static Extra ReadExtra(ReadOnlySpan<byte> bytes, bool central, ReadOnlySpan<byte> name)
    {
        uint? modified = null, accessed = null, created = null;
        long? ntfsModified = null, ntfsAccessed = null, ntfsCreated = null;
        var seen = new HashSet<ushort>();
        while (!bytes.IsEmpty)
        {
            Need(bytes, 0, 4);
            var id = U16(bytes, 0);
            var size = U16(bytes, 2);
            Need(bytes, 4, size);
            if (!seen.Add(id)) throw Fail();
            var value = bytes.Slice(4, size);
            if (id == 0x5455)
            {
                if (value.Length < 1 || (value[0] & ~7) != 0) throw Fail();
                var flags = value[0];
                var expected = 1 + ((flags & 1) != 0 ? 4 : 0) + (!central && (flags & 2) != 0 ? 4 : 0) + (!central && (flags & 4) != 0 ? 4 : 0);
                if (value.Length != expected) throw Fail();
                var offset = 1;
                if ((flags & 1) != 0) { modified = U32(value, offset); offset += 4; }
                if (!central && (flags & 2) != 0) { accessed = U32(value, offset); offset += 4; }
                if (!central && (flags & 4) != 0) created = U32(value, offset);
            }
            else if (id == 0x000a)
            {
                if (value.Length != 32 || U32(value, 0) != 0 || U16(value, 4) != 1 || U16(value, 6) != 24) throw Fail();
                ntfsModified = BinaryPrimitives.ReadInt64LittleEndian(value.Slice(8, 8));
                ntfsAccessed = BinaryPrimitives.ReadInt64LittleEndian(value.Slice(16, 8));
                ntfsCreated = BinaryPrimitives.ReadInt64LittleEndian(value.Slice(24, 8));
                if (ntfsModified < 0 || ntfsAccessed < 0 || ntfsCreated < 0) throw Fail();
            }
            else if (id == 0x7075)
            {
                if (value.Length < 5 || value[0] != 1 || U32(value, 1) != NameCrc(name)
                    || !value[5..].SequenceEqual(name)) throw Fail();
            }
            else throw Fail();
            bytes = bytes[(4 + size)..];
        }
        if (!TimesAgree(modified, ntfsModified) || !TimesAgree(accessed, ntfsAccessed) || !TimesAgree(created, ntfsCreated)) throw Fail();
        return new Extra(modified, accessed, created, ntfsModified, ntfsAccessed, ntfsCreated);
    }

    private static bool TimesAgree(uint? unix, long? ntfs)
        => !unix.HasValue || !ntfs.HasValue || ntfs.Value >= 116444736000000000L
            && (ntfs.Value - 116444736000000000L) / 10000000L == unix.Value;

    private static uint NameCrc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    private static ushort U16(ReadOnlySpan<byte> b, int p) { Need(b, p, 2); return BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(p, 2)); }
    private static uint U32(ReadOnlySpan<byte> b, int p) { Need(b, p, 4); return BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(p, 4)); }
    private static void Need(ReadOnlySpan<byte> b, int p, int count)
    {
        if (p < 0 || count < 0 || (long)p + count > b.Length) throw Fail();
    }
    private static PackageValidationException Fail() => new("structure", "ZIP structure is invalid or unsupported.");
    private static PackageValidationException Limit() => new("limit", "Package exceeds a resource limit.");
}

// DeflateStream accepts some truncated streams and trailing compressed bytes.
// Inspect the raw block grammar as well as verifying the decompressed CRC.
internal static class DeflateInspector
{
    private static readonly int[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly int[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly int[] DistanceBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly int[] DistanceExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
    private static readonly int[] Order = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
    private static readonly Huffman FixedLiteral = CreateFixedLiteral();
    private static readonly Huffman FixedDistance = new(Enumerable.Repeat(5, 32).ToArray(), false, false);

    internal static void Verify(ReadOnlySpan<byte> data, int expected)
    {
        var bits = new Bits(data);
        long produced = 0;
        bool final;
        do
        {
            final = bits.Read(1) != 0;
            var type = bits.Read(2);
            if (type == 0)
            {
                bits.Align();
                var length = bits.Read(16);
                var complement = bits.Read(16);
                if ((length ^ 0xffff) != complement) throw Fail();
                bits.Skip(length);
                produced += length;
                if (produced > expected) throw Fail();
                continue;
            }
            if (type == 3) throw Fail();
            Huffman literal, distance;
            if (type == 1) { literal = FixedLiteral; distance = FixedDistance; }
            else (literal, distance) = Dynamic(ref bits);
            while (true)
            {
                var symbol = literal.Read(ref bits);
                if (symbol < 256)
                {
                    produced++;
                }
                else if (symbol == 256) break;
                else
                {
                    var index = symbol - 257;
                    if ((uint)index >= LengthBase.Length) throw Fail();
                    var length = LengthBase[index] + bits.Read(LengthExtra[index]);
                    var distCode = distance.Read(ref bits);
                    if ((uint)distCode >= DistanceBase.Length) throw Fail();
                    var offset = DistanceBase[distCode] + bits.Read(DistanceExtra[distCode]);
                    if (offset > produced || offset > 32768) throw Fail();
                    produced += length;
                }
                if (produced > expected) throw Fail();
            }
        } while (!final);
        if (produced != expected || bits.BytesConsumed != data.Length) throw Fail();
    }

    private static (Huffman Literal, Huffman Distance) Dynamic(ref Bits bits)
    {
        var literals = bits.Read(5) + 257;
        var distances = bits.Read(5) + 1;
        var codeLengths = bits.Read(4) + 4;
        if (literals > 286 || distances > 32) throw Fail();
        var alphabet = new int[19];
        for (var i = 0; i < codeLengths; i++) alphabet[Order[i]] = bits.Read(3);
        var codeTree = new Huffman(alphabet, true, false);
        var lengths = new int[literals + distances];
        for (var index = 0; index < lengths.Length;)
        {
            var symbol = codeTree.Read(ref bits);
            if (symbol < 16) { lengths[index++] = symbol; continue; }
            int count, value;
            if (symbol == 16)
            {
                if (index == 0) throw Fail();
                count = bits.Read(2) + 3;
                value = lengths[index - 1];
            }
            else if (symbol == 17) { count = bits.Read(3) + 3; value = 0; }
            else if (symbol == 18) { count = bits.Read(7) + 11; value = 0; }
            else throw Fail();
            if (index + count > lengths.Length) throw Fail();
            Array.Fill(lengths, value, index, count);
            index += count;
        }
        if (lengths[256] == 0) throw Fail();
        return (new Huffman(lengths[..literals], false, false), new Huffman(lengths[literals..], false, true));
    }

    private static Huffman CreateFixedLiteral()
    {
        var lengths = new int[288];
        Array.Fill(lengths, 8, 0, 144);
        Array.Fill(lengths, 9, 144, 112);
        Array.Fill(lengths, 7, 256, 24);
        Array.Fill(lengths, 8, 280, 8);
        return new Huffman(lengths, false, false);
    }

    private sealed class Huffman
    {
        private readonly int[] counts = new int[16];
        private readonly int[] firstCode = new int[16];
        private readonly int[] firstIndex = new int[16];
        private readonly int[] symbols;
        private readonly int max;

        internal Huffman(int[] lengths, bool complete, bool allowEmpty)
        {
            foreach (var length in lengths)
            {
                if ((uint)length > 15) throw Fail();
                if (length != 0) { counts[length]++; max = Math.Max(max, length); }
            }
            if (max == 0 && !allowEmpty) throw Fail();
            var left = 1;
            var code = 0;
            var index = 0;
            for (var length = 1; length <= 15; length++)
            {
                left = (left << 1) - counts[length];
                if (left < 0) throw Fail();
                code = (code + counts[length - 1]) << 1;
                firstCode[length] = code;
                firstIndex[length] = index;
                index += counts[length];
            }
            if (max != 0 && left != 0 && (complete || max != 1 || counts[1] != 1)) throw Fail();
            symbols = new int[index];
            var positions = (int[])firstIndex.Clone();
            for (var symbol = 0; symbol < lengths.Length; symbol++)
                if (lengths[symbol] != 0) symbols[positions[lengths[symbol]]++] = symbol;
        }

        internal int Read(ref Bits bits)
        {
            var code = 0;
            for (var length = 1; length <= max; length++)
            {
                code = (code << 1) | bits.Read(1);
                var offset = code - firstCode[length];
                if ((uint)offset < counts[length]) return symbols[firstIndex[length] + offset];
            }
            throw Fail();
        }
    }

    private ref struct Bits
    {
        private readonly ReadOnlySpan<byte> data;
        private int position;
        internal Bits(ReadOnlySpan<byte> data) { this.data = data; position = 0; }
        internal readonly int BytesConsumed => (position + 7) / 8;
        internal int Read(int count)
        {
            if ((long)position + count > (long)data.Length * 8) throw Fail();
            var value = 0;
            for (var i = 0; i < count; i++, position++) value |= ((data[position >> 3] >> (position & 7)) & 1) << i;
            return value;
        }
        internal void Align() => position = (position + 7) & ~7;
        internal void Skip(int count)
        {
            if ((long)position + (long)count * 8 > (long)data.Length * 8) throw Fail();
            position += count * 8;
        }
    }

    private static PackageValidationException Fail() => new("structure", "Deflate data is invalid or incomplete.");
}
