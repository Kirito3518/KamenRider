using System.Runtime.Versioning;

namespace KamenRaider.Agent.Probe;

[SupportedOSPlatform("windows")]
internal static class WindowsProbeFileIo
{
    internal static ProbeFileContent WriteAndVerify(
        FileStream stream,
        ReadOnlySpan<byte> expected)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
            throw new ArgumentException("The managed probe stream must be an exclusive readable, writable, seekable handle.", nameof(stream));
        if (expected.Length != 4096)
            throw new InvalidDataException("Managed probe writes must contain exactly 4096 bytes.");
        _ = ProbeFileFormat.Decode(expected);

        stream.Position = 0;
        stream.SetLength(0);
        stream.Write(expected);
        stream.Flush(flushToDisk: true);

        stream.Position = 0;
        var reread = new byte[4096];
        stream.ReadExactly(reread);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(reread, expected))
            throw new IOException("The managed probe did not match the bytes written after a write-through flush.");
        _ = ProbeFileFormat.Decode(reread);

        return ProbeFileContent.FromBytes(reread);
    }

    internal static ProbeFileContent ReadAndVerify(
        FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("The managed probe stream must be a readable, seekable handle.", nameof(stream));
        if (stream.Length != 4096)
            throw new InvalidDataException("A managed probe must contain exactly 4096 bytes.");

        stream.Position = 0;
        var bytes = new byte[4096];
        stream.ReadExactly(bytes);
        _ = ProbeFileFormat.Decode(bytes);
        return ProbeFileContent.FromBytes(bytes);
    }
}
