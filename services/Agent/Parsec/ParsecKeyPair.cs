using System.Security.Cryptography;
using System.Text;
using KamenRaider.Contracts;

namespace KamenRaider.Agent.Parsec;

/// <summary>
/// The process/boot-only RSA-2048 key that opens renter-supplied Parsec credentials.
/// </summary>
/// <remarks>
/// Ported from 0jay-v2 <c>agent/parsec_crypto.go</c>. The browser seals
/// <c>{"email","password"}</c> with the base64url SubjectPublicKeyInfo advertised in the
/// signed heartbeat: a one-time AES-256 key, wrapped with RSA-OAEP(SHA-256), encrypts the
/// payload with AES-256-GCM and appends the 16-byte tag (the WebCrypto layout).
///
/// The private half exists only in this object's <see cref="RSA"/> instance and is never
/// written to disk or logged; there is no credential file to protect. Callers own the
/// returned plaintext byte array and must clear it with
/// <see cref="CryptographicOperations.ZeroMemory"/>. Failures are reported as a single
/// <see cref="ParsecSecretException"/> that never repeats ciphertext, key or plaintext
/// material so it cannot leak through logs or status reports.
/// </remarks>
public sealed class ParsecKeyPair : IDisposable
{
    /// <summary>RSA modulus size; agent-parsec and the browser both use 2048 bits.</summary>
    public const int RsaKeySizeBits = 2048;

    private const int AesKeyBytes = 32;
    private const int GcmNonceBytes = 12;
    private const int GcmTagBytes = 16;
    private const int MaximumWrappedKeyChars = 4096;
    private const int MaximumNonceChars = 64;
    private const int MaximumCiphertextChars = 65536;
    private const int MaximumCiphertextBytes = MaximumCiphertextChars * 3 / 4 + 3;

    private readonly RSA rsa;
    private readonly object cryptoGate = new();
    private bool disposed;

    /// <summary>Creates the boot key pair. Generation happens once per Agent process.</summary>
    public ParsecKeyPair() : this(RSA.Create(RsaKeySizeBits)) { }

    internal ParsecKeyPair(RSA rsa)
    {
        ArgumentNullException.ThrowIfNull(rsa);
        this.rsa = rsa;
        var publicKeyInfo = rsa.ExportSubjectPublicKeyInfo();
        try
        {
            PublicKey = Base64Url.Encode(publicKeyInfo);
            // BootKeyId is base64url(SHA-256(SPKI)): the fingerprint the heartbeat advertises
            // and every status report echoes back, so a stale attempt from another boot is
            // never mistaken for this key's.
            BootKeyId = Base64Url.Encode(SHA256.HashData(publicKeyInfo));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKeyInfo);
        }
    }

    /// <summary>base64url (no padding) of the DER-encoded SubjectPublicKeyInfo.</summary>
    public string PublicKey { get; }

    /// <summary>base64url of the SHA-256 digest of the SPKI, advertised as the boot key id.</summary>
    public string BootKeyId { get; }

    /// <summary>
    /// Opens <see cref="ParsecSealedSecret.WrappedKey"/> with RSA-OAEP(SHA-256), then the
    /// payload with AES-256-GCM. The caller owns and must clear the returned plaintext.
    /// </summary>
    /// <exception cref="ParsecSecretException">The envelope is malformed or was sealed for another key.</exception>
    /// <exception cref="ObjectDisposedException">The key pair has been disposed.</exception>
    public byte[] Decrypt(ParsecSealedSecret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (disposed)
            throw new ObjectDisposedException(nameof(ParsecKeyPair));

        byte[]? wrappedKey = null;
        byte[]? nonce = null;
        byte[]? ciphertext = null;
        byte[]? aesKey = null;
        byte[]? plaintext = null;
        try
        {
            if (!Base64Url.TryDecode(secret.WrappedKey, MaximumWrappedKeyChars, out wrappedKey) ||
                wrappedKey.Length != RsaKeySizeBits / 8)
                throw new ParsecSecretException();
            if (!Base64Url.TryDecode(secret.Iv, MaximumNonceChars, out nonce) || nonce.Length != GcmNonceBytes)
                throw new ParsecSecretException();
            if (!Base64Url.TryDecode(secret.Ciphertext, MaximumCiphertextChars, out ciphertext) ||
                ciphertext.Length is < GcmTagBytes or > MaximumCiphertextBytes)
                throw new ParsecSecretException();

            try
            {
                // RSA implementations are not documented as thread-safe; the boot key is shared
                // by the capability heartbeat and by attempts, so serialize the private-key use.
                lock (cryptoGate)
                {
                    aesKey = rsa.Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256);
                }
            }
            catch (CryptographicException)
            {
                throw new ParsecSecretException();
            }
            if (aesKey.Length != AesKeyBytes)
                throw new ParsecSecretException();

            var payload = ciphertext.AsSpan(0, ciphertext.Length - GcmTagBytes);
            var tag = ciphertext.AsSpan(ciphertext.Length - GcmTagBytes);
            plaintext = new byte[payload.Length];
            try
            {
                using var gcm = new AesGcm(aesKey, GcmTagBytes);
                gcm.Decrypt(nonce, payload, tag, plaintext);
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                plaintext = null;
                throw new ParsecSecretException();
            }
            return plaintext;
        }
        finally
        {
            if (wrappedKey is not null) CryptographicOperations.ZeroMemory(wrappedKey);
            if (nonce is not null) CryptographicOperations.ZeroMemory(nonce);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
            if (aesKey is not null) CryptographicOperations.ZeroMemory(aesKey);
            // On failure the plaintext is cleared; on success the caller owns it.
            if (plaintext is not null && plaintext.Length == 0) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Seals a payload exactly as the browser does. Internal on purpose: the Agent only ever
    /// decrypts. Tests use it to exercise <see cref="Decrypt"/>, and local acceptance runs use
    /// it to prove the advertised public key opens a real envelope.
    /// </summary>
    internal ParsecSealedSecret Seal(ReadOnlySpan<byte> plaintext)
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(ParsecKeyPair));

        var aesKey = RandomNumberGenerator.GetBytes(AesKeyBytes);
        var nonce = RandomNumberGenerator.GetBytes(GcmNonceBytes);
        var ciphertext = new byte[plaintext.Length + GcmTagBytes];
        byte[]? wrappedKey = null;
        try
        {
            using (var gcm = new AesGcm(aesKey, GcmTagBytes))
                gcm.Encrypt(nonce, plaintext, ciphertext.AsSpan(0, plaintext.Length), ciphertext.AsSpan(plaintext.Length));
            lock (cryptoGate)
            {
                wrappedKey = rsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);
            }
            return new ParsecSealedSecret(Base64Url.Encode(wrappedKey), Base64Url.Encode(nonce), Base64Url.Encode(ciphertext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aesKey);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            if (wrappedKey is not null) CryptographicOperations.ZeroMemory(wrappedKey);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        rsa.Dispose();
    }
}

/// <summary>
/// Raised when a sealed Parsec secret cannot be opened. The message is deliberately generic:
/// neither the ciphertext (whose first bytes can identify the key or payload) nor any partial
/// plaintext may reach the log, the status report or the renter's screen.
/// </summary>
public sealed class ParsecSecretException : Exception
{
    public ParsecSecretException() : base("The sealed Parsec secret could not be opened.") { }
}

internal static class Base64Url
{
    internal static string Encode(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return string.Empty;
        var base64 = Convert.ToBase64String(value);
        var builder = new StringBuilder(base64.Length);
        foreach (var character in base64)
        {
            switch (character)
            {
                case '+': builder.Append('-'); break;
                case '/': builder.Append('_'); break;
                case '=': break;
                default: builder.Append(character); break;
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// Strict base64url decode: only the URL-safe alphabet and trailing padding are accepted,
    /// so a status envelope cannot smuggle whitespace or arbitrary text into the decoder.
    /// </summary>
    internal static bool TryDecode(string? value, int maximumChars, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(value) || value.Length > maximumChars)
            return false;

        var buffer = new char[value.Length];
        var paddingSeen = false;
        var paddingCount = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '-': buffer[index] = '+'; break;
                case '_': buffer[index] = '/'; break;
                case '=':
                    paddingSeen = true;
                    paddingCount++;
                    if (paddingCount > 2) return false;
                    buffer[index] = '=';
                    break;
                default:
                    if (paddingSeen || !IsBase64UrlCharacter(character))
                        return false;
                    buffer[index] = character;
                    break;
            }
        }

        var unpadded = value.TrimEnd('=');
        if (unpadded.Length == 0 || unpadded.Length % 4 == 1)
            return false;
        var padding = (4 - unpadded.Length % 4) % 4;
        if (paddingCount != 0 && paddingCount != padding)
            return false;

        try
        {
            var text = new string(buffer).TrimEnd('=');
            text += new string('=', padding);
            bytes = Convert.FromBase64String(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsBase64UrlCharacter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}
