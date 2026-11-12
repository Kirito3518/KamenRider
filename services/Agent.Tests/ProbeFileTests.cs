using System.Buffers.Binary;
using System.Text;
using KamenRaider.Agent.Probe;

namespace KamenRaider.Agent.Tests;

public sealed class ProbeFileTests
{
    private const string MachineId = "0123456789abcdef0123456789abcdef";
    private static readonly string Fingerprint = string.Concat(Enumerable.Repeat("AB", 32));
    private const string Seed = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string CanonicalCleanJson = "{\"schemaVersion\":1,\"machineId\":\"0123456789abcdef0123456789abcdef\",\"machineKeyFingerprint\":\"ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB\",\"policyRevision\":7,\"baselineId\":\"baseline-雪-🌱\",\"recoveryGeneration\":\"recovery-é\",\"kind\":\"clean\",\"generation\":0,\"seed\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}";
    private const string EscapingGoldenJson = "{\"schemaVersion\":1,\"machineId\":\"0123456789abcdef0123456789abcdef\",\"machineKeyFingerprint\":\"ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB\",\"policyRevision\":7,\"baselineId\":\"quote\\\" backslash\\\\ slash/ <>& 雪 🌱\",\"recoveryGeneration\":\"recovery-é\",\"kind\":\"clean\",\"generation\":0,\"seed\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}";

    [Fact]
    public void EncodeWritesExactFixedRecordAndCanonicalJsonAndRoundTripsUnicode()
    {
        var record = CleanRecord();

        var bytes = ProbeFileFormat.Encode(record);

        Assert.Equal(4096, bytes.Length);
        Assert.Equal("KRPROBE1", Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));
        var jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
        Assert.Equal((uint)Encoding.UTF8.GetByteCount(CanonicalCleanJson), jsonLength);
        Assert.Equal(CanonicalCleanJson, Encoding.UTF8.GetString(bytes, 16, (int)jsonLength));
        Assert.All(bytes.Skip(16 + (int)jsonLength), padding => Assert.Equal((byte)0, padding));
        Assert.Equal(record, ProbeFileFormat.Decode(bytes));
    }

    [Fact]
    public void EncodeAndDecodeAcceptMaximumPositiveInt64DirtyGeneration()
    {
        var record = CleanRecord() with
        {
            PolicyRevision = long.MaxValue,
            Kind = "dirty",
            Generation = long.MaxValue,
        };

        var decoded = ProbeFileFormat.Decode(ProbeFileFormat.Encode(record));

        Assert.Equal(record, decoded);
    }

    [Fact]
    public void EncodeUsesExactCanonicalEscapingGoldenVector()
    {
        var record = CleanRecord() with
        {
            BaselineId = "quote\" backslash\\ slash/ <>& 雪 🌱",
        };

        var bytes = ProbeFileFormat.Encode(record);
        var jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
        var jsonBytes = bytes.AsSpan(16, (int)jsonLength).ToArray();

        Assert.Equal(Encoding.UTF8.GetBytes(EscapingGoldenJson), jsonBytes);
        Assert.Equal(record, ProbeFileFormat.Decode(FileBytes(Encoding.UTF8.GetBytes(EscapingGoldenJson))));
    }

    [Theory]
    [MemberData(nameof(EquivalentButNonCanonicalJson))]
    public void DecodeRejectsEquivalentButNoncanonicalStringEscapes(string json)
    {
        Assert.Throws<InvalidDataException>(() => ProbeFileFormat.Decode(FileBytes(Encoding.UTF8.GetBytes(json))));
    }

    [Fact]
    public void DecodeRejectsEscapedControlCharacterInTextField()
    {
        var json = CanonicalCleanJson.Replace("baseline-雪-🌱", "baseline-\\u000A", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => ProbeFileFormat.Decode(FileBytes(Encoding.UTF8.GetBytes(json))));
    }

    [Theory]
    [InlineData(4095)]
    [InlineData(4097)]
    public void DecodeRejectsAnyRecordSizeOtherThan4096(int length)
    {
        Assert.Throws<InvalidDataException>(() => ProbeFileFormat.Decode(new byte[length]));
    }

    [Fact]
    public void DecodeRejectsWrongMagic()
    {
        var bytes = ProbeFileFormat.Encode(CleanRecord());
        bytes[0] ^= 0x01;

        Assert.Throws<InvalidDataException>(() => ProbeFileFormat.Decode(bytes));
    }

    [Fact]
    public void DecodeRejectsUnsupportedHeaderSchema()
    {
        var bytes = ProbeFileFormat.Encode(CleanRecord());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 2);

        Assert.Throws<InvalidDataException>(() => ProbeFileFormat.Decode(bytes));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4081u)]
    public void DecodeRejectsEmptyOrOversizedJsonLength(uint length)
    {
        var bytes = ProbeFileFormat.Encode(CleanRecord());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), length);

        Assert.Throws<InvalidDataException>(() => ProbeFileFormat.Decode(bytes));
    }

    [Fact]
    public void DecodeRejectsNonzeroPadding()
    {
        var bytes = ProbeFileFormat.Encode(CleanRecord());
        bytes[^1] = 0x01;

        Assert.Throws<InvalidDataException>(() => ProbeFileFormat.Decode(bytes));
    }

    [Fact]
    public void DecodeRejectsInvalidUtf8()
    {
        var bytes = FileBytes(new byte[] { 0xFF });

        Assert.Throws<InvalidDataException>(() => ProbeFileFormat.Decode(bytes));
    }

    [Theory]
    [MemberData(nameof(NonCanonicalJson))]
    public void DecodeRejectsDuplicateUnknownReorderedOrNoncanonicalJson(string json)
    {
        Assert.Throws<InvalidDataException>(() => ProbeFileFormat.Decode(FileBytes(Encoding.UTF8.GetBytes(json))));
    }

    [Theory]
    [MemberData(nameof(WronglyTypedJson))]
    public void DecodeRejectsWrongJsonTypesAndOutOfRangeNumbers(string json)
    {
        Assert.Throws<InvalidDataException>(() => ProbeFileFormat.Decode(FileBytes(Encoding.UTF8.GetBytes(json))));
    }

    [Theory]
    [MemberData(nameof(InvalidRecords))]
    public void EncodeRejectsInvalidRecordValues(object value)
    {
        var record = (ProbeFileRecord)value;
        Assert.ThrowsAny<ArgumentException>(() => ProbeFileFormat.Encode(record));
    }

    public static IEnumerable<object[]> NonCanonicalJson()
    {
        yield return new object[] { CanonicalCleanJson.Replace("\"kind\":\"clean\"", "\"kind\":\"clean\",\"kind\":\"clean\"", StringComparison.Ordinal) };
        yield return new object[] { CanonicalCleanJson.Replace("\"generation\":0,\"seed\"", "\"generation\":0,\"ignored\":1,\"seed\"", StringComparison.Ordinal) };
        yield return new object[] { CanonicalCleanJson.Replace("{\"schemaVersion\":1,\"machineId\":\"" + MachineId + "\",", "{\"machineId\":\"" + MachineId + "\",\"schemaVersion\":1,", StringComparison.Ordinal) };
        yield return new object[] { CanonicalCleanJson.Replace(",", ", ", StringComparison.Ordinal) };
        yield return new object[] { CanonicalCleanJson.Replace("雪", "\\u96EA", StringComparison.Ordinal) };
        yield return new object[] { CanonicalCleanJson.Replace("schemaVersion", "SchemaVersion", StringComparison.Ordinal) };
        yield return new object[] { CanonicalCleanJson.Replace("\"policyRevision\":7", "\"policyRevision\":7.0", StringComparison.Ordinal) };
    }

    public static IEnumerable<object[]> WronglyTypedJson()
    {
        yield return new object[] { CanonicalCleanJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\"", StringComparison.Ordinal) };
        yield return new object[] { CanonicalCleanJson.Replace("\"machineId\":\"" + MachineId + "\"", "\"machineId\":null", StringComparison.Ordinal) };
        yield return new object[] { CanonicalCleanJson.Replace("\"policyRevision\":7", "\"policyRevision\":9223372036854775808", StringComparison.Ordinal) };
        yield return new object[] { CanonicalCleanJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":2147483648", StringComparison.Ordinal) };
        yield return new object[] { CanonicalCleanJson.Replace("\"generation\":0", "\"generation\":-1", StringComparison.Ordinal) };
    }

    public static IEnumerable<object[]> EquivalentButNonCanonicalJson()
    {
        yield return new object[] { EscapingGoldenJson.Replace("quote\\\"", "quote\\u0022", StringComparison.Ordinal) };
        yield return new object[] { EscapingGoldenJson.Replace("backslash\\\\", "backslash\\u005C", StringComparison.Ordinal) };
        yield return new object[] { EscapingGoldenJson.Replace("slash/", "slash\\/", StringComparison.Ordinal) };
        yield return new object[] { EscapingGoldenJson.Replace("<", "\\u003C", StringComparison.Ordinal) };
        yield return new object[] { EscapingGoldenJson.Replace("雪", "\\u96EA", StringComparison.Ordinal) };
        yield return new object[] { EscapingGoldenJson.Replace("🌱", "\\uD83C\\uDF31", StringComparison.Ordinal) };
    }

    public static IEnumerable<object[]> InvalidRecords()
    {
        var valid = CleanRecord();
        yield return new object[] { valid with { SchemaVersion = 2 } };
        yield return new object[] { valid with { MachineId = "not-a-machine-id" } };
        yield return new object[] { valid with { MachineId = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF" } };
        yield return new object[] { valid with { MachineKeyFingerprint = "abc" } };
        yield return new object[] { valid with { MachineKeyFingerprint = Fingerprint.ToLowerInvariant() } };
        yield return new object[] { valid with { PolicyRevision = 0 } };
        yield return new object[] { valid with { PolicyRevision = -1 } };
        yield return new object[] { valid with { BaselineId = "" } };
        yield return new object[] { valid with { BaselineId = new string('b', 129) } };
        yield return new object[] { valid with { BaselineId = "baseline\nname" } };
        yield return new object[] { valid with { RecoveryGeneration = " " } };
        yield return new object[] { valid with { RecoveryGeneration = new string('r', 129) } };
        yield return new object[] { valid with { Kind = "Clean" } };
        yield return new object[] { valid with { Generation = 1 } };
        yield return new object[] { valid with { Generation = -1 } };
        yield return new object[] { valid with { Kind = "dirty", Generation = 0 } };
        yield return new object[] { valid with { Seed = "not-base64url" } };
        yield return new object[] { valid with { Seed = new string('A', 42) } };
        yield return new object[] { valid with { Seed = new string('A', 42) + "!" } };
    }

    private static ProbeFileRecord CleanRecord() => new(
        SchemaVersion: 1,
        MachineId: MachineId,
        MachineKeyFingerprint: Fingerprint,
        PolicyRevision: 7,
        BaselineId: "baseline-雪-🌱",
        RecoveryGeneration: "recovery-é",
        Kind: "clean",
        Generation: 0,
        Seed: Seed);

    private static byte[] FileBytes(ReadOnlySpan<byte> json)
    {
        var bytes = new byte[4096];
        Encoding.ASCII.GetBytes("KRPROBE1").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), checked((uint)json.Length));
        json.CopyTo(bytes.AsSpan(16));
        return bytes;
    }
}
