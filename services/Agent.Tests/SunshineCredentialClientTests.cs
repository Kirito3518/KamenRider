using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KamenRaider.Agent.Identity;
using KamenRaider.Agent.Sunshine;
using KamenRaider.Contracts;
using KamenRaider.Controller.Security;

namespace Agent.Tests;

public class SunshineCredentialClientTests
{
    private const string Path = "/agent/sunshine-credential";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SignedFetchParsesDeliveryAndSendsKnownRevision()
    {
        using var signer = new TestSigner();
        var delivery = new SunshineCredentialDelivery("fleet-user", "fleet-secret", 9);
        using var handler = new RecordingHandler(signer, HttpStatusCode.OK, JsonSerializer.Serialize(delivery, JsonOptions));
        using var http = new HttpClient(handler);
        var client = new SunshineCredentialClient(http, signer);

        var outcome = await client.FetchAsync(new Uri("https://controller.test"), 3, CancellationToken.None);

        Assert.True(handler.SignatureVerified);
        Assert.Equal(Path, handler.RequestPath);
        Assert.Equal(3, Assert.IsType<JsonElement>(handler.RequestBody).GetProperty("knownRevision").GetInt64());
        var value = Assert.IsType<SunshineCredentialFetchOutcome.Delivery>(outcome).Value;
        Assert.Equal(delivery, value);
    }

    [Theory]
    [InlineData(204)]
    [InlineData(404)]
    [InlineData(401)]
    [InlineData(409)]
    public async Task NonDeliveringStatusesMapToOutcomesWithoutSecrets(int statusCode)
    {
        using var signer = new TestSigner();
        using var handler = new RecordingHandler(signer, (HttpStatusCode)statusCode, "");
        using var http = new HttpClient(handler);
        var client = new SunshineCredentialClient(http, signer);

        var outcome = await client.FetchAsync(new Uri("https://controller.test"), 4, CancellationToken.None);

        switch (statusCode)
        {
            case 204:
                Assert.IsType<SunshineCredentialFetchOutcome.AlreadyCurrent>(outcome);
                break;
            case 404:
                Assert.IsType<SunshineCredentialFetchOutcome.NotConfigured>(outcome);
                break;
            case 401:
                Assert.Equal(401, Assert.IsType<SunshineCredentialFetchOutcome.Rejected>(outcome).StatusCode);
                break;
            case 409:
                Assert.Equal(409, Assert.IsType<SunshineCredentialFetchOutcome.Rejected>(outcome).StatusCode);
                break;
        }

        Assert.True(handler.SignatureVerified);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(403)]
    public async Task UnexpectedStatusIsARequestFailure(int statusCode)
    {
        using var signer = new TestSigner();
        using var handler = new RecordingHandler(signer, (HttpStatusCode)statusCode, "");
        using var http = new HttpClient(handler);
        var client = new SunshineCredentialClient(http, signer);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.FetchAsync(new Uri("https://controller.test"), 1, CancellationToken.None));
    }

    [Fact]
    public async Task DeliveryMustAdvanceTheKnownRevision()
    {
        using var signer = new TestSigner();
        var sameRevision = new SunshineCredentialDelivery("fleet-user", "fleet-secret", 9);
        using var handler = new RecordingHandler(signer, HttpStatusCode.OK, JsonSerializer.Serialize(sameRevision, JsonOptions));
        using var http = new HttpClient(handler);
        var client = new SunshineCredentialClient(http, signer);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.FetchAsync(new Uri("https://controller.test"), 9, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not-json")]
    [InlineData("{\"userName\":\"fleet-user\",\"password\":\"\",\"revision\":2}")]
    [InlineData("{\"userName\":\"fleet:user\",\"password\":\"secret\",\"revision\":2}")]
    [InlineData("{\"userName\":\"\",\"password\":\"secret\",\"revision\":2}")]
    [InlineData("{\"userName\":\"fleet-user\",\"password\":\"secret\",\"revision\":0}")]
    public async Task InvalidDeliveryIsRejected(string body)
    {
        using var signer = new TestSigner();
        using var handler = new RecordingHandler(signer, HttpStatusCode.OK, body);
        using var http = new HttpClient(handler);
        var client = new SunshineCredentialClient(http, signer);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.FetchAsync(new Uri("https://controller.test"), 1, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedDeliveryIsRejectedBeforeParsing()
    {
        using var signer = new TestSigner();
        var body = "{\"userName\":\"" + new string('u', 32 * 1024) + "\",\"password\":\"secret\",\"revision\":2}";
        using var handler = new RecordingHandler(signer, HttpStatusCode.OK, body);
        using var http = new HttpClient(handler);
        var client = new SunshineCredentialClient(http, signer);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.FetchAsync(new Uri("https://controller.test"), 1, CancellationToken.None));
    }

    [Fact]
    public async Task NegativeKnownRevisionIsRejected()
    {
        using var signer = new TestSigner();
        using var handler = new RecordingHandler(signer, HttpStatusCode.NoContent, "");
        using var http = new HttpClient(handler);
        var client = new SunshineCredentialClient(http, signer);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.FetchAsync(new Uri("https://controller.test"), -1, CancellationToken.None));
    }

    /// <summary>In-memory signer that produces the same canonical signature bytes as DeviceIdentity.</summary>
    private sealed class TestSigner : IAgentRequestSigner, IDisposable
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public string MachineId { get; } = Guid.NewGuid().ToString("N");
        public string PublicKey => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        public string Sign(string method, string path, DateTimeOffset timestamp, string nonce, string body) =>
            Convert.ToBase64String(key.SignData(
                Encoding.UTF8.GetBytes($"{MachineId}\n{method}\n{path}\n{timestamp.ToUnixTimeSeconds()}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}"),
                HashAlgorithmName.SHA256));

        public void Dispose() => key.Dispose();
    }

    private sealed class RecordingHandler(TestSigner signer, HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public bool SignatureVerified { get; private set; }
        public string? RequestPath { get; private set; }
        public JsonElement? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestPath = request.RequestUri!.AbsolutePath;
            var requestBody = await request.Content!.ReadAsStringAsync(ct);
            var timestamp = request.Headers.GetValues("X-Agent-Timestamp").Single();
            var nonce = request.Headers.GetValues("X-Agent-Nonce").Single();
            SignatureVerified = new AgentAuthentication().VerifySignature(
                signer.MachineId, signer.PublicKey, "POST", Path, timestamp, nonce,
                Encoding.UTF8.GetBytes(requestBody), request.Headers.GetValues("X-Agent-Signature").Single()) is not null;
            using var json = JsonDocument.Parse(requestBody);
            RequestBody = json.RootElement.Clone();
            var response = new HttpResponseMessage(statusCode);
            if (statusCode == HttpStatusCode.OK)
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return response;
        }
    }
}
