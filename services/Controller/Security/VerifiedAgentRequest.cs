using System.Security.Cryptography;
using System.Text;

namespace KamenRaider.Controller.Security;

public sealed class VerifiedAgentRequest
{
    internal VerifiedAgentRequest(string machineId, string keyFingerprint, string method, string path,
        long signedSeconds, string nonceDigest, string bodyDigest, bool isTestFixture = false)
    {
        MachineId = machineId;
        KeyFingerprint = keyFingerprint;
        Method = method;
        Path = path;
        SignedSeconds = signedSeconds;
        NonceDigest = nonceDigest;
        BodyDigest = bodyDigest;
        IsTestFixture = isTestFixture;
    }

    public string MachineId { get; }
    public string KeyFingerprint { get; }
    public string Method { get; }
    public string Path { get; }
    public long SignedSeconds { get; }
    public string NonceDigest { get; }
    public string BodyDigest { get; }

    internal bool IsTestFixture { get; }

    internal static VerifiedAgentRequest CreateForTest(string machineId, string publicKey, string method,
        string path, long signedSeconds, string nonce, string body)
    {
        var keyBytes = Convert.FromBase64String(publicKey);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        return new VerifiedAgentRequest(machineId,
            Convert.ToHexString(SHA256.HashData(keyBytes)), method, path, signedSeconds,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))),
            Convert.ToHexString(SHA256.HashData(bodyBytes)), isTestFixture: true);
    }
}
