using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Controller.Tests.Fixtures;
using KamenRaider.AgentMaintenance;
using KamenRaider.Contracts;
using KamenRaider.Controller;
using KamenRaider.Controller.Data;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Controller.Tests;

// Real Controller HTTP, signatures, SQLite, ZIP validation and artifact IO. The updater
// peer supplies simulated service outcomes; no PowerShell, SCM, TLS socket or hardware runs here.
public sealed class AgentReleaseAcceptanceTests
{
    [Fact]
    public async Task ProtocolTwoChallengeIsRepeatableAndDoesNotApplyRejectedHeartbeat()
    {
        await using var fixture = await ProofFixture.CreateAsync();
        var heartbeat = new SyncRequest("new-boot", "192.0.2.41", "001122334455", 1, "ready", [])
        {
            SyncProofProtocol = 2,
            AgentVersion = "2.3.4",
        };
        using var first = await fixture.SendAsync(heartbeat);
        Assert.Equal(HttpStatusCode.PreconditionRequired, first.StatusCode);
        var bytes = await first.Content.ReadAsByteArrayAsync();
        Assert.InRange(bytes.Length, 1, 4096);
        using var firstJson = JsonDocument.Parse(bytes);
        var fields = firstJson.RootElement.EnumerateObject().ToArray();
        Assert.Single(fields);
        Assert.Equal("agentUpdateChallenge", fields[0].Name);
        var token = fields[0].Value.GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(token!.Length <= 128);
        var unchanged = Assert.Single(fixture.Database.List());
        Assert.Null(unchanged.LastSeenAt);
        Assert.Null(unchanged.AppliedConfigRevision);
        Assert.Equal("192.0.2.40", unchanged.LocalIp);
        Assert.Null(unchanged.AgentVersion);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM machine_heartbeats WHERE machine_id='pc'"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM machine_boots WHERE machine_id='pc'"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM machine_config_acks WHERE machine_id='pc'"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM command_outbox WHERE machine_id='pc'"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM pairing_offers WHERE machine_id='pc'"));

        using var lostResponseRetry = await fixture.SendAsync(heartbeat);
        Assert.Equal(HttpStatusCode.PreconditionRequired, lostResponseRetry.StatusCode);
        var repeated = await lostResponseRetry.Content.ReadFromJsonAsync<SyncChallengeRequired>();
        Assert.Equal(token, repeated!.AgentUpdateChallenge);
        using var accepted = await fixture.SendAsync(heartbeat with { AgentUpdateChallenge = token });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("2.3.4", Assert.Single(fixture.Database.List()).AgentVersion);
        var acceptedLastSeen = Assert.Single(fixture.Database.List()).LastSeenAt;

        using var replay = await fixture.SendAsync(heartbeat with { AgentUpdateChallenge = token, AgentVersion = "1.0.0" });
        Assert.Equal(HttpStatusCode.PreconditionRequired, replay.StatusCode);
        Assert.Equal("2.3.4", Assert.Single(fixture.Database.List()).AgentVersion);
        Assert.Equal(acceptedLastSeen, Assert.Single(fixture.Database.List()).LastSeenAt);
        Assert.Equal("ready", Assert.Single(fixture.Database.List()).Readiness);
    }

    [Fact]
    public async Task SourceChallengeAndBindingRequireTheEnrolledKeyAndSupportExactRetry()
    {
        await using var fixture = await AcceptanceFixture.CreateAsync();
        var ticket = await fixture.PublishAndReserveAsync();
        var operation = await fixture.StartAndDownloadAsync(ticket);
        var staleSignedSecond = fixture.Clock.Now.AddDays(-30).ToUnixTimeSeconds();
        var challengeRequest = new AgentUpdateSourceChallengeRequest(ticket.Ticket, ticket.ReleaseVersion,
            ticket.TargetVersion, ticket.TargetPackageSha256, "source-challenge-acceptance", new string('a', 64), "win-x64", 1);
        using var unsignedChallenge = await fixture.SendSignedAsync(AcceptanceFixture.UpdatePath(ticket, "source-challenge"),
            challengeRequest, badSignature: true);
        Assert.Equal(HttpStatusCode.Unauthorized, unsignedChallenge.StatusCode);
        using var challengeResponse = await fixture.SendSignedAsync(AcceptanceFixture.UpdatePath(ticket, "source-challenge"),
            challengeRequest, nonce: "stale-source-challenge", signedSecond: staleSignedSecond);
        Assert.Equal(HttpStatusCode.OK, challengeResponse.StatusCode);
        Assert.Equal("no-store", challengeResponse.Headers.CacheControl?.ToString());
        var challenge = Assert.IsType<AgentUpdateSourceChallengeResponse>(
            await challengeResponse.Content.ReadFromJsonAsync<AgentUpdateSourceChallengeResponse>());
        using var challengeReplay = await fixture.SendSignedAsync(AcceptanceFixture.UpdatePath(ticket, "source-challenge"),
            challengeRequest, nonce: "stale-source-challenge", signedSecond: staleSignedSecond);
        Assert.Equal(HttpStatusCode.Unauthorized, challengeReplay.StatusCode);
        using var challengeRetry = await fixture.SendSignedAsync(AcceptanceFixture.UpdatePath(ticket, "source-challenge"),
            challengeRequest, nonce: "stale-source-challenge-retry", signedSecond: staleSignedSecond);
        Assert.Equal(HttpStatusCode.OK, challengeRetry.StatusCode);
        Assert.Equal(challenge, await challengeRetry.Content.ReadFromJsonAsync<AgentUpdateSourceChallengeResponse>());

        var binding = fixture.CreateSignedSourceBinding(ticket, operation, challengeRequest, challenge,
            "source-binding-acceptance", "boot-acceptance");
        using var firstBinding = await fixture.SendSignedAsync(AcceptanceFixture.UpdatePath(ticket, "source-binding"), binding,
            nonce: "stale-source-binding", signedSecond: staleSignedSecond);
        Assert.Equal(HttpStatusCode.OK, firstBinding.StatusCode);
        var bound = Assert.IsType<AgentUpdateOperationDto>(await firstBinding.Content.ReadFromJsonAsync<AgentUpdateOperationDto>());
        Assert.Equal("1.2.3", bound.SourceVersion);
        Assert.Equal(binding.BackupDigest.ToUpperInvariant(), bound.BackupDigest);

        using var bindingReplay = await fixture.SendSignedAsync(AcceptanceFixture.UpdatePath(ticket, "source-binding"), binding,
            nonce: "stale-source-binding", signedSecond: staleSignedSecond);
        Assert.Equal(HttpStatusCode.Unauthorized, bindingReplay.StatusCode);
        using var exactRetry = await fixture.SendSignedAsync(AcceptanceFixture.UpdatePath(ticket, "source-binding"), binding,
            nonce: "stale-source-binding-retry", signedSecond: staleSignedSecond);
        Assert.Equal(HttpStatusCode.OK, exactRetry.StatusCode);
        Assert.Equal(bound, await exactRetry.Content.ReadFromJsonAsync<AgentUpdateOperationDto>());

        using var changedBody = await fixture.SendSignedAsync(AcceptanceFixture.UpdatePath(ticket, "source-binding"),
            binding with { NativeBootId = "different-boot" }, nonce: "stale-source-binding-changed", signedSecond: staleSignedSecond);
        Assert.Equal(HttpStatusCode.Conflict, changedBody.StatusCode);

        var changedChallenge = challengeRequest with { HelperBuildSha256 = new string('b', 64) };
        using var challengeIdReuse = await fixture.SendSignedAsync(AcceptanceFixture.UpdatePath(ticket, "source-challenge"),
            changedChallenge, nonce: "stale-source-challenge-changed", signedSecond: staleSignedSecond);
        Assert.Equal(HttpStatusCode.Conflict, challengeIdReuse.StatusCode);
    }

    [Fact]
    public async Task LegacySameSecondCannotReplaceNewerReceiptOrGainRentalAuthority()
    {
        await using var fixture = await ProofFixture.CreateAsync();
        fixture.Database.Update("pc", new("PC", 500, 1, 2), 1);
        fixture.Database.Approve("pc", "admin");
        var second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var heartbeat = new SyncRequest("boot", "192.0.2.40", "001122334455", 3, "ready", []) { AgentVersion = "2.0.0" };
        using var first = await fixture.SendAsync(heartbeat, second);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var sameSecond = await fixture.SendAsync(heartbeat with { AgentVersion = "1.0.0" }, second);
        Assert.False(sameSecond.IsSuccessStatusCode);
        Assert.NotEqual("1.0.0", Assert.Single(fixture.Database.List()).AgentVersion);
        Assert.False(Assert.Single(fixture.Database.List()).CanRent);
        Assert.Throws<InvalidOperationException>(() => fixture.Database.CreateTrialLease("renter", "pc", 60, "request"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedLegacySyncAfterProofModeIsLatchedHasNoBodyEffects(bool seedLegacyHighWater)
    {
        await using var fixture = await ProofFixture.CreateAsync();
        fixture.Database.Update("pc", new("PC", 500, 1, 2), 1);
        fixture.Database.Approve("pc", "admin");
        var machineBefore = Assert.Single(fixture.Database.List());
        var second = fixture.Clock.Now.ToUnixTimeSeconds();

        if (seedLegacyHighWater)
        {
            var priorLegacy = new SyncRequest("prior-legacy-boot", "192.0.2.43", "001122334455", 0, "notReady", [])
            { AgentVersion = "0.9.0" };
            using var acceptedPrior = await fixture.SendAsync(priorLegacy, second - 2, nonce: "prior-legacy-request");
            Assert.Equal(HttpStatusCode.OK, acceptedPrior.StatusCode);
        }

        var capturedLegacy = new SyncRequest("captured-legacy-boot", "192.0.2.99", "001122334466",
            machineBefore.ConfigRevision, "ready", [new("captured-command", "applied", 99)],
            new("captured-lease", 99, "captured-legacy-boot", fixture.Clock.Now.AddMinutes(1), [PairingIds.FoundationSunshineSinglePendingSlotId]))
        { AgentVersion = "1.0.0" };
        using var captured = fixture.CreateSignedRequest(capturedLegacy, second - 1, nonce: "captured-legacy-request");

        var proof = new SyncRequest("proved-not-ready-boot", "192.0.2.41", "001122334455", 0, "notReady", [])
        {
            SyncProofProtocol = 2,
            AgentVersion = "2.3.4",
            ReadinessDiagnostics = [new(ReadinessDiagnosticIds.RuntimeHealth, ReadinessDiagnosticStatuses.Blocked)],
        };
        using var challenge = await fixture.SendAsync(proof, second, nonce: "proof-challenge-request");
        Assert.Equal(HttpStatusCode.PreconditionRequired, challenge.StatusCode);
        var token = (await challenge.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;
        using var acceptedProof = await fixture.SendAsync(proof with { AgentUpdateChallenge = token }, second, nonce: "proof-accepted-request");
        Assert.Equal(HttpStatusCode.OK, acceptedProof.StatusCode);
        var beforeReplay = Assert.Single(fixture.Database.List());
        Assert.False(beforeReplay.CanRent);
        var proofStateBefore = fixture.ReadText("SELECT mode||'|'||sequence||'|'||COALESCE(last_proven_sequence,'')||'|'||COALESCE(last_proven_tick,'')||'|'||COALESCE(last_proven_version,'')||'|'||COALESCE(legacy_last_second,'')||'|'||COALESCE(legacy_reported_version,'') FROM agent_sync_proof_state WHERE machine_id='pc'");
        var ackBefore = fixture.Scalar("SELECT applied_revision FROM machine_config_acks WHERE machine_id='pc'");
        var nonceRowsBefore = fixture.Scalar("SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id='pc' AND path='/agent/sync'");
        var bootRowsBefore = fixture.Scalar("SELECT COUNT(*) FROM machine_boots WHERE machine_id='pc'");

        using var delayed = await fixture.SendPreparedAsync(captured);

        Assert.False(delayed.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, delayed.StatusCode);
        var afterReplay = Assert.Single(fixture.Database.List());
        Assert.False(afterReplay.CanRent);
        Assert.Equal(beforeReplay.Readiness, afterReplay.Readiness);
        Assert.Equal(beforeReplay.BootId, afterReplay.BootId);
        Assert.Equal(beforeReplay.LocalIp, afterReplay.LocalIp);
        Assert.Equal(beforeReplay.MacAddress, afterReplay.MacAddress);
        Assert.Equal(beforeReplay.AppliedConfigRevision, afterReplay.AppliedConfigRevision);
        Assert.Equal(ackBefore, fixture.Scalar("SELECT applied_revision FROM machine_config_acks WHERE machine_id='pc'"));
        Assert.Equal("notReady", afterReplay.Readiness);
        Assert.Equal("proved-not-ready-boot", afterReplay.BootId);
        Assert.Equal("192.0.2.41", afterReplay.LocalIp);
        Assert.Equal(0L, afterReplay.AppliedConfigRevision);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM machine_boots WHERE machine_id='pc' AND boot_id='captured-legacy-boot'"));
        Assert.Equal(bootRowsBefore, fixture.Scalar("SELECT COUNT(*) FROM machine_boots WHERE machine_id='pc'"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM command_outbox WHERE machine_id='pc' AND command_id='captured-command'"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM pairing_offers WHERE machine_id='pc'"));
        Assert.Equal(nonceRowsBefore, fixture.Scalar("SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id='pc' AND path='/agent/sync'"));
        Assert.Equal(proofStateBefore, fixture.ReadText("SELECT mode||'|'||sequence||'|'||COALESCE(last_proven_sequence,'')||'|'||COALESCE(last_proven_tick,'')||'|'||COALESCE(last_proven_version,'')||'|'||COALESCE(legacy_last_second,'')||'|'||COALESCE(legacy_reported_version,'') FROM agent_sync_proof_state WHERE machine_id='pc'"));
        Assert.Throws<InvalidOperationException>(() => fixture.Database.CreateTrialLease("renter", "pc", 60, "after-captured-legacy"));
    }

    [Fact]
    public async Task SignatureValidStaleProtocolTwoGetsChallengeButMalformedSecondsAndBadSignaturesAreUnauthorized()
    {
        await using var fixture = await ProofFixture.CreateAsync();
        var heartbeat = new SyncRequest("boot", "192.0.2.40", "001122334455", 1, "notReady", []) { SyncProofProtocol = 2 };
        using var stale = await fixture.SendAsync(heartbeat, DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.PreconditionRequired, stale.StatusCode);
        var token = (await stale.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;
        using var badSignature = await fixture.SendAsync(heartbeat, badSignature: true);
        Assert.Equal(HttpStatusCode.Unauthorized, badSignature.StatusCode);
        using var outOfRange = await fixture.SendAsync(heartbeat, 253402300800);
        Assert.Equal(HttpStatusCode.Unauthorized, outOfRange.StatusCode);
        using var malformed = await fixture.SendAsync(heartbeat, timestampHeader: "not-a-unix-second");
        Assert.Equal(HttpStatusCode.Unauthorized, malformed.StatusCode);
        using var missing = await fixture.SendAsync(heartbeat, omitTimestamp: true);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        using var retry = await fixture.SendAsync(heartbeat);
        Assert.Equal(HttpStatusCode.PreconditionRequired, retry.StatusCode);
        Assert.Equal(token, (await retry.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge);
        Assert.Null(Assert.Single(fixture.Database.List()).LastSeenAt);
    }

    [Fact]
    public async Task RestartInvalidatesOutstandingChallengeWithoutAcceptingItsBody()
    {
        await using var fixture = await ProofFixture.CreateAsync();
        var heartbeat = new SyncRequest("boot", "192.0.2.40", "001122334455", 1, "notReady", []) { SyncProofProtocol = 2, AgentVersion = "2.3.4" };
        using var challenge = await fixture.SendAsync(heartbeat);
        Assert.Equal(HttpStatusCode.PreconditionRequired, challenge.StatusCode);
        var token = (await challenge.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;
        await fixture.RestartAsync();
        using var stale = await fixture.SendAsync(heartbeat with { AgentUpdateChallenge = token });
        Assert.Equal(HttpStatusCode.PreconditionRequired, stale.StatusCode);
        Assert.NotEqual(token, (await stale.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge);
        Assert.Null(Assert.Single(fixture.Database.List()).LastSeenAt);
    }

    [Fact]
    public async Task ProtocolTwoOrdersSameSecondHeartbeatsAndAllowsProvenUnversionedClear()
    {
        await using var fixture = await ProofFixture.CreateAsync();
        var second = fixture.Clock.Now.ToUnixTimeSeconds();
        var older = new SyncRequest("boot-A", "192.0.2.41", "001122334455", 0, "notReady", [new("unissued-command", "applied", 1)])
        { SyncProofProtocol = 2, AgentVersion = "1.0.0" };
        using var challenge = await fixture.SendAsync(older, second);
        Assert.Equal(HttpStatusCode.PreconditionRequired, challenge.StatusCode);
        var firstToken = (await challenge.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;

        var newer = new SyncRequest("boot-B", "192.0.2.42", "001122334455", 1, "ready", [])
        { SyncProofProtocol = 2, AgentVersion = "2.0.0", AgentUpdateChallenge = firstToken };
        using var acceptedNewer = await fixture.SendAsync(newer, second);
        Assert.Equal(HttpStatusCode.OK, acceptedNewer.StatusCode);
        var secondToken = (await acceptedNewer.Content.ReadFromJsonAsync<SyncResponse>())!.AgentUpdateChallenge;

        using var outOfOrderOlder = await fixture.SendAsync(older with { AgentUpdateChallenge = firstToken }, second);
        Assert.Equal(HttpStatusCode.PreconditionRequired, outOfOrderOlder.StatusCode);
        Assert.Equal(secondToken, (await outOfOrderOlder.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge);
        var afterRejectedOlder = Assert.Single(fixture.Database.List());
        Assert.Equal("2.0.0", afterRejectedOlder.AgentVersion);
        Assert.Equal("192.0.2.42", afterRejectedOlder.LocalIp);
        Assert.Equal("ready", afterRejectedOlder.Readiness);
        Assert.Equal(1, afterRejectedOlder.AppliedConfigRevision);
        Assert.Equal("boot-B", afterRejectedOlder.BootId);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM machine_boots WHERE machine_id='pc' AND boot_id='boot-A'"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM command_outbox WHERE machine_id='pc'"));

        using var unversioned = await fixture.SendAsync(newer with { BootId = "boot-C", AgentVersion = null, AgentUpdateChallenge = secondToken }, second);
        Assert.Equal(HttpStatusCode.OK, unversioned.StatusCode);
        var afterUnversioned = Assert.Single(fixture.Database.List());
        Assert.Null(afterUnversioned.AgentVersion);
        Assert.Equal("boot-C", afterUnversioned.BootId);
    }

    [Fact]
    public async Task ConcurrentChallengeIssueAndConsumptionHaveOneWinner()
    {
        await using var fixture = await ProofFixture.CreateAsync();
        var second = fixture.Clock.Now.ToUnixTimeSeconds();
        var left = new SyncRequest("race-left", "192.0.2.41", "001122334455", 1, "ready", [])
        { SyncProofProtocol = 2, AgentVersion = "1.0.0" };
        var right = new SyncRequest("race-right", "192.0.2.42", "001122334455", 1, "ready", [])
        { SyncProofProtocol = 2, AgentVersion = "2.0.0" };

        var issued = await Task.WhenAll(fixture.SendAsync(left, second), fixture.SendAsync(right, second));
        try
        {
            Assert.All(issued, response => Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode));
            var tokens = await Task.WhenAll(issued.Select(async response =>
                (await response.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge));
            Assert.Equal(tokens[0], tokens[1]);
            Assert.Null(Assert.Single(fixture.Database.List()).LastSeenAt);

            var consumed = await Task.WhenAll(
                fixture.SendAsync(left with { AgentUpdateChallenge = tokens[0] }, second),
                fixture.SendAsync(right with { AgentUpdateChallenge = tokens[0] }, second));
            try
            {
                var accepted = Assert.Single(consumed, response => response.StatusCode == HttpStatusCode.OK);
                var rejected = Assert.Single(consumed, response => response.StatusCode == HttpStatusCode.PreconditionRequired);
                var acceptedBody = (await accepted.Content.ReadFromJsonAsync<SyncResponse>())!;
                var rejectedBody = (await rejected.Content.ReadFromJsonAsync<SyncChallengeRequired>())!;
                Assert.Equal(acceptedBody.AgentUpdateChallenge, rejectedBody.AgentUpdateChallenge);
                var machine = Assert.Single(fixture.Database.List());
                Assert.Contains(machine.AgentVersion, new[] { "1.0.0", "2.0.0" });
                Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM machine_heartbeats WHERE machine_id='pc'"));
                Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM machine_boots WHERE machine_id='pc'"));
                var rejectedBoot = machine.BootId == "race-left" ? "race-right" : "race-left";
                Assert.Equal(0L, fixture.Scalar($"SELECT COUNT(*) FROM machine_boots WHERE machine_id='pc' AND boot_id='{rejectedBoot}'"));
            }
            finally
            {
                foreach (var response in consumed) response.Dispose();
            }
        }
        finally
        {
            foreach (var response in issued) response.Dispose();
        }
    }

    [Fact]
    public async Task DurableSyncNonceWindowPrunesExpiredReplayRows()
    {
        await using var fixture = await ProofFixture.CreateAsync();
        var firstSecond = fixture.Clock.Now.ToUnixTimeSeconds();
        var request = new SyncRequest("nonce-boot", "192.0.2.41", "001122334455", 1, "notReady", [])
        { SyncProofProtocol = 2 };
        using var challenge = await fixture.SendAsync(request, firstSecond);
        Assert.Equal(HttpStatusCode.PreconditionRequired, challenge.StatusCode);
        var token = (await challenge.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;
        using var firstAccepted = await fixture.SendAsync(request with { AgentUpdateChallenge = token }, firstSecond);
        Assert.Equal(HttpStatusCode.OK, firstAccepted.StatusCode);
        var nextToken = (await firstAccepted.Content.ReadFromJsonAsync<SyncResponse>())!.AgentUpdateChallenge;
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id='pc' AND path='/agent/sync#protocol2'"));

        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(121);
        fixture.Clock.AdvanceMonotonic(TimeSpan.FromSeconds(121));
        using var secondAccepted = await fixture.SendAsync(request with { AgentUpdateChallenge = nextToken }, nonce: "after-retention-window");
        Assert.Equal(HttpStatusCode.OK, secondAccepted.StatusCode);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id='pc' AND path='/agent/sync#protocol2'"));
    }

    [Fact]
    public async Task ProtocolTwoNonceRowsStayBoundedDuringControllerClockRollback()
    {
        const int maximumRows = 4096;
        await using var fixture = await ProofFixture.CreateAsync();
        var initialTime = fixture.Clock.Now;
        var signedSecond = initialTime.ToUnixTimeSeconds();
        var request = new SyncRequest("bounded-nonce-boot", "192.0.2.41", "001122334455", 1, "notReady", [])
        { SyncProofProtocol = 2 };
        using var challenge = await fixture.SendAsync(request, signedSecond);
        Assert.Equal(HttpStatusCode.PreconditionRequired, challenge.StatusCode);
        var token = (await challenge.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;

        var fingerprint = Assert.Single(fixture.Database.List()).PublicKeyFingerprint!;
        var cutoff = signedSecond - 120;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO agent_signed_request_scopes(machine_id,key_fingerprint,path,cutoff_second) VALUES('pc',$fingerprint,'/agent/sync#protocol2',$cutoff)";
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.Parameters.AddWithValue("$cutoff", cutoff);
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO agent_signed_request_nonces(machine_id,key_fingerprint,path,nonce_digest,signed_second,retention_second) VALUES('pc',$fingerprint,'/agent/sync#protocol2',$nonce,$cutoff,$cutoff)";
            var nonceParameter = command.Parameters.Add("$nonce", SqliteType.Text);
            for (var index = 0; index < maximumRows - 1; index++)
            {
                nonceParameter.Value = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("retained-nonce-" + index)));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        fixture.Clock.Now = fixture.Clock.Now.AddDays(-1);
        using var accepted = await fixture.SendAsync(request with { AgentUpdateChallenge = token });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var currentToken = (await accepted.Content.ReadFromJsonAsync<SyncResponse>())!.AgentUpdateChallenge;
        Assert.Equal((long)maximumRows, fixture.Scalar("SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id='pc' AND path='/agent/sync#protocol2'"));

        var cappedBody = request with { BootId = "cap-overflow", LocalIp = "192.0.2.44", AgentVersion = "9.9.9", AgentUpdateChallenge = currentToken };
        var beforeCapReject = Assert.Single(fixture.Database.List());
        using var atCapacity = await fixture.SendAsync(cappedBody, nonce: "capacity-rejected-nonce");
        Assert.Equal(HttpStatusCode.PreconditionRequired, atCapacity.StatusCode);
        Assert.Equal(currentToken, (await atCapacity.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge);
        var afterCapReject = Assert.Single(fixture.Database.List());
        Assert.Equal(beforeCapReject.LastSeenAt, afterCapReject.LastSeenAt);
        Assert.Equal(beforeCapReject.BootId, afterCapReject.BootId);
        Assert.Equal(beforeCapReject.AgentVersion, afterCapReject.AgentVersion);
        Assert.Equal((long)maximumRows, fixture.Scalar("SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id='pc' AND path='/agent/sync#protocol2'"));

        fixture.Clock.Now = initialTime.AddSeconds(121);
        using var recovered = await fixture.SendAsync(cappedBody, nonce: "capacity-rejected-nonce");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal("cap-overflow", Assert.Single(fixture.Database.List()).BootId);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id='pc' AND path='/agent/sync#protocol2'"));
    }

    [Fact]
    public async Task ProtocolTwoFutureSignedSecondsDoNotPinNonceCapacityPastServerRetention()
    {
        const int maximumRows = 4096;
        await using var fixture = await ProofFixture.CreateAsync();
        var serverSecond = fixture.Clock.Now.ToUnixTimeSeconds();
        const long farFutureSecond = 253402300799;
        var request = new SyncRequest("future-time-boot", "192.0.2.41", "001122334455", 1, "notReady", [])
        { SyncProofProtocol = 2 };
        using var challenge = await fixture.SendAsync(request, farFutureSecond, nonce: "future-challenge-request");
        Assert.Equal(HttpStatusCode.PreconditionRequired, challenge.StatusCode);
        var fingerprint = Assert.Single(fixture.Database.List()).PublicKeyFingerprint!;
        var legacyCutoff = serverSecond - 120;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO agent_signed_request_scopes(machine_id,key_fingerprint,path,cutoff_second) VALUES('pc',$fingerprint,'/agent/sync',$cutoff)";
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.Parameters.AddWithValue("$cutoff", legacyCutoff);
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO agent_signed_request_nonces(machine_id,key_fingerprint,path,nonce_digest,signed_second,retention_second) VALUES('pc',$fingerprint,'/agent/sync',$nonce,$second,$second)";
            var nonceParameter = command.Parameters.Add("$nonce", SqliteType.Text);
            var secondParameter = command.Parameters.Add("$second", SqliteType.Integer);
            for (var index = 0; index < maximumRows; index++)
            {
                nonceParameter.Value = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("pre-v2-future-nonce-" + index)));
                secondParameter.Value = farFutureSecond;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        // These v15 rows have ambiguous legacy/v2 provenance. Restart rotates the old token epoch;
        // the new protocol-2 scope must recover without deleting or reclassifying that old evidence.
        await fixture.RestartAsync();
        using var reissued = await fixture.SendAsync(request, farFutureSecond, nonce: "future-reissue-request");
        Assert.Equal(HttpStatusCode.PreconditionRequired, reissued.StatusCode);
        var token = (await reissued.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;

        const string preMigrationNonce = "pre-v2-future-nonce-0";
        using var oldScopeDuplicate = await fixture.SendAsync(request with { AgentUpdateChallenge = token, BootId = "old-scope-replay" },
            farFutureSecond, nonce: preMigrationNonce);
        Assert.Equal(HttpStatusCode.PreconditionRequired, oldScopeDuplicate.StatusCode);
        Assert.Equal(token, (await oldScopeDuplicate.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge);
        Assert.Null(Assert.Single(fixture.Database.List()).LastSeenAt);

        using var accepted = await fixture.SendAsync(request with { AgentUpdateChallenge = token }, farFutureSecond, nonce: "future-accepted-request");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var nextToken = (await accepted.Content.ReadFromJsonAsync<SyncResponse>())!.AgentUpdateChallenge;
        Assert.Equal((long)maximumRows, fixture.Scalar("SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id='pc' AND path='/agent/sync' AND signed_second=253402300799"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id='pc' AND path='/agent/sync#protocol2'"));
        Assert.Equal(farFutureSecond, fixture.Scalar("SELECT signed_second FROM agent_signed_request_nonces WHERE machine_id='pc' AND path='/agent/sync#protocol2'"));

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO agent_signed_request_nonces(machine_id,key_fingerprint,path,nonce_digest,signed_second,retention_second) VALUES('pc',$fingerprint,'/agent/sync#protocol2',$nonce,$audit,$retention)";
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            var nonceParameter = command.Parameters.Add("$nonce", SqliteType.Text);
            var auditParameter = command.Parameters.Add("$audit", SqliteType.Integer);
            var retentionParameter = command.Parameters.Add("$retention", SqliteType.Integer);
            for (var index = 0; index < maximumRows - 1; index++)
            {
                nonceParameter.Value = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("future-retained-nonce-" + index)));
                auditParameter.Value = farFutureSecond;
                retentionParameter.Value = serverSecond;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        var cappedBody = request with
        {
            BootId = "future-cap-body",
            LocalIp = "192.0.2.99",
            MacAddress = "001122334466",
            AppliedConfigRevision = 0,
            Readiness = "ready",
            CommandResults = [new("future-cap-command", "applied", 99)],
            PendingPairing = new("future-cap-lease", 99, "future-cap-body", fixture.Clock.Now.AddMinutes(1), [PairingIds.FoundationSunshineSinglePendingSlotId]),
            AgentVersion = "9.9.9",
            ReadinessDiagnostics = [new(ReadinessDiagnosticIds.RuntimeHealth, ReadinessDiagnosticStatuses.Passed)],
            AgentUpdateChallenge = nextToken,
        };
        var beforeCap = Assert.Single(fixture.Database.List());
        using var capped = await fixture.SendAsync(cappedBody, farFutureSecond, nonce: "future-cap-attempt");
        Assert.Equal(HttpStatusCode.PreconditionRequired, capped.StatusCode);
        var capChallenge = (await capped.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;
        Assert.Equal(nextToken, capChallenge);
        var afterCap = Assert.Single(fixture.Database.List());
        Assert.Equal(beforeCap.LastSeenAt, afterCap.LastSeenAt);
        Assert.Equal(beforeCap.BootId, afterCap.BootId);
        Assert.Equal(beforeCap.AgentVersion, afterCap.AgentVersion);
        Assert.Equal(beforeCap.Readiness, afterCap.Readiness);
        Assert.Equal(beforeCap.LocalIp, afterCap.LocalIp);
        Assert.Equal(beforeCap.MacAddress, afterCap.MacAddress);
        Assert.Equal(beforeCap.AppliedConfigRevision, afterCap.AppliedConfigRevision);
        Assert.Equal(1L, fixture.Scalar("SELECT applied_revision FROM machine_config_acks WHERE machine_id='pc'"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM machine_boots WHERE machine_id='pc'"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM command_outbox WHERE machine_id='pc'"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM pairing_offers WHERE machine_id='pc'"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM leases WHERE machine_id='pc'"));
        Assert.Equal((long)maximumRows, fixture.Scalar("SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id='pc' AND path='/agent/sync#protocol2'"));

        fixture.Clock.Now = fixture.Clock.Now.AddDays(-1);
        using var duringRollback = await fixture.SendAsync(cappedBody with { AgentUpdateChallenge = capChallenge }, farFutureSecond, nonce: "future-cap-during-rollback");
        Assert.Equal(HttpStatusCode.PreconditionRequired, duringRollback.StatusCode);
        var rollbackMachine = Assert.Single(fixture.Database.List());
        Assert.Equal(beforeCap.LastSeenAt, rollbackMachine.LastSeenAt);
        Assert.Equal((long)maximumRows, fixture.Scalar("SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id='pc' AND path='/agent/sync#protocol2'"));

        fixture.Clock.Now = fixture.Clock.Now.AddDays(1).AddSeconds(119);
        fixture.Clock.AdvanceMonotonic(TimeSpan.FromSeconds(119));
        var rollbackToken = (await duringRollback.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;
        using var beforeRetention = await fixture.SendAsync(cappedBody with { AgentUpdateChallenge = rollbackToken }, farFutureSecond, nonce: "future-cap-before-retention");
        Assert.Equal(HttpStatusCode.PreconditionRequired, beforeRetention.StatusCode);
        Assert.Equal(beforeCap.LastSeenAt, Assert.Single(fixture.Database.List()).LastSeenAt);
        Assert.Equal((long)maximumRows, fixture.Scalar("SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id='pc' AND path='/agent/sync#protocol2'"));

        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(2);
        fixture.Clock.AdvanceMonotonic(TimeSpan.FromSeconds(2));
        var beforeRetentionToken = (await beforeRetention.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;
        using var recovered = await fixture.SendAsync(cappedBody with { AgentUpdateChallenge = beforeRetentionToken }, farFutureSecond, nonce: "future-cap-attempt");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal("future-cap-body", Assert.Single(fixture.Database.List()).BootId);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id='pc' AND path='/agent/sync#protocol2'"));
        Assert.Equal((long)maximumRows, fixture.Scalar("SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id='pc' AND path='/agent/sync' AND signed_second=253402300799"));
    }

    [Fact]
    public async Task RepeatedStaleProofChallengesAreRateLimitedAndRecoverAfterRestart()
    {
        await using var fixture = await ProofFixture.CreateAsync();
        var staleSecond = fixture.Clock.Now.AddDays(-1).ToUnixTimeSeconds();
        var request = new SyncRequest("rate-limit-boot", "192.0.2.41", "001122334455", 1, "notReady", [])
        { SyncProofProtocol = 2 };

        using var firstRequest = fixture.CreateSignedRequest(request, staleSecond, nonce: "rate-lost-response");
        using var first = await fixture.SendPreparedAsync(firstRequest);
        Assert.Equal(HttpStatusCode.PreconditionRequired, first.StatusCode);
        var token = (await first.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;
        using var retryRequest = fixture.CreateSignedRequest(request, staleSecond, nonce: "rate-lost-response");
        using var oneRetry = await fixture.SendPreparedAsync(retryRequest);
        Assert.Equal(HttpStatusCode.PreconditionRequired, oneRetry.StatusCode);
        Assert.Equal(token, (await oneRetry.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge);

        using var limited = await fixture.SendAsync(request with { BootId = "rate-limited-body", AgentVersion = "9.9.9" }, staleSecond, nonce: "rate-third");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("10", limited.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var afterLimited = Assert.Single(fixture.Database.List());
        Assert.Null(afterLimited.LastSeenAt);
        Assert.Equal("192.0.2.40", afterLimited.LocalIp);
        Assert.Null(afterLimited.AppliedConfigRevision);
        Assert.Null(Assert.Single(fixture.Database.List()).LastSeenAt);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM machine_boots WHERE machine_id='pc'"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM machine_config_acks WHERE machine_id='pc'"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM command_outbox WHERE machine_id='pc'"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM pairing_offers WHERE machine_id='pc'"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM leases WHERE machine_id='pc'"));
        Assert.Equal(2L, fixture.Scalar("SELECT challenge_response_count FROM agent_sync_proof_state WHERE machine_id='pc'"));

        var rateWindowStart = fixture.Clock.Timestamp;
        fixture.Clock.Timestamp -= TimeSpan.TicksPerSecond;
        using var tickRollback = await fixture.SendAsync(request, staleSecond, nonce: "rate-during-tick-rollback");
        Assert.Equal(HttpStatusCode.TooManyRequests, tickRollback.StatusCode);
        fixture.Clock.Timestamp = rateWindowStart + TimeSpan.FromSeconds(11).Ticks;
        using var afterWindow = await fixture.SendAsync(request, staleSecond, nonce: "rate-after-window");
        Assert.Equal(HttpStatusCode.PreconditionRequired, afterWindow.StatusCode);

        await fixture.RestartAsync();
        using var recovered = await fixture.SendAsync(request, staleSecond, nonce: "rate-after-restart");
        Assert.Equal(HttpStatusCode.PreconditionRequired, recovered.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await recovered.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge));
        Assert.Null(Assert.Single(fixture.Database.List()).LastSeenAt);
    }

    [Fact]
    public async Task AcceptedRequestReplayAfterRestartGetsChallengeWithoutBodyEffects()
    {
        await using var fixture = await ProofFixture.CreateAsync();
        var second = fixture.Clock.Now.ToUnixTimeSeconds();
        var request = new SyncRequest("replay-boot", "192.0.2.41", "001122334455", 1, "ready", [])
        { SyncProofProtocol = 2, AgentVersion = "2.3.4" };
        using var issued = await fixture.SendAsync(request, second);
        Assert.Equal(HttpStatusCode.PreconditionRequired, issued.StatusCode);
        var token = (await issued.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;
        var acceptedRequest = request with { AgentUpdateChallenge = token };
        var nonce = Guid.NewGuid().ToString("N");
        using var accepted = await fixture.SendAsync(acceptedRequest, second, nonce: nonce);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var beforeRestart = Assert.Single(fixture.Database.List());
        var oldResponseToken = (await accepted.Content.ReadFromJsonAsync<SyncResponse>())!.AgentUpdateChallenge;

        await fixture.RestartAsync();
        using var replay = await fixture.SendAsync(acceptedRequest, second, nonce: nonce);
        Assert.Equal(HttpStatusCode.PreconditionRequired, replay.StatusCode);
        var replacement = (await replay.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;
        Assert.NotEqual(oldResponseToken, replacement);
        var afterReplay = Assert.Single(fixture.Database.List());
        Assert.Equal(beforeRestart.LastSeenAt, afterReplay.LastSeenAt);
        Assert.Equal(beforeRestart.AgentVersion, afterReplay.AgentVersion);
        Assert.Equal(beforeRestart.BootId, afterReplay.BootId);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM machine_boots WHERE machine_id='pc'"));
    }

    [Fact]
    public async Task ProtocolTwoTokenAgeUsesMonotonicTimeAcrossWallClockJumpsAndFailsClosedOnTickRollback()
    {
        await using var fixture = await ProofFixture.CreateAsync();
        var second = fixture.Clock.Now.ToUnixTimeSeconds();
        var request = new SyncRequest("clock-boot", "192.0.2.41", "001122334455", 1, "ready", [])
        { SyncProofProtocol = 2, AgentVersion = "2.3.4" };
        using var challenge = await fixture.SendAsync(request, second);
        Assert.Equal(HttpStatusCode.PreconditionRequired, challenge.StatusCode);
        var token = (await challenge.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;

        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        fixture.Clock.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        using var accepted = await fixture.SendAsync(request with { AgentUpdateChallenge = token }, second);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var acceptedResponse = (await accepted.Content.ReadFromJsonAsync<SyncResponse>())!;
        var acceptedMachine = Assert.Single(fixture.Database.List());

        fixture.Clock.Now = fixture.Clock.Now.AddDays(-2);
        fixture.Clock.Timestamp -= TimeSpan.FromSeconds(2).Ticks;
        using var rollback = await fixture.SendAsync(request with { BootId = "tick-rollback", AgentVersion = "9.9.9", AgentUpdateChallenge = acceptedResponse.AgentUpdateChallenge }, second);
        Assert.Equal(HttpStatusCode.PreconditionRequired, rollback.StatusCode);
        var replacement = (await rollback.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;
        var afterRollback = Assert.Single(fixture.Database.List());
        Assert.Equal(acceptedMachine.AgentVersion, afterRollback.AgentVersion);
        Assert.Equal(acceptedMachine.BootId, afterRollback.BootId);

        fixture.Clock.AdvanceMonotonic(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));
        using var expired = await fixture.SendAsync(request with { BootId = "expired-token", AgentVersion = "8.8.8", AgentUpdateChallenge = replacement }, second);
        Assert.Equal(HttpStatusCode.PreconditionRequired, expired.StatusCode);
        var afterExpiry = Assert.Single(fixture.Database.List());
        Assert.Equal(acceptedMachine.AgentVersion, afterExpiry.AgentVersion);
        Assert.Equal(acceptedMachine.BootId, afterExpiry.BootId);
    }

    [Fact]
    public async Task LegacyHighWaterCannotBeReplayedAcrossAgentClockRollbackAndForwardJump()
    {
        await using var fixture = await ProofFixture.CreateAsync();
        var initialTime = fixture.Clock.Now;
        var initialSecond = initialTime.ToUnixTimeSeconds();
        var first = new SyncRequest("legacy-boot", "192.0.2.40", "001122334455", 1, "ready", []) { AgentVersion = "1.0.0" };
        using var stale = await fixture.SendAsync(first, initialSecond - 61);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        using var future = await fixture.SendAsync(first, initialSecond + 61);
        Assert.Equal(HttpStatusCode.Unauthorized, future.StatusCode);
        Assert.Null(Assert.Single(fixture.Database.List()).LastSeenAt);
        using var accepted = await fixture.SendAsync(first, initialSecond);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var lastSeen = Assert.Single(fixture.Database.List()).LastSeenAt;

        fixture.Clock.Now = initialTime.AddSeconds(-1);
        using var rollback = await fixture.SendAsync(first with { AgentVersion = "9.9.9" }, initialSecond - 1);
        Assert.False(rollback.IsSuccessStatusCode);
        Assert.Equal(lastSeen, Assert.Single(fixture.Database.List()).LastSeenAt);

        fixture.Clock.Now = initialTime.AddSeconds(121);
        using var forward = await fixture.SendAsync(first with { AgentVersion = "2.0.0" }, initialSecond + 120);
        Assert.Equal(HttpStatusCode.OK, forward.StatusCode);
        var afterForward = Assert.Single(fixture.Database.List()).LastSeenAt;

        fixture.Clock.Now = initialTime;
        using var replayAfterRollback = await fixture.SendAsync(first with { AgentVersion = "3.0.0" }, initialSecond + 1);
        Assert.False(replayAfterRollback.IsSuccessStatusCode);
        Assert.Equal(afterForward, Assert.Single(fixture.Database.List()).LastSeenAt);
    }

    [Fact]
    public async Task SyncChallengeIsBoundToMachineAndCannotReuseBootPurposeToken()
    {
        await using var fixture = await ProofFixture.CreateAsync();
        var second = fixture.Clock.Now.ToUnixTimeSeconds();
        using var otherKey = ECDsa.Create();
        fixture.Database.EnrollWithTicket(new("other-pc", Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo()), "192.0.2.43", "001122334466", 1, 2));

        var heartbeat = new SyncRequest("purpose-boot", "192.0.2.41", "001122334455", 1, "ready", [])
        { SyncProofProtocol = 2, AgentVersion = "2.3.4" };
        using var original = await fixture.SendAsync(heartbeat, second);
        Assert.Equal(HttpStatusCode.PreconditionRequired, original.StatusCode);
        var machineToken = (await original.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;

        using var crossMachine = await fixture.SendAsync(heartbeat with { AgentUpdateChallenge = machineToken }, second,
            machineId: "other-pc", signingKey: otherKey);
        Assert.Equal(HttpStatusCode.PreconditionRequired, crossMachine.StatusCode);
        var otherToken = (await crossMachine.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;
        Assert.NotEqual(machineToken, otherToken);
        Assert.Null(fixture.Database.List().Single(machine => machine.MachineId == "other-pc").LastSeenAt);

        var bootChallenge = fixture.Database.Reconcile("pc", new("boot-purpose", 1, false)).Challenge!;
        using var wrongPurpose = await fixture.SendAsync(heartbeat with { AgentUpdateChallenge = bootChallenge.Token }, second);
        Assert.Equal(HttpStatusCode.PreconditionRequired, wrongPurpose.StatusCode);
        Assert.Equal(machineToken, (await wrongPurpose.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge);
        Assert.Null(fixture.Database.List().Single(machine => machine.MachineId == "pc").LastSeenAt);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM machine_boots WHERE machine_id='pc'"));
    }

    private sealed class ProofClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public long Timestamp { get; set; } = TimeSpan.TicksPerSecond * 1000;
        public override DateTimeOffset GetUtcNow() => Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public void AdvanceMonotonic(TimeSpan elapsed) => Timestamp = checked(Timestamp + elapsed.Ticks);
    }

    private sealed class ProofFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly ECDsa key = ECDsa.Create();
        private WebApplication app;
        private HttpClient client;
        public ProofClock Clock { get; }
        public string DatabasePath => Path.Combine(root, "controller.db");
        public ControllerDatabase Database => app.Services.GetRequiredService<ControllerDatabase>();

        public long Scalar(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "controller.db"),
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        public string? ReadText(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "controller.db"),
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar()?.ToString();
        }

        private ProofFixture(string root, ProofClock clock, WebApplication app)
        {
            this.root = root;
            Clock = clock;
            this.app = app;
            client = app.GetTestClient();
            client.BaseAddress = new Uri("https://localhost/");
            client.DefaultRequestHeaders.Add("Test-Local-Port", "5102");
            Database.EnrollWithTicket(new("pc", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "192.0.2.40", "001122334455", 1, 2));
        }

        public static async Task<ProofFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "kr-sync-proof-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var clock = new ProofClock();
            var app = await StartAsync(root, clock);
            return new ProofFixture(root, clock, app);
        }

        private static async Task<WebApplication> StartAsync(string root, ProofClock clock)
        {
            var app = ControllerHost.Build([], builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["InternalUrl"] = "http://127.0.0.1:5101",
                    ["AgentUrl"] = "https://127.0.0.1:5102",
                    ["DatabasePath"] = Path.Combine(root, "controller.db"),
                    ["ServiceCredential"] = "proof-fixture-only",
                });
                builder.WebHost.UseTestServer();
                builder.Services.AddTransient<IStartupFilter, LoopbackTestFilter>();
                builder.Services.AddSingleton<TimeProvider>(clock);
            });
            await app.StartAsync();
            return app;
        }

        public async Task RestartAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            app = await StartAsync(root, Clock);
            client = app.GetTestClient();
            client.BaseAddress = new Uri("https://localhost/");
            client.DefaultRequestHeaders.Add("Test-Local-Port", "5102");
        }

        public async Task<HttpResponseMessage> SendAsync(SyncRequest request, long? second = null, bool badSignature = false,
            string? nonce = null, string machineId = "pc", ECDsa? signingKey = null,
            string? timestampHeader = null, bool omitTimestamp = false)
        {
            using var message = CreateSignedRequest(request, second, badSignature, nonce, machineId, signingKey, timestampHeader, omitTimestamp);
            return await client.SendAsync(message);
        }

        public HttpRequestMessage CreateSignedRequest(SyncRequest request, long? second = null, bool badSignature = false,
            string? nonce = null, string machineId = "pc", ECDsa? signingKey = null,
            string? timestampHeader = null, bool omitTimestamp = false)
        {
            var body = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var at = second ?? Clock.Now.ToUnixTimeSeconds();
            nonce ??= Guid.NewGuid().ToString("N");
            var signed = $"{machineId}\nPOST\n/agent/sync\n{at}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}";
            var message = new HttpRequestMessage(HttpMethod.Post, "/agent/sync") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            message.Headers.Add("X-Agent-Id", machineId);
            if (!omitTimestamp) message.Headers.Add("X-Agent-Timestamp", timestampHeader ?? at.ToString());
            message.Headers.Add("X-Agent-Nonce", nonce);
            using var wrong = ECDsa.Create();
            var signer = badSignature ? wrong : signingKey ?? key;
            message.Headers.Add("X-Agent-Signature", Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes(signed), HashAlgorithmName.SHA256)));
            return message;
        }

        public Task<HttpResponseMessage> SendPreparedAsync(HttpRequestMessage message) => client.SendAsync(message);

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            key.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("awaitingHeartbeat", "2.3.4")]
    [InlineData("rollingBack", "1.2.3")]
    public async Task SignedHeartbeatPreparedBeforeOutcomeCannotConfirmIt(string outcome, string version)
    {
        await using var fixture = await AcceptanceFixture.CreateAsync();
        var ticket = await fixture.PublishAndReserveAsync();
        await fixture.StartAndDownloadAsync(ticket);
        using var delayed = await fixture.PrepareHeartbeatAsync(version);
        await fixture.ReportAsync(ticket, outcome);

        using var response = await fixture.SendPreparedHeartbeatAsync(delayed);
        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        await fixture.AssertLockedAsync(ticket, outcome);
    }
    [Theory]
    [InlineData("awaitingHeartbeat", "2.3.4")]
    [InlineData("rollingBack", "1.2.3")]
    public async Task StartedReleaseRemainsHeldAfterReportedVersionHeartbeat(string outcome, string version)
    {
        await using var fixture = await AcceptanceFixture.CreateAsync();
        var ticket = await fixture.PublishAndReserveAsync();
        await fixture.StartAndDownloadAsync(ticket);
        await fixture.ReportAsync(ticket, outcome);
        await fixture.HeartbeatAsync(version);
        await fixture.AssertLockedAsync(ticket, outcome);
    }

    [Fact]
    public async Task FailedUpdateRemainsHeldAndLegacyRecoveryTicketIsUnavailable()
    {
        await using var fixture = await AcceptanceFixture.CreateAsync();
        var ticket = await fixture.PublishAndReserveAsync();
        await fixture.StartAndDownloadAsync(ticket);
        await fixture.ReportAsync(ticket, "failed");
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(11);
        await fixture.HeartbeatAsync("1.2.3");
        await fixture.AssertLockedAsync(ticket, "failed");
        using var recovery = await fixture.Admin.PostAsync($"/internal/machines/0123456789abcdef0123456789abcdef/agent-updates/{ticket.OperationId}/recovery-ticket", null);
        Assert.Equal(HttpStatusCode.Conflict, recovery.StatusCode);
        await fixture.AssertLockedAsync(ticket, "failed");
    }

    [Fact]
    public async Task MaintenanceHeadChangeRejectsOldSyncChallengeWithoutApplyingHeartbeat()
    {
        await using var fixture = await AcceptanceFixture.CreateAsync();
        var ticket = await fixture.PublishAndReserveAsync();
        var oldToken = fixture.CurrentSyncChallenge;
        Assert.False(string.IsNullOrWhiteSpace(oldToken));
        var before = await fixture.MachineAsync();

        using var delayed = await fixture.PrepareHeartbeatAsync("2.3.4");
        using var response = await fixture.SendPreparedHeartbeatAsync(delayed);
        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        var replacement = (await response.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;
        Assert.NotEqual(oldToken, replacement);

        var after = await fixture.MachineAsync();
        Assert.Equal(before.LastSeenAt, after.LastSeenAt);
        Assert.Equal(before.AgentVersion, after.AgentVersion);
        Assert.Equal(before.BootId, after.BootId);
        await fixture.AssertLockedAsync(ticket, "awaitingInstaller");
    }

    private sealed class AcceptanceClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AcceptanceFixture : IAsyncDisposable
    {
        private const string MachineId = "0123456789abcdef0123456789abcdef";
        private readonly string root;
        private readonly ECDsa key = ECDsa.Create();
        private sealed class TestTrustedIdentitySource(TrustedIdentityDocument document) : ITrustedIdentitySource
        {
            public TrustedIdentityDocument ReadExisting() => document;
        }
        private readonly WebApplication app;
        private readonly HttpClient agent;
        private readonly HttpClient renter;
        private readonly byte[] package = AgentPackageCorpus.Valid();
        private int rentalAttempt;
        private string? syncChallenge;
        public AcceptanceClock Clock { get; }
        public HttpClient Admin { get; }
        public string? CurrentSyncChallenge => syncChallenge;

        private AcceptanceFixture(string root, AcceptanceClock clock, WebApplication app)
        {
            this.root = root;
            Clock = clock;
            this.app = app;
            Admin = app.GetTestClient();
            Admin.DefaultRequestHeaders.Add("X-Service-Credential", "acceptance-fixture-only");
            Admin.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "acceptance-admin");
            Admin.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role", "admin");
            renter = app.GetTestClient();
            renter.DefaultRequestHeaders.Add("X-Service-Credential", "acceptance-fixture-only");
            renter.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "acceptance-renter");
            agent = app.GetTestClient();
            agent.BaseAddress = new Uri("https://localhost/");
            agent.DefaultRequestHeaders.Add("Test-Local-Port", "5102");
        }

        public static async Task<AcceptanceFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "kr-release-acceptance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var clock = new AcceptanceClock();
            var app = ControllerHost.Build([], builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["InternalUrl"] = "http://127.0.0.1:5101",
                    ["AgentUrl"] = "https://127.0.0.1:5102",
                    ["DatabasePath"] = Path.Combine(root, "controller.db"),
                    ["ServiceCredential"] = "acceptance-fixture-only",
                    ["PublicIpEndpoint"] = "",
                    ["AgentReleaseRemediationEnabled"] = "true",
                });
                builder.WebHost.UseTestServer();
                builder.Services.AddTransient<IStartupFilter, LoopbackTestFilter>();
                builder.Services.AddSingleton<TimeProvider>(clock);
            });
            await app.StartAsync();
            return new AcceptanceFixture(root, clock, app);
        }

        public async Task<AgentUpdateTicketResponse> PublishAndReserveAsync()
        {
            var databasePath = Path.Combine(root, "controller.db");
            var hash = Convert.ToHexString(SHA256.HashData(package));
            var artifact = Path.Combine(root, "agent-releases", "sha256", hash[..2].ToLowerInvariant(), hash.ToLowerInvariant() + ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            await File.WriteAllBytesAsync(artifact, package);
            var database = app.Services.GetRequiredService<ControllerDatabase>();
            var release = database.PublishAgentRelease(new("2.3.4", "stable", "fixture", hash, artifact, package.Length,
                "0123456789ab", false, true, "win-x64", 2));

            using var enrollmentResponse = await Admin.PostAsync("/internal/agent-enrollment-tickets", null);
            Assert.Equal(HttpStatusCode.OK, enrollmentResponse.StatusCode);
            var enrollment = Assert.IsType<EnrollmentTicketResponse>(
                await enrollmentResponse.Content.ReadFromJsonAsync<EnrollmentTicketResponse>());
            using var enrolled = await SendSignedAsync("/agent/enroll", new EnrollmentRequest(MachineId,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "192.0.2.40", "001122334455", 47989, 47990), enrollment.Ticket);
            Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
            await HeartbeatAsync("1.2.3");
            Assert.Equal("pending", (await MachineAsync()).ApprovalStatus);
            await AssertRentalRejectedAsync();
            using var pendingUpdate = await Admin.PostAsJsonAsync($"/internal/machines/{MachineId}/agent-updates", new AgentUpdateReservationRequest(release.Version));
            Assert.Equal(HttpStatusCode.OK, pendingUpdate.StatusCode);
            Assert.Equal("pending", (await MachineAsync()).ApprovalStatus);
            await AssertRentalRejectedAsync();
            Clock.Now = Clock.Now.AddMinutes(10);
            await HeartbeatAsync("1.2.3");
            Assert.Equal("expired", (await MachineAsync()).AgentUpdateStatus);

            await ConfigureAsync();
            using var approved = await Admin.PostAsJsonAsync($"/internal/machines/{MachineId}/approve",
                new MachineIdentityConfirmation(Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))));
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            // Approval increments configuration; even ready on the old ACK cannot rent.
            await AssertRentalRejectedAsync();
            await HeartbeatAsync("1.2.3");
            var ready = await MachineAsync();
            Assert.True(ready.CanRent);
            Assert.False(ready.AllocationHeld);

            using var reserved = await Admin.PostAsJsonAsync($"/internal/machines/{MachineId}/agent-updates", new AgentUpdateReservationRequest(release.Version));
            Assert.Equal(HttpStatusCode.OK, reserved.StatusCode);
            var ticket = Assert.IsType<AgentUpdateTicketResponse>(await reserved.Content.ReadFromJsonAsync<AgentUpdateTicketResponse>());
            Assert.Equal(MachineId, ticket.MachineId);
            Assert.Equal("2.3.4", ticket.ReleaseVersion);
            Assert.Equal("2.3.4", ticket.TargetVersion);
            Assert.Equal(release.PackageSha256, ticket.TargetPackageSha256);
            await AssertLockedAsync(ticket, "awaitingInstaller");
            return ticket;
        }

        public async Task<AgentUpdateOperationDto> StartAndDownloadAsync(AgentUpdateTicketResponse ticket)
        {
            using var started = await agent.PostAsJsonAsync(UpdatePath(ticket, "start"),
                new AgentUpdateTicketRequest(ticket.Ticket, ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256,
                    "start-" + ticket.OperationId));
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
            var operation = Assert.IsType<AgentUpdateOperationDto>(await started.Content.ReadFromJsonAsync<AgentUpdateOperationDto>());
            Assert.Null(operation.SourceVersion);
            await AssertLockedAsync(ticket, "installing");
            using var request = new HttpRequestMessage(HttpMethod.Get, UpdatePath(ticket, "package"));
            request.Headers.Add("X-Agent-Update-Ticket", ticket.Ticket);
            request.Headers.Add("X-Agent-Update-Release-Version", ticket.ReleaseVersion);
            request.Headers.Add("X-Agent-Update-Target-Version", ticket.TargetVersion);
            request.Headers.Add("X-Agent-Update-Package-SHA256", ticket.TargetPackageSha256);
            using var download = await agent.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("no-store", download.Headers.CacheControl?.ToString());
            Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
            Assert.Equal(package, await download.Content.ReadAsByteArrayAsync());
            return operation;
        }

        public AgentUpdateSourceBindingRequest CreateSignedSourceBinding(AgentUpdateTicketResponse ticket,
            AgentUpdateOperationDto operation, AgentUpdateSourceChallengeRequest challengeRequest,
            AgentUpdateSourceChallengeResponse challenge, string requestId, string bootId)
        {
            var request = new AgentUpdateSourceBindingRequest(ticket.Ticket, ticket.ReleaseVersion, ticket.TargetVersion,
                ticket.TargetPackageSha256, challenge.ChallengeId, challenge.Challenge, challengeRequest.RequestId,
                requestId, challengeRequest.HelperBuildSha256, challengeRequest.HelperRuntime, challengeRequest.HelperProtocol,
                challenge.RuntimeEpoch, "1.2.3", ticket.OperationId, new string('b', 64), new string('c', 64),
                new string('d', 64), bootId, "backupPublished", operation.ChainId, operation.CurrentHeadOperationId,
                operation.Generation);
            using var identity = TrustedIdentity.ReadExisting(new TestTrustedIdentitySource(new(MachineId,
                Convert.ToBase64String(key.ExportPkcs8PrivateKey()))));
            var evidence = new AgentUpdateSourceEvidence(MachineId,
                identity.KeyFingerprint, operation.OperationId, operation.ChainId,
                operation.CurrentHeadOperationId, operation.Generation, ticket.TargetVersion, ticket.TargetPackageSha256,
                challenge.ChallengeId, challengeRequest.RequestId, requestId, challengeRequest.HelperBuildSha256,
                challengeRequest.HelperRuntime, challengeRequest.HelperProtocol, challenge.RuntimeEpoch,
                request.SourceVersion, request.BackupOwnerOperationId, request.BackupDigest, request.InventoryDigest,
                request.InstalledTreeDigest, request.NativeBootId, request.JournalPhase);
            return request with
            {
                EvidenceSignature = identity.SignSourceEvidence(evidence),
            };
        }

        public async Task ConfigureAsync()
        {
            var machine = await MachineAsync();
            using var response = await Admin.PatchAsJsonAsync($"/internal/machines/{MachineId}?expectedRevision={machine.ConfigRevision}",
                new MachineUpdate("Acceptance PC", 500, 47989, 47990));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task HeartbeatAsync(string version, string readiness = "ready", long? appliedRevision = null)
        {
            var request = new SyncRequest("acceptance-boot", "192.0.2.40", "001122334455",
                appliedRevision ?? (await MachineAsync()).ConfigRevision, readiness, [])
            { AgentVersion = version, SyncProofProtocol = 2, AgentUpdateChallenge = syncChallenge };
            using var response = await SendSignedAsync("/agent/sync", request);
            SyncResponse? result;
            if (response.StatusCode == HttpStatusCode.PreconditionRequired)
            {
                var challenge = (await response.Content.ReadFromJsonAsync<SyncChallengeRequired>())?.AgentUpdateChallenge;
                Assert.False(string.IsNullOrWhiteSpace(challenge));
                syncChallenge = challenge;
                using var confirmation = await SendSignedAsync("/agent/sync", request with { AgentUpdateChallenge = challenge });
                Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
                result = await confirmation.Content.ReadFromJsonAsync<SyncResponse>();
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                result = await response.Content.ReadFromJsonAsync<SyncResponse>();
            }
            syncChallenge = result?.AgentUpdateChallenge;
        }

        public async Task<HttpRequestMessage> PrepareHeartbeatAsync(string version)
        {
            var body = new SyncRequest("acceptance-boot", "192.0.2.40", "001122334455", (await MachineAsync()).ConfigRevision, "ready", [])
            { AgentVersion = version, SyncProofProtocol = 2, AgentUpdateChallenge = syncChallenge };
            return PrepareSigned("/agent/sync", body);
        }

        public Task<HttpResponseMessage> SendPreparedHeartbeatAsync(HttpRequestMessage request) => agent.SendAsync(request);

        public async Task<HttpResponseMessage> SendSignedAsync<T>(string path, T body, string? enrollmentTicket = null,
            bool badSignature = false, string? nonce = null, long? signedSecond = null)
        {
            using var request = PrepareSigned(path, body, enrollmentTicket, badSignature, nonce, signedSecond);
            return await agent.SendAsync(request);
        }

        private HttpRequestMessage PrepareSigned<T>(string path, T body, string? enrollmentTicket = null, bool badSignature = false,
            string? nonce = null, long? signedSecond = null)
        {
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var timestamp = signedSecond ?? Clock.Now.ToUnixTimeSeconds();
            nonce ??= Guid.NewGuid().ToString("N");
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Agent-Id", MachineId);
            request.Headers.Add("X-Agent-Timestamp", timestamp.ToString());
            request.Headers.Add("X-Agent-Nonce", nonce);
            var signature = Convert.ToBase64String(key.SignData(
                Encoding.UTF8.GetBytes($"{MachineId}\nPOST\n{path}\n{timestamp}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))}"), HashAlgorithmName.SHA256));
            request.Headers.Add("X-Agent-Signature", badSignature ? "not-a-signature" : signature);
            if (enrollmentTicket is not null) request.Headers.Add("X-Enrollment-Ticket", enrollmentTicket);
            return request;
        }

        public async Task ReportAsync(AgentUpdateTicketResponse ticket, string status)
        {
            using var response = await SendReportAsync(ticket, status);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public Task<HttpResponseMessage> SendReportAsync(AgentUpdateTicketResponse ticket, string status) =>
            agent.PostAsJsonAsync(UpdatePath(ticket, "status"), new AgentUpdateStatusReportRequest(
                ticket.Ticket, ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, status));

        public async Task<MachineDto> MachineAsync() => Assert.Single((await Admin.GetFromJsonAsync<MachineDto[]>("/internal/machines"))!);

        public async Task AssertLockedAsync(AgentUpdateTicketResponse ticket, string status)
        {
            var machine = await MachineAsync();
            Assert.Equal(ticket.OperationId, machine.AgentUpdateOperationId);
            Assert.Equal(status, machine.AgentUpdateStatus);
            Assert.False(machine.AllocationHeld);
            await AssertRentalRejectedAsync();
        }

        private Task<HttpResponseMessage> CreateRentalAsync() => renter.PostAsJsonAsync("/internal/trial-leases",
            new TrialLeaseRequest("acceptance-renter", MachineId, 60, "acceptance-rental-" + ++rentalAttempt));

        public async Task AssertRentalRejectedAsync()
        {
            Assert.False((await MachineAsync()).CanRent);
            using var rejected = await CreateRentalAsync();
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Empty((await renter.GetFromJsonAsync<LeaseDto[]>("/internal/users/acceptance-renter/leases"))!);
        }

        public async Task AssertRentalCreatedAsync()
        {
            Assert.True((await MachineAsync()).CanRent);
            using var created = await CreateRentalAsync();
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            Assert.Equal("preparing", (await created.Content.ReadFromJsonAsync<LeaseDto>())?.State);
            Assert.True((await MachineAsync()).AllocationHeld);
            Assert.False((await MachineAsync()).CanRent);
        }

        public static string UpdatePath(AgentUpdateTicketResponse ticket, string action) =>
            $"/agent/machines/{MachineId}/agent-updates/{ticket.OperationId}/{action}";

        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
            renter.Dispose();
            agent.Dispose();
            key.Dispose();
            await app.DisposeAsync();
            var resolved = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("kr-release-acceptance-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe acceptance fixture cleanup path");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
