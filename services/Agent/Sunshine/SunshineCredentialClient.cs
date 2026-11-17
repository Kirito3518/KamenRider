using System.Net;
using System.Text;
using System.Text.Json;
using KamenRaider.Agent.Identity;
using KamenRaider.Contracts;

namespace KamenRaider.Agent.Sunshine;

/// <summary>Result of one signed fleet Sunshine credential fetch.</summary>
public abstract record SunshineCredentialFetchOutcome
{
    /// <summary>The Controller delivered the current fleet credential.</summary>
    public sealed record Delivery(SunshineCredentialDelivery Value) : SunshineCredentialFetchOutcome;

    /// <summary>The Controller reports the Agent is already at the current fleet revision (204).</summary>
    public sealed record AlreadyCurrent : SunshineCredentialFetchOutcome;

    /// <summary>No fleet-wide credential is configured (404); the local credential is left untouched.</summary>
    public sealed record NotConfigured : SunshineCredentialFetchOutcome;

    /// <summary>The Agent identity was rejected (401) or is not approved/quarantined (409).</summary>
    public sealed record Rejected(int StatusCode) : SunshineCredentialFetchOutcome;
}

/// <summary>
/// Signed client for the Controller fleet Sunshine credential endpoint. Signing matches
/// <see cref="KamenRaider.Agent.Sync.SyncClient"/>: the canonical Agent signature covers the method,
/// path, timestamp, nonce, and SHA-256 of the exact JSON body. TLS validation is never bypassed and
/// the response body is bounded before it is parsed. Credential values are never logged here.
/// </summary>
public sealed class SunshineCredentialClient(HttpClient http, IAgentRequestSigner identity)
{
    public const string Path = "/agent/sunshine-credential";
    private const int MaximumResponseBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SunshineCredentialFetchOutcome> FetchAsync(Uri controller, long knownRevision, CancellationToken ct)
    {
        if (knownRevision < 0) throw new ArgumentOutOfRangeException(nameof(knownRevision), "Known credential revision cannot be negative.");
        var body = JsonSerializer.Serialize(new SunshineCredentialFetchRequest(knownRevision), JsonOptions);
        var timestamp = DateTimeOffset.UtcNow;
        var nonce = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(controller, Path))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Agent-Id", identity.MachineId);
        request.Headers.Add("X-Agent-Timestamp", timestamp.ToUnixTimeSeconds().ToString());
        request.Headers.Add("X-Agent-Nonce", nonce);
        request.Headers.Add("X-Agent-Signature", identity.Sign("POST", Path, timestamp, nonce, body));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (http.Timeout != Timeout.InfiniteTimeSpan) timeout.CancelAfter(http.Timeout);
        var requestCt = timeout.Token;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCt);
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return new SunshineCredentialFetchOutcome.Delivery(await ReadDeliveryAsync(response, knownRevision, requestCt));
            case HttpStatusCode.NoContent:
                return new SunshineCredentialFetchOutcome.AlreadyCurrent();
            case HttpStatusCode.NotFound:
                return new SunshineCredentialFetchOutcome.NotConfigured();
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Conflict:
                return new SunshineCredentialFetchOutcome.Rejected((int)response.StatusCode);
            default:
                throw new HttpRequestException("Unexpected Sunshine credential response status", null, response.StatusCode);
        }
    }

    private static async Task<SunshineCredentialDelivery> ReadDeliveryAsync(HttpResponseMessage response, long knownRevision, CancellationToken ct)
    {
        if (response.Content is null)
            throw new InvalidDataException("Sunshine credential delivery has no content.");
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("Sunshine credential delivery is too large.");
        var buffer = new byte[MaximumResponseBytes + 1];
        var length = 0;
        using (var stream = await response.Content.ReadAsStreamAsync(ct))
        {
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
                if (read == 0) break;
                length += read;
            }
        }

        if (length is <= 0 or > MaximumResponseBytes)
            throw new InvalidDataException("Sunshine credential delivery has an invalid size.");

        SunshineCredentialDelivery delivery;
        try
        {
            delivery = JsonSerializer.Deserialize<SunshineCredentialDelivery>(buffer.AsSpan(0, length), JsonOptions)
                ?? throw new InvalidDataException("Sunshine credential delivery is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Sunshine credential delivery is invalid.", ex);
        }
        finally
        {
            Array.Clear(buffer);
        }

        ValidateCredential(delivery.UserName, delivery.Password);
        if (delivery.Revision <= knownRevision)
            throw new InvalidDataException("Sunshine credential delivery does not advance the known revision.");
        return delivery;
    }

    private static void ValidateCredential(string? userName, string? password)
    {
        if (string.IsNullOrWhiteSpace(userName) || userName.Length > 128 || userName.Contains(':') || userName.Any(char.IsControl))
            throw new InvalidDataException("Sunshine credential delivery has an invalid username.");
        if (string.IsNullOrEmpty(password) || password.Length > 2048 || password.Any(character => character == '\0'))
            throw new InvalidDataException("Sunshine credential delivery has an invalid password.");
    }
}
