using System.Net;
using System.Text.Json;
using KamenRaider.Contracts;
using KamenRaider.Agent.Sunshine;

namespace Agent.Tests;

public sealed class FoundationSunshineClientTests
{
    private const string SlotId = PairingIds.FoundationSunshineSinglePendingSlotId;

    [Fact]
    public async Task PendingPairingOfferUsesTheInstalledClientListRoute()
    {
        var transport = new FoundationTransport((request, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.OK, "{\"status\":\"true\",\"named_certs\":[]}")));
        using var api = new SunshineClient(new Uri("https://localhost:47990"), new NetworkCredential("u", "p"), transport);

        Assert.Equal(new[] { SlotId }, await api.PendingIdsAsync(default));
        Assert.Equal(new[] { "GET /api/clients/list" }, transport.Paths);
    }

    [Fact]
    public async Task PinSubmissionUsesFoundationBodyAndWaitsForCompletedHandshake()
    {
        string? submittedName = null;
        var transport = new FoundationTransport(async (request, index) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/pin")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                submittedName = body.RootElement.GetProperty("name").GetString();
                return JsonResponse(HttpStatusCode.OK, "{\"status\":\"true\"}");
            }

            return JsonResponse(HttpStatusCode.OK,
                "{\"status\":\"true\",\"named_certs\":[{\"name\":\"" + submittedName + "\",\"uuid\":\"client-uuid\"}]}");
        });
        using var api = new SunshineClient(new Uri("https://localhost:47990"), new NetworkCredential("u", "p"), transport);

        Assert.True(await api.SubmitAsync(SlotId, "1234", default));
        Assert.Equal(new[] { "POST /api/pin", "GET /api/clients/list" }, transport.Paths);
        Assert.StartsWith("KR-", submittedName);
        using var bodyJson = JsonDocument.Parse(transport.Bodies[0]!);
        Assert.Equal("1234", bodyJson.RootElement.GetProperty("pin").GetString());
        Assert.Equal(submittedName, bodyJson.RootElement.GetProperty("name").GetString());
        Assert.False(bodyJson.RootElement.TryGetProperty("pairing_id", out _));
    }

    [Theory]
    [InlineData("other-slot", "1234")]
    [InlineData(SlotId, "123")]
    [InlineData(SlotId, "１２３４")]
    public async Task InvalidPairingTargetOrPinNeverCallsSunshine(string id, string pin)
    {
        var transport = new FoundationTransport((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}")));
        using var api = new SunshineClient(new Uri("https://localhost:47990"), new NetworkCredential("u", "p"), transport);

        Assert.False(await api.SubmitAsync(id, pin, default));
        Assert.Empty(transport.Paths);
    }

    [Fact]
    public async Task SunshineDecliningPinDoesNotClaimPairingSucceeded()
    {
        var transport = new FoundationTransport((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"status\":\"false\"}")));
        using var api = new SunshineClient(new Uri("https://localhost:47990"), new NetworkCredential("u", "p"), transport);

        Assert.False(await api.SubmitAsync(SlotId, "1234", default));
        Assert.Equal(new[] { "POST /api/pin" }, transport.Paths);
    }

    [Fact]
    public async Task PinAcceptedWithoutACompletedClientTimesOutAsAnAmbiguousResult()
    {
        var transport = new FoundationTransport((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/api/pin" => JsonResponse(HttpStatusCode.OK, "{\"status\":\"true\"}"),
            _ => JsonResponse(HttpStatusCode.OK, "{\"status\":\"true\",\"named_certs\":[]}")
        }));
        using var api = new SunshineClient(new Uri("https://localhost:47990"), new NetworkCredential("u", "p"), transport, TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAsync<IOException>(() => api.SubmitAsync(SlotId, "1234", default));
        Assert.Contains("GET /api/clients/list", transport.Paths);
    }

    [Fact]
    public async Task CleanupUsesFoundationRoutesAndVerifiesNoSavedClients()
    {
        var transport = new FoundationTransport((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/api/clients/list" => JsonResponse(HttpStatusCode.OK, "{\"status\":\"true\",\"named_certs\":[]}"),
            _ => JsonResponse(HttpStatusCode.OK, "{\"status\":\"true\"}")
        }));
        using var api = new SunshineClient(new Uri("https://localhost:47990"), new NetworkCredential("u", "p"), transport);

        await api.ClearAsync(default);
        Assert.Equal(new[]
        {
            "POST /api/clients/unpair-all",
            "POST /api/apps/close",
            "GET /api/clients/list"
        }, transport.Paths);
    }

    [Theory]
    [InlineData("{\"status\":\"true\",\"named_certs\":[{\"name\":\"old-client\",\"uuid\":\"client-uuid\"}]}")]
    [InlineData("{\"status\":true,\"named_certs\":[]}")]
    [InlineData("{\"status\":\"false\",\"named_certs\":[]}")]
    [InlineData("not-json")]
    public async Task CleanupFailsWhenClientsRemainOrTheListCannotBeVerified(string clientList)
    {
        var transport = new FoundationTransport((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/api/clients/list"
                ? JsonResponse(HttpStatusCode.OK, clientList)
                : JsonResponse(HttpStatusCode.OK, "{\"status\":\"true\"}")));
        using var api = new SunshineClient(new Uri("https://localhost:47990"), new NetworkCredential("u", "p"), transport);

        var error = await Record.ExceptionAsync(() => api.ClearAsync(default));
        Assert.True(error is IOException or InvalidDataException);
    }

    [Theory]
    [InlineData("{\"status\":true,\"named_certs\":[]}")]
    [InlineData("{\"status\":\"false\",\"named_certs\":[]}")]
    [InlineData("{\"status\":\"true\"}")]
    [InlineData("not-json")]
    public async Task UnsupportedClientListSchemasFailClosed(string json)
    {
        var transport = new FoundationTransport((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, json)));
        using var api = new SunshineClient(new Uri("https://localhost:47990"), new NetworkCredential("u", "p"), transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => api.PendingIdsAsync(default));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json) };

    private sealed class FoundationTransport(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, Task<HttpResponseMessage>> responder = respond;
        public List<string> Paths { get; } = [];
        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            return await responder(request, Paths.Count);
        }
    }
}
