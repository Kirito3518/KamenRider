using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KamenRaider.Agent.Probe;

internal sealed record ProbeFileRecord(
    int SchemaVersion,
    string MachineId,
    string MachineKeyFingerprint,
    long PolicyRevision,
    string BaselineId,
    string RecoveryGeneration,
    string Kind,
    long Generation,
    string Seed);

internal static class ProbeFileFormat
{
    private const int RecordSize = 4096;
    private const int HeaderSize = 16;
    private const uint SchemaVersion = 1;
    private const int MaximumJsonLength = RecordSize - HeaderSize;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("KRPROBE1");
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 4,
    };
    private static readonly string[] PropertyNames =
    [
        "schemaVersion",
        "machineId",
        "machineKeyFingerprint",
        "policyRevision",
        "baselineId",
        "recoveryGeneration",
        "kind",
        "generation",
        "seed",
    ];

    internal static byte[] Encode(ProbeFileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateRecord(record);

        var json = SerializeCanonicalJson(record);
        if (json.Length > MaximumJsonLength)
            throw new ArgumentException("Probe record does not fit the fixed file format.", nameof(record));

        var result = new byte[RecordSize];
        Magic.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, sizeof(uint)), SchemaVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, sizeof(uint)), checked((uint)json.Length));
        json.CopyTo(result.AsSpan(HeaderSize));
        return result;
    }

    internal static ProbeFileRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != RecordSize)
            throw new InvalidDataException("Probe file must be exactly 4096 bytes.");
        if (!bytes[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("Probe file magic is invalid.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, sizeof(uint))) != SchemaVersion)
            throw new InvalidDataException("Probe file schema is unsupported.");

        var jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12, sizeof(uint)));
        if (jsonLength == 0 || jsonLength > MaximumJsonLength)
            throw new InvalidDataException("Probe file JSON length is invalid.");

        var payloadLength = checked((int)jsonLength);
        var padding = bytes[(HeaderSize + payloadLength)..];
        foreach (var value in padding)
        {
            if (value != 0)
                throw new InvalidDataException("Probe file padding must be zero.");
        }

        var json = bytes.Slice(HeaderSize, payloadLength);
        try
        {
            _ = StrictUtf8.GetString(json);
            using var document = JsonDocument.Parse(json.ToArray(), DocumentOptions);
            var record = ReadCanonicalShape(document.RootElement);
            ValidateRecord(record);

            var canonicalJson = SerializeCanonicalJson(record);
            if (!json.SequenceEqual(canonicalJson))
                throw new InvalidDataException("Probe file JSON is not canonical.");

            return record;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
                                          or DecoderFallbackException
                                          or ArgumentException
                                          or InvalidOperationException
                                          or FormatException
                                          or OverflowException)
        {
            throw new InvalidDataException("Probe file JSON is invalid.", exception);
        }
    }

    private static ProbeFileRecord ReadCanonicalShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Probe file JSON must be an object.");

        var properties = root.EnumerateObject().ToArray();
        if (properties.Length != PropertyNames.Length)
            throw new InvalidDataException("Probe file JSON has an unexpected property count.");

        for (var index = 0; index < PropertyNames.Length; index++)
        {
            if (!string.Equals(properties[index].Name, PropertyNames[index], StringComparison.Ordinal))
                throw new InvalidDataException("Probe file JSON properties are missing, duplicated, or out of order.");
        }

        return new ProbeFileRecord(
            ReadInt32(properties[0]),
            ReadString(properties[1]),
            ReadString(properties[2]),
            ReadInt64(properties[3]),
            ReadString(properties[4]),
            ReadString(properties[5]),
            ReadString(properties[6]),
            ReadInt64(properties[7]),
            ReadString(properties[8]));
    }

    private static int ReadInt32(JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var value))
            throw new InvalidDataException($"Probe file property '{property.Name}' must be an Int32.");
        return value;
    }

    private static long ReadInt64(JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out var value))
            throw new InvalidDataException($"Probe file property '{property.Name}' must be an Int64.");
        return value;
    }

    private static string ReadString(JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Probe file property '{property.Name}' must be a string.");
        return property.Value.GetString()!;
    }

    private static byte[] SerializeCanonicalJson(ProbeFileRecord record)
    {
        var json = new StringBuilder(512);
        json.Append("{\"schemaVersion\":");
        json.Append(record.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"machineId\":");
        AppendJsonString(json, record.MachineId);
        json.Append(",\"machineKeyFingerprint\":");
        AppendJsonString(json, record.MachineKeyFingerprint);
        json.Append(",\"policyRevision\":");
        json.Append(record.PolicyRevision.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"baselineId\":");
        AppendJsonString(json, record.BaselineId);
        json.Append(",\"recoveryGeneration\":");
        AppendJsonString(json, record.RecoveryGeneration);
        json.Append(",\"kind\":");
        AppendJsonString(json, record.Kind);
        json.Append(",\"generation\":");
        json.Append(record.Generation.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"seed\":");
        AppendJsonString(json, record.Seed);
        json.Append('}');
        return StrictUtf8.GetBytes(json.ToString());
    }

    private static void AppendJsonString(StringBuilder json, string value)
    {
        json.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    json.Append("\\\"");
                    break;
                case '\\':
                    json.Append("\\\\");
                    break;
                default:
                    if (character < ' ')
                    {
                        json.Append("\\u00");
                        json.Append("0123456789ABCDEF"[character >> 4]);
                        json.Append("0123456789ABCDEF"[character & 0x0F]);
                    }
                    else
                    {
                        json.Append(character);
                    }
                    break;
            }
        }
        json.Append('"');
    }

    private static void ValidateRecord(ProbeFileRecord record)
    {
        if (record.SchemaVersion != SchemaVersion)
            throw new ArgumentException("Probe record schema must be 1.", nameof(record));
        if (!IsCanonicalMachineId(record.MachineId))
            throw new ArgumentException("Probe record machine ID is invalid.", nameof(record));
        if (!IsCanonicalFingerprint(record.MachineKeyFingerprint))
            throw new ArgumentException("Probe record key fingerprint must be 64 uppercase hexadecimal characters.", nameof(record));
        if (record.PolicyRevision <= 0)
            throw new ArgumentException("Probe record policy revision must be positive.", nameof(record));
        if (!IsBoundedText(record.BaselineId) || !IsBoundedText(record.RecoveryGeneration))
            throw new ArgumentException("Probe record baseline and recovery identifiers must be non-empty text of at most 128 characters.", nameof(record));
        if (!IsCanonicalSeed(record.Seed))
            throw new ArgumentException("Probe record seed must be 32 bytes encoded as unpadded base64url.", nameof(record));

        if (record.Kind switch
            {
                "clean" => record.Generation != 0,
                "dirty" => record.Generation <= 0,
                _ => true,
            })
            throw new ArgumentException("Probe record kind and generation are inconsistent.", nameof(record));
    }

    private static bool IsCanonicalMachineId(string? value)
        => value is not null
           && Guid.TryParseExact(value, "N", out var parsed)
           && parsed != Guid.Empty
           && string.Equals(value, parsed.ToString("N"), StringComparison.Ordinal);

    private static bool IsCanonicalFingerprint(string? value)
        => value is { Length: 64 }
           && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool IsBoundedText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
            return false;

        try
        {
            _ = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsCanonicalSeed(string? value)
    {
        if (value is not { Length: 43 }
            || value.Any(character => !(character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-'
                or '_')))
            return false;

        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/') + "=";
            var bytes = Convert.FromBase64String(base64);
            var canonical = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return bytes.Length == 32 && string.Equals(value, canonical, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
