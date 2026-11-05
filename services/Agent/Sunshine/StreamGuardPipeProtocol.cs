using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using KamenRaider.Agent.Leases;

namespace KamenRaider.Agent.Sunshine;

public sealed record StreamGuardPipeRequest(int Version, string RequestId, string Command, StreamLeaseGrant? Grant);
public sealed record StreamGuardPipeResponse(int Version, string RequestId, bool Success, string? Error);

public static class StreamGuardPipeProtocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] EncodeRequest(StreamGuardPipeRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);

    public static StreamGuardPipeRequest DecodeRequest(ReadOnlySpan<byte> frame) =>
        JsonSerializer.Deserialize<StreamGuardPipeRequest>(frame, JsonOptions)
        ?? throw new InvalidDataException("The Stream Guard request is empty.");

    public static StreamGuardPipeResponse DecodeResponse(ReadOnlySpan<byte> frame) =>
        JsonSerializer.Deserialize<StreamGuardPipeResponse>(frame, JsonOptions)
        ?? throw new InvalidDataException("The Stream Guard acknowledgment is empty.");

    public static async Task WriteFrameAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length > MaxFrameBytes)
            throw new InvalidDataException("The Stream Guard frame exceeds the protocol size limit.");

        var frame = new byte[payload.Length + 1];
        payload.CopyTo(frame, 0);
        frame[^1] = (byte)'\n';
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var frame = new byte[MaxFrameBytes];
        var oneByte = new byte[1];
        var length = 0;

        while (true)
        {
            var read = await stream.ReadAsync(oneByte.AsMemory(), cancellationToken);
            if (read == 0)
            {
                if (length == 0) return null;
                throw new InvalidDataException("The Stream Guard connection ended inside a frame.");
            }

            var value = oneByte[0];
            if (value == (byte)'\n')
            {
                if (length > 0 && frame[length - 1] == (byte)'\r') length--;
                return frame.AsSpan(0, length).ToArray();
            }

            if (length == frame.Length)
                throw new InvalidDataException("The Stream Guard frame exceeds the protocol size limit.");

            frame[length++] = value;
        }
    }
}
