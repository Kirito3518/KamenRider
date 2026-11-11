using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using KamenRaider.Controller.Security;

namespace KamenRaider.Controller.Data;

public sealed partial class ControllerDatabase
{
    private static readonly TimeSpan SyncTokenLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProvenHeartbeatLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LegacySignedRequestWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SignedNonceRetention = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SyncChallengeRateWindow = TimeSpan.FromSeconds(10);
    private const int MaximumSignedRequestNoncesPerScope = 4096;
    private const int MaximumSyncChallengeResponsesPerWindow = 2;
    private const string ProtocolTwoSyncNonceScope = "/agent/sync#protocol2";

    private sealed record SyncProofState(
        string? KeyFingerprint,
        string Mode,
        long Sequence,
        long? TokenSequence,
        string? TokenDigest,
        string? TokenEpoch,
        long? TokenIssuedTick,
        long? LastProvenSequence,
        string? LastProvenRuntimeEpoch,
        long? LastProvenTick,
        string? LastProvenVersion,
        long? LegacyLastSecond,
        long? ChallengeWindowTick,
        int ChallengeResponseCount);

    private sealed record SyncProofBinding(string ChainId, string HeadOperationId, long Generation, long ReportGeneration);

    private SyncProofState? ReadSyncProofState(SqliteTransaction tx, string machineId)
    {
        using var command = Command("SELECT key_fingerprint,mode,sequence,token_sequence,token_digest,token_epoch,token_issued_tick,last_proven_sequence,last_proven_runtime_epoch,last_proven_tick,last_proven_version,legacy_last_second,challenge_window_tick,challenge_response_count FROM agent_sync_proof_state WHERE machine_id=$machine", ("$machine", machineId));
        command.Transaction = tx;
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        return new(
            row.IsDBNull(0) ? null : row.GetString(0),
            row.GetString(1),
            row.GetInt64(2),
            row.IsDBNull(3) ? null : row.GetInt64(3),
            row.IsDBNull(4) ? null : row.GetString(4),
            row.IsDBNull(5) ? null : row.GetString(5),
            row.IsDBNull(6) ? null : row.GetInt64(6),
            row.IsDBNull(7) ? null : row.GetInt64(7),
            row.IsDBNull(8) ? null : row.GetString(8),
            row.IsDBNull(9) ? null : row.GetInt64(9),
            row.IsDBNull(10) ? null : row.GetString(10),
            row.IsDBNull(11) ? null : row.GetInt64(11),
            row.IsDBNull(12) ? null : row.GetInt64(12),
            row.GetInt32(13));
    }

    private static bool SyncProofKeyMatches(SyncProofState? state, string keyFingerprint)
        => state is not null && (state.KeyFingerprint is null
            || string.Equals(state.KeyFingerprint, keyFingerprint, StringComparison.OrdinalIgnoreCase));

    private SyncProofBinding ReadSyncProofBinding(SqliteTransaction tx, string machineId)
    {
        using var command = Command("SELECT c.chain_id,c.current_head_operation_id,c.generation,COALESCE((SELECT MAX(r.generation) FROM agent_update_reports r WHERE r.operation_id=c.current_head_operation_id),0) FROM agent_maintenance_chains c WHERE c.machine_id=$machine ORDER BY c.held DESC,c.rowid DESC LIMIT 1", ("$machine", machineId));
        command.Transaction = tx;
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetString(1), row.GetInt64(2), row.GetInt64(3)) : new("", "", 0, 0);
    }

    private string IssueOrReconstructSyncToken(SqliteTransaction tx, string machineId, string keyFingerprint)
    {
        var state = ReadSyncProofState(tx, machineId) ?? throw new InvalidOperationException("Agent proof state is missing.");
        var binding = ReadSyncProofBinding(tx, machineId);
        var nowTick = clock.GetTimestamp();
        if (state.TokenSequence is { } tokenSequence && state.TokenDigest is { } tokenDigest
            && state.TokenEpoch == syncProofRuntimeEpoch && state.TokenIssuedTick is { } issuedTick
            && nowTick >= issuedTick
            && clock.GetElapsedTime(issuedTick, nowTick) <= SyncTokenLifetime)
        {
            var reconstructed = CreateSyncToken(machineId, keyFingerprint, syncProofRuntimeEpoch, tokenSequence, binding);
            var reconstructedDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reconstructed)));
            if (FixedHexEquals(tokenDigest, reconstructedDigest)) return reconstructed;
        }

        var nextSequence = checked(Math.Max(state.Sequence, state.TokenSequence ?? 0) + 1);
        var token = CreateSyncToken(machineId, keyFingerprint, syncProofRuntimeEpoch, nextSequence, binding);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        using var update = Command("UPDATE agent_sync_proof_state SET key_fingerprint=$fingerprint,sequence=$sequence,token_sequence=$sequence,token_digest=$digest,token_epoch=$epoch,token_issued_tick=$tick WHERE machine_id=$machine",
            ("$fingerprint", keyFingerprint), ("$sequence", nextSequence), ("$digest", digest), ("$epoch", syncProofRuntimeEpoch), ("$tick", nowTick), ("$machine", machineId));
        update.Transaction = tx;
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Agent proof state changed during challenge issuance.");
        return token;
    }

    private bool IsCurrentSyncToken(SqliteTransaction tx, VerifiedAgentRequest verified, string? suppliedToken)
    {
        if (string.IsNullOrEmpty(suppliedToken) || suppliedToken.Length > 128) return false;
        var state = ReadSyncProofState(tx, verified.MachineId);
        if (state is null || state.TokenSequence is not { } tokenSequence || state.TokenDigest is not { } tokenDigest
            || state.TokenEpoch != syncProofRuntimeEpoch || state.TokenIssuedTick is not { } issuedTick
            || state.LastProvenSequence is { } priorSequence && tokenSequence <= priorSequence
            || !string.Equals(state.KeyFingerprint, verified.KeyFingerprint, StringComparison.OrdinalIgnoreCase))
            return false;
        var nowTick = clock.GetTimestamp();
        if (nowTick < issuedTick || clock.GetElapsedTime(issuedTick, nowTick) > SyncTokenLifetime) return false;
        var binding = ReadSyncProofBinding(tx, verified.MachineId);
        var reconstructed = CreateSyncToken(verified.MachineId, verified.KeyFingerprint, syncProofRuntimeEpoch, tokenSequence, binding);
        var expectedDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reconstructed)));
        var suppliedDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(suppliedToken)));
        return FixedHexEquals(tokenDigest, expectedDigest) && FixedHexEquals(tokenDigest, suppliedDigest);
    }

    private string CreateSyncToken(string machineId, string keyFingerprint, string runtimeEpoch, long sequence, SyncProofBinding binding)
    {
        var canonical = string.Join('\n', "KamenRaider.AgentSyncProof.v2", machineId, keyFingerprint, runtimeEpoch,
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), "sync", binding.ChainId,
            binding.HeadOperationId, binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            binding.ReportGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var token = HMACSHA256.HashData(syncProofHmacKey, Encoding.UTF8.GetBytes(canonical));
        return Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool FixedHexEquals(string left, string right)
    {
        try
        {
            var leftBytes = Convert.FromHexString(left);
            var rightBytes = Convert.FromHexString(right);
            return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException) { return false; }
    }

    private bool IsRecentProvenHeartbeat(string? runtimeEpoch, long? proofTick)
    {
        if (runtimeEpoch != syncProofRuntimeEpoch || proofTick is not { } tick) return false;
        var nowTick = clock.GetTimestamp();
        return nowTick >= tick && clock.GetElapsedTime(tick, nowTick) <= ProvenHeartbeatLifetime;
    }

    private bool AcceptScopedNonce(SqliteTransaction tx, VerifiedAgentRequest verified, bool enforceCutoff = true,
        string? scopePathOverride = null, string? duplicateScopePath = null)
    {
        var scopePath = scopePathOverride ?? verified.Path;
        long? existingCutoff;
        using (var cutoffQuery = Command("SELECT cutoff_second FROM agent_signed_request_scopes WHERE machine_id=$machine AND key_fingerprint=$fingerprint AND path=$path",
            ("$machine", verified.MachineId), ("$fingerprint", verified.KeyFingerprint), ("$path", scopePath)))
        {
            cutoffQuery.Transaction = tx;
            var value = cutoffQuery.ExecuteScalar();
            existingCutoff = value is long cutoff ? cutoff : null;
            if (enforceCutoff && existingCutoff is { } durableCutoff && verified.SignedSeconds < durableCutoff) return false;
        }

        using (var duplicate = Command("SELECT 1 FROM agent_signed_request_nonces WHERE machine_id=$machine AND key_fingerprint=$fingerprint AND path=$path AND nonce_digest=$nonce",
            ("$machine", verified.MachineId), ("$fingerprint", verified.KeyFingerprint), ("$path", scopePath), ("$nonce", verified.NonceDigest)))
        {
            duplicate.Transaction = tx;
            if (duplicate.ExecuteScalar() is not null) return false;
        }
        if (duplicateScopePath is not null && duplicateScopePath != scopePath)
        {
            using var priorScopeDuplicate = Command("SELECT 1 FROM agent_signed_request_nonces WHERE machine_id=$machine AND key_fingerprint=$fingerprint AND path=$path AND nonce_digest=$nonce",
                ("$machine", verified.MachineId), ("$fingerprint", verified.KeyFingerprint), ("$path", duplicateScopePath), ("$nonce", verified.NonceDigest));
            priorScopeDuplicate.Transaction = tx;
            if (priorScopeDuplicate.ExecuteScalar() is not null) return false;
        }

        var serverNow = clock.GetUtcNow().ToUnixTimeSeconds();
        var retentionSeconds = (long)SignedNonceRetention.TotalSeconds;
        var serverCutoff = serverNow - retentionSeconds;
        var signedCutoff = verified.SignedSeconds - (long)SignedNonceRetention.TotalSeconds;
        var nextCutoff = Math.Max(existingCutoff ?? long.MinValue, enforceCutoff ? signedCutoff : serverCutoff);
        // Protocol-2 signed seconds are audit input only. Retain from server receipt time,
        // clamped to the prior durable server-time high-water mark on Controller clock rollback.
        var priorServerTime = existingCutoff is { } priorCutoff ? checked(priorCutoff + retentionSeconds) : serverNow;
        var retentionSecond = enforceCutoff ? verified.SignedSeconds : Math.Max(serverNow, priorServerTime);
        using (var scope = Command("INSERT INTO agent_signed_request_scopes(machine_id,key_fingerprint,path,cutoff_second) VALUES($machine,$fingerprint,$path,$cutoff) ON CONFLICT(machine_id,key_fingerprint,path) DO UPDATE SET cutoff_second=MAX(cutoff_second,excluded.cutoff_second)",
            ("$machine", verified.MachineId), ("$fingerprint", verified.KeyFingerprint), ("$path", scopePath), ("$cutoff", nextCutoff)))
        {
            scope.Transaction = tx;
            scope.ExecuteNonQuery();
        }
        using (var prune = Command("DELETE FROM agent_signed_request_nonces WHERE machine_id=$machine AND key_fingerprint=$fingerprint AND path=$path AND retention_second < (SELECT cutoff_second FROM agent_signed_request_scopes WHERE machine_id=$machine AND key_fingerprint=$fingerprint AND path=$path)",
            ("$machine", verified.MachineId), ("$fingerprint", verified.KeyFingerprint), ("$path", scopePath)))
        {
            prune.Transaction = tx;
            prune.ExecuteNonQuery();
        }
        using (var count = Command("SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id=$machine AND key_fingerprint=$fingerprint AND path=$path",
            ("$machine", verified.MachineId), ("$fingerprint", verified.KeyFingerprint), ("$path", scopePath)))
        {
            count.Transaction = tx;
            if (Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) >= MaximumSignedRequestNoncesPerScope)
                return false;
        }
        using var insert = Command("INSERT INTO agent_signed_request_nonces(machine_id,key_fingerprint,path,nonce_digest,signed_second,retention_second) VALUES($machine,$fingerprint,$path,$nonce,$signed,$retention)",
            ("$machine", verified.MachineId), ("$fingerprint", verified.KeyFingerprint), ("$path", scopePath),
            ("$nonce", verified.NonceDigest), ("$signed", verified.SignedSeconds), ("$retention", retentionSecond));
        insert.Transaction = tx;
        return insert.ExecuteNonQuery() == 1;
    }

    private bool TryConsumeSyncChallengeResponse(SqliteTransaction tx, string machineId)
    {
        var state = ReadSyncProofState(tx, machineId) ?? throw new InvalidOperationException("Agent proof state is missing.");
        var nowTick = clock.GetTimestamp();
        if (state.ChallengeWindowTick is { } existingTick && nowTick < existingTick) return false;
        var newWindow = state.ChallengeWindowTick is not { } startTick
            || clock.GetElapsedTime(startTick, nowTick) >= SyncChallengeRateWindow;
        if (!newWindow && state.ChallengeResponseCount >= MaximumSyncChallengeResponsesPerWindow) return false;

        var nextCount = newWindow ? 1 : state.ChallengeResponseCount + 1;
        using var update = Command("UPDATE agent_sync_proof_state SET challenge_window_tick=$tick,challenge_response_count=$count WHERE machine_id=$machine",
            ("$tick", newWindow ? nowTick : state.ChallengeWindowTick), ("$count", nextCount), ("$machine", machineId));
        update.Transaction = tx;
        return update.ExecuteNonQuery() == 1;
    }

    private bool IsWithinLegacyWindow(VerifiedAgentRequest verified)
    {
        if (verified.IsTestFixture) return true;
        var difference = clock.GetUtcNow().ToUnixTimeSeconds() - verified.SignedSeconds;
        return difference >= -LegacySignedRequestWindow.TotalSeconds && difference <= LegacySignedRequestWindow.TotalSeconds;
    }

    private static string PublicKeyFingerprint(string publicKey)
        => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKey)));

    private bool ValidateVerifiedRequest(SqliteTransaction tx, VerifiedAgentRequest verified, string path)
    {
        if (verified.Method != "POST" || verified.Path != path || string.IsNullOrWhiteSpace(verified.MachineId)
            || verified.KeyFingerprint.Length != 64 || verified.NonceDigest.Length != 64 || verified.BodyDigest.Length != 64)
            return false;
        string? publicKey;
        using (var key = Command("SELECT public_key FROM machines WHERE machine_id=$machine", ("$machine", verified.MachineId)))
        {
            key.Transaction = tx;
            publicKey = key.ExecuteScalar() as string;
        }
        if (publicKey is null) return false;
        try { return FixedHexEquals(PublicKeyFingerprint(publicKey), verified.KeyFingerprint); }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    private sealed record SyncMachine(long ConfigRevision, int MoonlightPort, int SunshinePort, bool HasAllocation, string ApprovalStatus);

    private SyncMachine? ReadSyncMachine(SqliteTransaction tx, string machineId)
    {
        using var command = Command("SELECT m.revision,m.moonlight_port,m.sunshine_port,EXISTS(SELECT 1 FROM leases l WHERE l.machine_id=m.machine_id AND l.allocation_held=1),m.approval_status FROM machines m WHERE m.machine_id=$machine", ("$machine", machineId));
        command.Transaction = tx;
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetInt64(0), row.GetInt32(1), row.GetInt32(2), row.GetInt64(3) != 0, row.GetString(4)) : null;
    }

    private bool AcceptLegacySync(SqliteTransaction tx, VerifiedAgentRequest verified, SyncProofState state, string? reportedVersion)
    {
        if (!IsWithinLegacyWindow(verified) || state.LegacyLastSecond is { } lastSecond && verified.SignedSeconds <= lastSecond)
            return false;
        if (!AcceptScopedNonce(tx, verified)) return false;
        using var update = Command("UPDATE agent_sync_proof_state SET key_fingerprint=$fingerprint,legacy_last_second=$second,last_received_at=$received,legacy_reported_version=$version WHERE machine_id=$machine",
            ("$fingerprint", verified.KeyFingerprint), ("$second", verified.SignedSeconds), ("$received", clock.GetUtcNow().ToString("O")), ("$version", reportedVersion), ("$machine", verified.MachineId));
        update.Transaction = tx;
        return update.ExecuteNonQuery() == 1;
    }

    private SyncDecision IssueRateLimitedSyncChallenge(SqliteTransaction tx, string machineId, string keyFingerprint)
    {
        if (!TryConsumeSyncChallengeResponse(tx, machineId)) return SyncDecision.Rejected(429);
        return SyncDecision.ChallengeRequired(IssueOrReconstructSyncToken(tx, machineId, keyFingerprint));
    }

    private void MarkSyncProofAccepted(SqliteTransaction tx, VerifiedAgentRequest verified, SyncProofState state, string? version)
    {
        var proofSequence = state.TokenSequence ?? throw new InvalidOperationException("Accepted Agent proof has no token sequence.");
        using var update = Command("UPDATE agent_sync_proof_state SET key_fingerprint=$fingerprint,mode='challengeRequired',sequence=MAX(sequence,$sequence),last_proven_sequence=$sequence,last_proven_runtime_epoch=$epoch,last_proven_tick=$tick,last_proven_version=$version,last_received_at=$received,token_sequence=NULL,token_digest=NULL,token_epoch=NULL,token_issued_tick=NULL,challenge_window_tick=NULL,challenge_response_count=0 WHERE machine_id=$machine",
            ("$fingerprint", verified.KeyFingerprint), ("$sequence", proofSequence), ("$epoch", syncProofRuntimeEpoch), ("$tick", clock.GetTimestamp()),
            ("$version", version), ("$received", clock.GetUtcNow().ToString("O")), ("$machine", verified.MachineId));
        update.Transaction = tx;
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Agent proof state changed during sync acceptance.");
    }
}
