using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Agents;
using KamenRaider.Controller.Machines;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Controller.Tests.Fixtures;

namespace Controller.Tests;

public sealed class AgentUpdateTests
{
    [Fact]
    public void ReservationIsDisabledByDefault()
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");

        Assert.Throws<InvalidOperationException>(() => fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", Guid.NewGuid().ToString("N")));
        Assert.True(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public void RetainedProtocolTwoPackageCanBeReservedWithoutAReportedSourceVersion()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        var bytes = AgentPackageCorpus.Valid();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        AddRelease(fixture.Path, AgentPackageCorpus.Version, hash, bytes.Length);
        var path = ControllerReleasePath(fixture.Path, hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);

        var error = Record.Exception(() => fixture.Db.ReserveAgentUpdate("pc", AgentPackageCorpus.Version, "admin", Guid.NewGuid().ToString("N")));

        Assert.Null(error);
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public void StartAckCanBeRecoveredByTheSameRequestAndDifferentBodyConflicts()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "start-idempotent");
        const string requestId = "start-request-1";

        var started = fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, requestId);
        fixture.Reopen();
        var retried = fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, requestId);

        Assert.Equal(started, retried);
        Assert.Equal("installing", retried.Status);
        Assert.Throws<InvalidOperationException>(() => fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "different-start-request"));
    }

    [Fact]
    public void UnstartedTicketCannotSurviveControllerRestartOrMonotonicLifetimeAfterClockRollback()
    {
        using (var restarted = new LeaseFixture(agentReleaseRemediationEnabled: true))
        {
            AddRelease(restarted.Path, "2.0.0");
            var ticket = restarted.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "ticket-runtime-restart");
            restarted.Reopen();
            Assert.Throws<UnauthorizedAccessException>(() => restarted.Db.StartAgentUpdate("pc", ticket.OperationId,
                ticket.Ticket, ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "start-after-restart"));
        }

        using (var rolledBack = new LeaseFixture(agentReleaseRemediationEnabled: true))
        {
            AddRelease(rolledBack.Path, "2.0.0");
            var issuedAt = rolledBack.Time.Now;
            var ticket = rolledBack.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "ticket-runtime-rollback");
            rolledBack.Time.Now = issuedAt.AddMinutes(11);
            rolledBack.Time.Now = issuedAt.AddMinutes(1);
            Assert.Throws<UnauthorizedAccessException>(() => rolledBack.Db.StartAgentUpdate("pc", ticket.OperationId,
                ticket.Ticket, ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "start-after-rollback"));
        }
    }

    [Fact]
    public void SourceChallengeAndBindingAreSignedBoundAndExactRetryIsIdempotent()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "source-binding");
        var started = fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "start-source");
        var challengePath = $"/agent/machines/pc/agent-updates/{ticket.OperationId}/source-challenge";
        var challengeRequest = new AgentUpdateSourceChallengeRequest(
            ticket.Ticket, ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256,
            "source-challenge-request", new string('a', 64), "win-x64", 1);
        var challengeEnvelope = VerifiedAgentRequestFixture.Create(fixture.Db, "pc", challengePath, challengeRequest);

        var challenge = fixture.Db.IssueAgentUpdateSourceChallenge(challengeEnvelope, ticket.OperationId, challengeRequest);
        var repeatedChallenge = fixture.Db.IssueAgentUpdateSourceChallenge(
            VerifiedAgentRequestFixture.Create(fixture.Db, "pc", challengePath, challengeRequest), ticket.OperationId, challengeRequest);
        var bindingPath = $"/agent/machines/pc/agent-updates/{ticket.OperationId}/source-binding";
        var bindingRequest = new AgentUpdateSourceBindingRequest(
            ticket.Ticket, ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256,
            challenge.ChallengeId, challenge.Challenge, challengeRequest.RequestId, "source-binding-request",
            challengeRequest.HelperBuildSha256, challengeRequest.HelperRuntime, challengeRequest.HelperProtocol,
            challenge.RuntimeEpoch, "1.0.0", ticket.OperationId, new string('b', 64), new string('c', 64),
            new string('d', 64), "boot-1", "backupPublished", started.ChainId, started.CurrentHeadOperationId, started.Generation);
        var publicKey = Convert.FromBase64String(fixture.Db.PublicKey("pc")!);
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(publicKey));
        var evidence = new AgentUpdateSourceEvidence("pc", fingerprint, ticket.OperationId, started.ChainId,
            started.CurrentHeadOperationId, started.Generation, ticket.TargetVersion, ticket.TargetPackageSha256,
            challenge.ChallengeId, challengeRequest.RequestId, bindingRequest.RequestId, challengeRequest.HelperBuildSha256,
            challengeRequest.HelperRuntime, challengeRequest.HelperProtocol, challenge.RuntimeEpoch, bindingRequest.SourceVersion,
            bindingRequest.BackupOwnerOperationId, bindingRequest.BackupDigest, bindingRequest.InventoryDigest,
            bindingRequest.InstalledTreeDigest, bindingRequest.NativeBootId, bindingRequest.JournalPhase);
        bindingRequest = bindingRequest with
        {
            EvidenceSignature = Convert.ToBase64String(fixture.AgentKey.SignData(
                AgentUpdateSourceEvidenceCanonicalizer.CanonicalBytes(evidence), System.Security.Cryptography.HashAlgorithmName.SHA256)),
        };
        var bindingEnvelope = VerifiedAgentRequestFixture.Create(fixture.Db, "pc", bindingPath, bindingRequest);
        var badEvidence = bindingRequest with { EvidenceSignature = Convert.ToBase64String(new byte[64]) };
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Db.BindAgentUpdateSource(
            VerifiedAgentRequestFixture.Create(fixture.Db, "pc", bindingPath, badEvidence), ticket.OperationId, badEvidence));

        _ = fixture.Db.BindAgentUpdateSource(bindingEnvelope, ticket.OperationId, bindingRequest);
        // The Controller may have committed this exact body before its response was lost.
        fixture.Reopen();
        var retried = fixture.Db.BindAgentUpdateSource(
            VerifiedAgentRequestFixture.Create(fixture.Db, "pc", bindingPath, bindingRequest), ticket.OperationId, bindingRequest);

        Assert.Equal(challenge, repeatedChallenge);
        Assert.Equal("1.0.0", retried.SourceVersion);
        Assert.Equal(new string('B', 64), retried.BackupDigest);
        var changed = bindingRequest with { NativeBootId = "boot-2" };
        Assert.Throws<InvalidOperationException>(() => fixture.Db.BindAgentUpdateSource(
            VerifiedAgentRequestFixture.Create(fixture.Db, "pc", bindingPath, changed), ticket.OperationId, changed));
    }

    [Fact]
    public void SourceChallengeIssuanceIsBoundedPerHeadAndGeneration()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "source-challenge-limit");
        fixture.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
            ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "start-source-limit");
        var path = $"/agent/machines/pc/agent-updates/{ticket.OperationId}/source-challenge";

        for (var index = 0; index < 5; index++)
        {
            var hex = index.ToString("x");
            var request = new AgentUpdateSourceChallengeRequest(ticket.Ticket, ticket.ReleaseVersion,
                ticket.TargetVersion, ticket.TargetPackageSha256, "source-limit-" + index,
                new string(hex[0], 64), "win-x64", 1);
            var response = fixture.Db.IssueAgentUpdateSourceChallenge(
                VerifiedAgentRequestFixture.Create(fixture.Db, "pc", path, request), ticket.OperationId, request);
            Assert.Equal(fixture.Db.IssueAgentUpdateSourceChallenge(
                VerifiedAgentRequestFixture.Create(fixture.Db, "pc", path, request), ticket.OperationId, request), response);
        }

        var sixth = new AgentUpdateSourceChallengeRequest(ticket.Ticket, ticket.ReleaseVersion,
            ticket.TargetVersion, ticket.TargetPackageSha256, "source-limit-5", new string('f', 64), "win-x64", 1);
        Assert.Throws<InvalidOperationException>(() => fixture.Db.IssueAgentUpdateSourceChallenge(
            VerifiedAgentRequestFixture.Create(fixture.Db, "pc", path, sixth), ticket.OperationId, sixth));
    }

    [Fact]
    public void SourceChallengeExpiresMonotonicallyAndOldRuntimeRowsDoNotConsumeNewRuntimeLimit()
    {
        using (var rolledBack = new LeaseFixture(agentReleaseRemediationEnabled: true))
        {
            AddRelease(rolledBack.Path, "2.0.0");
            var ticket = rolledBack.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "source-clock-rollback");
            rolledBack.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
                ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "start-source-clock");
            var path = $"/agent/machines/pc/agent-updates/{ticket.OperationId}/source-challenge";
            var request = new AgentUpdateSourceChallengeRequest(ticket.Ticket, ticket.ReleaseVersion,
                ticket.TargetVersion, ticket.TargetPackageSha256, "source-clock-request", new string('a', 64), "win-x64", 1);
            _ = rolledBack.Db.IssueAgentUpdateSourceChallenge(
                VerifiedAgentRequestFixture.Create(rolledBack.Db, "pc", path, request), ticket.OperationId, request);
            var issuedAt = rolledBack.Time.Now;
            rolledBack.Time.Now = issuedAt.AddMinutes(11);
            rolledBack.Time.Now = issuedAt.AddMinutes(1);
            Assert.Throws<AgentUpdateSourceChallengeRenewalRequiredException>(() => rolledBack.Db.IssueAgentUpdateSourceChallenge(
                VerifiedAgentRequestFixture.Create(rolledBack.Db, "pc", path, request), ticket.OperationId, request));
        }

        using (var restarted = new LeaseFixture(agentReleaseRemediationEnabled: true))
        {
            AddRelease(restarted.Path, "2.0.0");
            var ticket = restarted.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "source-runtime-restart");
            restarted.Db.StartAgentUpdate("pc", ticket.OperationId, ticket.Ticket,
                ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "start-source-restart");
            var path = $"/agent/machines/pc/agent-updates/{ticket.OperationId}/source-challenge";
            var request = new AgentUpdateSourceChallengeRequest(ticket.Ticket, ticket.ReleaseVersion,
                ticket.TargetVersion, ticket.TargetPackageSha256, "source-runtime-old", new string('b', 64), "win-x64", 1);
            var oldChallenge = restarted.Db.IssueAgentUpdateSourceChallenge(
                VerifiedAgentRequestFixture.Create(restarted.Db, "pc", path, request), ticket.OperationId, request);
            restarted.Reopen();
            Assert.Throws<AgentUpdateSourceChallengeRenewalRequiredException>(() => restarted.Db.IssueAgentUpdateSourceChallenge(
                VerifiedAgentRequestFixture.Create(restarted.Db, "pc", path, request), ticket.OperationId, request));

            var newRequest = request with { RequestId = "source-runtime-new" };
            var newChallenge = restarted.Db.IssueAgentUpdateSourceChallenge(
                VerifiedAgentRequestFixture.Create(restarted.Db, "pc", path, newRequest), ticket.OperationId, newRequest);
            Assert.NotEqual(oldChallenge.ChallengeId, newChallenge.ChallengeId);
            Assert.NotEqual(oldChallenge.RuntimeEpoch, newChallenge.RuntimeEpoch);
        }
    }

    [Theory]
    [InlineData("start")]
    [InlineData("download")]
    [InlineData("report")]
    public void GenerationAdvanceRevokesEveryTicketPurpose(string action)
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "generation-test");
        if (action != "start") fixture.Db.StartAgentUpdate("pc", update.Ticket);
        using (var connection = Open(fixture.Path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE agent_maintenance_chains SET generation=generation+1 WHERE current_head_operation_id=$id";
            command.Parameters.AddWithValue("$id", update.OperationId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        if (action == "start") Assert.Throws<UnauthorizedAccessException>(() => fixture.Db.StartAgentUpdate("pc", update.Ticket));
        else if (action == "download") Assert.Throws<UnauthorizedAccessException>(() => fixture.Db.ConsumeAgentUpdatePackageDownload("pc", update.OperationId, update.Ticket, update.ReleaseVersion, update.TargetVersion, update.TargetPackageSha256));
        else Assert.Throws<UnauthorizedAccessException>(() => fixture.Db.ReportAgentUpdateStatus("pc", update.Ticket, "failed"));
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public void ChainCannotMoveItsHeadToAnUnrelatedOperation()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "head-guard");
        using var connection = Open(fixture.Path);
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO agent_update_operations(operation_id,machine_id,chain_id,release_version,target_version,target_package_sha256,target_runtime,target_protocol,status,ticket_hash,ticket_generation,ticket_purpose,ticket_expires_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) SELECT 'unrelated',machine_id,chain_id,release_version,target_version,target_package_sha256,target_runtime,target_protocol,'awaitingInstaller','other-ticket',2,'apply',ticket_expires_at_unix_ms,created_at_unix_ms,updated_at_unix_ms FROM agent_update_operations WHERE operation_id=$id";
        command.Parameters.AddWithValue("$id", update.OperationId);
        Assert.Equal(1, command.ExecuteNonQuery());
        command.CommandText = "UPDATE agent_maintenance_chains SET current_head_operation_id='unrelated',generation=2 WHERE current_head_operation_id=$id";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void SuccessorHandoffKeepsHoldVisibleAndRevokesOldHeadTicket()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "trusted-admin", "handoff-request");
        var started = fixture.Db.StartAgentUpdate("pc", ticket.Ticket);
        Assert.Equal(started.OperationId, started.CurrentHeadOperationId);
        Assert.Equal(1, started.Generation);
        Assert.Equal("win-x64", started.TargetRuntime);
        Assert.Equal(2, started.TargetProtocol);
        Assert.Null(started.SourceVersion);
        Assert.Null(started.BackupDigest);
        Assert.Null(started.LegacyReportedPreviousVersion);

        using (var connection = Open(fixture.Path))
        using (var tx = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.Parameters.AddWithValue("$id", ticket.OperationId);
            command.CommandText = "INSERT INTO agent_update_operations(operation_id,machine_id,chain_id,parent_operation_id,release_version,target_version,target_package_sha256,target_runtime,target_protocol,status,ticket_hash,ticket_generation,ticket_purpose,ticket_expires_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) SELECT 'successor',machine_id,chain_id,operation_id,release_version,target_version,target_package_sha256,target_runtime,target_protocol,'awaitingInstaller','successor-ticket',2,'apply',ticket_expires_at_unix_ms,created_at_unix_ms,updated_at_unix_ms FROM agent_update_operations WHERE operation_id=$id; UPDATE agent_maintenance_chains SET current_head_operation_id='successor',generation=2 WHERE current_head_operation_id=$id; UPDATE agent_update_operations SET status='superseded' WHERE operation_id=$id";
            command.ExecuteNonQuery();
            using (var observer = Open(fixture.Path))
            using (var read = observer.CreateCommand())
            {
                read.CommandText = "SELECT current_head_operation_id || ':' || held FROM agent_maintenance_chains WHERE held=1";
                Assert.Equal(ticket.OperationId + ":1", read.ExecuteScalar());
            }
            tx.Commit();
        }

        Assert.Equal("successor", Assert.Single(fixture.Db.List()).AgentUpdateOperationId);
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Db.ConsumeAgentUpdatePackageDownload("pc", ticket.OperationId, ticket.Ticket, ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Db.ReportAgentUpdateStatus("pc", ticket.OperationId, ticket.Ticket, ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "failed"));
        using var auditConnection = Open(fixture.Path);
        using var audit = auditConnection.CreateCommand();
        audit.CommandText = "SELECT actor_id || ':' || request_id FROM agent_update_audit";
        Assert.Equal("trusted-admin:handoff-request", audit.ExecuteScalar());
    }

    [Fact]
    public void SourceEvidenceRequiresARealSha256Digest()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "source-digest");
        using var connection = Open(fixture.Path);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_update_operations SET source_version='1.0.0',source_evidence_digest='not-a-sha256' WHERE operation_id=$id";
        command.Parameters.AddWithValue("$id", ticket.OperationId);
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void ExpiredChainCannotBeReopened()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "reopen-chain");
        fixture.Time.Now = fixture.Time.Now.AddMinutes(10);
        fixture.Db.List();
        using var connection = Open(fixture.Path);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_maintenance_chains SET held=1 WHERE current_head_operation_id=$id";
        command.Parameters.AddWithValue("$id", ticket.OperationId);
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void StartedWorkCannotBeMadeExpirableByClearingItsStartTime()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "start-immutable");
        fixture.Db.StartAgentUpdate("pc", ticket.Ticket);
        using var connection = Open(fixture.Path);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_update_operations SET started_at_unix_ms=NULL WHERE operation_id=$id";
        command.Parameters.AddWithValue("$id", ticket.OperationId);
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void SupersededHistoryCannotBeInsertedWithoutAHandoff()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "superseded-history");
        using var connection = Open(fixture.Path);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO agent_update_operations(operation_id,machine_id,chain_id,release_version,target_version,target_package_sha256,target_runtime,target_protocol,status,ticket_hash,ticket_generation,ticket_purpose,ticket_expires_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) SELECT 'fake-history',machine_id,chain_id,release_version,target_version,target_package_sha256,target_runtime,target_protocol,'superseded','unused-ticket',1,'apply',ticket_expires_at_unix_ms,created_at_unix_ms,updated_at_unix_ms FROM agent_update_operations WHERE operation_id=$id";
        command.Parameters.AddWithValue("$id", ticket.OperationId);
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void BoundProvenanceAndAuditCannotBeChanged()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var ticket = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "immutable");
        using var connection = Open(fixture.Path);
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$id", ticket.OperationId);
        command.Parameters.AddWithValue("$digest", new string('a', 64));
        command.CommandText = "UPDATE agent_update_operations SET source_version='1.0.0',source_evidence_digest=$digest,backup_owner_operation_id=$id,backup_digest=$digest WHERE operation_id=$id";
        Assert.Equal(1, command.ExecuteNonQuery());
        command.CommandText = "UPDATE agent_update_operations SET source_version='other' WHERE operation_id=$id";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        command.CommandText = "UPDATE agent_update_operations SET backup_digest=upper(backup_digest) WHERE operation_id=$id";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        command.CommandText = "UPDATE agent_update_operations SET target_version='other' WHERE operation_id=$id";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        command.CommandText = "DELETE FROM agent_update_audit WHERE operation_id=$id";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingHashDoesNotMakeInvalidProtocolOrCrcEligible(bool badCrc)
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        var bytes = badCrc ? AgentPackageCorpus.CrcTamper() : AgentPackageCorpus.Replace("AgentPackage.manifest.json", AgentPackageCorpus.Manifest.Replace("\"agentUpdateChallengeProtocol\":2", "\"agentUpdateChallengeProtocol\":1"));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        AddRelease(fixture.Path, AgentPackageCorpus.Version, hash, bytes.Length);
        var path = ControllerReleasePath(fixture.Path, hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        var unavailable = Assert.Throws<AgentReleaseUnavailableException>(() => fixture.Db.ReserveAgentUpdate("pc", AgentPackageCorpus.Version, "admin", "bad-package"));
        Assert.Equal(AgentReleaseUnavailableKind.InvalidArchive, unavailable.Kind);
        Assert.True(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public async Task ReservationAuditUsesAuthenticatedActorAndExplicitRequestId()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        await using var host = await StartHost(fixture.Db);
        using var response = await host.Client.PostAsJsonAsync("/internal/machines/pc/agent-updates", new { releaseVersion = "2.0.0", requestId = "explicit-request", actorId = "forged" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var connection = Open(fixture.Path);
        using var audit = connection.CreateCommand();
        audit.CommandText = "SELECT actor_id || ':' || request_id FROM agent_update_audit";
        Assert.Equal("admin:explicit-request", audit.ExecuteScalar());
    }

    [Theory]
    [InlineData("awaitingHeartbeat", "2.0.0")]
    [InlineData("rollingBack", "1.0.0")]
    public void LegacyHeartbeatIsRejectedAfterProofModeAndCannotConfirmAnOutcome(string status, string version)
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "legacy-proof");
        fixture.Db.StartAgentUpdate("pc", update.Ticket);
        fixture.Db.ReportAgentUpdateStatus("pc", update.Ticket, status);
        var request = new SyncRequest("boot", "192.0.2.1", "001122334455", 3, "ready", []) { AgentVersion = version, AgentUpdateChallenge = "old-proof" };
        var before = Assert.Single(fixture.Db.List());
        var response = fixture.Db.SyncLegacy("pc", request);
        Assert.Equal(409, response.StatusCode);
        Assert.Null(response.Response);
        Assert.Equal(status, OperationStatus(fixture.Path, update.OperationId));
        var after = Assert.Single(fixture.Db.List());
        Assert.False(after.CanRent);
        Assert.Equal(before.LastSeenAt, after.LastSeenAt);
        Assert.Equal(before.BootId, after.BootId);
        Assert.Equal(before.AgentVersion, after.AgentVersion);
        Assert.Equal(before.Readiness, after.Readiness);
        Assert.Equal(before.LocalIp, after.LocalIp);
        Assert.Equal(before.AppliedConfigRevision, after.AppliedConfigRevision);
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM command_outbox WHERE machine_id='pc'"));
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM pairing_offers WHERE machine_id='pc'"));
    }

    [Fact]
    public void ADelayedHeartbeatWithoutPostOutcomeProofCannotResolveAnUpdate()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", Guid.NewGuid().ToString("N"));
        fixture.Db.StartAgentUpdate("pc", update.Ticket);
        var delayed = new SyncRequest("boot", "192.0.2.1", "001122334455", 3, "ready", []) { AgentVersion = "1.0.0" };
        fixture.Db.ReportAgentUpdateStatus("pc", update.Ticket, "rollingBack");

        fixture.Db.Sync("pc", delayed);

        Assert.Equal("rollingBack", OperationStatus(fixture.Path, update.OperationId));
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public void RestartCannotReleaseAStartedChain()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "restart");
        fixture.Db.StartAgentUpdate("pc", update.Ticket);
        fixture.Db.ReportAgentUpdateStatus("pc", update.Ticket, "rollingBack");
        fixture.Time.Now = fixture.Time.Now.AddHours(1);
        fixture.Reopen();
        fixture.SyncVersion("1.0.0");
        Assert.Equal("rollingBack", OperationStatus(fixture.Path, update.OperationId));
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public void UnversionedHeartbeatDoesNotPreventProvisionalReservation()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        fixture.Sync();
        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", "unversioned");
        Assert.Equal(update.OperationId, Assert.Single(fixture.Db.List()).AgentUpdateOperationId);
    }

    [Fact]
    public async Task PendingMachineMayBeUpdatedButStillCannotRent()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        using (var connection = Open(fixture.Path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE machines SET approval_status='pending' WHERE machine_id='pc'";
            command.ExecuteNonQuery();
        }
        fixture.SyncVersion("1.0.0");

        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", Guid.NewGuid().ToString("N"));

        var machine = Assert.Single(fixture.Db.List());
        Assert.Equal("pending", machine.ApprovalStatus);
        Assert.Equal("ready", machine.Readiness);
        Assert.Equal(update.OperationId, machine.AgentUpdateOperationId);
        Assert.False(machine.CanRent);
        Assert.Throws<InvalidOperationException>(() => fixture.Db.RejectPending("pc", machine.PublicKeyFingerprint!));
        Assert.Equal("pending", Assert.Single(fixture.Db.List()).ApprovalStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Create());
    }

    [Fact]
    public void MissingReleaseArtifactCannotAcquireAnUpdateLock()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0", missing: true);
        fixture.SyncVersion("1.0.0");

        var unavailable = Assert.Throws<AgentReleaseUnavailableException>(() => fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", Guid.NewGuid().ToString("N")));
        Assert.Equal(AgentReleaseUnavailableKind.StorageUnavailable, unavailable.Kind);
        Assert.Null(Assert.Single(fixture.Db.List()).AgentUpdateOperationId);
        Assert.True(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void LegacyPublishedArtifactWithoutChallengeCapabilityCannotAcquireAnUpdateLock(bool legacyManifest, bool legacyAssembly)
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        var bytes = AgentUpdateTestPackage.Create("2.3.4", legacyAssembly, legacyManifest);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        AddRelease(fixture.Path, "2.3.4", hash, bytes.Length);
        var path = ControllerReleasePath(fixture.Path, hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        fixture.SyncVersion("1.0.0");

        var unavailable = Assert.Throws<AgentReleaseUnavailableException>(() => fixture.Db.ReserveAgentUpdate("pc", "2.3.4", "admin", Guid.NewGuid().ToString("N")));
        Assert.Equal(AgentReleaseUnavailableKind.InvalidArchive, unavailable.Kind);
        Assert.Null(Assert.Single(fixture.Db.List()).AgentUpdateOperationId);
        Assert.True(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public void HistoricalProtocolOneReleaseRemainsListedButCannotBeReserved()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        var bytes = AgentUpdateTestPackage.Create("2.3.4", challengeProtocol: 1);
        AddRelease(fixture.Path, "2.3.4", packageBytes: bytes, targetRuntime: "unknown", challengeProtocol: 1);

        Assert.Equal("2.3.4", Assert.Single(fixture.Db.ListAgentReleases()).Version);
        var unavailable = Assert.Throws<AgentReleaseUnavailableException>(() => fixture.Db.ReserveAgentUpdate("pc", "2.3.4", "admin", "legacy-release"));
        Assert.Equal(AgentReleaseUnavailableKind.InvalidArchive, unavailable.Kind);
        Assert.Null(Assert.Single(fixture.Db.List()).AgentUpdateOperationId);
        Assert.True(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public async Task SameSizeCorruptionReturnsSafeAdminReasonWithoutCreatingAnUpdateLock()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        var release = Assert.Single(fixture.Db.ListAgentReleases());
        var artifactPath = ControllerReleasePath(fixture.Path, release.PackageSha256);
        var originalBytes = File.ReadAllBytes(artifactPath);
        var corruptedBytes = (byte[])originalBytes.Clone();
        corruptedBytes[corruptedBytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(artifactPath, corruptedBytes);
        Assert.Equal(originalBytes.Length, new FileInfo(artifactPath).Length);
        fixture.SyncVersion("1.0.0");
        await using var host = await StartHost(fixture.Db);

        using var response = await Reserve(host.Client, "pc", "2.0.0");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal("release_unavailable", json.RootElement.GetProperty("code").GetString());
        Assert.False(json.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("Stored package does not match its published SHA-256.", json.RootElement.GetProperty("reason").GetString());
        Assert.DoesNotContain("agent-releases", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetDirectoryName(artifactPath)!, body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(release.PackageSha256, Assert.Single(fixture.Db.ListAgentReleases()).PackageSha256);
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_maintenance_chains WHERE held=1"));
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_operations"));
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM agent_update_audit"));
        Assert.Null(Assert.Single(fixture.Db.List()).AgentUpdateOperationId);
        Assert.True(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public async Task MachineListDistinguishesHeldLeaseFromUnreadyMachine()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        var revision = Assert.Single(fixture.Db.List()).ConfigRevision;
        fixture.Db.Sync("pc", new SyncRequest("boot", "192.0.2.1", "001122334455", revision, "notReady", []));
        await using var host = await StartHost(fixture.Db);

        using var unready = JsonDocument.Parse(await host.Client.GetStringAsync("/internal/machines"));
        var unreadyMachine = unready.RootElement[0];
        Assert.False(unreadyMachine.GetProperty("canRent").GetBoolean());
        Assert.False(unreadyMachine.GetProperty("allocationHeld").GetBoolean());

        fixture.Sync();
        await fixture.Create();
        using var allocated = JsonDocument.Parse(await host.Client.GetStringAsync("/internal/machines"));
        Assert.True(allocated.RootElement[0].GetProperty("allocationHeld").GetBoolean());
    }

    [Fact]
    public async Task ReportedInstalledVersionIsOnlyAHintAndDoesNotBlockReservation()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "1.0.0");
        fixture.SyncVersion("1.0.0");
        await using var host = await StartHost(fixture.Db);

        using var response = await Reserve(host.Client, "pc", "1.0.0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Theory]
    [InlineData("installing")]
    [InlineData("failed")]
    [InlineData("rollingBack")]
    public async Task TargetHeartbeatCannotCompleteAnUnconfirmedTransaction(string status)
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", Guid.NewGuid().ToString("N"));
        fixture.Db.StartAgentUpdate("pc", update.Ticket);
        if (status != "installing") fixture.Db.ReportAgentUpdateStatus("pc", update.Ticket, status);

        fixture.SyncVersion("2.0.0");

        Assert.Equal(status, OperationStatus(fixture.Path, update.OperationId));
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Create("unconfirmed-update"));
    }

    [Fact]
    public async Task TargetHeartbeatAfterReportCannotReleaseTheStartedChain()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", Guid.NewGuid().ToString("N"));
        fixture.Db.StartAgentUpdate("pc", update.Ticket);

        fixture.SyncVersion("2.0.0");
        Assert.Equal("installing", OperationStatus(fixture.Path, update.OperationId));
        fixture.Db.ReportAgentUpdateStatus("pc", update.Ticket, "awaitingHeartbeat");
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Create("before-target-heartbeat"));
        fixture.SyncVersion("2.0.0");
        Assert.Equal("awaitingHeartbeat", OperationStatus(fixture.Path, update.OperationId));
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public async Task RollbackAndTargetHeartbeatsCannotReleaseTheStartedChain()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0", "admin", Guid.NewGuid().ToString("N"));
        fixture.Db.StartAgentUpdate("pc", update.Ticket);

        fixture.SyncVersion("1.0.0");
        fixture.Db.ReportAgentUpdateStatus("pc", update.Ticket, "rollingBack");
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
        fixture.SyncVersion("2.0.0");
        Assert.Equal("rollingBack", OperationStatus(fixture.Path, update.OperationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Create("during-rollback"));
        fixture.SyncVersion("1.0.0");
        Assert.Equal("rollingBack", OperationStatus(fixture.Path, update.OperationId));
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public async Task UpdateReservationRejectsMachineWithHeldLease()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        await fixture.Create();
        await using var host = await StartHost(fixture.Db);

        using var response = await Reserve(host.Client, "pc", "2.0.0");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task UpdateReservationDoesNotTrustHeartbeatVersionOrAge()
    {
        using var unversioned = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(unversioned.Path, "2.0.0");
        await using var unversionedHost = await StartHost(unversioned.Db);
        using var noVersion = await Reserve(unversionedHost.Client, "pc", "2.0.0");
        Assert.Equal(HttpStatusCode.OK, noVersion.StatusCode);

        using var stale = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(stale.Path, "2.0.0");
        stale.SyncVersion("1.0.0");
        stale.Time.Now = stale.Time.Now.AddSeconds(21);
        await using var staleHost = await StartHost(stale.Db);
        using var staleHeartbeat = await Reserve(staleHost.Client, "pc", "2.0.0");
        Assert.Equal(HttpStatusCode.OK, staleHeartbeat.StatusCode);
    }

    [Fact]
    public void ReleaseMetadataIsImmutableAndListedWithoutThePrivatePackagePath()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        var publishedAt = fixture.Time.Now;
        var release = new AgentReleasePublication("2.0.0", "stable", "release notes", new string('a', 64),
            Path.Combine(Path.GetTempPath(), "private-agent-package.zip"), 1024, "commit-id", false, true);

        var saved = fixture.Db.PublishAgentRelease(release);

        Assert.Equal("2.0.0", saved.Version);
        Assert.Equal("stable", saved.Channel);
        Assert.Equal(new string('A', 64), saved.PackageSha256);
        Assert.Equal(1024, saved.PackageSizeBytes);
        Assert.Equal(publishedAt, saved.PublishedAtUtc);
        Assert.DoesNotContain("PackagePath", typeof(AgentReleaseDto).GetProperties().Select(property => property.Name));
        Assert.Equal(saved, Assert.Single(fixture.Db.ListAgentReleases()));
        Assert.Throws<SqliteException>(() => fixture.Db.PublishAgentRelease(release with { Channel = "beta", ReleaseNotes = "replacement" }));
    }

    [Fact]
    public async Task LeaseCreationAndUpdateReservationSerializeOnTheDatabase()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        using var secondConnection = new ControllerDatabase(fixture.Path, clock: fixture.Time, agentReleaseRemediationEnabled: true);
        await using var host = await StartHost(secondConnection);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> TryCreateLease()
        {
            await start.Task;
            try
            {
                await fixture.Leases.CreateTrialLeaseAsync("alice", "pc", 60, "race", default);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        async Task<HttpStatusCode> TryReserveUpdate()
        {
            await start.Task;
            using var response = await Reserve(host.Client, "pc", "2.0.0");
            return response.StatusCode;
        }

        var leaseAttempt = Task.Run(TryCreateLease);
        var updateAttempt = Task.Run(TryReserveUpdate);
        start.SetResult();
        var leaseWon = await leaseAttempt;
        var updateStatus = await updateAttempt;

        Assert.Equal(leaseWon ? HttpStatusCode.Conflict : HttpStatusCode.OK, updateStatus);
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public async Task MaintenanceLockMakesMachineUnavailableAndBlocksLeaseCreation()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        await using var host = await StartHost(fixture.Db);

        using var response = await Reserve(host.Client, "pc", "2.0.0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Create("maintenance-lock"));
    }

    [Fact]
    public async Task ExpiredUnstartedTicketExpiresReservationAndAllowsANewOperation()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        AddRelease(fixture.Path, "3.0.0");
        fixture.SyncVersion("1.0.0");
        await using var host = await StartHost(fixture.Db);

        string firstOperationId;
        using (var first = await Reserve(host.Client, "pc", "2.0.0"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            firstOperationId = await ReadString(first, "operationId");
        }
        fixture.Time.Now = fixture.Time.Now.AddMinutes(10);
        fixture.SyncVersion("1.0.0");

        using var second = await Reserve(host.Client, "pc", "3.0.0");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotEqual(firstOperationId, await ReadString(second, "operationId"));
        Assert.Equal("expired", OperationStatus(fixture.Path, firstOperationId));
    }

    [Fact]
    public async Task ExpiredUnstartedTicketReleasesTheLockForLeaseCreation()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        await using var host = await StartHost(fixture.Db);
        using var reservation = await Reserve(host.Client, "pc", "2.0.0");
        var operationId = await ReadString(reservation, "operationId");
        fixture.Time.Now = fixture.Time.Now.AddMinutes(10);
        fixture.SyncVersion("1.0.0");

        Assert.True(Assert.Single(fixture.Db.List()).CanRent);
        var lease = await fixture.Create("after-ticket-expiry");

        Assert.Equal("preparing", lease.State);
        Assert.Equal("expired", OperationStatus(fixture.Path, operationId));
        Assert.Equal("expired", Assert.Single(fixture.Db.List()).AgentUpdateStatus);
    }

    [Fact]
    public async Task StartedFailedOperationKeepsMaintenanceLockUntilRecovery()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        AddRelease(fixture.Path, "3.0.0");
        fixture.SyncVersion("1.0.0");
        await using var host = await StartHost(fixture.Db);
        using var initial = await Reserve(host.Client, "pc", "2.0.0");
        var operationId = await ReadString(initial, "operationId");
        SetStartedStatus(fixture.Path, operationId, "failed");

        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
        Assert.Equal("failed", Assert.Single(fixture.Db.List()).AgentUpdateStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Create("failed-operation-lock"));
        using var otherReservation = await Reserve(host.Client, "pc", "3.0.0");
        Assert.Equal(HttpStatusCode.Conflict, otherReservation.StatusCode);
    }

    [Fact]
    public async Task LegacyRecoveryTicketEndpointFailsClosedForFailedOperation()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        await using var host = await StartHost(fixture.Db);
        using var initial = await Reserve(host.Client, "pc", "2.0.0");
        var operationId = await ReadString(initial, "operationId");
        SetStartedStatus(fixture.Path, operationId, "failed");

        using var recovery = await host.Client.PostAsync($"/internal/machines/pc/agent-updates/{operationId}/recovery-ticket", content: null);

        Assert.Equal(HttpStatusCode.Conflict, recovery.StatusCode);
        Assert.Equal("failed", OperationStatus(fixture.Path, operationId));
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public async Task PublicUpdateEndpointsBindStartAndOnePackageDownloadToTheCurrentTicket()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        var package = ProtocolTwoPackage("2.0.0");
        var packageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(package));
        AddRelease(fixture.Path, "2.0.0", packageHash, package.Length);
        var storeRoot = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "agent-releases");
        var packageDirectory = Path.Combine(storeRoot, "sha256", packageHash[..2].ToLowerInvariant());
        Directory.CreateDirectory(packageDirectory);
        await File.WriteAllBytesAsync(Path.Combine(packageDirectory, packageHash.ToLowerInvariant() + ".zip"), package);
        fixture.SyncVersion("1.0.0");
        await using var host = await StartHost(fixture.Db, storeRoot);
        using var reservation = await Reserve(host.Client, "pc", "2.0.0");
        var operationId = await ReadString(reservation, "operationId");
        var ticket = await ReadString(reservation, "ticket");

        using var absentTicket = await host.Client.GetAsync($"/agent/machines/pc/agent-updates/{operationId}/package");
        Assert.Equal(HttpStatusCode.Unauthorized, absentTicket.StatusCode);

        var request = new
        {
            ticket,
            releaseVersion = "2.0.0",
            targetVersion = "2.0.0",
            targetPackageSha256 = packageHash,
            requestId = "start-" + operationId,
        };
        using var wrongMachine = await host.Client.PostAsJsonAsync($"/agent/machines/other/agent-updates/{operationId}/start", request);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongMachine.StatusCode);
        using var wrongRelease = await host.Client.PostAsJsonAsync($"/agent/machines/pc/agent-updates/{operationId}/start", request with { releaseVersion = "3.0.0" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongRelease.StatusCode);
        using var wrongHash = await host.Client.PostAsJsonAsync($"/agent/machines/pc/agent-updates/{operationId}/start", request with { targetPackageSha256 = new string('C', 64) });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongHash.StatusCode);

        using var started = await host.Client.PostAsJsonAsync($"/agent/machines/pc/agent-updates/{operationId}/start", request);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var repeatedStart = await host.Client.PostAsJsonAsync($"/agent/machines/pc/agent-updates/{operationId}/start", request);
        Assert.Equal(HttpStatusCode.OK, repeatedStart.StatusCode);
        using var changedRetry = await host.Client.PostAsJsonAsync($"/agent/machines/pc/agent-updates/{operationId}/start",
            request with { targetVersion = "2.0.1" });
        Assert.Equal(HttpStatusCode.Conflict, changedRetry.StatusCode);

        var statusReport = new
        {
            ticket,
            releaseVersion = "2.0.0",
            targetVersion = "2.0.0",
            targetPackageSha256 = packageHash,
            status = "awaitingHeartbeat",
        };
        using var wrongStatusIdentity = await host.Client.PostAsJsonAsync($"/agent/machines/pc/agent-updates/{operationId}/status", statusReport with { targetVersion = "3.0.0" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongStatusIdentity.StatusCode);
        using var reported = await host.Client.PostAsJsonAsync($"/agent/machines/pc/agent-updates/{operationId}/status", statusReport);
        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        Assert.Equal("no-store", reported.Headers.CacheControl?.ToString());
        using var repeatedReport = await host.Client.PostAsJsonAsync($"/agent/machines/pc/agent-updates/{operationId}/status", statusReport);
        Assert.Equal(HttpStatusCode.OK, repeatedReport.StatusCode);

        using var wrongDownloadRelease = CreatePackageRequest("3.0.0", "2.0.0", packageHash, ticket, operationId);
        using var rejectedDownload = await host.Client.SendAsync(wrongDownloadRelease);
        Assert.Equal(HttpStatusCode.Unauthorized, rejectedDownload.StatusCode);
        using var download = await host.Client.SendAsync(CreatePackageRequest("2.0.0", "2.0.0", packageHash, ticket, operationId));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("no-store", download.Headers.CacheControl?.ToString());
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(package, await download.Content.ReadAsByteArrayAsync());

        using var repeatedDownload = await host.Client.SendAsync(CreatePackageRequest("2.0.0", "2.0.0", packageHash, ticket, operationId));
        Assert.Equal(HttpStatusCode.Unauthorized, repeatedDownload.StatusCode);
        using var reopenedDatabase = new ControllerDatabase(fixture.Path, clock: fixture.Time);
        Assert.Throws<UnauthorizedAccessException>(() => reopenedDatabase.ConsumeAgentUpdatePackageDownload(
            "pc", operationId, ticket, "2.0.0", "2.0.0", packageHash));
    }

    [Fact]
    public async Task ExpiredTicketCannotStartAndStartedFailureCannotBeRenewed()
    {
        using var fixture = new LeaseFixture(agentReleaseRemediationEnabled: true);
        AddRelease(fixture.Path, "2.0.0");
        await using var host = await StartHost(fixture.Db);
        using var first = await Reserve(host.Client, "pc", "2.0.0");
        var expiredId = await ReadString(first, "operationId");
        var expiredTicket = await ReadString(first, "ticket");
        var hash = Assert.Single(fixture.Db.ListAgentReleases()).PackageSha256;
        fixture.Time.Now = fixture.Time.Now.AddMinutes(10);
        using var expiredStart = await host.Client.PostAsync($"/agent/machines/pc/agent-updates/{expiredId}/start", CreateStartContent(expiredId, expiredTicket, hash));
        Assert.Equal(HttpStatusCode.Unauthorized, expiredStart.StatusCode);
        using var second = await Reserve(host.Client, "pc", "2.0.0");
        var operationId = await ReadString(second, "operationId");
        var ticket = await ReadString(second, "ticket");
        fixture.Db.StartAgentUpdate("pc", ticket);
        fixture.Db.ReportAgentUpdateStatus("pc", ticket, "failed");
        using var recovery = await host.Client.PostAsync($"/internal/machines/pc/agent-updates/{operationId}/recovery-ticket", null);
        Assert.Equal(HttpStatusCode.Conflict, recovery.StatusCode);
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    private static async Task<TestHost> StartHost(ControllerDatabase database, string? storeRoot = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(database);
        var artifactRoot = storeRoot ?? Path.Combine(Path.GetTempPath(), "agent-update-tests", Guid.NewGuid().ToString("N"));
        builder.Services.AddSingleton(new AgentReleaseStore(artifactRoot));
        var app = builder.Build();
        app.MapMachines();
        app.MapAgentReleases();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "admin");
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role", "admin");
        return new TestHost(app, client, artifactRoot);
    }

    private static Task<HttpResponseMessage> Reserve(HttpClient client, string machineId, string releaseVersion) =>
        client.PostAsJsonAsync($"/internal/machines/{machineId}/agent-updates", new { releaseVersion });

    private static JsonContent CreateStartContent(string operationId, string ticket, string packageHash) =>
        JsonContent.Create(new { ticket, releaseVersion = "2.0.0", targetVersion = "2.0.0", targetPackageSha256 = packageHash, requestId = "start-" + operationId });

    private static HttpRequestMessage CreatePackageRequest(string releaseVersion, string targetVersion, string packageHash, string ticket, string operationId = "placeholder")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/agent/machines/pc/agent-updates/{operationId}/package");
        request.Headers.Add("X-Agent-Update-Ticket", ticket);
        request.Headers.Add("X-Agent-Update-Release-Version", releaseVersion);
        request.Headers.Add("X-Agent-Update-Target-Version", targetVersion);
        request.Headers.Add("X-Agent-Update-Package-SHA256", packageHash);
        return request;
    }

    private static void AddRelease(string path, string version, string? packageHash = null, long packageSize = 100, bool missing = false,
        string targetRuntime = "win-x64", int challengeProtocol = 2, byte[]? packageBytes = null)
    {
        var bytes = packageBytes ?? ProtocolTwoPackage(version);
        var hash = packageHash ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var package = ControllerReleasePath(path, hash);
        if (packageHash is null && !missing)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(package)!);
            File.WriteAllBytes(package, bytes);
            packageSize = bytes.Length;
        }
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO agent_releases(version,channel,release_notes,package_sha256,package_path,package_size_bytes,source_commit,source_dirty,release_build,package_format,published_at_unix_ms,target_runtime,agent_update_challenge_protocol) VALUES($version,'stable','release notes',$hash,$package,$size,'test-commit',0,1,'AgentPackage.zip',1,$runtime,$protocol)";
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$package", package);
        command.Parameters.AddWithValue("$size", packageSize);
        command.Parameters.AddWithValue("$runtime", targetRuntime);
        command.Parameters.AddWithValue("$protocol", challengeProtocol);
        command.ExecuteNonQuery();
    }

    private static byte[] ProtocolTwoPackage(string version)
    {
        var oldVersion = System.Text.Encoding.UTF8.GetBytes(AgentPackageCorpus.Version);
        var newVersion = System.Text.Encoding.UTF8.GetBytes(version);
        if (oldVersion.Length != newVersion.Length) throw new ArgumentException("Fixture versions must have equal byte lengths");
        var entries = AgentPackageCorpus.Entries().Select(item =>
        {
            var bytes = item.Bytes.ToArray();
            for (var i = 0; i <= bytes.Length - oldVersion.Length; i++)
                if (bytes.AsSpan(i, oldVersion.Length).SequenceEqual(oldVersion)) newVersion.CopyTo(bytes, i);
            return (item.Name, bytes);
        });
        return AgentPackageCorpus.Build(entries);
    }

    private static string ControllerReleasePath(string database, string hash) => Path.Combine(Path.GetDirectoryName(database)!, "agent-releases", "sha256", hash[..2].ToLowerInvariant(), hash.ToLowerInvariant() + ".zip");

    private static string OperationStatus(string path, string operationId)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM agent_update_operations WHERE operation_id=$id";
        command.Parameters.AddWithValue("$id", operationId);
        return (string)command.ExecuteScalar()!;
    }

    private static long Scalar(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void SetStartedStatus(string path, string operationId, string status)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_update_operations SET status=$status,started_at_unix_ms=created_at_unix_ms,ticket_started_at_unix_ms=created_at_unix_ms WHERE operation_id=$id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$id", operationId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static async Task<string> ReadString(HttpResponseMessage response, string name)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty(name).GetString()!;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class TestHost(WebApplication app, HttpClient client, string artifactRoot) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
        }
    }
}

internal static class LeaseFixtureAgentVersionExtensions
{
    public static SyncResponse SyncVersion(this LeaseFixture fixture, string version)
    {
        var machine = Assert.Single(fixture.Db.List());
        var request = new SyncRequest("boot", "192.0.2.1", "001122334455", machine.ConfigRevision, "ready", [])
        {
            AgentVersion = version,
        };
        var response = fixture.Db.Sync("pc", request);
        return response.AgentUpdateChallenge is null ? response : fixture.Db.Sync("pc", request with { AgentUpdateChallenge = response.AgentUpdateChallenge });
    }
}
