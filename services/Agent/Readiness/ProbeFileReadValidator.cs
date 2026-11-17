using System.Security.Cryptography;

namespace KamenRaider.Agent.Probe;

internal enum ProbeFileAccessVerdict
{
    Validated,
    Rejected,
    Unavailable,
}

internal enum ProbeFileReadStatus
{
    MatchesExpectedBytes,
    Rejected,
    Unavailable,
}

internal sealed class ProbeFileContent
{
    private readonly byte[] sha256;

    private ProbeFileContent(int byteLength, byte[] sha256)
    {
        ByteLength = byteLength;
        this.sha256 = sha256;
    }

    internal int ByteLength { get; }
    internal ReadOnlySpan<byte> Sha256 => sha256;

    internal static ProbeFileContent FromBytes(ReadOnlySpan<byte> bytes)
        => new(bytes.Length, SHA256.HashData(bytes));
}

// Holds only a digest and handle-derived metadata; marker payloads never escape the store boundary.
internal sealed class ProbeFileRead
{
    private readonly byte[] sha256;

    private ProbeFileRead(int byteLength, byte[] sha256, string? volumeId, string? fileId, ProbeFileAccessVerdict accessVerdict)
    {
        ByteLength = byteLength;
        this.sha256 = sha256;
        VolumeId = volumeId;
        FileId = fileId;
        AccessVerdict = accessVerdict;
    }

    internal int ByteLength { get; }
    internal string? VolumeId { get; }
    internal string? FileId { get; }
    internal ProbeFileAccessVerdict AccessVerdict { get; }
    internal ReadOnlySpan<byte> Sha256 => sha256;

    internal static ProbeFileRead FromBytes(
        ReadOnlySpan<byte> bytes,
        string? volumeId,
        string? fileId,
        ProbeFileAccessVerdict accessVerdict)
        => new(bytes.Length, SHA256.HashData(bytes), volumeId, fileId, accessVerdict);

    internal static ProbeFileRead FromContent(
        ProbeFileContent content,
        string? volumeId,
        string? fileId,
        ProbeFileAccessVerdict accessVerdict)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new(content.ByteLength, content.Sha256.ToArray(), volumeId, fileId, accessVerdict);
    }
}

internal static class ProbeFileReadValidator
{
    private const int ExpectedFileSize = 4096;
    private const int Sha256Length = 32;

    internal static ProbeFileReadStatus Validate(
        ProbeFileRead? read,
        ReadOnlySpan<byte> expectedFullLengthSha256,
        string? expectedVolumeId)
    {
        if (read is null || expectedFullLengthSha256.Length != Sha256Length || string.IsNullOrWhiteSpace(expectedVolumeId))
            return ProbeFileReadStatus.Unavailable;

        if (read.AccessVerdict == ProbeFileAccessVerdict.Unavailable)
            return ProbeFileReadStatus.Unavailable;
        if (read.AccessVerdict != ProbeFileAccessVerdict.Validated)
            return ProbeFileReadStatus.Rejected;

        if (read.ByteLength != ExpectedFileSize ||
            string.IsNullOrWhiteSpace(read.VolumeId) ||
            string.IsNullOrWhiteSpace(read.FileId) ||
            !string.Equals(read.VolumeId, expectedVolumeId, StringComparison.Ordinal) ||
            read.Sha256.Length != Sha256Length)
            return ProbeFileReadStatus.Rejected;

        return CryptographicOperations.FixedTimeEquals(read.Sha256, expectedFullLengthSha256)
            ? ProbeFileReadStatus.MatchesExpectedBytes
            : ProbeFileReadStatus.Rejected;
    }
}
