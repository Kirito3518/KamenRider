using KamenRaider.Contracts;
using KamenRaider.Controller.Agents;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Machines;
using KamenRaider.Controller.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Controller.Tests;

public sealed class AgentRecoveryTests
{
    [Fact]
    public void ControllerDatabaseMigratesToVersionTwentyRecoverySchema()
    {
        using var fixture = new LeaseFixture();
        using var connection = Open(fixture.Path);
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";

        Assert.Equal(28L, Convert.ToInt64(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        ControllerDatabaseMaintenance.ValidateCanonicalSchema(connection, 28);
        ControllerDatabaseMaintenance.ValidateRecoverySchema(connection);
        ControllerDatabaseMaintenance.ValidateRecoverySuccessorSchema(connection);
        ControllerDatabaseMaintenance.ValidateUpdateConfirmationSchema(connection);
        ControllerDatabaseMaintenance.ValidatePcControlSchema(connection);
    }

    [Fact]
    public void RecoveryIntentDurablyAdvancesFenceAndExactRetryDoesNotAdvanceItAgain()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var oldTicket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "recovery-seed");
        var oldOperation = fixture.Db.StartAgentUpdate("pc", oldTicket.OperationId, oldTicket.Ticket,
            oldTicket.ReleaseVersion, oldTicket.TargetVersion, oldTicket.TargetPackageSha256, "recovery-seed-start");
        var initialGeneration = oldOperation.Generation;

        var first = CreateIntent(fixture.Db, oldOperation, initialGeneration, "recovery-request-1");
        Assert.Equal(oldOperation.ChainId, Get<string>(first, "ChainId"));
        Assert.Equal(oldOperation.CurrentHeadOperationId, Get<string>(first, "PreviousHeadOperationId"));
        Assert.Equal(initialGeneration + 1, Get<long>(first, "Generation"));
        Assert.True(Get<bool>(first, "Held"));
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Db.StartAgentUpdate("pc", oldTicket.OperationId,
            oldTicket.Ticket, oldTicket.ReleaseVersion, oldTicket.TargetVersion, oldTicket.TargetPackageSha256, "recovery-seed-start"));

        fixture.Reopen();
        var retried = CreateIntent(fixture.Db, oldOperation, initialGeneration, "recovery-request-1");
        Assert.Equal(Get<string>(first, "IntentId"), Get<string>(retried, "IntentId"));
        Assert.Equal(initialGeneration + 1, Get<long>(retried, "Generation"));
        var competing = Record.Exception(() => CreateIntent(fixture.Db, oldOperation, initialGeneration,
            "recovery-competing-request"));
        Assert.IsAssignableFrom<InvalidOperationException>(competing);
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_intents"));
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_operations WHERE status='superseded'"));
    }

    [Fact]
    public void RecoveryInspectionAuthenticatesEnrolledKeyAndPersistsBlockedEvidenceWithoutSupersession()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "recovery-proof-seed");
        var operation = fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "recovery-proof-start");
        var intent = CreateIntent(fixture.Db, operation, operation.Generation, "recovery-proof-intent");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(fixture.Db.PublicKey("pc")!)));
        const string requestId = "inspection-request-1";
        var path = $"/agent/machines/pc/agent-update-recovery/{Get<string>(intent, "IntentId")}/inspection";
        var inspection = CreateSignedInspection(intent, operation, fingerprint, requestId, fixture.AgentKey);
        var badSignature = inspection with { EvidenceSignature = Convert.ToBase64String(new byte[64]) };
        var challenge = CreateRecoveryChallenge(fixture, intent, inspection);
        var challengeToken = Get<string>(challenge, "Challenge");

        var wrongHeadInspection = SignRecoveryInspection(inspection with { HeadOperationId = "another-head" }, fixture.AgentKey);
        var wrongHead = Record.Exception(() => InvokeRequired(fixture.Db, "CommitRecoveryInspection",
            VerifiedAgentRequestFixture.CreateWithNonce(fixture.Db, "pc", path, wrongHeadInspection, challengeToken),
            Get<string>(intent, "IntentId"), requestId, wrongHeadInspection));
        Assert.IsAssignableFrom<UnauthorizedAccessException>(wrongHead);
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_inspections"));

        var wrongGenerationInspection = SignRecoveryInspection(inspection with { Generation = inspection.Generation + 1 }, fixture.AgentKey);
        var wrongGeneration = Record.Exception(() => InvokeRequired(fixture.Db, "CommitRecoveryInspection",
            VerifiedAgentRequestFixture.CreateWithNonce(fixture.Db, "pc", path, wrongGenerationInspection, challengeToken),
            Get<string>(intent, "IntentId"), requestId, wrongGenerationInspection));
        Assert.IsAssignableFrom<UnauthorizedAccessException>(wrongGeneration);
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_inspections"));

        var wrongChallenge = Record.Exception(() => InvokeRequired(fixture.Db, "CommitRecoveryInspection",
            VerifiedAgentRequestFixture.CreateWithNonce(fixture.Db, "pc", path, inspection, "not-the-issued-challenge"),
            Get<string>(intent, "IntentId"), requestId, inspection));
        Assert.IsAssignableFrom<UnauthorizedAccessException>(wrongChallenge);
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_inspections"));

        var invalid = Record.Exception(() => InvokeRequired(fixture.Db, "CommitRecoveryInspection",
            VerifiedAgentRequestFixture.CreateWithNonce(fixture.Db, "pc", path, badSignature, challengeToken),
            Get<string>(intent, "IntentId"), requestId, badSignature));

        Assert.IsAssignableFrom<UnauthorizedAccessException>(invalid);
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_inspections"));

        var blocked = Record.Exception(() => InvokeRequired(fixture.Db, "CommitRecoveryInspection",
            VerifiedAgentRequestFixture.CreateWithNonce(fixture.Db, "pc", path, inspection, challengeToken),
            Get<string>(intent, "IntentId"), requestId, inspection));
        Assert.NotNull(blocked);
        Assert.Equal("AgentRecoveryQualificationUnavailableException", blocked!.GetType().Name);
        Assert.Equal("originalBackupBindingUnavailable", Get<string>(blocked, "BlockReason"));
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_inspections"));
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_evidence_challenges WHERE purpose='recoveryInspection' AND consumed_at_unix_ms IS NOT NULL AND request_id='inspection-request-1'"));
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_operations WHERE status='superseded'"));
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);

        var differentRequest = CreateSignedInspection(intent, operation, fingerprint, "inspection-request-2", fixture.AgentKey);
        var replay = Record.Exception(() => InvokeRequired(fixture.Db, "CommitRecoveryInspection",
            VerifiedAgentRequestFixture.CreateWithNonce(fixture.Db, "pc", path, differentRequest, challengeToken),
            Get<string>(intent, "IntentId"), "inspection-request-2", differentRequest));
        Assert.IsAssignableFrom<UnauthorizedAccessException>(replay);
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_inspections"));

        fixture.Reopen();
        var retried = Record.Exception(() => InvokeRequired(fixture.Db, "CommitRecoveryInspection",
            VerifiedAgentRequestFixture.Create(fixture.Db, "pc", path, inspection),
            Get<string>(intent, "IntentId"), requestId, inspection));
        Assert.NotNull(retried);
        Assert.Equal("AgentRecoveryQualificationUnavailableException", retried!.GetType().Name);
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_inspections"));
    }

    [Fact]
    public void RecoveryInspectionWithoutAnIntentBoundChallengeIsRejectedBeforePersistence()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "recovery-no-challenge-seed");
        var operation = fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "recovery-no-challenge-start");
        var intent = CreateIntent(fixture.Db, operation, operation.Generation, "recovery-no-challenge-intent");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(fixture.Db.PublicKey("pc")!)));
        const string requestId = "inspection-without-challenge";
        var path = $"/agent/machines/pc/agent-update-recovery/{Get<string>(intent, "IntentId")}/inspection";
        var inspection = CreateSignedInspection(intent, operation, fingerprint, requestId, fixture.AgentKey);

        var rejected = Record.Exception(() => InvokeRequired(fixture.Db, "CommitRecoveryInspection",
            VerifiedAgentRequestFixture.Create(fixture.Db, "pc", path, inspection),
            Get<string>(intent, "IntentId"), requestId, inspection));

        Assert.IsAssignableFrom<UnauthorizedAccessException>(rejected);
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_inspections"));
    }

    [Fact]
    public void RecoveryChallengeIssuanceRejectsPrunedEnvelopeReplayAndClockRollbackButAllowsFreshRetry()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "recovery-freshness-seed");
        var operation = fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "recovery-freshness-start");
        var intent = CreateIntent(fixture.Db, operation, operation.Generation, "recovery-freshness-intent");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(fixture.Db.PublicKey("pc")!)));
        var inspection = CreateSignedInspection(intent, operation, fingerprint, "recovery-freshness-request", fixture.AgentKey);
        var request = new AgentUpdateRecoveryInspectionChallengeRequest(inspection);
        var intentId = Get<string>(intent, "IntentId");
        var path = $"/agent/machines/pc/agent-update-recovery/{intentId}/challenge";
        var publicKey = fixture.Db.PublicKey("pc")!;
        var body = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        VerifiedAgentRequest Envelope(DateTimeOffset signedAt, string nonce)
            => VerifiedAgentRequest.CreateForTest("pc", publicKey, "POST", path,
                signedAt.ToUnixTimeSeconds(), nonce, body);

        var issuedAt = fixture.Time.Now;
        var originalEnvelope = Envelope(issuedAt, "recovery-freshness-original");
        var originalChallenge = fixture.Db.IssueAgentUpdateRecoveryInspectionChallenge(
            originalEnvelope, intentId, request);

        fixture.Time.Now = issuedAt.AddSeconds(121);
        var freshRetryEnvelope = Envelope(fixture.Time.Now, "recovery-freshness-retry");
        var freshRetry = fixture.Db.IssueAgentUpdateRecoveryInspectionChallenge(
            freshRetryEnvelope, intentId, request);
        var freshRetryDigest = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(freshRetry.Challenge)));

        Assert.NotEqual(originalChallenge.Challenge, freshRetry.Challenge);
        Assert.Equal(0L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id='pc' AND path=$path AND nonce_digest=$nonce",
            ("$path", path), ("$nonce", originalEnvelope.NonceDigest)));

        var replay = Record.Exception(() => fixture.Db.IssueAgentUpdateRecoveryInspectionChallenge(
            originalEnvelope, intentId, request));
        Assert.IsAssignableFrom<UnauthorizedAccessException>(replay);
        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM agent_update_evidence_challenges WHERE purpose='recoveryInspection' AND challenge_request_id=$request AND nonce_digest=$digest",
            ("$request", inspection.RequestId), ("$digest", freshRetryDigest)));

        fixture.Reopen();
        fixture.Time.Now = issuedAt.AddSeconds(30);
        var clockRolledBackEnvelope = Envelope(fixture.Time.Now, "recovery-freshness-rollback");
        var rollback = Record.Exception(() => fixture.Db.IssueAgentUpdateRecoveryInspectionChallenge(
            clockRolledBackEnvelope, intentId, request));
        Assert.IsAssignableFrom<UnauthorizedAccessException>(rollback);
        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM agent_update_evidence_challenges WHERE purpose='recoveryInspection' AND challenge_request_id=$request AND nonce_digest=$digest",
            ("$request", inspection.RequestId), ("$digest", freshRetryDigest)));

        var futureEnvelope = Envelope(issuedAt.AddMinutes(5), "recovery-freshness-future");
        var future = Record.Exception(() => fixture.Db.IssueAgentUpdateRecoveryInspectionChallenge(
            futureEnvelope, intentId, request));
        Assert.IsAssignableFrom<UnauthorizedAccessException>(future);
    }

    [Fact]
    public async Task RecoveryEndpointsRequireEnrolledAgentSignatureAndReturnBlockedWithoutSuccessor()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "recovery-http-seed");
        var operation = fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "recovery-http-start");
        var intent = CreateIntent(fixture.Db, operation, operation.Generation, "recovery-http-intent");
        var keyFingerprint = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(fixture.Db.PublicKey("pc")!)));
        var inspection = CreateSignedInspection(intent, operation, keyFingerprint, "recovery-http-inspection", fixture.AgentKey);
        var challengeRequest = new AgentUpdateRecoveryInspectionChallengeRequest(inspection);
        var intentId = Get<string>(intent, "IntentId");
        var challengePath = $"/agent/machines/pc/agent-update-recovery/{intentId}/challenge";
        var inspectionPath = $"/agent/machines/pc/agent-update-recovery/{intentId}/inspection";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Db);
        builder.Services.AddSingleton<AgentAuthentication>();
        builder.Services.AddSingleton(new AgentReleaseStore(
            Path.Combine(Path.GetDirectoryName(fixture.Path)!, "agent-release-http")));
        await using var app = builder.Build();
        app.MapAgentReleases();
        await app.StartAsync();
        using var client = app.GetTestServer().CreateClient();

        using (var unsigned = new HttpRequestMessage(HttpMethod.Post, challengePath)
        {
            Content = new StringContent(JsonSerializer.Serialize(challengeRequest, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
        })
        using (var rejected = await client.SendAsync(unsigned))
        {
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_evidence_challenges WHERE purpose='recoveryInspection'"));

        using var signedChallengeRequest = CreateSignedAgentRequest(fixture, challengePath, challengeRequest, "http-challenge-request");
        using var challengeResponse = await client.SendAsync(signedChallengeRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, challengeResponse.StatusCode);
        Assert.Equal("no-store", challengeResponse.Headers.CacheControl?.ToString());
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<AgentUpdateRecoveryInspectionChallengeResponse>();
        Assert.NotNull(challenge);
        var challengeDigest = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(challenge!.Challenge)));
        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM agent_update_evidence_challenges WHERE purpose='recoveryInspection' AND nonce_digest=$digest",
            ("$digest", challengeDigest)));
        Assert.Equal(0L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM agent_update_evidence_challenges WHERE purpose='recoveryInspection' AND nonce_digest=$raw",
            ("$raw", challenge.Challenge)));

        var differentRequest = CreateSignedInspection(intent, operation, keyFingerprint, "recovery-http-other-inspection", fixture.AgentKey);
        using (var replayRequest = CreateSignedAgentRequest(fixture, inspectionPath, differentRequest, challenge!.Challenge))
        using (var replayResponse = await client.SendAsync(replayRequest))
        {
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, replayResponse.StatusCode);
        }
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_inspections"));

        using var signedInspectionRequest = CreateSignedAgentRequest(fixture, inspectionPath, inspection, challenge.Challenge);
        using var blockedResponse = await client.SendAsync(signedInspectionRequest);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, blockedResponse.StatusCode);
        using var blockedJson = JsonDocument.Parse(await blockedResponse.Content.ReadAsStringAsync());
        Assert.Equal("recoveryQualificationUnavailable", blockedJson.RootElement.GetProperty("code").GetString());
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_inspections"));
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_evidence_challenges WHERE purpose='recoveryInspection' AND consumed_at_unix_ms IS NOT NULL AND request_id='recovery-http-inspection'"));
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_operations WHERE status='superseded'"));
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public async Task AdminRecoveryIntentHttpSurfaceUsesTrustedActorAndReturnsSameIntentForRetryAndLookup()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "recovery-admin-seed");
        var operation = fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "recovery-admin-start");
        await using var app = BuildRecoveryAdminApi(fixture.Db);
        await app.StartAsync();
        using var client = app.GetTestServer().CreateClient();
        AddAdminActor(client, "trusted-admin", "admin");

        var request = new
        {
            expectedChainId = operation.ChainId,
            expectedHeadId = operation.CurrentHeadOperationId,
            expectedGeneration = operation.Generation,
            targetReleaseVersion = "2.0.0",
            reason = "recover the held update",
            requestId = "admin-recovery-request-1",
        };

        using var createdResponse = await client.PostAsJsonAsync(
            "/internal/machines/pc/agent-update-recovery-intents", request);
        Assert.Equal(System.Net.HttpStatusCode.OK, createdResponse.StatusCode);
        using var createdJson = JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync());
        var created = createdJson.RootElement;
        Assert.Equal("trusted-admin", created.GetProperty("actorId").GetString());
        Assert.Equal("admin-recovery-request-1", created.GetProperty("requestId").GetString());
        Assert.Equal(operation.ChainId, created.GetProperty("chainId").GetString());
        Assert.Equal(operation.CurrentHeadOperationId, created.GetProperty("previousHeadOperationId").GetString());
        Assert.Equal(operation.Generation, created.GetProperty("expectedGeneration").GetInt64());
        Assert.Equal(operation.Generation + 1, created.GetProperty("generation").GetInt64());
        Assert.Equal("2.0.0", created.GetProperty("targetReleaseVersion").GetString());
        Assert.Equal("recover the held update", created.GetProperty("reason").GetString());
        Assert.True(created.GetProperty("held").GetBoolean());
        var intentId = created.GetProperty("intentId").GetString();
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_operations"));

        using var retryResponse = await client.PostAsJsonAsync(
            "/internal/machines/pc/agent-update-recovery-intents", request);
        Assert.Equal(System.Net.HttpStatusCode.OK, retryResponse.StatusCode);
        using var retryJson = JsonDocument.Parse(await retryResponse.Content.ReadAsStringAsync());
        Assert.Equal(intentId, retryJson.RootElement.GetProperty("intentId").GetString());

        using var lookupResponse = await client.GetAsync(
            "/internal/machines/pc/agent-update-recovery-intents?requestId=admin-recovery-request-1");
        Assert.Equal(System.Net.HttpStatusCode.OK, lookupResponse.StatusCode);
        using var lookupJson = JsonDocument.Parse(await lookupResponse.Content.ReadAsStringAsync());
        Assert.Equal(intentId, lookupJson.RootElement.GetProperty("intentId").GetString());
        Assert.Equal("trusted-admin", lookupJson.RootElement.GetProperty("actorId").GetString());

        client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Id");
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "another-admin");
        using var otherActorLookup = await client.GetAsync(
            "/internal/machines/pc/agent-update-recovery-intents?requestId=admin-recovery-request-1");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, otherActorLookup.StatusCode);
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_intents"));
    }

    [Fact]
    public async Task AdminRecoveryContextReadsHeldHeadInOneControllerTransactionAndIsAdminOnly()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "recovery-context-seed");
        var operation = fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "recovery-context-start");
        await using var app = BuildRecoveryAdminApi(fixture.Db);
        await app.StartAsync();
        using var client = app.GetTestServer().CreateClient();

        using (var unauthenticated = await client.GetAsync("/internal/machines/pc/agent-update-recovery-context"))
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, unauthenticated.StatusCode);

        AddAdminActor(client, "trusted-admin", "admin");
        using var response = await client.GetAsync("/internal/machines/pc/agent-update-recovery-context");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var raw = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(raw);
        Assert.Equal("pc", json.RootElement.GetProperty("machineId").GetString());
        Assert.Equal(operation.ChainId, json.RootElement.GetProperty("chainId").GetString());
        Assert.Equal(operation.CurrentHeadOperationId, json.RootElement.GetProperty("currentHeadOperationId").GetString());
        Assert.Equal(operation.Generation, json.RootElement.GetProperty("currentGeneration").GetInt64());
        Assert.Equal("installing", json.RootElement.GetProperty("currentStatus").GetString());
        Assert.Equal("2.0.0", json.RootElement.GetProperty("currentTargetVersion").GetString());
        Assert.True(json.RootElement.GetProperty("held").GetBoolean());
        Assert.False(json.RootElement.GetProperty("allocationHeld").GetBoolean());
        Assert.DoesNotContain("targetPackageSha256", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backupDigest", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ticket", raw, StringComparison.OrdinalIgnoreCase);

        _ = CreateIntent(fixture.Db, operation, operation.Generation, "recovery-context-generation-advance");
        using var refreshed = await client.GetAsync("/internal/machines/pc/agent-update-recovery-context");
        Assert.Equal(System.Net.HttpStatusCode.OK, refreshed.StatusCode);
        using var refreshedJson = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());
        Assert.Equal(operation.CurrentHeadOperationId,
            refreshedJson.RootElement.GetProperty("currentHeadOperationId").GetString());
        Assert.Equal(operation.Generation + 1,
            refreshedJson.RootElement.GetProperty("currentGeneration").GetInt64());
        Assert.True(refreshedJson.RootElement.GetProperty("held").GetBoolean());
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public async Task AdminRecoveryContextFailsClosedWhenDisabledOrQuarantinedAndOmitsUnheldChains()
    {
        using var disabledFixture = new LeaseFixture();
        await using (var app = BuildRecoveryAdminApi(disabledFixture.Db))
        {
            await app.StartAsync();
            using var client = app.GetTestServer().CreateClient();
            AddAdminActor(client, "trusted-admin", "admin");
            using var response = await client.GetAsync("/internal/machines/pc/agent-update-recovery-context");
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        await using var quarantinedApp = BuildRecoveryAdminApi(fixture.Db);
        await quarantinedApp.StartAsync();
        using var quarantinedClient = quarantinedApp.GetTestServer().CreateClient();
        AddAdminActor(quarantinedClient, "trusted-admin", "admin");
        using (var unheld = await quarantinedClient.GetAsync("/internal/machines/pc/agent-update-recovery-context"))
            Assert.Equal(System.Net.HttpStatusCode.NotFound, unheld.StatusCode);

        fixture.Db.ActivateRestoreQuarantine("test-recovery-context-quarantine", 1);
        using var quarantined = await quarantinedClient.GetAsync("/internal/machines/pc/agent-update-recovery-context");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, quarantined.StatusCode);
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public async Task AdminRecoveryIntentHttpSurfaceReturnsActualHeldChainForStaleAndDifferentChainRequests()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "recovery-admin-stale-seed");
        var operation = fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "recovery-admin-stale-start");
        await using var app = BuildRecoveryAdminApi(fixture.Db);
        await app.StartAsync();
        using var client = app.GetTestServer().CreateClient();
        AddAdminActor(client, "trusted-admin", "admin");

        var differentChain = new
        {
            expectedChainId = "stale-chain-id",
            expectedHeadId = operation.CurrentHeadOperationId,
            expectedGeneration = operation.Generation,
            targetReleaseVersion = "2.0.0",
            reason = "recover the held update",
            requestId = "admin-recovery-stale-chain",
        };
        using (var response = await client.PostAsJsonAsync(
            "/internal/machines/pc/agent-update-recovery-intents", differentChain))
        {
            Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(operation.ChainId, json.RootElement.GetProperty("currentChainId").GetString());
            Assert.Equal(operation.CurrentHeadOperationId, json.RootElement.GetProperty("currentHeadOperationId").GetString());
            Assert.Equal(operation.Generation, json.RootElement.GetProperty("currentGeneration").GetInt64());
        }

        var created = new
        {
            expectedChainId = operation.ChainId,
            expectedHeadId = operation.CurrentHeadOperationId,
            expectedGeneration = operation.Generation,
            targetReleaseVersion = "2.0.0",
            reason = "recover the held update",
            requestId = "admin-recovery-fence-advance",
        };
        using (var response = await client.PostAsJsonAsync(
            "/internal/machines/pc/agent-update-recovery-intents", created))
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var staleGeneration = new
        {
            expectedChainId = operation.ChainId,
            expectedHeadId = operation.CurrentHeadOperationId,
            expectedGeneration = operation.Generation,
            targetReleaseVersion = "2.0.0",
            reason = "another stale recovery attempt",
            requestId = "admin-recovery-stale-generation",
        };
        using (var response = await client.PostAsJsonAsync(
            "/internal/machines/pc/agent-update-recovery-intents", staleGeneration))
        {
            Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(operation.ChainId, json.RootElement.GetProperty("currentChainId").GetString());
            Assert.Equal(operation.CurrentHeadOperationId, json.RootElement.GetProperty("currentHeadOperationId").GetString());
            Assert.Equal(operation.Generation + 1, json.RootElement.GetProperty("currentGeneration").GetInt64());
        }

        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_intents"));
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_operations"));
    }

    [Theory]
    [InlineData(null, "admin")]
    [InlineData("untrusted-user", "user")]
    public async Task AdminRecoveryIntentHttpSurfaceRejectsMissingOrNonAdminForwardedActor(string? actorId, string role)
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        await using var app = BuildRecoveryAdminApi(fixture.Db);
        await app.StartAsync();
        using var client = app.GetTestServer().CreateClient();
        if (actorId is not null) client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", actorId);
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role", role);

        using var response = await client.PostAsJsonAsync("/internal/machines/pc/agent-update-recovery-intents", new
        {
            expectedChainId = "chain",
            expectedHeadId = "head",
            expectedGeneration = 1,
            targetReleaseVersion = "2.0.0",
            reason = "test",
            requestId = "untrusted-recovery-request",
        });

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        using var lookupResponse = await client.GetAsync(
            "/internal/machines/pc/agent-update-recovery-intents?requestId=untrusted-recovery-request");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, lookupResponse.StatusCode);
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_intents"));
    }

    [Fact]
    public async Task AdminRecoveryIntentHttpSurfaceRejectsActorProvidedInRequestBody()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        await using var app = BuildRecoveryAdminApi(fixture.Db);
        await app.StartAsync();
        using var client = app.GetTestServer().CreateClient();
        AddAdminActor(client, "trusted-admin", "admin");

        using var response = await client.PostAsJsonAsync("/internal/machines/pc/agent-update-recovery-intents", new
        {
            expectedChainId = "chain",
            expectedHeadId = "head",
            expectedGeneration = 1,
            targetReleaseVersion = "2.0.0",
            reason = "test",
            requestId = "body-actor-recovery-request",
            actorId = "forged-body-actor",
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_intents"));
    }

    [Fact]
    public async Task AdminRecoveryIntentHttpSurfaceDoesNotCreateIntentDuringGlobalRestoreQuarantine()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "recovery-admin-quarantine-seed");
        var operation = fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "recovery-admin-quarantine-start");
        fixture.Db.ActivateRestoreQuarantine("task7-http-test", 1);
        await using var app = BuildRecoveryAdminApi(fixture.Db);
        await app.StartAsync();
        using var client = app.GetTestServer().CreateClient();
        AddAdminActor(client, "trusted-admin", "admin");

        using var response = await client.PostAsJsonAsync("/internal/machines/pc/agent-update-recovery-intents", new
        {
            expectedChainId = operation.ChainId,
            expectedHeadId = operation.CurrentHeadOperationId,
            expectedGeneration = operation.Generation,
            targetReleaseVersion = "2.0.0",
            reason = "recovery during restore quarantine",
            requestId = "admin-recovery-quarantine",
        });

        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_intents"));
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_operations"));
    }

    [Fact]
    public async Task AdminRecoveryIntentLookupIsUnavailableDuringGlobalRestoreQuarantine()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "recovery-admin-lookup-quarantine-seed");
        var operation = fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "recovery-admin-lookup-quarantine-start");
        fixture.Db.CreateRecoveryIntent("pc", operation.ChainId, operation.CurrentHeadOperationId,
            operation.Generation, "2.0.0", "created before restore quarantine", "recovery-admin-lookup-quarantine",
            "trusted-admin");
        fixture.Db.ActivateRestoreQuarantine("task7-http-lookup-test", 1);

        await using var app = BuildRecoveryAdminApi(fixture.Db);
        await app.StartAsync();
        using var client = app.GetTestServer().CreateClient();
        AddAdminActor(client, "trusted-admin", "admin");

        using var response = await client.GetAsync(
            "/internal/machines/pc/agent-update-recovery-intents?requestId=recovery-admin-lookup-quarantine");

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_intents"));
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public void RecoveryInspectionCanonicalPayloadBindsBackupSourceAndInstalledInventory()
    {
        var inspection = ValidInspection();
        var original = Convert.ToHexString(AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(inspection));

        var changedBackup = Convert.ToHexString(AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { OldBackupDigest = new string('F', 64) }));
        var changedInstalled = Convert.ToHexString(AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { ActualInstalledDigest = new string('E', 64) }));
        var changedSourceEvidence = Convert.ToHexString(AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { BoundSourceEvidenceDigest = new string('9', 64) }));

        Assert.NotEqual(original, changedBackup);
        Assert.NotEqual(original, changedInstalled);
        Assert.NotEqual(original, changedSourceEvidence);
        Assert.StartsWith("KamenRaider.AgentRecoveryInspection.v1\n", Encoding.UTF8.GetString(
            AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(inspection)));
    }

    [Fact]
    public void RecoveryInspectionCanonicalizationRejectsUnpairedSurrogates()
    {
        var replacement = ValidInspection() with { IntentId = "intent-\uFFFD" };
        var replacementBytes = AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(replacement);

        foreach (var unpairedSurrogate in new[] { '\uD800', '\uDC00' })
        {
            var malformed = ValidInspection() with { IntentId = $"intent-{unpairedSurrogate}" };
            Assert.ThrowsAny<ArgumentException>(() => AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(malformed));
        }

        Assert.Contains("intent-\uFFFD", Encoding.UTF8.GetString(replacementBytes));
        Assert.Equal(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(replacementBytes)), replacementBytes);

        var validUnicodeBytes = AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            ValidInspection() with { IntentId = "intent-ฮป-๐€" });
        var validUnicodeText = Encoding.UTF8.GetString(validUnicodeBytes);
        Assert.Contains("intent-ฮป-๐€", validUnicodeText);
        Assert.Equal(Encoding.UTF8.GetBytes(validUnicodeText), validUnicodeBytes);
    }

    [Fact]
    public void RecoveryInspectionMustMatchEnrolledKeyIntentHeadSourceTargetAndOriginalBackup()
    {
        var inspection = ValidInspection();
        var expected = ValidExpectation();

        Assert.True(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(inspection, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection with { KeyFingerprint = new string('F', 64) }, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection with { HeadOperationId = "stale-head" }, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection with { TargetPackageSha256 = new string('F', 64) }, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection with { BoundSourceVersion = "another-source" }, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection with { BoundSourceEvidenceDigest = new string('9', 64) }, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection with { OldBackupOwnerOperationId = "another-owner" }, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection with { OldBackupDigest = new string('F', 64) }, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection, expected with { ExpectedPriorNativeBootId = new string('B', 32) }));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection, expected with { ExpectedPriorNativeBootId = "boot-before" }));
    }

    [Fact]
    public void RecoveryInspectionRejectsUnknownOrUnqualifiedOldUpdaterAutoResumeEvidence()
    {
        var inspection = ValidInspection();

        Assert.Throws<ArgumentException>(() => AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { ExclusionProvenance = "unknown" }));
        Assert.Throws<ArgumentException>(() => AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { NativeBootIdAfter = inspection.NativeBootIdBefore }));
        Assert.Throws<ArgumentException>(() => AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { NativeBootIdBefore = "boot-before" }));
        Assert.Throws<ArgumentException>(() => AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { NativeBootIdBefore = "00000000000000000000000000000000" }));
        Assert.Throws<ArgumentException>(() => AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { OldUpdaterAutoResumeStatus = "unknown" }));
        Assert.Throws<ArgumentException>(() => AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { OldUpdaterAutoResumeStatus = "unqualified" }));
        Assert.Throws<ArgumentException>(() => AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { ActualInstalledDigest = "missing" }));

        var expected = ValidExpectation();
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection, expected with { HelperBuildQualified = false }));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection, expected with { ExclusionProcedureQualified = false }));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection, expected with { QualifiedOldUpdaterAutoResumeStatus = "unknown" }));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection, expected with { QualifiedOldUpdaterAutoResumeStatus = "unqualified" }));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection, expected with { SourceEvidenceQualified = false }));
    }

    [Fact]
    public void LegacyRecoveryMayUseOnlyTheExplicitlyQualifiedOriginalBackupAuthority()
    {
        var inspection = ValidInspection() with
        {
            SourceAuthority = "verifiedOriginalBackup",
            BoundSourceVersion = null,
            BoundSourceEvidenceDigest = null,
        };
        var expected = ValidExpectation() with
        {
            SourceAuthority = "verifiedOriginalBackup",
            BoundSourceVersion = null,
            BoundSourceEvidenceDigest = null,
        };

        Assert.True(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(inspection, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection with { SourceAuthority = "unverifiedLegacyHint" }, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection, expected with { SourceAuthority = "unverifiedLegacyHint" }));
    }

    private static void SetRecoveryBackupBinding(string databasePath, string operationId, string digest)
    {
        using var connection = Open(databasePath);
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE agent_update_operations SET backup_owner_operation_id=$owner,backup_digest=$digest WHERE operation_id=$operation";
        update.Parameters.AddWithValue("$owner", operationId);
        update.Parameters.AddWithValue("$digest", digest);
        update.Parameters.AddWithValue("$operation", operationId);
        Assert.Equal(1, update.ExecuteNonQuery());
    }

    [Fact]
    public void QualifiedRecoveryInspectionCreatesSuccessorTicketAndSupersedesTheHeldChain()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true,
            pinnedHelperSha256: new string('E', 64), exclusionProcedureQualified: true,
            qualifiedAutoResumeStatus: "qualifiedNotApplicable");
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "qualified-recovery-seed");
        var operation = fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "qualified-recovery-start");
        SetRecoveryBackupBinding(fixture.Path, operation.OperationId, new string('C', 64));
        var intent = CreateIntent(fixture.Db, operation, operation.Generation, "qualified-recovery-intent");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(fixture.Db.PublicKey("pc")!)));
        const string requestId = "qualified-inspection-1";
        var path = $"/agent/machines/pc/agent-update-recovery/{Get<string>(intent, "IntentId")}/inspection";
        var inspection = CreateSignedInspection(intent, operation, fingerprint, requestId, fixture.AgentKey);
        var challenge = CreateRecoveryChallenge(fixture, intent, inspection);
        var generationBefore = Scalar(fixture.Path,
            "SELECT generation FROM agent_maintenance_chains WHERE chain_id=$chain", ("$chain", operation.ChainId));

        var successor = Assert.IsType<AgentUpdateTicketResponse>(InvokeRequired(fixture.Db, "CommitRecoveryInspection",
            VerifiedAgentRequestFixture.CreateWithNonce(fixture.Db, "pc", path, inspection, Get<string>(challenge, "Challenge")),
            Get<string>(intent, "IntentId"), requestId, inspection));

        Assert.NotEqual(operation.OperationId, successor.OperationId);
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(successor.Ticket, @"\A[A-Za-z0-9_-]{43}\z"),
            $"successor ticket has length {successor.Ticket.Length}");
        Assert.Equal(operation.MachineId, successor.MachineId);
        Assert.Equal(operation.TargetVersion, successor.TargetVersion);
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_successors"));
        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM agent_update_operations WHERE operation_id=$successor AND parent_operation_id=$old",
            ("$successor", successor.OperationId), ("$old", operation.OperationId)));
        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM agent_maintenance_chains WHERE chain_id=$chain AND generation=$expectedGeneration AND held=1 AND current_head_operation_id=$successor",
            ("$chain", operation.ChainId), ("$expectedGeneration", generationBefore + 1), ("$successor", successor.OperationId)));
        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM agent_update_operations WHERE operation_id=$old AND status='superseded'",
            ("$old", operation.OperationId)));
        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM agent_update_recovery_intents WHERE intent_id=$intent AND status='superseded'",
            ("$intent", Get<string>(intent, "IntentId"))));
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_inspections"));
        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM agent_update_audit WHERE event_type='recoveryInspectionQualified'"));
        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM agent_update_evidence_challenges WHERE purpose='recoveryInspection' AND consumed_at_unix_ms IS NOT NULL"));
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);

        var started = fixture.Db.StartAgentUpdate("pc", successor.OperationId, successor.Ticket,
            successor.ReleaseVersion, successor.TargetVersion, successor.TargetPackageSha256,
            "qualified-recovery-successor-start");
        Assert.Equal("installing", started.Status);
        Assert.NotNull(Record.Exception(() => fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "old-ticket-after-successor")));
        Assert.NotNull(Record.Exception(() => InvokeRequired(fixture.Db, "CommitRecoveryInspection",
            VerifiedAgentRequestFixture.Create(fixture.Db, "pc", path, inspection),
            Get<string>(intent, "IntentId"), requestId, inspection)));
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_successors"));
    }

    [Fact]
    public void QualifiedRecoveryInspectionBlocksWhenTheHelperBuildIsNotThePinnedBuild()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true,
            pinnedHelperSha256: new string('E', 64), exclusionProcedureQualified: true,
            qualifiedAutoResumeStatus: "qualifiedNotApplicable");
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "pin-mismatch-seed");
        var operation = fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "pin-mismatch-start");
        SetRecoveryBackupBinding(fixture.Path, operation.OperationId, new string('C', 64));
        var intent = CreateIntent(fixture.Db, operation, operation.Generation, "pin-mismatch-intent");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(fixture.Db.PublicKey("pc")!)));
        const string requestId = "pin-mismatch-inspection";
        var path = $"/agent/machines/pc/agent-update-recovery/{Get<string>(intent, "IntentId")}/inspection";
        var inspection = CreateSignedInspection(intent, operation, fingerprint, requestId, fixture.AgentKey);
        var mismatched = SignRecoveryInspection(inspection with { HelperBuildSha256 = new string('9', 64) }, fixture.AgentKey);
        var challenge = CreateRecoveryChallenge(fixture, intent, mismatched);

        var blocked = Record.Exception(() => InvokeRequired(fixture.Db, "CommitRecoveryInspection",
            VerifiedAgentRequestFixture.CreateWithNonce(fixture.Db, "pc", path, mismatched, Get<string>(challenge, "Challenge")),
            Get<string>(intent, "IntentId"), requestId, mismatched));

        Assert.NotNull(blocked);
        Assert.Equal("AgentRecoveryQualificationUnavailableException", blocked!.GetType().Name);
        Assert.Equal("helperBuildNotPinned", Get<string>(blocked, "BlockReason"));
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_successors"));
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_inspections"));
    }

    [Fact]
    public void QualifiedRecoveryInspectionBlocksWhenTheExclusionProcedureIsNotQualified()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true,
            pinnedHelperSha256: new string('E', 64), exclusionProcedureQualified: false,
            qualifiedAutoResumeStatus: "qualifiedNotApplicable");
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "exclusion-missing-seed");
        var operation = fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "exclusion-missing-start");
        SetRecoveryBackupBinding(fixture.Path, operation.OperationId, new string('C', 64));
        var intent = CreateIntent(fixture.Db, operation, operation.Generation, "exclusion-missing-intent");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(fixture.Db.PublicKey("pc")!)));
        const string requestId = "exclusion-missing-inspection";
        var path = $"/agent/machines/pc/agent-update-recovery/{Get<string>(intent, "IntentId")}/inspection";
        var inspection = CreateSignedInspection(intent, operation, fingerprint, requestId, fixture.AgentKey);
        var challenge = CreateRecoveryChallenge(fixture, intent, inspection);

        var blocked = Record.Exception(() => InvokeRequired(fixture.Db, "CommitRecoveryInspection",
            VerifiedAgentRequestFixture.CreateWithNonce(fixture.Db, "pc", path, inspection, Get<string>(challenge, "Challenge")),
            Get<string>(intent, "IntentId"), requestId, inspection));

        Assert.NotNull(blocked);
        Assert.Equal("AgentRecoveryQualificationUnavailableException", blocked!.GetType().Name);
        Assert.Equal("exclusionProvenanceUnavailable", Get<string>(blocked, "BlockReason"));
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_recovery_successors"));
    }
    private static SignedRecoveryInspection ValidInspection() => new(
        "0123456789abcdef0123456789abcdef",
        new string('A', 64),
        "intent-1",
        "chain-1",
        "head-1",
        3,
        "inspection-request-1",
        "2.0.0",
        new string('B', 64),
        "1.0.0",
        "boundOperationSource",
        "0.9.0",
        new string('F', 64),
        "backup-owner-1",
        new string('C', 64),
        new string('D', 64),
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        "verifiedFullHostRestart",
        "qualifiedDisabled",
        new string('E', 64),
        "win-x64",
        1,
        "signature");

    private static AgentUpdateRecoveryInspectionExpectation ValidExpectation() => new(
        "0123456789abcdef0123456789abcdef",
        "intent-1",
        "chain-1",
        "head-1",
        3,
        "inspection-request-1",
        new string('A', 64),
        "2.0.0",
        new string('B', 64),
        "boundOperationSource",
        "0.9.0",
        new string('F', 64),
        "backup-owner-1",
        new string('C', 64),
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        new string('E', 64),
        "win-x64",
        1,
        true,
        true,
        "qualifiedDisabled",
        true);

    private static object CreateIntent(ControllerDatabase database, AgentUpdateOperationDto operation,
        long expectedGeneration, string requestId)
        => InvokeRequired(database, "CreateRecoveryIntent", "pc", operation.ChainId,
            operation.CurrentHeadOperationId, expectedGeneration, "2.0.0", "test recovery", requestId, "admin");

    private static object CreateRecoveryChallenge(LeaseFixture fixture, object intent,
        SignedRecoveryInspection inspection)
    {
        var database = fixture.Db;
        var requestType = typeof(SignedRecoveryInspection).Assembly.GetType("KamenRaider.Contracts.AgentUpdateRecoveryInspectionChallengeRequest");
        Assert.NotNull(requestType);
        var request = Activator.CreateInstance(requestType!, inspection);
        Assert.NotNull(request);
        var intentId = Get<string>(intent, "IntentId");
        var path = $"/agent/machines/pc/agent-update-recovery/{intentId}/challenge";
        var body = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var verified = VerifiedAgentRequest.CreateForTest("pc", database.PublicKey("pc")!, "POST", path,
            fixture.Time.Now.ToUnixTimeSeconds(), Guid.NewGuid().ToString("N"), body);
        return InvokeRequired(database, "IssueAgentUpdateRecoveryInspectionChallenge", verified, intentId, request!);
    }

    private static SignedRecoveryInspection CreateSignedInspection(object intent, AgentUpdateOperationDto operation,
        string keyFingerprint, string requestId, System.Security.Cryptography.ECDsa agentKey)
    {
        var bootId = Guid.NewGuid().ToString("N");
        var inspection = new SignedRecoveryInspection(
            "pc",
            keyFingerprint,
            Get<string>(intent, "IntentId"),
            Get<string>(intent, "ChainId"),
            Get<string>(intent, "PreviousHeadOperationId"),
            Get<long>(intent, "Generation"),
            requestId,
            Get<string>(intent, "TargetVersion"),
            Get<string>(intent, "TargetPackageSha256"),
            "1.0.0",
            "verifiedOriginalBackup",
            null,
            null,
            operation.OperationId,
            new string('C', 64),
            new string('D', 64),
            bootId,
            bootId,
            "continuousSharedLock",
            "qualifiedNotApplicable",
            new string('E', 64),
            "win-x64",
            1,
            string.Empty);
        return SignRecoveryInspection(inspection, agentKey);
    }

    private static SignedRecoveryInspection SignRecoveryInspection(SignedRecoveryInspection inspection,
        System.Security.Cryptography.ECDsa agentKey)
    {
        var signature = agentKey.SignData(AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(inspection),
            HashAlgorithmName.SHA256);
        return inspection with { EvidenceSignature = Convert.ToBase64String(signature) };
    }

    private static HttpRequestMessage CreateSignedAgentRequest(LeaseFixture fixture, string path, object body, string nonce)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var signedSeconds = fixture.Time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var bodyDigest = Convert.ToHexString(SHA256.HashData(bytes));
        var canonical = Encoding.UTF8.GetBytes($"pc\nPOST\n{path}\n{signedSeconds}\n{nonce}\n{bodyDigest}");
        var signature = Convert.ToBase64String(fixture.AgentKey.SignData(canonical, HashAlgorithmName.SHA256));
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-Agent-Id", "pc");
        request.Headers.TryAddWithoutValidation("X-Agent-Timestamp", signedSeconds);
        request.Headers.TryAddWithoutValidation("X-Agent-Nonce", nonce);
        request.Headers.TryAddWithoutValidation("X-Agent-Signature", signature);
        return request;
    }

    private static object InvokeRequired(object target, string methodName, params object[] arguments)
    {
        var parameterTypes = arguments.Select(argument => argument.GetType()).ToArray();
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public,
            binder: null, types: parameterTypes, modifiers: null);
        Assert.NotNull(method);
        try { return method!.Invoke(target, arguments)!; }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static T Get<T>(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        return Assert.IsType<T>(property!.GetValue(value));
    }

    private static WebApplication BuildRecoveryAdminApi(ControllerDatabase database)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(database);
        var app = builder.Build();
        app.MapMachines();
        return app;
    }

    private static void AddAdminActor(HttpClient client, string actorId, string role)
    {
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", actorId);
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role", role);
    }

    private static void AddRelease(string databasePath, string version)
    {
        var bytes = AgentUpdateTestPackage.Create(version);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var packagePath = Path.Combine(Path.GetDirectoryName(databasePath)!, "agent-releases", "sha256",
            hash[..2].ToLowerInvariant(), hash.ToLowerInvariant() + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        File.WriteAllBytes(packagePath, bytes);
        using var connection = Open(databasePath);
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO agent_releases(version,channel,release_notes,package_sha256,package_path,package_size_bytes,source_commit,source_dirty,release_build,package_format,published_at_unix_ms,target_runtime,agent_update_challenge_protocol) VALUES($version,'stable','release notes',$hash,$path,$size,'0123456789ab',0,1,'AgentPackage.zip',1,'win-x64',2)";
        insert.Parameters.AddWithValue("$version", version);
        insert.Parameters.AddWithValue("$hash", hash);
        insert.Parameters.AddWithValue("$path", packagePath);
        insert.Parameters.AddWithValue("$size", bytes.Length);
        insert.ExecuteNonQuery();
    }

    private static long Scalar(string databasePath, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
