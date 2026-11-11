using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KamenRaider.Contracts;
using KamenRaider.Controller.Security;
using Microsoft.Data.Sqlite;

namespace KamenRaider.Controller.Data;

public sealed class AgentUpdateSourceChallengeRenewalRequiredException : InvalidOperationException
{
    public AgentUpdateSourceChallengeRenewalRequiredException()
        : base("The unconsumed source challenge has expired or belongs to a previous Controller runtime.") { }
}

public sealed partial class ControllerDatabase
{
    private static readonly JsonSerializerOptions SourceBindingJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan AgentUpdateSourceChallengeLifetime = TimeSpan.FromMinutes(10);
    private const int MaximumActiveSourceChallengesPerScope = 5;

    private sealed record AgentUpdateSourceScope(string ChainId, string HeadOperationId, long Generation,
        long TicketGeneration, bool Held, string? TargetRuntime, int? TargetProtocol, string Status,
        long? StartedAtUnixMs, string? SourceVersion, string? SourceEvidenceDigest,
        string? BackupOwnerOperationId, string? BackupDigest);

    public AgentUpdateSourceChallengeResponse IssueAgentUpdateSourceChallenge(
        VerifiedAgentRequest verified, string operationId, AgentUpdateSourceChallengeRequest request)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(request);
        Identifier(operationId);
        ValidateCanonicalVerifiedBody(verified, request);
        var expectedPath = $"/agent/machines/{verified.MachineId}/agent-updates/{operationId}/source-challenge";
        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            if (!ValidateVerifiedRequest(tx, verified, expectedPath))
                throw new UnauthorizedAccessException("Signed source challenge request has an invalid identity or path.");
            if (!AcceptScopedNonce(tx, verified, enforceCutoff: false))
                throw new UnauthorizedAccessException("Signed source challenge request was replayed.");

            var scope = ReadAgentUpdateSourceScope(tx, verified.MachineId, operationId,
                AgentUpdateTicketHash(request.Ticket), request.ReleaseVersion, request.TargetVersion, request.TargetPackageSha256);
            ValidateSourcePreparation(scope, request.HelperBuildSha256, request.HelperRuntime, request.HelperProtocol);
            if (!string.Equals(scope.HeadOperationId, operationId, StringComparison.Ordinal))
                throw new InvalidOperationException("Source challenge does not target the held chain head.");
            Identifier(request.RequestId);

            var now = clock.GetUtcNow();
            var bindingDigest = verified.BodyDigest.ToUpperInvariant();
            using (var prior = Command("SELECT challenge_id,nonce_digest,expires_at_unix_ms,challenge_binding_digest,challenge_issued_at_unix_ms,challenge_issued_monotonic_tick,challenge_runtime_epoch FROM agent_update_evidence_challenges WHERE machine_id=$machine AND key_fingerprint=$fingerprint AND chain_id=$chain AND head_operation_id=$head AND generation=$generation AND purpose='source' AND challenge_request_id=$request",
                ("$machine", verified.MachineId), ("$fingerprint", verified.KeyFingerprint), ("$chain", scope.ChainId),
                ("$head", scope.HeadOperationId), ("$generation", scope.Generation), ("$request", request.RequestId)))
            {
                prior.Transaction = tx;
                using var row = prior.ExecuteReader();
                if (row.Read())
                {
                    var priorDigest = row.IsDBNull(3) ? null : row.GetString(3);
                    if (!string.Equals(priorDigest, bindingDigest, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Source challenge request ID was reused with a different body.");
                    var challengeId = row.GetString(0);
                    var tokenDigest = row.GetString(1);
                    var expiresAt = row.GetInt64(2);
                    if (row.IsDBNull(4) || row.IsDBNull(5) || row.IsDBNull(6))
                        throw new InvalidOperationException("Source challenge lacks trusted issue-time metadata.");
                    EnsureSourceChallengeFresh(row.GetInt64(4), row.GetInt64(5), expiresAt, row.GetString(6), now.ToUnixTimeMilliseconds());
                    var challenge = CreateAgentUpdateSourceChallengeToken(verified.MachineId, verified.KeyFingerprint,
                        scope.ChainId, scope.HeadOperationId, scope.Generation, challengeId, bindingDigest, syncProofRuntimeEpoch);
                    if (!FixedHexEquals(tokenDigest, Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(challenge)))))
                        throw new InvalidOperationException("Source challenge cannot be reconstructed in this Controller runtime.");
                    tx.Commit();
                    return new(challengeId, challenge, syncProofRuntimeEpoch, DateTimeOffset.FromUnixTimeMilliseconds(expiresAt));
                }
            }

            using (var activeCount = Command("SELECT expires_at_unix_ms,challenge_issued_at_unix_ms,challenge_issued_monotonic_tick,challenge_runtime_epoch FROM agent_update_evidence_challenges WHERE machine_id=$machine AND key_fingerprint=$fingerprint AND chain_id=$chain AND head_operation_id=$head AND generation=$generation AND purpose='source' AND challenge_runtime_epoch=$epoch",
                ("$machine", verified.MachineId), ("$fingerprint", verified.KeyFingerprint), ("$chain", scope.ChainId),
                ("$head", scope.HeadOperationId), ("$generation", scope.Generation), ("$epoch", syncProofRuntimeEpoch)))
            {
                activeCount.Transaction = tx;
                using var rows = activeCount.ExecuteReader();
                var active = 0;
                while (rows.Read())
                {
                    if (rows.IsDBNull(1) || rows.IsDBNull(2) || rows.IsDBNull(3))
                        throw new InvalidOperationException("An active source challenge lacks trusted issue-time metadata.");
                    if (IsActiveSourceChallenge(rows.GetInt64(1), rows.GetInt64(2), rows.GetInt64(0), rows.GetString(3), now.ToUnixTimeMilliseconds()))
                        active++;
                }
                if (active >= MaximumActiveSourceChallengesPerScope)
                    throw new InvalidOperationException("Source challenge issuance limit reached for this maintenance generation.");
            }

            var newChallengeId = Guid.NewGuid().ToString("N");
            var expires = now.Add(AgentUpdateSourceChallengeLifetime);
            var issuedAtUnixMs = now.ToUnixTimeMilliseconds();
            var issuedMonotonicTick = clock.GetTimestamp();
            var expiresAtUnixMs = expires.ToUnixTimeMilliseconds();
            var newToken = CreateAgentUpdateSourceChallengeToken(verified.MachineId, verified.KeyFingerprint,
                scope.ChainId, scope.HeadOperationId, scope.Generation, newChallengeId, bindingDigest, syncProofRuntimeEpoch);
            var newTokenDigest = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(newToken)));
            using (var insert = Command("INSERT INTO agent_update_evidence_challenges(challenge_id,machine_id,key_fingerprint,chain_id,head_operation_id,generation,purpose,nonce_digest,expires_at_unix_ms,challenge_binding_digest,challenge_request_id,challenge_issued_at_unix_ms,challenge_issued_monotonic_tick,challenge_runtime_epoch) VALUES($id,$machine,$fingerprint,$chain,$head,$generation,'source',$nonce,$expires,$binding,$request,$issuedAt,$issuedTick,$epoch)",
                ("$id", newChallengeId), ("$machine", verified.MachineId), ("$fingerprint", verified.KeyFingerprint),
                ("$chain", scope.ChainId), ("$head", scope.HeadOperationId), ("$generation", scope.Generation),
                ("$nonce", newTokenDigest), ("$expires", expiresAtUnixMs), ("$binding", bindingDigest),
                ("$request", request.RequestId), ("$issuedAt", issuedAtUnixMs), ("$issuedTick", issuedMonotonicTick),
                ("$epoch", syncProofRuntimeEpoch)))
            {
                insert.Transaction = tx;
                insert.ExecuteNonQuery();
            }
            tx.Commit();
            return new(newChallengeId, newToken, syncProofRuntimeEpoch, DateTimeOffset.FromUnixTimeMilliseconds(expiresAtUnixMs));
        }
    }

    public AgentUpdateOperationDto BindAgentUpdateSource(
        VerifiedAgentRequest verified, string operationId, AgentUpdateSourceBindingRequest request)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(request);
        Identifier(operationId);
        ValidateCanonicalVerifiedBody(verified, request);
        var expectedPath = $"/agent/machines/{verified.MachineId}/agent-updates/{operationId}/source-binding";
        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            if (!ValidateVerifiedRequest(tx, verified, expectedPath))
                throw new UnauthorizedAccessException("Signed source binding request has an invalid identity or path.");
            if (!AcceptScopedNonce(tx, verified, enforceCutoff: false))
                throw new UnauthorizedAccessException("Signed source binding request was replayed.");

            Identifier(request.RequestId);
            Identifier(request.ChallengeRequestId);
            Identifier(request.SourceVersion);
            if (request.SourceVersion.Equals(request.TargetVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The source and target Agent versions must differ.");
            var scope = ReadAgentUpdateSourceScope(tx, verified.MachineId, operationId,
                AgentUpdateTicketHash(request.Ticket), request.ReleaseVersion, request.TargetVersion, request.TargetPackageSha256);
            ValidateSourcePreparation(scope, request.HelperBuildSha256, request.HelperRuntime, request.HelperProtocol);
            if (scope.Status != "installing" || scope.StartedAtUnixMs is null
                || !string.Equals(scope.HeadOperationId, operationId, StringComparison.Ordinal)
                || request.ChainId != scope.ChainId || request.HeadOperationId != scope.HeadOperationId || request.Generation != scope.Generation
                || request.BackupOwnerOperationId != operationId || request.JournalPhase != "backupPublished"
                || request.NativeBootId.Length is < 1 or > 256 || request.NativeBootId.Any(char.IsControl)
                || !IsSha256(request.BackupDigest) || !IsSha256(request.InventoryDigest) || !IsSha256(request.InstalledTreeDigest)
                || !Guid.TryParseExact(request.ChallengeId, "N", out _))
                throw new InvalidOperationException("Source binding is outside the active maintenance scope or journal phase.");

            var challengeRequest = new AgentUpdateSourceChallengeRequest(request.Ticket, request.ReleaseVersion,
                request.TargetVersion, request.TargetPackageSha256, request.ChallengeRequestId, request.HelperBuildSha256,
                request.HelperRuntime, request.HelperProtocol);
            var expectedChallengeBodyDigest = CanonicalBodyDigest(challengeRequest);
            var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
            string challengeTokenDigest;
            long? consumedAt;
            string? consumedBy;
            string? challengeBindingDigest;
            long challengeIssuedAtUnixMs;
            long challengeIssuedMonotonicTick;
            string challengeRuntimeEpoch;
            long challengeExpiresAtUnixMs;
            using (var challengeQuery = Command("SELECT nonce_digest,expires_at_unix_ms,consumed_at_unix_ms,request_id,challenge_binding_digest,challenge_request_id,challenge_issued_at_unix_ms,challenge_issued_monotonic_tick,challenge_runtime_epoch FROM agent_update_evidence_challenges WHERE challenge_id=$id AND machine_id=$machine AND key_fingerprint=$fingerprint AND chain_id=$chain AND head_operation_id=$head AND generation=$generation AND purpose='source'",
                ("$id", request.ChallengeId), ("$machine", verified.MachineId), ("$fingerprint", verified.KeyFingerprint),
                ("$chain", scope.ChainId), ("$head", scope.HeadOperationId), ("$generation", scope.Generation)))
            {
                challengeQuery.Transaction = tx;
                using var row = challengeQuery.ExecuteReader();
                if (!row.Read()) throw new UnauthorizedAccessException("Source challenge is unknown or outside the maintenance scope.");
                challengeTokenDigest = row.GetString(0);
                challengeExpiresAtUnixMs = row.GetInt64(1);
                consumedAt = row.IsDBNull(2) ? null : row.GetInt64(2);
                consumedBy = row.IsDBNull(3) ? null : row.GetString(3);
                challengeBindingDigest = row.IsDBNull(4) ? null : row.GetString(4);
                if (row.IsDBNull(5) || row.GetString(5) != request.ChallengeRequestId)
                    throw new UnauthorizedAccessException("Source challenge request binding does not match.");
                if (row.IsDBNull(6) || row.IsDBNull(7) || row.IsDBNull(8))
                    throw new InvalidOperationException("Source challenge lacks trusted issue-time metadata.");
                challengeIssuedAtUnixMs = row.GetInt64(6);
                challengeIssuedMonotonicTick = row.GetInt64(7);
                challengeRuntimeEpoch = row.GetString(8);
            }
            if (!FixedHexEquals(challengeBindingDigest ?? string.Empty, expectedChallengeBodyDigest))
                throw new UnauthorizedAccessException("Source challenge body binding does not match.");

            if (consumedAt is not null)
            {
                if (!string.Equals(consumedBy, request.RequestId, StringComparison.Ordinal)
                    || !string.Equals(scope.SourceVersion, request.SourceVersion, StringComparison.Ordinal)
                    || !FixedHexEquals(scope.SourceEvidenceDigest ?? string.Empty, verified.BodyDigest)
                    || !string.Equals(scope.BackupOwnerOperationId, request.BackupOwnerOperationId, StringComparison.Ordinal)
                    || !FixedHexEquals(scope.BackupDigest ?? string.Empty, request.BackupDigest))
                    throw new InvalidOperationException("Source binding request ID was reused with a different body.");
                var priorResult = FindAgentUpdateOperation(tx, operationId);
                tx.Commit();
                return priorResult;
            }
            if (request.RuntimeEpoch != syncProofRuntimeEpoch || challengeRuntimeEpoch != syncProofRuntimeEpoch)
                throw new InvalidOperationException("The source challenge belongs to a previous Controller runtime.");
            EnsureSourceChallengeFresh(challengeIssuedAtUnixMs, challengeIssuedMonotonicTick,
                challengeExpiresAtUnixMs, challengeRuntimeEpoch, nowMs);
            var expectedChallengeToken = CreateAgentUpdateSourceChallengeToken(verified.MachineId, verified.KeyFingerprint,
                scope.ChainId, scope.HeadOperationId, scope.Generation, request.ChallengeId,
                expectedChallengeBodyDigest, syncProofRuntimeEpoch);
            if (!FixedHexEquals(challengeTokenDigest,
                    Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(expectedChallengeToken))))
                || !FixedBase64UrlEquals(expectedChallengeToken, request.Challenge))
                throw new UnauthorizedAccessException("Source challenge token is invalid.");
            if (scope.SourceVersion is not null || scope.SourceEvidenceDigest is not null || scope.BackupDigest is not null)
                throw new InvalidOperationException("Agent update source provenance is already bound.");

            var evidence = new AgentUpdateSourceEvidence(verified.MachineId, verified.KeyFingerprint, operationId,
                scope.ChainId, scope.HeadOperationId, scope.Generation, request.TargetVersion,
                request.TargetPackageSha256, request.ChallengeId, request.ChallengeRequestId,
                request.RequestId, request.HelperBuildSha256, request.HelperRuntime, request.HelperProtocol,
                request.RuntimeEpoch, request.SourceVersion, request.BackupOwnerOperationId,
                request.BackupDigest, request.InventoryDigest, request.InstalledTreeDigest, request.NativeBootId, request.JournalPhase);
            if (!VerifySourceEvidenceSignature(tx, verified.KeyFingerprint, evidence, request.EvidenceSignature))
                throw new UnauthorizedAccessException("Enrollment-key source evidence signature is invalid.");

            using (var consume = Command("UPDATE agent_update_evidence_challenges SET consumed_at_unix_ms=$now,request_id=$request WHERE challenge_id=$id AND consumed_at_unix_ms IS NULL AND expires_at_unix_ms>$now",
                ("$now", nowMs), ("$request", request.RequestId), ("$id", request.ChallengeId)))
            {
                consume.Transaction = tx;
                if (consume.ExecuteNonQuery() != 1) throw new InvalidOperationException("Source challenge was already consumed or expired.");
            }
            using (var bind = Command("UPDATE agent_update_operations SET source_version=$source,source_evidence_digest=$evidence,backup_owner_operation_id=$owner,backup_digest=$backup,updated_at_unix_ms=$now WHERE operation_id=$operation AND source_version IS NULL AND source_evidence_digest IS NULL AND backup_owner_operation_id IS NULL AND backup_digest IS NULL AND status='installing' AND ticket_started_at_unix_ms IS NOT NULL AND EXISTS(SELECT 1 FROM agent_maintenance_chains c WHERE c.chain_id=agent_update_operations.chain_id AND c.machine_id=agent_update_operations.machine_id AND c.current_head_operation_id=agent_update_operations.operation_id AND c.held=1 AND c.generation=agent_update_operations.ticket_generation)",
                ("$source", request.SourceVersion), ("$evidence", verified.BodyDigest.ToUpperInvariant()),
                ("$owner", request.BackupOwnerOperationId), ("$backup", request.BackupDigest.ToUpperInvariant()),
                ("$now", nowMs), ("$operation", operationId)))
            {
                bind.Transaction = tx;
                if (bind.ExecuteNonQuery() != 1) throw new InvalidOperationException("Agent update source binding lost its maintenance compare-and-swap.");
            }
            var result = FindAgentUpdateOperation(tx, operationId);
            tx.Commit();
            return result;
        }
    }

    private AgentUpdateSourceScope ReadAgentUpdateSourceScope(SqliteTransaction tx, string machineId, string operationId,
        string? ticketHash, string releaseVersion, string targetVersion, string targetPackageSha256)
    {
        if (ticketHash is null) throw new UnauthorizedAccessException("Update ticket is invalid.");
        var canonicalHash = CanonicalAgentUpdateHash(targetPackageSha256);
        using var operation = Command("SELECT o.chain_id,c.current_head_operation_id,c.generation,o.ticket_generation,c.held,o.target_runtime,o.target_protocol,o.status,o.ticket_started_at_unix_ms,o.source_version,o.source_evidence_digest,o.backup_owner_operation_id,o.backup_digest FROM agent_update_operations o JOIN agent_maintenance_chains c ON c.chain_id=o.chain_id AND c.machine_id=o.machine_id WHERE o.operation_id=$operation AND o.machine_id=$machine AND o.ticket_hash=$ticket AND o.release_version=$release AND o.target_version=$target AND upper(o.target_package_sha256)=upper($hash)",
            ("$operation", operationId), ("$machine", machineId), ("$ticket", ticketHash), ("$release", releaseVersion),
            ("$target", targetVersion), ("$hash", canonicalHash));
        operation.Transaction = tx;
        using var row = operation.ExecuteReader();
        if (!row.Read()) throw new UnauthorizedAccessException("Update ticket is invalid.");
        return new(row.GetString(0), row.GetString(1), row.GetInt64(2), row.GetInt64(3), row.GetInt64(4) == 1,
            row.IsDBNull(5) ? null : row.GetString(5), row.IsDBNull(6) ? null : row.GetInt32(6), row.GetString(7),
            row.IsDBNull(8) ? null : row.GetInt64(8), row.IsDBNull(9) ? null : row.GetString(9),
            row.IsDBNull(10) ? null : row.GetString(10), row.IsDBNull(11) ? null : row.GetString(11),
            row.IsDBNull(12) ? null : row.GetString(12));
    }

    private static void ValidateSourcePreparation(AgentUpdateSourceScope scope, string helperBuildSha256,
        string helperRuntime, int helperProtocol)
    {
        if (!scope.Held || scope.HeadOperationId is null || scope.Generation != scope.TicketGeneration
            || scope.Status != "installing" || scope.StartedAtUnixMs is null || scope.TargetProtocol != 2
            || scope.TargetRuntime is not ("win-x64" or "win-arm64") || helperRuntime != scope.TargetRuntime
            || helperProtocol != 1 || !IsSha256(helperBuildSha256))
            throw new InvalidOperationException("Agent update is not in a valid source-preparation state.");
    }

    private static void ValidateCanonicalVerifiedBody<T>(VerifiedAgentRequest verified, T request)
    {
        if (!FixedHexEquals(verified.BodyDigest, CanonicalBodyDigest(request)))
            throw new UnauthorizedAccessException("Signed Agent body is not the canonical request body.");
    }

    private static string CanonicalBodyDigest<T>(T request)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, SourceBindingJsonOptions)));

    private bool IsActiveSourceChallenge(long issuedAtUnixMs, long issuedMonotonicTick, long expiresAtUnixMs,
        string runtimeEpoch, long nowUnixMs)
    {
        if (!string.Equals(runtimeEpoch, syncProofRuntimeEpoch, StringComparison.Ordinal)) return false;
        var currentTick = clock.GetTimestamp();
        if (nowUnixMs < issuedAtUnixMs || currentTick < issuedMonotonicTick)
            throw new InvalidOperationException("Controller clock moved backwards during source challenge issuance.");
        if (nowUnixMs >= expiresAtUnixMs) return false;
        var elapsed = clock.GetElapsedTime(issuedMonotonicTick, currentTick);
        if (elapsed < TimeSpan.Zero)
            throw new InvalidOperationException("Controller monotonic time moved backwards during source challenge issuance.");
        return elapsed < AgentUpdateSourceChallengeLifetime;
    }

    private void EnsureSourceChallengeFresh(long issuedAtUnixMs, long issuedMonotonicTick, long expiresAtUnixMs,
        string runtimeEpoch, long nowUnixMs)
    {
        if (!IsActiveSourceChallenge(issuedAtUnixMs, issuedMonotonicTick, expiresAtUnixMs, runtimeEpoch, nowUnixMs))
            throw new AgentUpdateSourceChallengeRenewalRequiredException();
    }

    private string CreateAgentUpdateSourceChallengeToken(string machineId, string keyFingerprint, string chainId,
        string headOperationId, long generation, string challengeId, string challengeBodyDigest, string runtimeEpoch)
    {
        var canonical = string.Join('\n', "KamenRaider.AgentUpdateSourceChallenge.v1", machineId, keyFingerprint,
            chainId, headOperationId, generation.ToString(CultureInfo.InvariantCulture), challengeId,
            challengeBodyDigest.ToUpperInvariant(), runtimeEpoch);
        return Convert.ToBase64String(HMACSHA256.HashData(syncProofHmacKey, Encoding.UTF8.GetBytes(canonical)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private bool VerifySourceEvidenceSignature(SqliteTransaction tx, string keyFingerprint,
        AgentUpdateSourceEvidence evidence, string signature)
    {
        if (string.IsNullOrWhiteSpace(signature) || signature.Length > 512) return false;
        using var publicKeyQuery = Command("SELECT public_key FROM machines WHERE machine_id=$machine", ("$machine", evidence.MachineId));
        publicKeyQuery.Transaction = tx;
        if (publicKeyQuery.ExecuteScalar() is not string encodedPublicKey) return false;
        try
        {
            var publicKeyBytes = Convert.FromBase64String(encodedPublicKey);
            using var publicKey = ECDsa.Create();
            publicKey.ImportSubjectPublicKeyInfo(publicKeyBytes, out var bytesRead);
            if (bytesRead != publicKeyBytes.Length
                || !FixedHexEquals(Convert.ToHexString(SHA256.HashData(publicKeyBytes)), keyFingerprint)) return false;
            return publicKey.VerifyData(AgentUpdateSourceEvidenceCanonicalizer.CanonicalBytes(evidence),
                Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static bool FixedBase64UrlEquals(string left, string right)
    {
        if (left.Length != right.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    }

    private static bool IsSha256(string value)
    {
        if (value is null || value.Length != 64) return false;
        try { return Convert.FromHexString(value).Length == 32; }
        catch (FormatException) { return false; }
    }
}
