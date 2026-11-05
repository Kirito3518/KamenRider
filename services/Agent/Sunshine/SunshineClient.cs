using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace KamenRaider.Agent.Sunshine;

// Adapter for the installed Foundation Sunshine contract. The returned ID is an
// application pairing slot; this fork does not expose a pending-client ID.
public sealed class SunshineClient : ISunshinePairingApi, IDisposable
{
    public const string PairingSlotId = "foundation-sunshine-single-pending-slot";
    private const string ClientNamePrefix = "KR-";
    private readonly HttpClient http;
    private readonly TimeSpan handshakeTimeout;

    public SunshineClient(Uri endpoint, NetworkCredential credential, HttpMessageHandler? transport = null, TimeSpan? handshakeTimeout = null)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "https" ||
            !(endpoint.Host == "localhost" || IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)) ||
            endpoint.AbsolutePath != "/" || endpoint.Query != "" || endpoint.Fragment != "" || endpoint.UserInfo != "")
            throw new ArgumentException("Sunshine requires a localhost HTTPS origin");
        if (string.IsNullOrWhiteSpace(credential.UserName) || string.IsNullOrEmpty(credential.Password) || credential.UserName.Contains(':'))
            throw new ArgumentException("Sunshine credentials required");

        this.handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(15);
        if (this.handshakeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));

        // Default chain/name/expiry validation is mandatory. Never bypass TLS.
        http = new HttpClient(transport ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = 65536
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.UserName + ":" + credential.Password)));
    }

    // An authenticated API response cannot prove desktop, capture, audio/input, or stream readiness.
    public Task<bool> CheckReadyAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public async Task<string[]> PendingIdsAsync(CancellationToken ct)
    {
        _ = await GetClientsAsync(ct);
        // Foundation Sunshine exposes one server-side PIN slot, not pending client IDs.
        return [PairingSlotId];
    }

    public async Task<bool> SubmitAsync(string id, string pin, CancellationToken ct)
    {
        if (id != PairingSlotId || pin.Length != 4 || pin.Any(c => c is < '0' or > '9')) return false;

        var clientName = ClientNamePrefix + Guid.NewGuid().ToString("N");
        if (!await MutationAsync(HttpMethod.Post, "/api/pin", new { pin, name = clientName }, ct)) return false;

        // POST /api/pin confirms only that Sunshine sent the server certificate.
        // The named certificate appears only after Moonlight finishes its handshake.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(handshakeTimeout);
        var timer = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                var clients = await GetClientsAsync(timeout.Token);
                if (clients.Any(client => client.Name == clientName)) return true;
                if (timer.Elapsed >= handshakeTimeout) break;
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("Sunshine accepted the PIN but the client handshake was not confirmed.");
        }

        throw new IOException("Sunshine accepted the PIN but the client handshake was not confirmed.");
    }

    private async Task<bool> MutationAsync(HttpMethod method, string path, object payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(payload) };
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.String || status.GetString() is not ("true" or "false"))
                throw new InvalidDataException("Unsupported Foundation Sunshine mutation response");
            return status.GetString() == "true";
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Unsupported Foundation Sunshine mutation response");
        }
    }

    private async Task<(string Name, string Uuid)[]> GetClientsAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("/api/clients/list", ct);
        response.EnsureSuccessStatusCode();
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() != "true" ||
                !root.TryGetProperty("named_certs", out var certs) || certs.ValueKind != JsonValueKind.Array || certs.GetArrayLength() > 1000)
                throw new InvalidDataException("Unsupported Foundation Sunshine client-list schema");

            var clients = new List<(string Name, string Uuid)>(certs.GetArrayLength());
            var uuids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cert in certs.EnumerateArray())
            {
                if (cert.ValueKind != JsonValueKind.Object ||
                    !cert.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) ||
                    !cert.TryGetProperty("uuid", out var uuid) || uuid.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(uuid.GetString()) ||
                    !uuids.Add(uuid.GetString()!))
                    throw new InvalidDataException("Unsupported Foundation Sunshine client-list schema");
                clients.Add((name.GetString()!, uuid.GetString()!));
            }
            return clients.ToArray();
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Unsupported Foundation Sunshine client-list schema");
        }
    }

    // Caller must first fence new ingress, active streams, and in-flight pairing handshakes.
    // This fork has no management API for cancelling a pending PIN request.
    public async Task ClearAsync(CancellationToken ct)
    {
        if (!await MutationAsync(HttpMethod.Post, "/api/clients/unpair-all", new { }, ct) ||
            !await MutationAsync(HttpMethod.Post, "/api/apps/close", new { }, ct))
            throw new IOException("Foundation Sunshine cleanup failed");

        if ((await GetClientsAsync(ct)).Length != 0)
            throw new IOException("Foundation Sunshine paired clients remain");
    }

    public void Dispose() => http.Dispose();
}
