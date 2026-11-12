using System.Text.Json;
using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Security;

namespace Controller.Tests;

internal static class VerifiedAgentRequestFixture
{
    internal static VerifiedAgentRequest Create(ControllerDatabase database, string machineId, string path, string body)
    {
        var publicKey = database.PublicKey(machineId) ?? throw new InvalidOperationException("The test machine is not enrolled.");
        return VerifiedAgentRequest.CreateForTest(machineId, publicKey, "POST", path,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Guid.NewGuid().ToString("N"), body);
    }

    internal static VerifiedAgentRequest Create(ControllerDatabase database, string machineId, string path, object body)
        => Create(database, machineId, path, JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    internal static VerifiedAgentRequest CreateWithNonce(ControllerDatabase database, string machineId, string path,
        object body, string nonce)
    {
        var publicKey = database.PublicKey(machineId) ?? throw new InvalidOperationException("The test machine is not enrolled.");
        var serializedBody = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return VerifiedAgentRequest.CreateForTest(machineId, publicKey, "POST", path,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), nonce, serializedBody);
    }

    internal static VerifiedAgentRequest CreateEnrollment(EnrollmentRequest request)
    {
        var body = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return VerifiedAgentRequest.CreateForTest(request.MachineId, request.PublicKey, "POST", "/agent/enroll",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Guid.NewGuid().ToString("N"), body);
    }
}

internal static class VerifiedAgentDatabaseTestExtensions
{
    internal static SyncDecision SyncLegacy(this ControllerDatabase database, string machineId, SyncRequest request)
        => database.Sync(VerifiedAgentRequestFixture.Create(database, machineId, "/agent/sync", request), request);

    internal static SyncResponse Sync(this ControllerDatabase database, string machineId, SyncRequest request)
    {
        // Business-logic tests speak the current protocol through an explicit verified envelope.
        var protocolRequest = request with { SyncProofProtocol = 2, AgentUpdateChallenge = null };
        var challengeDecision = database.Sync(VerifiedAgentRequestFixture.Create(database, machineId, "/agent/sync", protocolRequest), protocolRequest);
        if (challengeDecision.StatusCode != 428 || string.IsNullOrWhiteSpace(challengeDecision.Challenge))
            throw new InvalidOperationException("The direct database fixture expected a protocol-2 challenge.");

        var echoedRequest = protocolRequest with { AgentUpdateChallenge = challengeDecision.Challenge };
        var accepted = database.Sync(VerifiedAgentRequestFixture.Create(database, machineId, "/agent/sync", echoedRequest), echoedRequest);
        return accepted.Response ?? throw new InvalidOperationException($"The direct database fixture sync was rejected with HTTP {accepted.StatusCode}.");
    }

    internal static ReconcileResponse Reconcile(this ControllerDatabase database, string machineId, ReconcileRequest request)
        => database.Reconcile(VerifiedAgentRequestFixture.Create(database, machineId, "/agent/reconcile", request), request);
}
