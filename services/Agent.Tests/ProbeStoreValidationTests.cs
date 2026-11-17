using System.Security.Cryptography;
using System.Text;
using KamenRaider.Agent.Probe;

namespace KamenRaider.Agent.Tests;

public sealed class ProbeStoreValidationTests
{
    private const string MachineId = "0123456789abcdef0123456789abcdef";
    private const string Fingerprint = "ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB";
    private const string Seed = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void ManagedPathUsesOnlyTheFixedVendorChildAndMachineId()
    {
        var parent = @"D:\Restored Data";

        var path = ProbeStorePath.BuildManagedFilePath(parent, MachineId);

        Assert.Equal(
            Path.Combine(parent, "KamenRaiderProbe", MachineId, "restore-test.bin"),
            path);
    }

    [Theory]
    [InlineData(@"relative\folder")]
    [InlineData(@"C:\parent\..\other")]
    [InlineData(@"C:\parent\.")]
    [InlineData(@"\\server\share\folder")]
    [InlineData(@"\\?\C:\folder")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"C:\folder\probe:stream")]
    [InlineData(@"C:\folder\*")]
    [InlineData(@"C:\")]
    public void ManagedPathRejectsNonlocalOrAmbiguousParent(string parent)
    {
        Assert.Throws<ArgumentException>(() => ProbeStorePath.BuildManagedFilePath(parent, MachineId));
    }

    [Theory]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("not-a-guid")]
    [InlineData("00000000000000000000000000000000")]
    public void ManagedPathRejectsNoncanonicalMachineId(string machineId)
    {
        Assert.Throws<ArgumentException>(() => ProbeStorePath.BuildManagedFilePath(@"C:\parent", machineId));
    }

    [Fact]
    public void ExactReadWithMatchingVolumeAndValidatedAccessMatchesExpectedDigest()
    {
        var expected = CleanFileBytes();
        var read = ProbeFileRead.FromBytes(expected, "volume-guid-1", "file-id-1", ProbeFileAccessVerdict.Validated);

        var result = ProbeFileReadValidator.Validate(read, SHA256.HashData(expected), "volume-guid-1");

        Assert.Equal(ProbeFileReadStatus.MatchesExpectedBytes, result);
    }

    [Fact]
    public void ChangedMarkerBytesDoNotMatchTheControllerDigest()
    {
        var expected = CleanFileBytes();
        var observed = expected.ToArray();
        observed[128] ^= 0x01;
        var read = ProbeFileRead.FromBytes(observed, "volume-guid-1", "file-id-1", ProbeFileAccessVerdict.Validated);

        var result = ProbeFileReadValidator.Validate(read, SHA256.HashData(expected), "volume-guid-1");

        Assert.Equal(ProbeFileReadStatus.Rejected, result);
    }

    [Fact]
    public void ReadFromDifferentVolumeIsRejectedEvenWhenBytesMatch()
    {
        var expected = CleanFileBytes();
        var read = ProbeFileRead.FromBytes(expected, "different-volume", "file-id-1", ProbeFileAccessVerdict.Validated);

        var result = ProbeFileReadValidator.Validate(read, SHA256.HashData(expected), "volume-guid-1");

        Assert.Equal(ProbeFileReadStatus.Rejected, result);
    }

    [Fact]
    public void UnavailableAccessValidationNeverMatchesExpectedBytes()
    {
        var expected = CleanFileBytes();
        var read = ProbeFileRead.FromBytes(expected, "volume-guid-1", "file-id-1", ProbeFileAccessVerdict.Unavailable);

        var result = ProbeFileReadValidator.Validate(read, SHA256.HashData(expected), "volume-guid-1");

        Assert.Equal(ProbeFileReadStatus.Unavailable, result);
    }

    [Fact]
    public void TruncatedReadIsRejectedEvenWhenItsDigestIsSupplied()
    {
        var truncated = CleanFileBytes()[..^1];
        var read = ProbeFileRead.FromBytes(truncated, "volume-guid-1", "file-id-1", ProbeFileAccessVerdict.Validated);

        var result = ProbeFileReadValidator.Validate(read, SHA256.HashData(truncated), "volume-guid-1");

        Assert.Equal(ProbeFileReadStatus.Rejected, result);
    }

    private static byte[] CleanFileBytes()
    {
        var record = new ProbeFileRecord(
            SchemaVersion: 1,
            MachineId: MachineId,
            MachineKeyFingerprint: Fingerprint,
            PolicyRevision: 7,
            BaselineId: "baseline-1",
            RecoveryGeneration: "recovery-1",
            Kind: "clean",
            Generation: 0,
            Seed: Seed);
        return ProbeFileFormat.Encode(record);
    }
}
