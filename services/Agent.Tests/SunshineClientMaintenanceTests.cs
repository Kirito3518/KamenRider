using System.Net;
using KamenRaider.Agent.Sunshine;

namespace Agent.Tests;

/// <summary>
/// SunshineClient is the only local Sunshine control surface. ClearAsync must prove the
/// trusted-client list is empty (that is the stream cut), and RestartProcessAsync must treat
/// the supervisor restarting the process as the successful path.
/// </summary>
public sealed class SunshineClientMaintenanceTests
{
    private static readonly Uri Endpoint = new("https://localhost:47990");

    [Fact]
    public async Task ClearUnpairsAllClientsAndThenProvesTheListIsEmpty()
    {
        var transport = new Transport(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/clients/unpair-all" => Json(HttpStatusCode.OK, "{\"status\":\"true\"}"),
            "/api/clients/list" => Json(HttpStatusCode.OK, "{\"status\":\"true\",\"named_certs\":[]}"),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });
        using var client = new SunshineClient(Endpoint, null, transport);

        await client.ClearAsync(default);

        Assert.Equal(new[] { "POST /api/clients/unpair-all", "GET /api/clients/list" }, transport.Requests);
    }

    [Fact]
    public async Task ClearFailsWhenSunshineDeclinesTheUnpair()
    {
        var transport = new Transport(request => request.RequestUri!.AbsolutePath == "/api/clients/unpair-all"
            ? Json(HttpStatusCode.OK, "{\"status\":\"false\"}")
            : Json(HttpStatusCode.OK, "{\"status\":\"true\",\"named_certs\":[]}"));
        using var client = new SunshineClient(Endpoint, null, transport);

        await Assert.ThrowsAsync<IOException>(() => client.ClearAsync(default));
        Assert.Equal(new[] { "POST /api/clients/unpair-all" }, transport.Requests);
    }

    [Fact]
    public async Task ClearFailsWhenATrustedClientRemains()
    {
        var transport = new Transport(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/clients/unpair-all" => Json(HttpStatusCode.OK, "{\"status\":\"true\"}"),
            _ => Json(HttpStatusCode.OK, "{\"status\":\"true\",\"named_certs\":[{\"name\":\"old\",\"uuid\":\"u\"}]}")
        });
        using var client = new SunshineClient(Endpoint, null, transport);

        await Assert.ThrowsAsync<IOException>(() => client.ClearAsync(default));
    }

    [Fact]
    public async Task ClearFailsClosedOnAnUnreadableClientList()
    {
        var transport = new Transport(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/clients/unpair-all" => Json(HttpStatusCode.OK, "{\"status\":\"true\"}"),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not-json") }
        });
        using var client = new SunshineClient(Endpoint, null, transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ClearAsync(default));
    }

    [Fact]
    public async Task RestartIsSuccessfulWhenSunshineLeavesBeforeAnswering()
    {
        var transport = new Transport(_ => throw new HttpRequestException("connection reset by peer"));
        using var client = new SunshineClient(Endpoint, null, transport);

        await client.RestartProcessAsync(default);

        Assert.Equal(new[] { "POST /api/restart" }, transport.Requests);
    }

    [Fact]
    public async Task RestartIsSuccessfulWhenTheRestartHandlerHangsPastTheClientTimeout()
    {
        var transport = new Transport(_ => throw new TaskCanceledException("handler timed out"));
        using var client = new SunshineClient(Endpoint, null, transport);

        await client.RestartProcessAsync(default);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RestartIsRejectedWhenSunshineAnswersWithAnError(HttpStatusCode status)
    {
        var transport = new Transport(_ => Json(status, "{}"));
        using var client = new SunshineClient(Endpoint, null, transport);

        await Assert.ThrowsAsync<IOException>(() => client.RestartProcessAsync(default));
    }

    [Fact]
    public async Task RestartIsRejectedWhenTheWebUiWasNeverSetUp()
    {
        var transport = new Transport(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://localhost:47990/welcome") }
        });
        using var client = new SunshineClient(Endpoint, null, transport);

        await Assert.ThrowsAsync<IOException>(() => client.RestartProcessAsync(default));
    }

    [Fact]
    public async Task RestartAcceptsASuccessfulAnswer()
    {
        var transport = new Transport(_ => Json(HttpStatusCode.OK, "{\"status\":\"true\"}"));
        using var client = new SunshineClient(Endpoint, null, transport);

        await client.RestartProcessAsync(default);

        Assert.Equal(new[] { "POST /api/restart" }, transport.Requests);
    }

    [Fact]
    public async Task PairedClientCountReadsTheTrustedClientList()
    {
        var transport = new Transport(_ => Json(HttpStatusCode.OK,
            "{\"status\":\"true\",\"named_certs\":[{\"name\":\"a\",\"uuid\":\"1\"},{\"name\":\"b\",\"uuid\":\"2\"}]}"));
        using var client = new SunshineClient(Endpoint, null, transport);

        Assert.Equal(2, await client.PairedClientCountAsync(default));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json) };

    private sealed class Transport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request));
        }
    }
}
