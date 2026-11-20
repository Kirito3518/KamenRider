using KamenRaider.Contracts;
using KamenRaider.Controller.Security;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace KamenRaider.Controller.Data;

public sealed partial class ControllerDatabase
{
    public SyncDecision Sync(VerifiedAgentRequest verified, SyncRequest request)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(request);
        ValidateNetworkMetadata(request.LocalIp, request.MacAddress);
        if (string.IsNullOrWhiteSpace(request.BootId) || request.BootId.Length > 128
            || request.Readiness is not ("ready" or "notReady" or "unknown")
            || request.CommandResults is null || request.CommandResults.Length > 100 || request.CommandResults.Any(result => result is null)
            || request.AgentVersion is { } version && (string.IsNullOrWhiteSpace(version) || version.Length > 64
                || version.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '+')))
            || request.AgentUpdateChallenge is { Length: > 128 }
            || request.SyncProofProtocol is not (null or 2))
            throw new ArgumentException("Invalid heartbeat");
        // Agent-reported hardware is bounded display data; a malformed profile rejects the sync
        // before any state is written. A missing profile leaves the stored values untouched.
        request = request with { Hardware = NormalizeReportedHardware(request.Hardware) };
        if (request.AppliedSunshineCredentialRevision is < 0)
            throw new ArgumentException("Invalid heartbeat");
        var diagnostics = ValidateReadinessDiagnostics(request.ReadinessDiagnostics);

        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            if (!ValidateVerifiedRequest(tx, verified, "/agent/sync")) return SyncDecision.Rejected(401);
            var machine = ReadSyncMachine(tx, verified.MachineId);
            var storedState = ReadSyncProofState(tx, verified.MachineId);
            if (machine is null || !SyncProofKeyMatches(storedState, verified.KeyFingerprint))
                return SyncDecision.Rejected(401);
            if (IsRestoreQuarantined(tx))
            {
                tx.Commit();
                return SyncDecision.Rejected(409);
            }
            var state = storedState!;
            if (request.SyncProofProtocol != 2 && state.Mode == "challengeRequired")
                return SyncDecision.Rejected(409);
            if (request.AppliedConfigRevision > machine.ConfigRevision)
                throw new ArgumentException("Agent acknowledged a configuration revision not issued by the Controller");

            if (request.SyncProofProtocol == 2)
            {
                if (!IsCurrentSyncToken(tx, verified, request.AgentUpdateChallenge))
                {
                    ExecuteLease(tx, "UPDATE agent_sync_proof_state SET key_fingerprint=$fingerprint WHERE machine_id=$machine AND key_fingerprint IS NULL",
                        ("$fingerprint", verified.KeyFingerprint), ("$machine", verified.MachineId));
                    var challengeDecision = IssueRateLimitedSyncChallenge(tx, verified.MachineId, verified.KeyFingerprint);
                    tx.Commit();
                    return challengeDecision;
                }

                if (!AcceptScopedNonce(tx, verified, enforceCutoff: false, scopePathOverride: ProtocolTwoSyncNonceScope,
                    duplicateScopePath: verified.Path))
                {
                    var challengeDecision = IssueRateLimitedSyncChallenge(tx, verified.MachineId, verified.KeyFingerprint);
                    tx.Commit();
                    return challengeDecision;
                }
                MarkSyncProofAccepted(tx, verified, state, request.AgentVersion);
                ConfirmUpdateFromProvenVersion(tx, verified.MachineId, request.AgentVersion,
                    state.TokenSequence ?? throw new InvalidOperationException("Accepted Agent proof has no token sequence."));
                var accepted = ApplyAcceptedSync(tx, verified.MachineId, machine, request, diagnostics,
                    IssueOrReconstructSyncToken, isProven: true);
                tx.Commit();
                return SyncDecision.Accepted(accepted);
            }

            if (!IsWithinLegacyWindow(verified) || state.LegacyLastSecond is { } previousSecond && verified.SignedSeconds <= previousSecond)
                return SyncDecision.Rejected(verified.IsTestFixture ? 409 : 401);
            if (machine.HasAllocation && !verified.IsTestFixture) return SyncDecision.Rejected(409);
            if (!AcceptLegacySync(tx, verified, state, request.AgentVersion)) return SyncDecision.Rejected(409);

            var legacyResponse = ApplyAcceptedSync(tx, verified.MachineId, machine, request, diagnostics,
                IssueOrReconstructSyncToken, isProven: false);
            tx.Commit();
            return SyncDecision.Accepted(legacyResponse);
        }
    }

    private SyncResponse ApplyAcceptedSync(SqliteTransaction tx, string id, SyncMachine machine, SyncRequest request,
        ReadinessDiagnostic[]? diagnostics, Func<SqliteTransaction, string, string, string> issueToken, bool isProven)
    {
        var now = clock.GetUtcNow();
        if (request.Readiness == "ready") RegisterBoot(tx, id, request.BootId, now.ToUnixTimeMilliseconds());
        else RejectRetiredBoot(tx, id, request.BootId);
        if (request.AppliedConfigRevision >= 0)
            ExecuteLease(tx, "INSERT INTO machine_config_acks(machine_id,applied_revision) VALUES($id,$revision) ON CONFLICT(machine_id) DO UPDATE SET applied_revision=$revision",
                ("$id", id), ("$revision", request.AppliedConfigRevision));
        ExpireLeases(tx, now.ToUnixTimeMilliseconds());
        ExecuteLease(tx, "INSERT INTO machine_heartbeats(machine_id,last_seen,boot_id,readiness,agent_version) VALUES($id,$at,$boot,$ready,NULL) ON CONFLICT(machine_id) DO UPDATE SET last_seen=$at,boot_id=$boot,readiness=$ready,agent_version=NULL; UPDATE machines SET local_ip=$ip,mac_address=$mac WHERE machine_id=$id",
            ("$id", id), ("$at", now.ToString("O")), ("$boot", request.BootId), ("$ready", request.Readiness),
            ("$ip", request.LocalIp), ("$mac", request.MacAddress));
        // Agent-reported hardware refreshes the stored profile on every sync; it never touches
        // display_name, price, approval or the configuration revision.
        if (request.Hardware is { } hardware)
            ExecuteLease(tx, "UPDATE machines SET hostname=$hostname,cpu=$cpu,gpu=$gpu,ram_bytes=$ram WHERE machine_id=$id",
                ("$hostname", hardware.Hostname), ("$cpu", hardware.Cpu), ("$gpu", hardware.Gpu),
                ("$ram", hardware.RamBytes), ("$id", id));
        if (request.AppliedSunshineCredentialRevision is { } appliedSunshineRevision)
            ExecuteLease(tx, "UPDATE machines SET sunshine_credential_revision=$revision WHERE machine_id=$id",
                ("$revision", appliedSunshineRevision), ("$id", id));
        if (diagnostics is null)
            ExecuteLease(tx, "DELETE FROM machine_readiness_diagnostics WHERE machine_id=$id", ("$id", id));
        else
            ExecuteLease(tx, "INSERT INTO machine_readiness_diagnostics(machine_id,diagnostics_json,updated_at) VALUES($id,$diagnostics,$at) ON CONFLICT(machine_id) DO UPDATE SET diagnostics_json=$diagnostics,updated_at=$at",
                ("$id", id), ("$diagnostics", JsonSerializer.Serialize(diagnostics)), ("$at", now.ToString("O")));

        var responseChallenge = isProven ? issueToken(tx, id, CurrentKeyFingerprint(tx, id)) : null;
        var sunshineCredentialRevision = ReadSunshineCredentialRevision(tx);
        if (request.Readiness != "ready")
            return new(now, new(machine.ConfigRevision, machine.MoonlightPort, machine.SunshinePort),
                PendingShutdownCommands(tx, id, request.BootId), null)
            { AgentUpdateChallenge = responseChallenge, SunshineCredentialRevision = sunshineCredentialRevision };

        string? unbound;
        using (var cmd = LeaseCommand(tx, "SELECT id FROM leases WHERE machine_id=$id AND state='preparing' AND boot_id IS NULL", ("$id", id)))
            unbound = cmd.ExecuteScalar() as string;
        if (unbound is not null) PrepareCommand(tx, FindLease(tx, unbound)!, request.BootId);
        ApplyResults(tx, id, request, now.ToUnixTimeMilliseconds());
        ObservePairingOffer(tx, id, request, now.ToUnixTimeMilliseconds());
        PurgePairingSecrets(tx, now.ToUnixTimeMilliseconds());
        return new(now, new(machine.ConfigRevision, machine.MoonlightPort, machine.SunshinePort),
            PendingCommands(tx, id, request.BootId), ConfirmLease(tx, id, request.BootId, request.Readiness))
        { AgentUpdateChallenge = responseChallenge, SunshineCredentialRevision = sunshineCredentialRevision };
    }

    private string CurrentKeyFingerprint(SqliteTransaction tx, string machineId)
    {
        using var command = LeaseCommand(tx, "SELECT public_key FROM machines WHERE machine_id=$machine", ("$machine", machineId));
        var publicKey = command.ExecuteScalar() as string ?? throw new KeyNotFoundException();
        return PublicKeyFingerprint(publicKey);
    }

    /// <summary>
    /// A proven protocol-2 heartbeat is the only evidence that may release a held maintenance
    /// chain. The current head operation must be awaiting the outcome and the proven Agent
    /// version must match its immutable target (completed) or rollback source (rolled back).
    /// Confirmation, terminal status, chain close and the immutable confirmation record are
    /// committed in the same transaction as the accepted proof.
    /// </summary>
    private void ConfirmUpdateFromProvenVersion(SqliteTransaction tx, string machineId, string? provenVersion,
        long proofSequence)
    {
        if (string.IsNullOrWhiteSpace(provenVersion)) return;
        string operationId;
        string chainId;
        string status;
        string targetVersion;
        string? sourceVersion;
        long generation;
        using (var operation = Command("SELECT o.operation_id,o.chain_id,o.status,o.target_version,o.source_version,c.generation " +
            "FROM agent_update_operations o JOIN agent_maintenance_chains c ON c.chain_id=o.chain_id AND c.machine_id=o.machine_id " +
            "WHERE o.machine_id=$machine AND c.held=1 AND c.current_head_operation_id=o.operation_id " +
            "AND c.generation=o.ticket_generation AND o.target_protocol=2 AND o.status IN ('awaitingHeartbeat','rollingBack') LIMIT 1",
            ("$machine", machineId)))
        {
            operation.Transaction = tx;
            using var row = operation.ExecuteReader();
            if (!row.Read()) return;
            operationId = row.GetString(0);
            chainId = row.GetString(1);
            status = row.GetString(2);
            targetVersion = row.GetString(3);
            sourceVersion = row.IsDBNull(4) ? null : row.GetString(4);
            generation = row.GetInt64(5);
        }

        string outcome;
        if (status == "awaitingHeartbeat" && string.Equals(provenVersion, targetVersion, StringComparison.Ordinal))
            outcome = "completed";
        else if (status == "rollingBack" && sourceVersion is not null && string.Equals(provenVersion, sourceVersion, StringComparison.Ordinal))
            outcome = "rolledBack";
        else
            return;

        var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
        using (var confirm = Command("UPDATE agent_update_operations SET status=$outcome,updated_at_unix_ms=$now " +
            "WHERE operation_id=$operation AND machine_id=$machine AND chain_id=$chain AND status=$expected AND ticket_generation=$generation",
            ("$outcome", outcome), ("$now", nowMs), ("$operation", operationId), ("$machine", machineId),
            ("$chain", chainId), ("$expected", status), ("$generation", generation)))
        {
            confirm.Transaction = tx;
            if (confirm.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The maintenance operation changed during confirmation.");
        }
        using (var close = Command("UPDATE agent_maintenance_chains SET held=0,updated_at_unix_ms=$now " +
            "WHERE chain_id=$chain AND machine_id=$machine AND held=1 AND current_head_operation_id=$operation AND generation=$generation",
            ("$now", nowMs), ("$chain", chainId), ("$machine", machineId), ("$operation", operationId),
            ("$generation", generation)))
        {
            close.Transaction = tx;
            if (close.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The maintenance chain changed during confirmation.");
        }
        using (var confirmation = Command("INSERT INTO agent_update_confirmations(confirmation_id,machine_id,chain_id,operation_id,generation,outcome,proven_version,proof_sequence,proof_runtime_epoch,created_at_unix_ms) " +
            "VALUES($id,$machine,$chain,$operation,$generation,$outcome,$version,$sequence,$epoch,$now)",
            ("$id", Guid.NewGuid().ToString("N")), ("$machine", machineId), ("$chain", chainId),
            ("$operation", operationId), ("$generation", generation), ("$outcome", outcome),
            ("$version", provenVersion), ("$sequence", proofSequence), ("$epoch", syncProofRuntimeEpoch), ("$now", nowMs)))
        {
            confirmation.Transaction = tx;
            if (confirmation.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The update confirmation could not be recorded.");
        }
        var oldBinding = JsonSerializer.Serialize(new
        {
            machineId,
            chainId,
            operationId,
            generation,
            previousStatus = status,
        });
        var newBinding = JsonSerializer.Serialize(new
        {
            machineId,
            chainId,
            operationId,
            generation,
            outcome,
            provenVersion,
            proofSequence,
        });
        using (var audit = Command("INSERT INTO agent_update_audit(audit_id,chain_id,operation_id,actor_id,request_id,event_type,old_binding_json,new_binding_json,created_at_unix_ms) " +
            "VALUES($id,$chain,$operation,'agent:' || $machine,$request,'updateConfirmed',$old,$new,$now)",
            ("$id", Guid.NewGuid().ToString("N")), ("$chain", chainId), ("$operation", operationId),
            ("$machine", machineId), ("$request", "confirm:" + operationId), ("$old", oldBinding),
            ("$new", newBinding), ("$now", nowMs)))
        {
            audit.Transaction = tx;
            audit.ExecuteNonQuery();
        }
    }

    private static ReadinessDiagnostic[]? ValidateReadinessDiagnostics(ReadinessDiagnostic[]? diagnostics)
    {
        if (diagnostics is null) return null;
        if (diagnostics.Length > ReadinessDiagnosticIds.Canonical.Count
            || diagnostics.Any(item => item is null || !ReadinessDiagnosticIds.Canonical.Contains(item.CheckId, StringComparer.Ordinal)
                || item.Status is not (ReadinessDiagnosticStatuses.Passed or ReadinessDiagnosticStatuses.Blocked
                    or ReadinessDiagnosticStatuses.Unavailable or ReadinessDiagnosticStatuses.Unknown))
            || diagnostics.Select(item => item.CheckId).Distinct(StringComparer.Ordinal).Count() != diagnostics.Length)
            throw new ArgumentException("Invalid heartbeat");
        return ReadinessDiagnosticIds.Canonical.Where(id => diagnostics.Any(item => item.CheckId == id))
            .Select(id => diagnostics.Single(item => item.CheckId == id)).ToArray();
    }
}
