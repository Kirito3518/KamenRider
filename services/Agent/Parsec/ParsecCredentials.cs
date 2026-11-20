using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Unicode;

namespace KamenRaider.Agent.Parsec;

/// <summary>
/// Strict reader for the decrypted <c>{"email","password"}</c> payload and builder for the one
/// line the desktop script reads from the SYSTEM agent's named pipe.
/// </summary>
/// <remarks>
/// Ported from 0jay-v2 <c>agent/parsec_manager.go</c> (<c>parsecCredentialSecret</c> and
/// <c>decodeJSONStringBytes</c>). The line is agent-parsec's shape:
/// <c>{"email":"&lt;base64&gt;","password":"&lt;base64&gt;"}</c> with standard padded base64 of
/// the UTF-8 values, validated with agent-parsec's <c>toSafeBase64</c> rule.
///
/// Everything here works on byte buffers so every intermediate copy can be cleared with
/// <see cref="CryptographicOperations.ZeroMemory"/>; no credential is ever turned into an
/// immutable <see cref="string"/>. Unknown fields, non-string values, empty values, oversized
/// values and malformed JSON are all refused, which the manager reports as INVALID_CREDENTIALS
/// without running the desktop script.
/// </remarks>
public static class ParsecCredentials
{
    /// <summary>Upper bound on the raw JSON string of the e-mail, as agent-parsec used.</summary>
    public const int MaximumEmailJsonBytes = 256;

    /// <summary>Upper bound on the raw JSON string of the password, as agent-parsec used.</summary>
    public const int MaximumPasswordJsonBytes = 6000;

    /// <summary>Upper bound on the raw JSON string of a 2FA code, as agent-parsec used.</summary>
    public const int MaximumVerificationCodeJsonBytes = 34;

    /// <summary>
    /// Turns the decrypted credential JSON into the pipe line. On success <paramref name="line"/>
    /// is owned by the caller and must be cleared after it has been handed to the script.
    /// </summary>
    public static bool TryBuildPipeLine(ReadOnlySpan<byte> plaintext, out byte[] line)
    {
        line = Array.Empty<byte>();
        if (!TryReadCredentials(plaintext, out var email, out var password))
            return false;
        byte[]? emailBase64 = null;
        byte[]? passwordBase64 = null;
        try
        {
            if (!TryToSafeBase64(email, out emailBase64) || !TryToSafeBase64(password, out passwordBase64))
                return false;
            line = BuildLine(emailBase64, passwordBase64);
            return true;
        }
        finally
        {
            ZeroMemory(email);
            ZeroMemory(password);
            if (emailBase64 is not null) ZeroMemory(emailBase64);
            if (passwordBase64 is not null) ZeroMemory(passwordBase64);
        }
    }

    /// <summary>
    /// Validates the shape of a decrypted 2FA payload without keeping the code: the Agent has no
    /// 2FA step, so the value is only used to decide whether the attempt answered
    /// TWO_FACTOR_UNSUPPORTED or was unreadable.
    /// </summary>
    public static bool TryValidateVerificationCode(ReadOnlySpan<byte> plaintext)
    {
        try
        {
            var reader = new Utf8JsonReader(plaintext);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;
            var sawCode = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return sawCode && !reader.Read();
                if (reader.TokenType != JsonTokenType.PropertyName ||
                    !string.Equals(reader.GetString(), "code", StringComparison.Ordinal))
                    return false;
                if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    return false;
                if (!IsNonEmptyRawJsonString(reader.ValueSpan, MaximumVerificationCodeJsonBytes))
                    return false;
                sawCode = true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>agent-parsec's toSafeBase64: base64 of the UTF-8 text, refused unless it is base64.</summary>
    public static bool TryToSafeBase64(ReadOnlySpan<byte> value, out byte[] encoded)
    {
        encoded = Array.Empty<byte>();
        var buffer = new byte[Base64.GetMaxEncodedToUtf8Length(value.Length)];
        try
        {
            if (Base64.EncodeToUtf8(value, buffer, out _, out var written) != OperationStatus.Done)
                return false;
            for (var index = 0; index < written; index++)
            {
                if (!IsBase64Character(buffer[index]) && buffer[index] != (byte)'=')
                    return false;
            }
            encoded = buffer.AsSpan(0, written).ToArray();
            return true;
        }
        finally
        {
            ZeroMemory(buffer);
        }
    }

    private static bool TryReadCredentials(ReadOnlySpan<byte> plaintext, out byte[] email, out byte[] password)
    {
        email = Array.Empty<byte>();
        password = Array.Empty<byte>();
        byte[]? emailRaw = null;
        byte[]? passwordRaw = null;
        byte[]? decodedEmail = null;
        byte[]? decodedPassword = null;
        try
        {
            var reader = new Utf8JsonReader(plaintext);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;

            var sawEnd = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    sawEnd = true;
                    break;
                }
                if (reader.TokenType != JsonTokenType.PropertyName)
                    return false;
                var name = reader.GetString();
                if (!reader.Read() || reader.TokenType != JsonTokenType.String || reader.HasValueSequence)
                    return false;
                // ValueSpan keeps the escaped bytes instead of an immutable string, so the
                // credential can be cleared after the pipe line has been built.
                var raw = reader.ValueSpan.ToArray();
                switch (name)
                {
                    case "email":
                        emailRaw = raw;
                        break;
                    case "password":
                        passwordRaw = raw;
                        break;
                    default:
                        ZeroMemory(raw);
                        return false;
                }
            }
            // Trailing content after the object is not the exact payload the browser sealed.
            if (!sawEnd || reader.Read())
                return false;

            if (!IsNonEmptyRawJsonString(emailRaw, MaximumEmailJsonBytes) ||
                !IsNonEmptyRawJsonString(passwordRaw, MaximumPasswordJsonBytes))
                return false;
            if (!TryDecodeJsonStringBytes(emailRaw!, out decodedEmail) ||
                !TryDecodeJsonStringBytes(passwordRaw!, out decodedPassword))
                return false;

            email = decodedEmail;
            password = decodedPassword;
            decodedEmail = null;
            decodedPassword = null;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            if (emailRaw is not null) ZeroMemory(emailRaw);
            if (passwordRaw is not null) ZeroMemory(passwordRaw);
            if (decodedEmail is not null) ZeroMemory(decodedEmail);
            if (decodedPassword is not null) ZeroMemory(decodedPassword);
        }
    }

    private static bool IsNonEmptyRawJsonString(ReadOnlySpan<byte> raw, int maximumJsonBytes) =>
        raw.Length is >= 1 && raw.Length <= maximumJsonBytes - 2 && ContainsNonWhitespace(raw);

    private static bool IsNonEmptyRawJsonString(byte[]? raw, int maximumJsonBytes) =>
        raw is not null && IsNonEmptyRawJsonString(raw.AsSpan(), maximumJsonBytes);

    private static bool ContainsNonWhitespace(ReadOnlySpan<byte> raw)
    {
        foreach (var value in raw)
        {
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Unescapes the raw JSON string bytes into UTF-8, with lone surrogates replaced by U+FFFD
    /// exactly as Go's encoding/json (and agent-parsec's JavaScript) did. The caller clears the
    /// returned buffer.
    /// </summary>
    internal static bool TryDecodeJsonStringBytes(ReadOnlySpan<byte> raw, out byte[] value)
    {
        value = Array.Empty<byte>();
        var output = new List<byte>(raw.Length);
        for (var index = 0; index < raw.Length; index++)
        {
            var character = raw[index];
            if (character < 0x20 || character == (byte)'"')
                return Fail(output);
            if (character != (byte)'\\')
            {
                output.Add(character);
                continue;
            }

            index++;
            if (index >= raw.Length)
                return Fail(output);
            switch ((char)raw[index])
            {
                case '"': output.Add((byte)'"'); break;
                case '\\': output.Add((byte)'\\'); break;
                case '/': output.Add((byte)'/'); break;
                case 'b': output.Add(0x08); break;
                case 'f': output.Add(0x0C); break;
                case 'n': output.Add(0x0A); break;
                case 'r': output.Add(0x0D); break;
                case 't': output.Add(0x09); break;
                case 'u':
                    if (index + 5 > raw.Length)
                        return Fail(output);
                    if (!TryParseHex(raw.Slice(index + 1, 4), out var first))
                        return Fail(output);
                    index += 4;
                    var scalar = (uint)first;
                    if (first is >= 0xD800 and <= 0xDFFF)
                    {
                        scalar = 0xFFFD;
                        if (first < 0xDC00 && index + 7 <= raw.Length &&
                            raw[index + 1] == (byte)'\\' && raw[index + 2] == (byte)'u' &&
                            TryParseHex(raw.Slice(index + 3, 4), out var second) &&
                            second is >= 0xDC00 and <= 0xDFFF)
                        {
                            scalar = (uint)(0x10000 + ((first - 0xD800) << 10) + (second - 0xDC00));
                            index += 6;
                        }
                    }
                    AppendUtf8(output, scalar);
                    break;
                default:
                    return Fail(output);
            }
        }

        var result = output.ToArray();
        if (!Utf8.IsValid(result))
        {
            ZeroMemory(result);
            return Fail(output);
        }
        value = result;
        return true;
    }

    private static bool TryParseHex(ReadOnlySpan<byte> raw, out ushort value)
    {
        value = 0;
        if (raw.Length < 4)
            return false;
        for (var index = 0; index < 4; index++)
        {
            var character = raw[index];
            var digit = character switch
            {
                >= (byte)'0' and <= (byte)'9' => character - '0',
                >= (byte)'a' and <= (byte)'f' => character - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => character - 'A' + 10,
                _ => -1
            };
            if (digit < 0)
                return false;
            value = (ushort)((value << 4) | digit);
        }
        return true;
    }

    private static void AppendUtf8(List<byte> output, uint scalar)
    {
        if (scalar < 0x80)
        {
            output.Add((byte)scalar);
        }
        else if (scalar < 0x800)
        {
            output.Add((byte)(0xC0 | (scalar >> 6)));
            output.Add((byte)(0x80 | (scalar & 0x3F)));
        }
        else if (scalar < 0x10000)
        {
            output.Add((byte)(0xE0 | (scalar >> 12)));
            output.Add((byte)(0x80 | ((scalar >> 6) & 0x3F)));
            output.Add((byte)(0x80 | (scalar & 0x3F)));
        }
        else
        {
            output.Add((byte)(0xF0 | (scalar >> 18)));
            output.Add((byte)(0x80 | ((scalar >> 12) & 0x3F)));
            output.Add((byte)(0x80 | ((scalar >> 6) & 0x3F)));
            output.Add((byte)(0x80 | (scalar & 0x3F)));
        }
    }

    private static bool Fail(List<byte> output)
    {
        for (var index = 0; index < output.Count; index++)
            output[index] = 0;
        output.Clear();
        return false;
    }

    private static bool IsBase64Character(byte character) =>
        character is >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'a' and <= (byte)'z'
            or >= (byte)'0' and <= (byte)'9'
            or (byte)'+'
            or (byte)'/';

    private static byte[] BuildLine(ReadOnlySpan<byte> emailBase64, ReadOnlySpan<byte> passwordBase64)
    {
        ReadOnlySpan<byte> prefix = "{\"email\":\""u8;
        ReadOnlySpan<byte> middle = "\",\"password\":\""u8;
        ReadOnlySpan<byte> suffix = "\"}"u8;
        var line = new byte[prefix.Length + emailBase64.Length + middle.Length + passwordBase64.Length + suffix.Length];
        var offset = 0;
        prefix.CopyTo(line.AsSpan(offset));
        offset += prefix.Length;
        emailBase64.CopyTo(line.AsSpan(offset));
        offset += emailBase64.Length;
        middle.CopyTo(line.AsSpan(offset));
        offset += middle.Length;
        passwordBase64.CopyTo(line.AsSpan(offset));
        offset += passwordBase64.Length;
        suffix.CopyTo(line.AsSpan(offset));
        return line;
    }

    private static void ZeroMemory(byte[] buffer) => CryptographicOperations.ZeroMemory(buffer);
}
