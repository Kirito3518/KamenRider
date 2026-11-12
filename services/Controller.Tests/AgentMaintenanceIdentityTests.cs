using System.Security.Cryptography;
using System.Text;
using KamenRaider.AgentMaintenance;
using KamenRaider.Contracts;

namespace Controller.Tests;

public sealed class AgentMaintenanceIdentityTests
{
    private sealed class MemoryIdentitySource(TrustedIdentityDocument? document) : ITrustedIdentitySource
    {
        public int Reads;
        public TrustedIdentityDocument ReadExisting()
        {
            Reads++;
            return document ?? throw new FileNotFoundException("Identity is missing.");
        }
    }

    [Fact]
    public void ReadExistingSignsCanonicalEnrollmentRequestWithoutAnyCreatePath()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var source = new MemoryIdentitySource(new(
            "0123456789abcdef0123456789abcdef",
            Convert.ToBase64String(key.ExportPkcs8PrivateKey())));
        using var identity = TrustedIdentity.ReadExisting(source);
        const string body = "{\"purpose\":\"source\"}";
        const string path = "/agent/machines/0123456789abcdef0123456789abcdef/agent-updates/op-1/source-challenge";
        var signature = identity.Sign("POST", path, 1_800_000_000, "nonce_1", body);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        var canonical = Encoding.UTF8.GetBytes($"0123456789abcdef0123456789abcdef\nPOST\n{path}\n1800000000\nnonce_1\n{digest}");
        var publicKey = ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo(), out _);

        Assert.True(publicKey.VerifyData(canonical, Convert.FromBase64String(signature), HashAlgorithmName.SHA256));
        Assert.Equal(1, source.Reads);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())), identity.KeyFingerprint);
    }

    [Fact]
    public void ReadExistingSignsDomainSeparatedSourceEvidence()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var source = new MemoryIdentitySource(new(
            "0123456789abcdef0123456789abcdef",
            Convert.ToBase64String(key.ExportPkcs8PrivateKey())));
        using var identity = TrustedIdentity.ReadExisting(source);
        var evidence = new AgentUpdateSourceEvidence(identity.MachineId, identity.KeyFingerprint, "operation-1",
            "chain-1", "operation-1", 1, "2.0.0", new string('A', 64), "challenge-1", "challenge-request-1",
            "binding-request-1", new string('B', 64), "win-x64", 1, "runtime-epoch-1", "1.0.0",
            "operation-1", new string('C', 64), new string('D', 64), new string('E', 64), "native-boot-1", "backupPublished");
        var signature = identity.SignSourceEvidence(evidence);
        using var publicKey = ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo(), out _);

        Assert.True(publicKey.VerifyData(AgentUpdateSourceEvidenceCanonicalizer.CanonicalBytes(evidence),
            Convert.FromBase64String(signature), HashAlgorithmName.SHA256));
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public void SourceEvidenceCanonicalizationRejectsUnpairedSurrogates()
    {
        var validReplacementCharacter = ValidSourceEvidence() with { RuntimeEpoch = "runtime-\uFFFD" };
        var validBytes = AgentUpdateSourceEvidenceCanonicalizer.CanonicalBytes(validReplacementCharacter);

        foreach (var unpairedSurrogate in new[] { '\uD800', '\uDC00' })
        {
            var malformed = ValidSourceEvidence() with { RuntimeEpoch = $"runtime-{unpairedSurrogate}" };
            Assert.ThrowsAny<ArgumentException>(() => AgentUpdateSourceEvidenceCanonicalizer.CanonicalBytes(malformed));
        }

        Assert.Contains("runtime-\uFFFD", Encoding.UTF8.GetString(validBytes));
        Assert.Equal(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(validBytes)), validBytes);

        var validUnicodeBytes = AgentUpdateSourceEvidenceCanonicalizer.CanonicalBytes(
            ValidSourceEvidence() with { RuntimeEpoch = "runtime-λ-🚀" });
        var validUnicodeText = Encoding.UTF8.GetString(validUnicodeBytes);
        Assert.Contains("runtime-λ-🚀", validUnicodeText);
        Assert.Equal(Encoding.UTF8.GetBytes(validUnicodeText), validUnicodeBytes);
    }

    [Fact]
    public void MissingExistingIdentityFailsWithoutGeneratingOne()
    {
        var source = new MemoryIdentitySource(null);

        Assert.Throws<FileNotFoundException>(() => TrustedIdentity.ReadExisting(source));

        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public void InvalidMachineIdFailsClosed()
    {
        var source = new MemoryIdentitySource(new("not-a-machine-id", "AQID"));

        Assert.Throws<InvalidDataException>(() => TrustedIdentity.ReadExisting(source));
    }

    [Fact]
    public void InvalidPkcs8KeyFailsClosed()
    {
        var source = new MemoryIdentitySource(new("0123456789abcdef0123456789abcdef", "AQID"));

        Assert.ThrowsAny<CryptographicException>(() => TrustedIdentity.ReadExisting(source));
    }

    private static AgentUpdateSourceEvidence ValidSourceEvidence() => new(
        "0123456789abcdef0123456789abcdef", new string('A', 64), "operation-1", "chain-1", "operation-1", 1,
        "2.0.0", new string('B', 64), "challenge-1", "challenge-request-1", "binding-request-1", new string('C', 64),
        "win-x64", 1, "runtime-epoch-1", "1.0.0", "operation-1", new string('D', 64), new string('E', 64),
        new string('F', 64), "native-boot-1", "backupPublished");
}
