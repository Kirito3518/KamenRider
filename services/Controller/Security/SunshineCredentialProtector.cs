using System.Security.Cryptography;
using System.Text;

namespace KamenRaider.Controller.Security;

/// <summary>
/// AES-256-GCM protector for the fleet-wide Sunshine management credential. The key is supplied
/// as base64 of exactly 32 bytes and is never persisted, logged or echoed. Encryption failures and
/// malformed stored records fail closed: callers never receive a partial or unauthenticated value.
/// </summary>
public sealed class SunshineCredentialProtector
{
    public const int KeyLength = 32;
    public const int NonceLength = 12;
    public const int TagLength = 16;
    private const int MaximumPlaintextLength = 256 * 4;

    private readonly byte[] key;

    public SunshineCredentialProtector(string base64Key)
    {
        if (!TryDecodeKey(base64Key, out var decoded))
            throw new ArgumentException("The Sunshine credential key must be base64 of exactly 32 bytes.", nameof(base64Key));
        key = decoded;
    }

    /// <summary>True when the configured value is base64 of exactly 32 bytes.</summary>
    public static bool IsValidKey(string? base64Key) => TryDecodeKey(base64Key, out _);

    public sealed record ProtectedCredential(byte[] Ciphertext, byte[] Nonce, byte[] Tag);

    public ProtectedCredential Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || plaintext.Length > MaximumPlaintextLength)
            throw new ArgumentException("Invalid Sunshine credential password.", nameof(plaintext));
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[TagLength];
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
            return new(ciphertext, nonce, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    public string Unprotect(byte[]? ciphertext, byte[]? nonce, byte[]? tag)
    {
        if (ciphertext is null || ciphertext.Length is 0 or > MaximumPlaintextLength
            || nonce is not { Length: NonceLength } || tag is not { Length: TagLength })
            throw new InvalidOperationException("Stored Sunshine credential is malformed.");
        var plaintextBytes = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);
            return Encoding.UTF8.GetString(plaintextBytes);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException("Stored Sunshine credential cannot be decrypted with the configured key.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    private static bool TryDecodeKey(string? base64Key, out byte[] decoded)
    {
        decoded = [];
        if (string.IsNullOrEmpty(base64Key)) return false;
        try
        {
            var bytes = Convert.FromBase64String(base64Key);
            if (bytes.Length != KeyLength) return false;
            decoded = bytes;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
