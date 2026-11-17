using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using KamenRaider.AgentMaintenance;
using KamenRaider.Contracts;

namespace Controller.Tests;

public sealed class MaintenanceHttpProtocolTests
{
    private const string MachineId = "0123456789abcdef0123456789abcdef";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class IdentitySource(TrustedIdentityDocument document) : ITrustedIdentitySource
    {
        public TrustedIdentityDocument ReadExisting() => document;
    }

    private sealed class StaticResponseHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    [Fact]
    public async Task LifecycleClientRejectsAcceptedInsteadOfExactOk()
    {
        var dto = new AgentUpdateOperationDto("op-1", MachineId, "chain-1", "op-1", 1, true, null,
            "2.3.4", "2.3.4", new string('b', 64), "win-x64", 2, null, null, null, null, null,
            "installing", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.Accepted,
            JsonSerializer.Serialize(dto, JsonOptions)));
        var client = new HttpMaintenanceLifecycleClient(http);

        await Assert.ThrowsAsync<IOException>(() => client.StartAsync("https://controller.example/", MachineId,
            "op-1", new AgentUpdateTicketRequest(new string('T', 43), "2.3.4", "2.3.4", new string('b', 64), "start-1"),
            CancellationToken.None));
    }

    [Fact]
    public async Task SourceBindingClientRejectsAcceptedInsteadOfExactOk()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var identity = TrustedIdentity.ReadExisting(new IdentitySource(new(MachineId,
            Convert.ToBase64String(key.ExportPkcs8PrivateKey()))));
        var challenge = new AgentUpdateSourceChallengeResponse("challenge-1", "opaque-challenge", "runtime-1",
            DateTimeOffset.UtcNow.AddMinutes(5));
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.Accepted,
            JsonSerializer.Serialize(challenge, JsonOptions)));
        var client = new HttpMaintenanceSourceBindingClient(http);
        var request = new AgentUpdateSourceChallengeRequest(new string('T', 43), "2.3.4", "2.3.4",
            new string('b', 64), "challenge-request-1", new string('c', 64), "win-x64", 1);

        await Assert.ThrowsAsync<IOException>(() => client.IssueChallengeAsync("https://controller.example/", MachineId,
            "op-1", request, identity, CancellationToken.None));
    }
}
