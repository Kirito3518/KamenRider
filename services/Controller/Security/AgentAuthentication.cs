using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KamenRaider.Controller.Security;

public sealed class AgentAuthentication
{
    public VerifiedAgentRequest? VerifySignature(
        string id,
        string publicKey,
        string method,
        string path,
        string signedSeconds,
        string nonce,
        ReadOnlySpan<byte> body,
        string signature)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(path)
            || !long.TryParse(signedSeconds, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds)
            || string.IsNullOrWhiteSpace(nonce) || nonce.Length > 128
            || nonce.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            return null;

        try
        {
            _ = DateTimeOffset.FromUnixTimeSeconds(seconds);
            var encodedKey = Convert.FromBase64String(publicKey);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(encodedKey, out var read);
            if (read != encodedKey.Length) return null;

            var bodyDigest = SHA256.HashData(body);
            var canonical = Encoding.UTF8.GetBytes($"{id}\n{method}\n{path}\n{seconds.ToString(CultureInfo.InvariantCulture)}\n{nonce}\n{Convert.ToHexString(bodyDigest)}");
            var signatureBytes = Convert.FromBase64String(signature);
            if (!key.VerifyData(canonical, signatureBytes, HashAlgorithmName.SHA256)) return null;

            return new VerifiedAgentRequest(
                id,
                Convert.ToHexString(SHA256.HashData(encodedKey)),
                method,
                path,
                seconds,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))),
                Convert.ToHexString(bodyDigest));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
