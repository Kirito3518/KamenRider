using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KamenRaider.Contracts;
using KamenRaider.Controller.Security;
using Microsoft.Data.Sqlite;

namespace KamenRaider.Controller.Data;

public sealed record AgentUpdateRecoveryIntentDto(
    string IntentId,
    string MachineId,
    string ChainId,
    string PreviousHeadOperationId,
    long ExpectedGeneration,
    long Generation,
    string TargetReleaseVersion,
    string TargetVersion,
    string TargetPackageSha256,
    string TargetRuntime,
    string Reason,
    string ActorId,
    string RequestId,
    string Status,
    bool Held,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed class AgentRecoveryIntentConflictException(string currentChainId, string currentHeadOperationId,
    long currentGeneration, string message)
    : InvalidOperationException(message)
{
    public string CurrentChainId { get; } = currentChainId;
    public string CurrentHeadOperationId { get; } = currentHeadOperationId;
    public long CurrentGeneration { get; } = currentGeneration;
}

public sealed class AgentUpdateRecoveryInspectionChallengeRenewalRequiredException()
    : InvalidOperationException("The recovery inspection challenge is expired, consumed, or belongs to a previous Controller runtime.");

public sealed class AgentRecoveryQualificationUnavailableException(string blockReason)
    : InvalidOperationException("Recovery inspection was authenticated and recorded, but independent recovery qualification is unavailable.")
{
    public string BlockReason { get; } = blockReason is "originalBackupBindingUnavailable" or "helperBuildNotPinned"
        or "exclusionProvenanceUnavailable" or "recoveryChallengeUnavailable"
        ? blockReason
        : throw new ArgumentOutOfRangeException(nameof(blockReason));
}

public sealed partial class ControllerDatabase
{
    private static readonly TimeSpan AgentRecoveryIntentLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan AgentRecoveryInspectionChallengeLifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions RecoveryIntentJsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record RecoveryIntentScope(
        string MachineId,
        string ChainId,
        string PreviousHeadOperationId,
        long ExpectedGeneration,
        long Generation,
        string TargetReleaseVersion,
        string TargetVersion,
        string TargetPackageSha256,
        string TargetRuntime,
        int TargetProtocol,
        string Status,
        long ExpiresAtUnixMs,
        string CurrentHeadOperationId,
        long CurrentGeneration,
        bool Held,
        string PreviousOperationStatus,
        string? BackupOwnerOperationId,
        string? BackupDigest,
        string? SourceVersion,
        string? SourceEvidenceDigest);

    private sealed record RecoveryInspectionRow(string KeyFingerprint, string RequestBodyDigest,
        string EvidenceDigest, string BlockReason);

    public AgentUpdateRecoveryIntentDto CreateRecoveryIntent(string machineId, string expectedChainId,
        string expectedHeadId, long expectedGeneration, string targetReleaseVersion, string reason,
        string requestId, string actorId)
    {
        if (!agentReleaseRemediationEnabled)
            throw new InvalidOperationException("Agent release remediation is disabled");
        RecoveryIdentifier(machineId, nameof(machineId));
        RecoveryIdentifier(expectedChainId, nameof(expectedChainId));
        RecoveryIdentifier(expectedHeadId, nameof(expectedHeadId));
        RecoveryIdentifier(targetReleaseVersion, nameof(targetReleaseVersion));
        RecoveryIdentifier(requestId, nameof(requestId));
        RecoveryIdentifier(actorId, nameof(actorId));
        if (expectedGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512 || reason.Any(char.IsControl))
            throw new ArgumentException("A recovery reason of at most 512 characters is required.", nameof(reason));

        var requestDigest = RecoveryIntentRequestDigest(machineId, expectedChainId, expectedHeadId,
            expectedGeneration, targetReleaseVersion, reason, requestId, actorId);
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var nowMs = now.ToUnixTimeMilliseconds();
            using var tx = connection.BeginTransaction();
            EnsureNotRestoreQuarantined(tx);

            var prior = FindRecoveryIntentByRequest(tx, actorId, requestId);
            if (prior is not null)
            {
                if (!FixedHexEquals(prior.RequestDigest, requestDigest))
                    throw new InvalidOperationException("Recovery intent request ID was reused with different content.");
                tx.Commit();
                return prior.Dto;
            }

            string currentChainId;
            string currentHead;
            long currentGeneration;
            using (var chain = Command("SELECT chain_id,current_head_operation_id,generation FROM agent_maintenance_chains WHERE machine_id=$machine AND held=1",
                ("$machine", machineId)))
            {
                chain.Transaction = tx;
                using var row = chain.ExecuteReader();
                if (!row.Read()) throw new KeyNotFoundException("Held maintenance chain was not found.");
                currentChainId = row.GetString(0);
                currentHead = row.GetString(1);
                currentGeneration = row.GetInt64(2);
            }
            if (currentChainId != expectedChainId || currentHead != expectedHeadId || currentGeneration != expectedGeneration)
                throw new AgentRecoveryIntentConflictException(currentChainId, currentHead, currentGeneration,
                    "Recovery intent is stale; refresh the current maintenance head and generation.");

            using (var allocation = Command("SELECT EXISTS(SELECT 1 FROM leases WHERE machine_id=$machine AND allocation_held=1)",
                ("$machine", machineId)))
            {
                allocation.Transaction = tx;
                if (Convert.ToInt64(allocation.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                    throw new InvalidOperationException("Recovery cannot begin while the machine has a held allocation.");
            }

            string oldStatus;
            string? oldRuntime;
            using (var head = Command("SELECT status,target_runtime FROM agent_update_operations WHERE operation_id=$head AND chain_id=$chain AND machine_id=$machine",
                ("$head", currentHead), ("$chain", expectedChainId), ("$machine", machineId)))
            {
                head.Transaction = tx;
                using var row = head.ExecuteReader();
                if (!row.Read()) throw new InvalidDataException("Maintenance chain has no valid head operation.");
                oldStatus = row.GetString(0);
                oldRuntime = row.IsDBNull(1) ? null : row.GetString(1);
            }
            if (oldStatus is "completed" or "rolledBack" or "expired" or "superseded")
                throw new InvalidOperationException("Only a held nonterminal Agent update can enter recovery.");

            using (var activeIntent = Command("SELECT intent_id,expires_at_unix_ms FROM agent_update_recovery_intents WHERE chain_id=$chain AND status='awaitingInspection'",
                ("$chain", expectedChainId)))
            {
                activeIntent.Transaction = tx;
                using var row = activeIntent.ExecuteReader();
                if (row.Read())
                {
                    var activeIntentId = row.GetString(0);
                    var expiresAt = row.GetInt64(1);
                    row.Close();
                    if (expiresAt > nowMs)
                        throw new AgentRecoveryIntentConflictException(currentChainId, currentHead, currentGeneration,
                            "A recovery intent is already open for this maintenance chain.");
                    using var expire = Command("UPDATE agent_update_recovery_intents SET status='expired' WHERE intent_id=$id AND status='awaitingInspection' AND expires_at_unix_ms<=$now",
                        ("$id", activeIntentId), ("$now", nowMs));
                    expire.Transaction = tx;
                    if (expire.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException("Expired recovery intent changed during replacement.");
                }
            }

            string targetVersion;
            string targetHash;
            string targetRuntime;
            string packagePath;
            long packageSize;
            int targetProtocol;
            using (var release = Command("SELECT version,package_sha256,package_path,package_size_bytes,target_runtime,agent_update_challenge_protocol FROM agent_releases WHERE version=$version",
                ("$version", targetReleaseVersion)))
            {
                release.Transaction = tx;
                using var row = release.ExecuteReader();
                if (!row.Read()) throw new KeyNotFoundException("Target Agent release was not found.");
                targetVersion = row.GetString(0);
                targetHash = row.GetString(1);
                packagePath = row.GetString(2);
                packageSize = row.GetInt64(3);
                targetRuntime = row.GetString(4);
                targetProtocol = row.GetInt32(5);
            }
            using (var package = OpenUpdateArtifact(packagePath, targetHash, packageSize, targetVersion, out var inventory))
            {
                if (targetProtocol != 2 || inventory.AgentUpdateChallengeProtocol != 2
                    || targetRuntime is not ("win-x64" or "win-arm64") || inventory.TargetRuntime != targetRuntime
                    || oldRuntime is not ("win-x64" or "win-arm64") || oldRuntime != targetRuntime)
                    throw new AgentReleaseUnavailableException(AgentReleaseUnavailableKind.UnsupportedProtocolOrRuntime);
            }

            using (var usedAuditRequest = Command("SELECT 1 FROM agent_update_audit WHERE actor_id=$actor AND request_id=$request",
                ("$actor", actorId), ("$request", requestId)))
            {
                usedAuditRequest.Transaction = tx;
                if (usedAuditRequest.ExecuteScalar() is not null)
                    throw new InvalidOperationException("Recovery intent request ID was already used by another Agent update action.");
            }

            var intentId = Guid.NewGuid().ToString("N");
            var expires = now.Add(AgentRecoveryIntentLifetime);
            var nextGeneration = checked(expectedGeneration + 1);
            using (var insert = Command("INSERT INTO agent_update_recovery_intents(intent_id,machine_id,chain_id,previous_head_operation_id,expected_generation,generation,target_release_version,target_version,target_package_sha256,target_runtime,target_protocol,reason,actor_id,request_id,request_digest,status,created_at_unix_ms,expires_at_unix_ms) VALUES($intent,$machine,$chain,$head,$expected,$generation,$release,$target,$hash,$runtime,2,$reason,$actor,$request,$digest,'awaitingInspection',$created,$expires)",
                ("$intent", intentId), ("$machine", machineId), ("$chain", expectedChainId), ("$head", expectedHeadId),
                ("$expected", expectedGeneration), ("$generation", nextGeneration), ("$release", targetReleaseVersion),
                ("$target", targetVersion), ("$hash", targetHash), ("$runtime", targetRuntime), ("$reason", reason),
                ("$actor", actorId), ("$request", requestId), ("$digest", requestDigest),
                ("$created", nowMs), ("$expires", expires.ToUnixTimeMilliseconds())))
            {
                insert.Transaction = tx;
                insert.ExecuteNonQuery();
            }

            using (var fence = Command("UPDATE agent_maintenance_chains SET generation=$generation,updated_at_unix_ms=$now WHERE chain_id=$chain AND machine_id=$machine AND current_head_operation_id=$head AND generation=$expected AND held=1",
                ("$generation", nextGeneration), ("$now", nowMs), ("$chain", expectedChainId), ("$machine", machineId),
                ("$head", expectedHeadId), ("$expected", expectedGeneration)))
            {
                fence.Transaction = tx;
                if (fence.ExecuteNonQuery() != 1)
                    throw new AgentRecoveryIntentConflictException(currentChainId, currentHead, currentGeneration,
                        "Recovery intent lost its maintenance-chain compare-and-swap.");
            }

            using (var expireChallenges = Command("UPDATE agent_update_evidence_challenges SET expires_at_unix_ms=MIN(expires_at_unix_ms,$now) WHERE chain_id=$chain AND head_operation_id=$head AND generation<=$expected AND expires_at_unix_ms>$now",
                ("$now", nowMs), ("$chain", expectedChainId), ("$head", expectedHeadId), ("$expected", expectedGeneration)))
            {
                expireChallenges.Transaction = tx;
                expireChallenges.ExecuteNonQuery();
            }

            var oldBinding = JsonSerializer.Serialize(new { machineId, chainId = expectedChainId, headOperationId = expectedHeadId, generation = expectedGeneration, status = oldStatus });
            var newBinding = JsonSerializer.Serialize(new { intentId, targetReleaseVersion, targetVersion, packageSha256 = targetHash, targetRuntime, targetProtocol, generation = nextGeneration, held = true, requestDigest });
            using (var audit = Command("INSERT INTO agent_update_audit(audit_id,chain_id,operation_id,actor_id,request_id,event_type,old_binding_json,new_binding_json,created_at_unix_ms) VALUES($audit,$chain,$head,$actor,$request,'recoveryIntentCreated',$old,$new,$now)",
                ("$audit", Guid.NewGuid().ToString("N")), ("$chain", expectedChainId), ("$head", expectedHeadId),
                ("$actor", actorId), ("$request", requestId), ("$old", oldBinding), ("$new", newBinding), ("$now", nowMs)))
            {
                audit.Transaction = tx;
                audit.ExecuteNonQuery();
            }

            var result = FindRecoveryIntent(tx, intentId) ?? throw new InvalidDataException("New recovery intent was not persisted.");
            tx.Commit();
            return result.Dto;
        }
    }

    public AgentUpdateRecoveryInspectionChallengeResponse IssueAgentUpdateRecoveryInspectionChallenge(
        VerifiedAgentRequest verified, string intentId, AgentUpdateRecoveryInspectionChallengeRequest request)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Inspection);
        if (!agentReleaseRemediationEnabled)
            throw new InvalidOperationException("Agent release remediation is disabled");
        RecoveryIdentifier(intentId, nameof(intentId));

        var inspection = request.Inspection;
        var path = $"/agent/machines/{verified.MachineId}/agent-update-recovery/{intentId}/challenge";
        if (!FixedHexEquals(verified.BodyDigest, RecoveryRequestBodyDigest(request)))
            throw new UnauthorizedAccessException("Signed recovery challenge body is not canonical.");
        try { _ = AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(inspection); }
        catch (ArgumentException exception)
        {
            throw new UnauthorizedAccessException("Signed recovery inspection is malformed.", exception);
        }

        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            EnsureNotRestoreQuarantined(tx);
            if (!ValidateVerifiedRequest(tx, verified, path))
                throw new UnauthorizedAccessException("Signed recovery challenge request has an invalid identity or path.");
            if (!VerifyRecoveryInspectionSignature(tx, verified, inspection))
                throw new UnauthorizedAccessException("Recovery inspection is not signed by the enrolled Agent key.");
            if (!IsFreshSignedIssuanceEnvelope(tx, verified))
                throw new UnauthorizedAccessException("Signed recovery challenge request is stale or outside the accepted clock skew.");

            var scope = ReadRecoveryIntentScope(tx, intentId)
                ?? throw new KeyNotFoundException("Recovery intent was not found.");
            var now = clock.GetUtcNow();
            ValidateRecoveryInspectionScope(verified, scope, intentId, inspection.RequestId, inspection, now.ToUnixTimeMilliseconds());
            if (!AcceptScopedNonce(tx, verified, enforceCutoff: false))
                throw new UnauthorizedAccessException("Signed recovery challenge request was replayed.");

            var bindingDigest = RecoveryInspectionChallengeBindingDigest(intentId, verified.KeyFingerprint, inspection);
            var challengeRequestId = inspection.RequestId;
            var nowMs = now.ToUnixTimeMilliseconds();
            var expiresAt = now.Add(AgentRecoveryInspectionChallengeLifetime).ToUnixTimeMilliseconds();
            var issuedTick = clock.GetTimestamp();
            var challengeToken = NewRecoveryInspectionChallengeToken();
            var challengeDigest = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(challengeToken)));
            var challengeId = Guid.NewGuid().ToString("N");
            long? priorConsumedAt = null;

            using (var prior = Command("SELECT challenge_id,challenge_binding_digest,consumed_at_unix_ms FROM agent_update_evidence_challenges WHERE machine_id=$machine AND key_fingerprint=$fingerprint AND chain_id=$chain AND head_operation_id=$head AND generation=$generation AND purpose='recoveryInspection' AND challenge_request_id=$request",
                ("$machine", scope.MachineId), ("$fingerprint", verified.KeyFingerprint), ("$chain", scope.ChainId),
                ("$head", scope.PreviousHeadOperationId), ("$generation", scope.Generation), ("$request", challengeRequestId)))
            {
                prior.Transaction = tx;
                using var row = prior.ExecuteReader();
                if (row.Read())
                {
                    challengeId = row.GetString(0);
                    var priorBindingDigest = row.IsDBNull(1) ? null : row.GetString(1);
                    priorConsumedAt = row.IsDBNull(2) ? null : row.GetInt64(2);
                    if (!FixedHexEquals(priorBindingDigest ?? string.Empty, bindingDigest))
                        throw new InvalidOperationException("Recovery challenge request ID was reused with different evidence.");
                }
            }

            if (priorConsumedAt is not null)
                throw new AgentUpdateRecoveryInspectionChallengeRenewalRequiredException();

            if (priorConsumedAt is null)
            {
                using var existing = Command("SELECT 1 FROM agent_update_evidence_challenges WHERE machine_id=$machine AND key_fingerprint=$fingerprint AND chain_id=$chain AND head_operation_id=$head AND generation=$generation AND purpose='recoveryInspection' AND challenge_request_id=$request",
                    ("$machine", scope.MachineId), ("$fingerprint", verified.KeyFingerprint), ("$chain", scope.ChainId),
                    ("$head", scope.PreviousHeadOperationId), ("$generation", scope.Generation), ("$request", challengeRequestId));
                existing.Transaction = tx;
                var hasExisting = existing.ExecuteScalar() is not null;
                if (hasExisting)
                {
                    using var rotate = Command("UPDATE agent_update_evidence_challenges SET nonce_digest=$nonce,expires_at_unix_ms=$expires,challenge_issued_at_unix_ms=$issuedAt,challenge_issued_monotonic_tick=$issuedTick,challenge_runtime_epoch=$runtimeEpoch WHERE challenge_id=$id AND consumed_at_unix_ms IS NULL AND challenge_binding_digest=$binding",
                        ("$nonce", challengeDigest), ("$expires", expiresAt), ("$issuedAt", nowMs), ("$issuedTick", issuedTick),
                        ("$runtimeEpoch", syncProofRuntimeEpoch), ("$id", challengeId), ("$binding", bindingDigest));
                    rotate.Transaction = tx;
                    if (rotate.ExecuteNonQuery() != 1)
                        throw new AgentUpdateRecoveryInspectionChallengeRenewalRequiredException();
                }
                else
                {
                    using (var expireOther = Command("UPDATE agent_update_evidence_challenges SET expires_at_unix_ms=MIN(expires_at_unix_ms,$now) WHERE machine_id=$machine AND key_fingerprint=$fingerprint AND chain_id=$chain AND head_operation_id=$head AND generation=$generation AND purpose='recoveryInspection' AND challenge_request_id<>$request AND consumed_at_unix_ms IS NULL AND expires_at_unix_ms>$now",
                        ("$now", nowMs), ("$machine", scope.MachineId), ("$fingerprint", verified.KeyFingerprint),
                        ("$chain", scope.ChainId), ("$head", scope.PreviousHeadOperationId),
                        ("$generation", scope.Generation), ("$request", challengeRequestId)))
                    {
                        expireOther.Transaction = tx;
                        expireOther.ExecuteNonQuery();
                    }

                    using var insert = Command("INSERT INTO agent_update_evidence_challenges(challenge_id,machine_id,key_fingerprint,chain_id,head_operation_id,generation,purpose,nonce_digest,expires_at_unix_ms,challenge_binding_digest,challenge_request_id,challenge_issued_at_unix_ms,challenge_issued_monotonic_tick,challenge_runtime_epoch) VALUES($id,$machine,$fingerprint,$chain,$head,$generation,'recoveryInspection',$nonce,$expires,$binding,$request,$issuedAt,$issuedTick,$runtimeEpoch)",
                        ("$id", challengeId), ("$machine", scope.MachineId), ("$fingerprint", verified.KeyFingerprint),
                        ("$chain", scope.ChainId), ("$head", scope.PreviousHeadOperationId), ("$generation", scope.Generation),
                        ("$nonce", challengeDigest), ("$expires", expiresAt), ("$binding", bindingDigest),
                        ("$request", challengeRequestId), ("$issuedAt", nowMs), ("$issuedTick", issuedTick),
                        ("$runtimeEpoch", syncProofRuntimeEpoch));
                    insert.Transaction = tx;
                    insert.ExecuteNonQuery();
                }
            }

            tx.Commit();
            return new(challengeToken, DateTimeOffset.FromUnixTimeMilliseconds(expiresAt));
        }
    }

    public AgentUpdateTicketResponse CommitRecoveryInspection(VerifiedAgentRequest verified, string intentId,
        string requestId, SignedRecoveryInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(inspection);
        if (!agentReleaseRemediationEnabled)
            throw new InvalidOperationException("Agent release remediation is disabled");
        RecoveryIdentifier(intentId, nameof(intentId));
        RecoveryIdentifier(requestId, nameof(requestId));

        var path = $"/agent/machines/{verified.MachineId}/agent-update-recovery/{intentId}/inspection";
        if (!FixedHexEquals(verified.BodyDigest, RecoveryRequestBodyDigest(inspection)))
            throw new UnauthorizedAccessException("Signed recovery inspection body is not canonical.");
        byte[] canonicalEvidence;
        try { canonicalEvidence = AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(inspection); }
        catch (ArgumentException exception)
        {
            throw new UnauthorizedAccessException("Signed recovery inspection is malformed.", exception);
        }
        var evidenceDigest = Convert.ToHexString(SHA256.HashData(canonicalEvidence));

        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            EnsureNotRestoreQuarantined(tx);
            if (!ValidateVerifiedRequest(tx, verified, path))
                throw new UnauthorizedAccessException("Signed recovery inspection has an invalid identity or path.");
            if (!VerifyRecoveryInspectionSignature(tx, verified, inspection))
                throw new UnauthorizedAccessException("Recovery inspection is not signed by the enrolled Agent key.");
            if (inspection.IntentId != intentId || inspection.RequestId != requestId
                || inspection.MachineId != verified.MachineId
                || !FixedHexEquals(inspection.KeyFingerprint, verified.KeyFingerprint))
                throw new UnauthorizedAccessException("Recovery inspection identity or request scope does not match.");

            var requestBodyDigest = verified.BodyDigest.ToUpperInvariant();
            var prior = FindRecoveryInspection(tx, intentId, requestId);
            if (prior is not null)
            {
                if (!FixedHexEquals(prior.KeyFingerprint, verified.KeyFingerprint)
                    || !FixedHexEquals(prior.RequestBodyDigest, requestBodyDigest)
                    || !FixedHexEquals(prior.EvidenceDigest, evidenceDigest))
                    throw new InvalidOperationException("Recovery inspection request ID was reused with different content.");
                tx.Commit();
                throw new AgentRecoveryQualificationUnavailableException(prior.BlockReason);
            }

            var scope = ReadRecoveryIntentScope(tx, intentId)
                ?? throw new KeyNotFoundException("Recovery intent was not found.");
            var now = clock.GetUtcNow();
            ValidateRecoveryInspectionScope(verified, scope, intentId, requestId, inspection, now.ToUnixTimeMilliseconds());
            var auditActor = $"agent:{scope.MachineId}";
            using (var usedAuditRequest = Command("SELECT 1 FROM agent_update_audit WHERE actor_id=$actor AND request_id=$request",
                ("$actor", auditActor), ("$request", requestId)))
            {
                usedAuditRequest.Transaction = tx;
                if (usedAuditRequest.ExecuteScalar() is not null)
                    throw new InvalidOperationException("Recovery inspection request ID was already used for another Agent action.");
            }
            var bindingDigest = RecoveryInspectionChallengeBindingDigest(intentId, verified.KeyFingerprint, inspection);
            var challenge = FindRecoveryInspectionChallenge(tx, scope, verified.KeyFingerprint, requestId);
            if (challenge is null || !FixedHexEquals(challenge.NonceDigest, verified.NonceDigest)
                || !FixedHexEquals(challenge.BindingDigest, bindingDigest))
                throw new UnauthorizedAccessException("Recovery inspection has no matching one-time intent challenge.");
            if (!IsActiveRecoveryInspectionChallenge(challenge.IssuedAtUnixMs,
                challenge.IssuedMonotonicTick, challenge.ExpiresAtUnixMs,
                challenge.RuntimeEpoch, now.ToUnixTimeMilliseconds()))
                throw new AgentUpdateRecoveryInspectionChallengeRenewalRequiredException();
            if (!AcceptScopedNonce(tx, verified, enforceCutoff: false))
                throw new UnauthorizedAccessException("Signed recovery inspection request was replayed.");

            using (var consume = Command("UPDATE agent_update_evidence_challenges SET consumed_at_unix_ms=$now,request_id=$request WHERE challenge_id=$challenge AND machine_id=$machine AND key_fingerprint=$fingerprint AND chain_id=$chain AND head_operation_id=$head AND generation=$generation AND purpose='recoveryInspection' AND nonce_digest=$nonce AND challenge_binding_digest=$binding AND challenge_request_id=$request AND consumed_at_unix_ms IS NULL AND expires_at_unix_ms>$now",
                ("$now", now.ToUnixTimeMilliseconds()), ("$request", requestId), ("$challenge", challenge.ChallengeId),
                ("$machine", scope.MachineId), ("$fingerprint", verified.KeyFingerprint), ("$chain", scope.ChainId),
                ("$head", scope.PreviousHeadOperationId), ("$generation", scope.Generation),
                ("$nonce", verified.NonceDigest), ("$binding", bindingDigest)))
            {
                consume.Transaction = tx;
                if (consume.ExecuteNonQuery() != 1)
                    throw new AgentUpdateRecoveryInspectionChallengeRenewalRequiredException();
            }

            var blockReason = RecoveryQualificationBlockReason(scope, inspection);
            using (var insert = Command("INSERT INTO agent_update_recovery_inspections(inspection_id,intent_id,request_id,key_fingerprint,request_body_digest,evidence_digest,outcome,block_reason,recorded_at_unix_ms) VALUES($id,$intent,$request,$fingerprint,$bodyDigest,$evidenceDigest,'blocked',$reason,$now)",
                ("$id", Guid.NewGuid().ToString("N")), ("$intent", intentId), ("$request", requestId),
                ("$fingerprint", verified.KeyFingerprint.ToUpperInvariant()), ("$bodyDigest", requestBodyDigest),
                ("$evidenceDigest", evidenceDigest), ("$reason", blockReason), ("$now", now.ToUnixTimeMilliseconds())))
            {
                insert.Transaction = tx;
                insert.ExecuteNonQuery();
            }

            var oldBinding = JsonSerializer.Serialize(new
            {
                machineId = scope.MachineId,
                chainId = scope.ChainId,
                headOperationId = scope.PreviousHeadOperationId,
                generation = scope.Generation,
                held = scope.Held,
            }, RecoveryIntentJsonOptions);
            var newBinding = JsonSerializer.Serialize(new
            {
                intentId,
                requestId,
                outcome = "blocked",
                blockReason,
                keyFingerprint = verified.KeyFingerprint.ToUpperInvariant(),
                requestBodyDigest,
                evidenceDigest,
                oldBackupOwnerOperationId = inspection.OldBackupOwnerOperationId,
                oldBackupDigest = inspection.OldBackupDigest,
                actualInstalledDigest = inspection.ActualInstalledDigest,
                nativeBootIdBefore = inspection.NativeBootIdBefore,
                nativeBootIdAfter = inspection.NativeBootIdAfter,
                exclusionProvenance = inspection.ExclusionProvenance,
                helperBuildSha256 = inspection.HelperBuildSha256,
            }, RecoveryIntentJsonOptions);
            using (var audit = Command("INSERT INTO agent_update_audit(audit_id,chain_id,operation_id,actor_id,request_id,event_type,old_binding_json,new_binding_json,created_at_unix_ms) VALUES($audit,$chain,$head,$actor,$request,'recoveryInspectionBlocked',$old,$new,$now)",
                ("$audit", Guid.NewGuid().ToString("N")), ("$chain", scope.ChainId), ("$head", scope.PreviousHeadOperationId),
                ("$actor", auditActor), ("$request", requestId), ("$old", oldBinding),
                ("$new", newBinding), ("$now", now.ToUnixTimeMilliseconds())))
            {
                audit.Transaction = tx;
                audit.ExecuteNonQuery();
            }

            tx.Commit();
            throw new AgentRecoveryQualificationUnavailableException(blockReason);
        }
    }

    public AgentUpdateRecoveryIntentDto? GetRecoveryByRequestId(string machineId, string actorId, string requestId)
    {
        RecoveryIdentifier(machineId, nameof(machineId));
        RecoveryIdentifier(actorId, nameof(actorId));
        RecoveryIdentifier(requestId, nameof(requestId));
        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            var result = FindRecoveryIntentByRequest(tx, actorId, requestId, machineId);
            tx.Commit();
            return result?.Dto;
        }
    }

    private sealed record RecoveryIntentRow(AgentUpdateRecoveryIntentDto Dto, string RequestDigest);

    private RecoveryIntentRow? FindRecoveryIntent(SqliteTransaction tx, string intentId)
    {
        using var query = Command("SELECT i.intent_id,i.machine_id,i.chain_id,i.previous_head_operation_id,i.expected_generation,i.generation,i.target_release_version,i.target_version,i.target_package_sha256,i.target_runtime,i.reason,i.actor_id,i.request_id,i.request_digest,i.status,i.created_at_unix_ms,i.expires_at_unix_ms,c.held FROM agent_update_recovery_intents i JOIN agent_maintenance_chains c ON c.chain_id=i.chain_id AND c.machine_id=i.machine_id WHERE i.intent_id=$intent",
            ("$intent", intentId));
        query.Transaction = tx;
        using var row = query.ExecuteReader();
        if (!row.Read()) return null;
        return new(new AgentUpdateRecoveryIntentDto(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
            row.GetInt64(4), row.GetInt64(5), row.GetString(6), row.GetString(7), row.GetString(8), row.GetString(9),
            row.GetString(10), row.GetString(11), row.GetString(12), row.GetString(14), row.GetInt32(17) != 0,
            DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(15)), DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(16))),
            row.GetString(13));
    }

    private RecoveryIntentRow? FindRecoveryIntentByRequest(SqliteTransaction tx, string actorId, string requestId,
        string? machineId = null)
    {
        using var query = Command("SELECT i.intent_id,i.machine_id,i.chain_id,i.previous_head_operation_id,i.expected_generation,i.generation,i.target_release_version,i.target_version,i.target_package_sha256,i.target_runtime,i.reason,i.actor_id,i.request_id,i.request_digest,i.status,i.created_at_unix_ms,i.expires_at_unix_ms,c.held FROM agent_update_recovery_intents i JOIN agent_maintenance_chains c ON c.chain_id=i.chain_id AND c.machine_id=i.machine_id WHERE i.actor_id=$actor AND i.request_id=$request AND ($machine IS NULL OR i.machine_id=$machine)",
            ("$actor", actorId), ("$request", requestId), ("$machine", machineId));
        query.Transaction = tx;
        using var row = query.ExecuteReader();
        if (!row.Read()) return null;
        return new(new AgentUpdateRecoveryIntentDto(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
            row.GetInt64(4), row.GetInt64(5), row.GetString(6), row.GetString(7), row.GetString(8), row.GetString(9),
            row.GetString(10), row.GetString(11), row.GetString(12), row.GetString(14), row.GetInt32(17) != 0,
            DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(15)), DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(16))),
            row.GetString(13));
    }

    private sealed record RecoveryInspectionChallenge(string ChallengeId, string NonceDigest,
        string BindingDigest, long ExpiresAtUnixMs, long IssuedAtUnixMs, long IssuedMonotonicTick,
        string RuntimeEpoch);

    private RecoveryIntentScope? ReadRecoveryIntentScope(SqliteTransaction tx, string intentId)
    {
        using var query = Command("SELECT i.machine_id,i.chain_id,i.previous_head_operation_id,i.expected_generation,i.generation,i.target_release_version,i.target_version,i.target_package_sha256,i.target_runtime,i.target_protocol,i.status,i.expires_at_unix_ms,c.current_head_operation_id,c.generation,c.held,o.status,o.backup_owner_operation_id,o.backup_digest,o.source_version,o.source_evidence_digest FROM agent_update_recovery_intents i JOIN agent_maintenance_chains c ON c.chain_id=i.chain_id AND c.machine_id=i.machine_id JOIN agent_update_operations o ON o.operation_id=i.previous_head_operation_id AND o.chain_id=i.chain_id AND o.machine_id=i.machine_id WHERE i.intent_id=$intent",
            ("$intent", intentId));
        query.Transaction = tx;
        using var row = query.ExecuteReader();
        if (!row.Read()) return null;
        return new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetInt64(3), row.GetInt64(4),
            row.GetString(5), row.GetString(6), row.GetString(7), row.GetString(8), row.GetInt32(9), row.GetString(10),
            row.GetInt64(11), row.GetString(12), row.GetInt64(13), row.GetInt32(14) != 0, row.GetString(15),
            row.IsDBNull(16) ? null : row.GetString(16), row.IsDBNull(17) ? null : row.GetString(17),
            row.IsDBNull(18) ? null : row.GetString(18), row.IsDBNull(19) ? null : row.GetString(19));
    }

    private static void ValidateRecoveryInspectionScope(VerifiedAgentRequest verified, RecoveryIntentScope scope,
        string intentId, string requestId, SignedRecoveryInspection inspection, long nowUnixMs)
    {
        if (scope.MachineId != verified.MachineId || scope.MachineId != inspection.MachineId
            || !FixedHexEquals(verified.KeyFingerprint, inspection.KeyFingerprint)
            || inspection.IntentId != intentId || inspection.ChainId != scope.ChainId
            || inspection.HeadOperationId != scope.PreviousHeadOperationId
            || inspection.Generation != scope.Generation || inspection.RequestId != requestId
            || inspection.TargetVersion != scope.TargetVersion
            || !FixedHexEquals(inspection.TargetPackageSha256, scope.TargetPackageSha256)
            || inspection.HelperRuntime != scope.TargetRuntime || inspection.HelperProtocol != 1)
            throw new UnauthorizedAccessException("Recovery inspection is outside its signed intent, target, or Agent identity scope.");

        if (scope.Status != "awaitingInspection" || scope.ExpiresAtUnixMs <= nowUnixMs)
            throw new InvalidOperationException("Recovery intent is expired or no longer awaiting inspection.");
        if (!scope.Held || scope.CurrentHeadOperationId != scope.PreviousHeadOperationId
            || scope.CurrentGeneration != scope.Generation)
            throw new AgentRecoveryIntentConflictException(scope.ChainId, scope.CurrentHeadOperationId, scope.CurrentGeneration,
                "Recovery inspection is stale; refresh the current maintenance head and generation.");
        if (scope.TargetProtocol != 2 || scope.PreviousOperationStatus is "completed" or "rolledBack" or "expired" or "superseded")
            throw new InvalidOperationException("Recovery intent is not attached to an eligible held update operation.");
    }

    private static string RecoveryQualificationBlockReason(RecoveryIntentScope scope,
        SignedRecoveryInspection inspection)
    {
        if (scope.BackupOwnerOperationId is null || scope.BackupDigest is null
            || scope.BackupOwnerOperationId != inspection.OldBackupOwnerOperationId
            || !FixedHexEquals(scope.BackupDigest, inspection.OldBackupDigest))
            return "originalBackupBindingUnavailable";
        if (inspection.SourceAuthority == "boundOperationSource"
            && (scope.SourceVersion is null || scope.SourceEvidenceDigest is null
                || scope.SourceVersion != inspection.BoundSourceVersion
                || !FixedHexEquals(scope.SourceEvidenceDigest, inspection.BoundSourceEvidenceDigest ?? string.Empty)))
            return "originalBackupBindingUnavailable";

        // No independently pinned helper or exclusion qualification source is configured yet.
        return "helperBuildNotPinned";
    }

    private static string RecoveryInspectionChallengeBindingDigest(string intentId, string keyFingerprint,
        SignedRecoveryInspection inspection)
    {
        var evidenceDigest = Convert.ToHexString(SHA256.HashData(
            AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(inspection)));
        var canonical = string.Join('\n', "KamenRaider.AgentRecoveryInspectionChallenge.v1", intentId,
            keyFingerprint.ToUpperInvariant(), inspection.RequestId, inspection.ChainId,
            inspection.HeadOperationId, inspection.Generation.ToString(CultureInfo.InvariantCulture), evidenceDigest);
        return Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(canonical)));
    }

    private RecoveryInspectionChallenge? FindRecoveryInspectionChallenge(SqliteTransaction tx,
        RecoveryIntentScope scope, string keyFingerprint, string requestId)
    {
        using var query = Command("SELECT challenge_id,nonce_digest,challenge_binding_digest,expires_at_unix_ms,challenge_issued_at_unix_ms,challenge_issued_monotonic_tick,challenge_runtime_epoch FROM agent_update_evidence_challenges WHERE machine_id=$machine AND key_fingerprint=$fingerprint AND chain_id=$chain AND head_operation_id=$head AND generation=$generation AND purpose='recoveryInspection' AND challenge_request_id=$request AND consumed_at_unix_ms IS NULL",
            ("$machine", scope.MachineId), ("$fingerprint", keyFingerprint), ("$chain", scope.ChainId),
            ("$head", scope.PreviousHeadOperationId), ("$generation", scope.Generation), ("$request", requestId));
        query.Transaction = tx;
        using var row = query.ExecuteReader();
        if (!row.Read()) return null;
        if (row.IsDBNull(2) || row.IsDBNull(4) || row.IsDBNull(5) || row.IsDBNull(6))
            throw new InvalidDataException("Recovery inspection challenge lacks its durable scope or issue-time metadata.");
        return new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetInt64(3), row.GetInt64(4),
            row.GetInt64(5), row.GetString(6));
    }

    private RecoveryInspectionRow? FindRecoveryInspection(SqliteTransaction tx, string intentId, string requestId)
    {
        using var query = Command("SELECT key_fingerprint,request_body_digest,evidence_digest,block_reason FROM agent_update_recovery_inspections WHERE intent_id=$intent AND request_id=$request",
            ("$intent", intentId), ("$request", requestId));
        query.Transaction = tx;
        using var row = query.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3)) : null;
    }

    private bool VerifyRecoveryInspectionSignature(SqliteTransaction tx, VerifiedAgentRequest verified,
        SignedRecoveryInspection inspection)
    {
        if (string.IsNullOrWhiteSpace(inspection.EvidenceSignature) || inspection.EvidenceSignature.Length > 512)
            return false;
        using var publicKeyQuery = Command("SELECT public_key FROM machines WHERE machine_id=$machine",
            ("$machine", verified.MachineId));
        publicKeyQuery.Transaction = tx;
        if (publicKeyQuery.ExecuteScalar() is not string encodedPublicKey) return false;
        try
        {
            var publicKeyBytes = Convert.FromBase64String(encodedPublicKey);
            using var publicKey = ECDsa.Create();
            publicKey.ImportSubjectPublicKeyInfo(publicKeyBytes, out var bytesRead);
            var actualFingerprint = Convert.ToHexString(SHA256.HashData(publicKeyBytes));
            return bytesRead == publicKeyBytes.Length
                && FixedHexEquals(actualFingerprint, verified.KeyFingerprint)
                && FixedHexEquals(actualFingerprint, inspection.KeyFingerprint)
                && publicKey.VerifyData(AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(inspection),
                    Convert.FromBase64String(inspection.EvidenceSignature), HashAlgorithmName.SHA256);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private bool IsActiveRecoveryInspectionChallenge(long issuedAtUnixMs, long issuedMonotonicTick,
        long expiresAtUnixMs, string runtimeEpoch, long nowUnixMs)
    {
        if (!string.Equals(runtimeEpoch, syncProofRuntimeEpoch, StringComparison.Ordinal)) return false;
        var nowTick = clock.GetTimestamp();
        if (nowUnixMs < issuedAtUnixMs || nowTick < issuedMonotonicTick)
            throw new InvalidOperationException("Controller clock moved backwards during recovery inspection challenge use.");
        if (nowUnixMs >= expiresAtUnixMs) return false;
        var elapsed = clock.GetElapsedTime(issuedMonotonicTick, nowTick);
        if (elapsed < TimeSpan.Zero)
            throw new InvalidOperationException("Controller monotonic time moved backwards during recovery inspection challenge use.");
        return elapsed < AgentRecoveryInspectionChallengeLifetime;
    }

    private static string NewRecoveryInspectionChallengeToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string RecoveryRequestBodyDigest<T>(T request)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, RecoveryIntentJsonOptions)));

    private static string RecoveryIntentRequestDigest(string machineId, string chainId, string headId,
        long expectedGeneration, string targetReleaseVersion, string reason, string requestId, string actorId)
    {
        var canonical = string.Join('\n', "KamenRaider.AgentRecoveryIntent.v1", machineId, chainId, headId,
            expectedGeneration.ToString(CultureInfo.InvariantCulture), targetReleaseVersion, reason, requestId, actorId);
        return Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(canonical)));
    }

    private static void RecoveryIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
            throw new ArgumentException("Invalid recovery identifier.", parameterName);
    }
}
