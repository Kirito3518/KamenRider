using KamenRaider.Contracts;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace KamenRaider.Controller.Data;

/// <summary>
/// Admin PC-control operations. All mutations are admin-actor scoped, fail closed while the
/// database is restore-quarantined, and keep the existing lease/command invariants intact.
/// </summary>
public sealed partial class ControllerDatabase
{
    private const long PcControlHeartbeatFreshMilliseconds = 20_000;
    private const int PcControlMinimumRemainingSeconds = 60;
    private const int PcControlMaximumRemainingSeconds = 7 * 24 * 60 * 60;
    private const int PcControlMaximumDeltaSeconds = 86_400;
    private const long PcControlCommandValidityMilliseconds = 60_000;

    public PcControlSnapshotDto GetPcControlSnapshot()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var nowMs = now.ToUnixTimeMilliseconds();
            var machines = List();
            var leases = new Dictionary<string, PcControlLeaseDto>(StringComparer.Ordinal);
            using var command = Command("SELECT id,user_id,machine_id,state,revision,pending_revision,starts_at,expires_at FROM leases WHERE allocation_held=1");
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                var expiresAt = rows.IsDBNull(7) ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(7));
                var remaining = expiresAt is { } expires
                    ? Math.Max(0, (expires - now).TotalSeconds is var seconds && seconds > 0 ? (long)seconds : 0)
                    : (long?)null;
                leases[rows.GetString(2)] = new PcControlLeaseDto(
                    rows.GetString(0),
                    rows.GetString(1),
                    rows.GetString(3),
                    rows.GetInt64(4),
                    rows.IsDBNull(5) ? null : rows.GetInt64(5),
                    rows.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(6)),
                    expiresAt,
                    remaining);
            }
            rows.Close();
            return new PcControlSnapshotDto(now,
                machines.Select(machine => new PcControlMachineDto(machine, leases.GetValueOrDefault(machine.MachineId))).ToArray());
        }
    }

    public MachinePowerOffResponse RequestMachinePowerOff(string machineId, string actorId, string requestId)
    {
        Identifier(machineId);
        Identifier(actorId);
        Identifier(requestId);
        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            EnsureNotRestoreQuarantined(tx);
            var now = clock.GetUtcNow();
            var nowMs = now.ToUnixTimeMilliseconds();
            ExpireLeases(tx, nowMs);

            if (FindMachineAdminAction(tx, actorId, requestId) is { } prior)
            {
                var existing = ReadMachineCommandStatus(tx, prior);
                tx.Commit();
                return existing;
            }

            string? bootId;
            DateTimeOffset? lastSeen;
            using (var machine = Command("SELECT m.approval_status,h.last_seen,h.boot_id,COALESCE(c.held,0),EXISTS(SELECT 1 FROM agent_update_operations o WHERE o.machine_id=m.machine_id AND o.status NOT IN ('completed','rolledBack','expired','superseded')) FROM machines m LEFT JOIN machine_heartbeats h ON h.machine_id=m.machine_id LEFT JOIN agent_maintenance_chains c ON c.chain_id=(SELECT c1.chain_id FROM agent_maintenance_chains c1 WHERE c1.machine_id=m.machine_id ORDER BY c1.held DESC,c1.rowid DESC LIMIT 1) WHERE m.machine_id=$id",
                ("$id", machineId)))
            {
                machine.Transaction = tx;
                using var row = machine.ExecuteReader();
                if (!row.Read()) throw new KeyNotFoundException("Machine was not found.");
                lastSeen = row.IsDBNull(1) ? null : DateTimeOffset.Parse(row.GetString(1));
                bootId = row.IsDBNull(2) ? null : row.GetString(2);
                if (row.GetInt64(3) != 0)
                    throw new InvalidOperationException("Machine maintenance is active; power off remains held.");
                if (row.GetInt64(4) != 0)
                    throw new InvalidOperationException("An Agent update is active; power off remains held.");
            }
            if (string.IsNullOrWhiteSpace(bootId) || lastSeen is null
                || lastSeen.Value > now.AddSeconds(1)
                || now - lastSeen.Value > TimeSpan.FromMilliseconds(PcControlHeartbeatFreshMilliseconds))
                throw new InvalidOperationException("Machine is offline; power off cannot be delivered.");

            using (var existingShutdown = Command("SELECT command_id,valid_until FROM command_outbox WHERE machine_id=$machine AND type='ShutdownMachine' AND status='pending' ORDER BY rowid LIMIT 1", ("$machine", machineId)))
            {
                existingShutdown.Transaction = tx;
                using var pending = existingShutdown.ExecuteReader();
                if (pending.Read())
                {
                    var pendingCommand = pending.GetString(0);
                    var pendingUntil = pending.GetInt64(1);
                    tx.Commit();
                    return new(pendingCommand, "requested", DateTimeOffset.FromUnixTimeMilliseconds(pendingUntil));
                }
            }
            string? leaseId = null;
            using (var lease = Command("SELECT id FROM leases WHERE machine_id=$machine AND allocation_held=1", ("$machine", machineId)))
            {
                lease.Transaction = tx;
                leaseId = lease.ExecuteScalar() as string;
            }
            if (leaseId is not null)
            {
                ExecuteLease(tx, "UPDATE leases SET state=CASE WHEN state IN ('preparing','active') THEN 'ending' ELSE state END,expires_at=$now,ended_at=COALESCE(ended_at,CASE WHEN state IN ('preparing','active') THEN $now END),pending_revision=NULL,pending_expires_at=NULL WHERE id=$lease",
                    ("$now", nowMs), ("$lease", leaseId));
                ExecuteLease(tx, "UPDATE command_outbox SET status='expired' WHERE rental_id=$lease AND status='pending' AND type<>'ShutdownMachine'",
                    ("$lease", leaseId));
            }
            var commandId = Guid.NewGuid().ToString("N");
            var validUntil = now.AddMilliseconds(PcControlCommandValidityMilliseconds).ToUnixTimeMilliseconds();
            ExecuteLease(tx, "INSERT INTO command_outbox(command_id,type,rental_id,machine_id,lease_revision,boot_id,valid_until,payload) VALUES($command,'ShutdownMachine',NULL,$machine,0,$boot,$until,'{}')",
                ("$command", commandId), ("$machine", machineId), ("$boot", bootId), ("$until", validUntil));
            RecordMachineAdminAction(tx, machineId, actorId, requestId, "powerOff", commandId,
                Digest(machineId, requestId, "powerOff"), nowMs);
            tx.Commit();
            return new(commandId, "requested", DateTimeOffset.FromUnixTimeMilliseconds(validUntil));
        }
    }

    public LeaseDto AdjustMachineLeaseTime(string machineId, int deltaSeconds, long expectedRevision,
        string idempotencyKey, string actorId)
    {
        Identifier(machineId);
        Identifier(actorId);
        Identifier(idempotencyKey);
        if (deltaSeconds is 0 or < -PcControlMaximumDeltaSeconds or > PcControlMaximumDeltaSeconds)
            throw new ArgumentException("Invalid lease time adjustment");
        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            EnsureNotRestoreQuarantined(tx);
            var now = clock.GetUtcNow();
            var nowMs = now.ToUnixTimeMilliseconds();
            ExpireLeases(tx, nowMs);
            var lease = FindAllocationLeaseForMachine(tx, machineId, "active");
            var scope = "adjust:" + lease.Dto.Id;
            var fingerprint = Digest(Convert.ToString(expectedRevision, System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToString(deltaSeconds, System.Globalization.CultureInfo.InvariantCulture));
            var prior = PriorRequest(tx, scope, idempotencyKey, fingerprint);
            if (prior is not null)
            {
                var existing = FindLease(tx, prior) ?? throw new KeyNotFoundException();
                tx.Commit();
                return existing.Dto;
            }
            if (lease.Dto.Revision != expectedRevision || lease.Dto.PendingRevision is not null)
                throw new InvalidOperationException("Lease state or revision conflict");
            if (HasPendingCommand(tx, lease.Dto.Id, "EndLease"))
                throw new InvalidOperationException("Lease cancellation is already in progress");
            var currentExpiry = lease.Dto.ExpiresAtUtc?.ToUnixTimeMilliseconds()
                ?? throw new InvalidOperationException("Lease has no expiry");
            var targetExpiry = checked(currentExpiry + checked((long)deltaSeconds * 1000));
            var minimum = nowMs + PcControlMinimumRemainingSeconds * 1000L;
            var maximum = nowMs + PcControlMaximumRemainingSeconds * 1000L;
            if (targetExpiry < minimum || targetExpiry > maximum)
                throw new ArgumentException("Lease duration is outside the allowed range");
            var revision = checked(expectedRevision + 1);
            ExecuteLease(tx, "UPDATE leases SET pending_revision=$revision,pending_expires_at=$expiry WHERE id=$id",
                ("$revision", revision), ("$expiry", targetExpiry), ("$id", lease.Dto.Id));
            var commandId = AddCommand(tx, lease, "ApplyLease", revision, lease.Boot!, lease.Dto.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds(),
                new { startsAtUtc = lease.Dto.StartsAtUtc, expiresAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(targetExpiry) });
            SaveRequest(tx, scope, idempotencyKey, fingerprint, lease.Dto.Id);
            RecordMachineAdminAction(tx, machineId, actorId, idempotencyKey, "adjustTime", commandId,
                Digest(lease.Dto.Id, idempotencyKey, "adjustTime"), nowMs);
            var result = FindLease(tx, lease.Dto.Id)!.Dto;
            tx.Commit();
            return result;
        }
    }

    public LeaseDto CancelMachineLease(string machineId, long expectedRevision, string idempotencyKey,
        string requestId, string actorId)
    {
        Identifier(machineId);
        Identifier(actorId);
        Identifier(idempotencyKey);
        Identifier(requestId);
        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            EnsureNotRestoreQuarantined(tx);
            var now = clock.GetUtcNow();
            var nowMs = now.ToUnixTimeMilliseconds();
            ExpireLeases(tx, nowMs);
            var lease = FindAllocationLeaseForMachine(tx, machineId, "active");
            var scope = "cancel:" + lease.Dto.Id;
            var fingerprint = Digest(Convert.ToString(expectedRevision, System.Globalization.CultureInfo.InvariantCulture));
            var prior = PriorRequest(tx, scope, idempotencyKey, fingerprint);
            if (prior is not null)
            {
                var existing = FindLease(tx, prior) ?? throw new KeyNotFoundException();
                tx.Commit();
                return existing.Dto;
            }
            if (lease.Dto.Revision != expectedRevision || lease.Dto.PendingRevision is not null)
                throw new InvalidOperationException("Lease state or revision conflict");
            if (HasPendingCommand(tx, lease.Dto.Id, "EndLease"))
                throw new InvalidOperationException("Lease cancellation is already in progress");
            // The effective end of a cancelled lease is the cancellation request, not the Agent ack.
            ExecuteLease(tx, "UPDATE leases SET ended_at=COALESCE(ended_at,$now) WHERE id=$lease",
                ("$now", nowMs), ("$lease", lease.Dto.Id));
            var validUntil = Math.Min(nowMs + PcControlCommandValidityMilliseconds,
                lease.Dto.ExpiresAtUtc?.ToUnixTimeMilliseconds() ?? nowMs + PcControlCommandValidityMilliseconds);
            var commandId = AddCommand(tx, lease, "EndLease", lease.Dto.Revision, lease.Boot!, validUntil, new { });
            SaveRequest(tx, scope, idempotencyKey, fingerprint, lease.Dto.Id);
            RecordMachineAdminAction(tx, machineId, actorId, requestId, "cancelLease", commandId,
                Digest(lease.Dto.Id, requestId, "cancelLease"), nowMs);
            var result = FindLease(tx, lease.Dto.Id)!.Dto;
            tx.Commit();
            return result;
        }
    }

    private LeaseRow FindAllocationLeaseForMachine(SqliteTransaction tx, string machineId, string state)
    {
        string? id;
        using (var lease = Command("SELECT id FROM leases WHERE machine_id=$machine AND allocation_held=1 AND state=$state",
            ("$machine", machineId), ("$state", state)))
        {
            lease.Transaction = tx;
            id = lease.ExecuteScalar() as string;
        }
        if (id is null) throw new InvalidOperationException("Machine has no active rental.");
        return FindLease(tx, id) ?? throw new KeyNotFoundException();
    }

    private bool HasPendingCommand(SqliteTransaction tx, string rentalId, string type)
    {
        using var command = Command("SELECT EXISTS(SELECT 1 FROM command_outbox WHERE rental_id=$rental AND type=$type AND status='pending')",
            ("$rental", rentalId), ("$type", type));
        command.Transaction = tx;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private string? FindMachineAdminAction(SqliteTransaction tx, string actorId, string requestId)
    {
        using var command = Command("SELECT command_id FROM machine_admin_actions WHERE actor_id=$actor AND request_id=$request",
            ("$actor", actorId), ("$request", requestId));
        command.Transaction = tx;
        return command.ExecuteScalar() as string;
    }

    private MachinePowerOffResponse ReadMachineCommandStatus(SqliteTransaction tx, string commandId)
    {
        using var command = Command("SELECT status,valid_until FROM command_outbox WHERE command_id=$command", ("$command", commandId));
        command.Transaction = tx;
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new KeyNotFoundException("Machine command was not found.");
        var status = row.GetString(0) switch
        {
            "pending" => "requested",
            "applied" => "applied",
            "failed" => "failed",
            "expired" or "invalidated" => "expired",
            var other => other,
        };
        return new(commandId, status, DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(1)));
    }

    private void RecordMachineAdminAction(SqliteTransaction tx, string machineId, string actorId, string requestId,
        string actionType, string? commandId, string payloadDigest, long nowMs)
    {
        ExecuteLease(tx, "INSERT INTO machine_admin_actions(action_id,machine_id,actor_id,request_id,action_type,command_id,payload_digest,created_at_unix_ms) VALUES($id,$machine,$actor,$request,$type,$command,$digest,$now)",
            ("$id", Guid.NewGuid().ToString("N")), ("$machine", machineId), ("$actor", actorId), ("$request", requestId),
            ("$type", actionType), ("$command", commandId), ("$digest", payloadDigest), ("$now", nowMs));
    }

    private static string Digest(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values))));
}
