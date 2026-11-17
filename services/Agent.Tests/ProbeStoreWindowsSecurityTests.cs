using System.Security.Principal;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using KamenRaider.Agent.Probe;

namespace KamenRaider.Agent.Tests;

[SupportedOSPlatform("windows")]
public sealed class ProbeStoreWindowsSecurityTests
{
    private const string FixtureRoot = @"D:\CodexBuild\kamenraiderplay\probe-store\fixtures";

    [Fact]
    public void NativeAccessCheckRejectsTenantWriteAccessToTheSelectedParent()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(FixtureRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);

            using var assessment = WindowsProbeAccessEvaluator.AssessDirectory(directory, identity.AccessToken);

            Assert.Equal(ProbeFileAccessVerdict.Rejected, assessment.Verdict);
            Assert.False(assessment.Handle.IsInvalid);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StoreValidationRejectsWritableTempParentWithoutCreatingManagedChildren()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(FixtureRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
            var tenantSid = identity.User?.Value ?? throw new InvalidOperationException("The test token has no user SID.");
            var volume = WindowsProbeVolume.GetIdentity(directory);
            var policy = new ProbeStorePolicy(
                volume.VolumeId,
                identity.User,
                [Path.Combine(directory, "protected-data")]);
            var store = new WindowsManagedProbeFile(policy);
            var failure = await Record.ExceptionAsync(async () =>
            {
                using var location = await store.ValidateAsync(
                    directory,
                    volume.VolumeId,
                    "0123456789abcdef0123456789abcdef",
                    new MachineSecurityContext(identity.AccessToken, tenantSid),
                    CancellationToken.None);
            });
            Assert.IsType<IOException>(failure);
            Assert.False(Directory.Exists(Path.Combine(directory, "KamenRaiderProbe")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExclusiveWriteAndVerifyFlushesAndRereadsTheExactMarkerBytes()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(FixtureRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "marker.bin");
        var original = new byte[4096];
        File.WriteAllBytes(path, original);
        var expected = CleanRecordBytes();
        try
        {
            ProbeFileContent content;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough))
                content = WindowsProbeFileIo.WriteAndVerify(stream, expected);

            Assert.Equal(expected, File.ReadAllBytes(path));
            Assert.Equal(4096, content.ByteLength);
            Assert.Equal(SHA256.HashData(expected), content.Sha256.ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void HeldNoFollowParentHandleAllowsItsOwnerToCreateOnlyInsideTheTempFixture()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(FixtureRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var child = Path.Combine(directory, "child");
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
            using var assessment = WindowsProbeAccessEvaluator.AssessDirectory(directory, identity.AccessToken, 0x00000002 | 0x00000004);
            Assert.False(assessment.Handle.IsInvalid);

            Directory.CreateDirectory(child);

            Assert.True(Directory.Exists(child));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] CleanRecordBytes()
    {
        return ProbeFileFormat.Encode(new ProbeFileRecord(
            SchemaVersion: 1,
            MachineId: "0123456789abcdef0123456789abcdef",
            MachineKeyFingerprint: "ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB",
            PolicyRevision: 7,
            BaselineId: "baseline-1",
            RecoveryGeneration: "recovery-1",
            Kind: "clean",
            Generation: 0,
            Seed: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
    }
}
