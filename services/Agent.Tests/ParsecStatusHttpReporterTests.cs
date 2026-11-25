using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KamenRaider.Agent.Identity;
using KamenRaider.Agent.Parsec;
using KamenRaider.Contracts;
using KamenRaider.Controller.Security;

namespace Agent.Tests;

/// <summary>
/// The signed Parsec status reporter must treat the Controller's <c>204 No Content</c> as
/// acceptance. It used to require a JSON <c>{"accepted":true}</c> body, so every real status was
/// reported as rejected, the manager aborted the login and the attempt stayed in
/// <c>starting</c> until it timed out.
/// </summary>
public class ParsecStatusHttpReporterTests
{
    private static readonly Uri Controller = new("https://controller.test");

    private static ParsecStatusReport Report() =>
        new("attempt-1", "boot-key-1", ParsecStates.Starting, null, null);

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task NoContentIsAcceptedWithASingleRequest()
    {
        using var signer = new TestSigner();
        using var handler = new StatusHandler(signer, () => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var http = new HttpClient(handler);
        var reporter = new ParsecStatusHttpReporter(http, Controller, signer);

        Assert.True(await reporter.ReportAsync(Report(), CancellationToken.None));
        Assert.Equal(1, handler.Requests);
        Assert.True(handler.SignatureVerified);
    }

    [Fact]
    public async Task OkWithAcceptedTrueIsAccepted()
    {
        using var signer = new TestSigner();
        using var handler = new StatusHandler(signer, () => JsonResponse("{\"accepted\":true}"));
        using var http = new HttpClient(handler);
        var reporter = new ParsecStatusHttpReporter(http, Controller, signer);

        Assert.True(await reporter.ReportAsync(Report(), CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task OkWithoutAnAcceptedPropertyIsAccepted()
    {
        using var signer = new TestSigner();
        using var handler = new StatusHandler(signer, () => JsonResponse("{}"));
        using var http = new HttpClient(handler);
        var reporter = new ParsecStatusHttpReporter(http, Controller, signer);

        Assert.True(await reporter.ReportAsync(Report(), CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task OkWithoutABodyIsAccepted()
    {
        using var signer = new TestSigner();
        using var handler = new StatusHandler(signer, () => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var reporter = new ParsecStatusHttpReporter(http, Controller, signer);

        Assert.True(await reporter.ReportAsync(Report(), CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task OkWithANonJsonBodyIsAccepted()
    {
        using var signer = new TestSigner();
        using var handler = new StatusHandler(signer, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("accepted", Encoding.UTF8, "text/plain")
        });
        using var http = new HttpClient(handler);
        var reporter = new ParsecStatusHttpReporter(http, Controller, signer);

        Assert.True(await reporter.ReportAsync(Report(), CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task OkWithAcceptedFalseIsARejection()
    {
        using var signer = new TestSigner();
        using var handler = new StatusHandler(signer, () => JsonResponse("{\"accepted\":false}"));
        using var http = new HttpClient(handler);
        var reporter = new ParsecStatusHttpReporter(http, Controller, signer);

        Assert.False(await reporter.ReportAsync(Report(), CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task ClientRejectionIsFinalAndNotRetried(HttpStatusCode status)
    {
        using var signer = new TestSigner();
        using var handler = new StatusHandler(signer, () => new HttpResponseMessage(status));
        using var http = new HttpClient(handler);
        var reporter = new ParsecStatusHttpReporter(http, Controller, signer);

        Assert.False(await reporter.ReportAsync(Report(), CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task TransientStatusIsRetriedUpToThreeTimesThenRejected(HttpStatusCode status)
    {
        using var signer = new TestSigner();
        using var handler = new StatusHandler(signer, () => new HttpResponseMessage(status));
        using var http = new HttpClient(handler);
        var reporter = new ParsecStatusHttpReporter(http, Controller, signer);

        Assert.False(await reporter.ReportAsync(Report(), CancellationToken.None));
        Assert.Equal(ParsecStatusHttpReporter.MaximumAttempts, handler.Requests);
        // Every retry re-signs with a fresh nonce (asserted by the handler).
        Assert.True(handler.SignatureVerified);
    }

    /// <summary>In-memory signer that produces the same canonical signature bytes as DeviceIdentity.</summary>
    private sealed class TestSigner : IAgentRequestSigner, IDisposable
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public string MachineId { get; } = Guid.NewGuid().ToString("N");
        public string PublicKey => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        public string Sign(string method, string path, DateTimeOffset timestamp, string nonce, string body) =>
            Convert.ToBase64String(key.SignData(
                Encoding.UTF8.GetBytes(
                    $"{MachineId}\n{method}\n{path}\n{timestamp.ToUnixTimeSeconds()}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}"),
                HashAlgorithmName.SHA256));

        public void Dispose() => key.Dispose();
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly TestSigner signer;
        private readonly Func<HttpResponseMessage> respond;
        private string? previousNonce;

        public StatusHandler(TestSigner signer, Func<HttpResponseMessage> respond)
        {
            this.signer = signer;
            this.respond = respond;
        }

        public int Requests { get; private set; }
        public bool SignatureVerified { get; private set; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests += 1;
            Assert.Equal(ParsecStatusHttpReporter.Route, request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("attempt-1", json.RootElement.GetProperty("attemptId").GetString());
            Assert.Equal("starting", json.RootElement.GetProperty("status").GetString());

            var timestamp = request.Headers.GetValues("X-Agent-Timestamp").Single();
            var nonce = request.Headers.GetValues("X-Agent-Nonce").Single();
            Assert.NotEqual(previousNonce, nonce);
            previousNonce = nonce;
            SignatureVerified &= new AgentAuthentication().VerifySignature(
                signer.MachineId, signer.PublicKey, "POST", ParsecStatusHttpReporter.Route,
                timestamp, nonce, Encoding.UTF8.GetBytes(body),
                request.Headers.GetValues("X-Agent-Signature").Single()) is not null;
            return respond();
        }
    }
}
