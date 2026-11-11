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
        var diagnostics = ValidateReadinessDiagnostics(request.ReadinessDiagnostics);

        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            if (!ValidateVerifiedRequest(tx, verified, "/agent/sync")) return SyncDecision.Rejected(401);
            var machine = ReadSyncMachine(tx, verified.MachineId);
            var storedState = ReadSyncProofState(tx, verified.MachineId);
            if (machine is null || !SyncProofKeyMatches(storedState, verified.KeyFingerprint))
                return SyncDecision.Rejected(401);
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
        if (request.Readiness == "ready") RegisterBoot(tx, id, request.BootId);
        else RejectRetiredBoot(tx, id, request.BootId);
        if (request.AppliedConfigRevision >= 0)
            ExecuteLease(tx, "INSERT INTO machine_config_acks(machine_id,applied_revision) VALUES($id,$revision) ON CONFLICT(machine_id) DO UPDATE SET applied_revision=$revision",
                ("$id", id), ("$revision", request.AppliedConfigRevision));
        ExpireLeases(tx, now.ToUnixTimeMilliseconds());
        ExecuteLease(tx, "INSERT INTO machine_heartbeats(machine_id,last_seen,boot_id,readiness,agent_version) VALUES($id,$at,$boot,$ready,NULL) ON CONFLICT(machine_id) DO UPDATE SET last_seen=$at,boot_id=$boot,readiness=$ready,agent_version=NULL; UPDATE machines SET local_ip=$ip,mac_address=$mac WHERE machine_id=$id",
            ("$id", id), ("$at", now.ToString("O")), ("$boot", request.BootId), ("$ready", request.Readiness),
            ("$ip", request.LocalIp), ("$mac", request.MacAddress));
        if (diagnostics is null)
            ExecuteLease(tx, "DELETE FROM machine_readiness_diagnostics WHERE machine_id=$id", ("$id", id));
        else
            ExecuteLease(tx, "INSERT INTO machine_readiness_diagnostics(machine_id,diagnostics_json,updated_at) VALUES($id,$diagnostics,$at) ON CONFLICT(machine_id) DO UPDATE SET diagnostics_json=$diagnostics,updated_at=$at",
                ("$id", id), ("$diagnostics", JsonSerializer.Serialize(diagnostics)), ("$at", now.ToString("O")));

        var responseChallenge = isProven ? issueToken(tx, id, CurrentKeyFingerprint(tx, id)) : null;
        if (request.Readiness != "ready")
            return new(now, new(machine.ConfigRevision, machine.MoonlightPort, machine.SunshinePort), [], null)
            { AgentUpdateChallenge = responseChallenge };

        string? unbound;
        using (var cmd = LeaseCommand(tx, "SELECT id FROM leases WHERE machine_id=$id AND state='preparing' AND boot_id IS NULL", ("$id", id)))
            unbound = cmd.ExecuteScalar() as string;
        if (unbound is not null) PrepareCommand(tx, FindLease(tx, unbound)!, request.BootId);
        ApplyResults(tx, id, request, now.ToUnixTimeMilliseconds());
        ObservePairingOffer(tx, id, request, now.ToUnixTimeMilliseconds());
        PurgePairingSecrets(tx, now.ToUnixTimeMilliseconds());
        return new(now, new(machine.ConfigRevision, machine.MoonlightPort, machine.SunshinePort),
            PendingCommands(tx, id, request.BootId), ConfirmLease(tx, id, request.BootId, request.Readiness))
        { AgentUpdateChallenge = responseChallenge };
    }

    private string CurrentKeyFingerprint(SqliteTransaction tx, string machineId)
    {
        using var command = LeaseCommand(tx, "SELECT public_key FROM machines WHERE machine_id=$machine", ("$machine", machineId));
        var publicKey = command.ExecuteScalar() as string ?? throw new KeyNotFoundException();
        return PublicKeyFingerprint(publicKey);
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
