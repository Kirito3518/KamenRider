using KamenRaider.Contracts;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace KamenRaider.Controller.Data;

public sealed partial class ControllerDatabase
{
    private static readonly string[] ActiveAgentUpdateStatuses = ["awaitingInstaller", "installing", "awaitingHeartbeat", "rollingBack", "failed"];

    public AgentReleaseDto PublishAgentRelease(AgentReleasePublication release)
    {
        ArgumentNullException.ThrowIfNull(release);
        Identifier(release.Version);
        if (release.Channel is not ("stable" or "beta") || string.IsNullOrWhiteSpace(release.ReleaseNotes)
            || !Path.IsPathFullyQualified(release.PackagePath) || release.PackageSizeBytes <= 0
            || string.IsNullOrWhiteSpace(release.SourceCommit) || release.SourceDirty || !release.ReleaseBuild)
            throw new ArgumentException("Invalid Agent release metadata");

        byte[] hash;
        try { hash = Convert.FromHexString(release.PackageSha256); }
        catch (FormatException) { throw new ArgumentException("Invalid package SHA-256"); }
        if (hash.Length != 32) throw new ArgumentException("Invalid package SHA-256");
        var canonicalHash = Convert.ToHexString(hash);

        lock (gate)
        {
            var publishedAt = clock.GetUtcNow();
            using var tx = connection.BeginTransaction();
            using (var insert = Command("INSERT INTO agent_releases(version,channel,release_notes,package_sha256,package_path,package_size_bytes,source_commit,source_dirty,release_build,package_format,published_at_unix_ms) VALUES($version,$channel,$notes,$hash,$path,$size,$commit,0,1,'AgentPackage.zip',$published)",
                ("$version", release.Version), ("$channel", release.Channel), ("$notes", release.ReleaseNotes), ("$hash", canonicalHash),
                ("$path", release.PackagePath), ("$size", release.PackageSizeBytes), ("$commit", release.SourceCommit),
                ("$published", publishedAt.ToUnixTimeMilliseconds())))
            {
                insert.Transaction = tx;
                insert.ExecuteNonQuery();
            }
            tx.Commit();
            return new(release.Version, release.Channel, release.ReleaseNotes, canonicalHash, release.PackageSizeBytes,
                release.SourceCommit, false, publishedAt);
        }
    }

    public AgentReleaseDto[] ListAgentReleases()
    {
        lock (gate)
        {
            using var command = Command("SELECT version,channel,release_notes,package_sha256,package_size_bytes,source_commit,source_dirty,published_at_unix_ms FROM agent_releases ORDER BY published_at_unix_ms DESC,version DESC");
            using var rows = command.ExecuteReader();
            var releases = new List<AgentReleaseDto>();
            while (rows.Read())
                releases.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetInt64(4),
                    rows.GetString(5), rows.GetInt32(6) != 0, DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(7))));
            return releases.ToArray();
        }
    }

    public AgentUpdateTicketResponse ReserveAgentUpdate(string machineId, string releaseVersion)
    {
        Identifier(machineId);
        Identifier(releaseVersion);
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var nowMs = now.ToUnixTimeMilliseconds();
            using var tx = connection.BeginTransaction();
            ExpireUnstartedAgentUpdates(tx, nowMs);

            string approvalStatus;
            DateTimeOffset lastSeen;
            string previousVersion;
            using (var machine = Command("SELECT m.approval_status,h.last_seen,h.agent_version FROM machines m LEFT JOIN machine_heartbeats h ON h.machine_id=m.machine_id WHERE m.machine_id=$id", ("$id", machineId)))
            {
                machine.Transaction = tx;
                using var row = machine.ExecuteReader();
                if (!row.Read()) throw new KeyNotFoundException();
                approvalStatus = row.GetString(0);
                if (row.IsDBNull(1) || row.IsDBNull(2)) throw new InvalidOperationException("A fresh Agent version heartbeat is required");
                lastSeen = DateTimeOffset.Parse(row.GetString(1));
                previousVersion = row.GetString(2);
            }
            if (approvalStatus != "approved") throw new InvalidOperationException("Machine must be approved before updating");
            if (lastSeen > now || now - lastSeen > TimeSpan.FromSeconds(20)) throw new InvalidOperationException("A fresh Agent version heartbeat is required");

            using (var allocation = Command("SELECT EXISTS(SELECT 1 FROM leases WHERE machine_id=$id AND allocation_held=1)", ("$id", machineId)))
            {
                allocation.Transaction = tx;
                if (Convert.ToInt64(allocation.ExecuteScalar()) != 0) throw new InvalidOperationException("Machine has a held lease");
            }
            using (var active = Command("SELECT EXISTS(SELECT 1 FROM agent_update_operations WHERE machine_id=$id AND status NOT IN ('completed','rolledBack','expired'))", ("$id", machineId)))
            {
                active.Transaction = tx;
                if (Convert.ToInt64(active.ExecuteScalar()) != 0) throw new InvalidOperationException("Machine already has an active Agent update");
            }

            string targetVersion;
            string packageHash;
            using (var release = Command("SELECT version,package_sha256 FROM agent_releases WHERE version=$version", ("$version", releaseVersion)))
            {
                release.Transaction = tx;
                using var row = release.ExecuteReader();
                if (!row.Read()) throw new KeyNotFoundException();
                targetVersion = row.GetString(0);
                packageHash = row.GetString(1);
            }

            var ticket = NewAgentUpdateTicket(out var ticketHash);
            var operationId = Guid.NewGuid().ToString("N");
            var expires = now.AddMinutes(10);
            using (var insert = Command("INSERT INTO agent_update_operations(operation_id,machine_id,release_version,target_version,target_package_sha256,previous_version,status,ticket_hash,ticket_expires_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) VALUES($operation,$machine,$release,$target,$hash,$previous,'awaitingInstaller',$ticket,$expires,$now,$now)",
                ("$operation", operationId), ("$machine", machineId), ("$release", releaseVersion), ("$target", targetVersion),
                ("$hash", packageHash), ("$previous", previousVersion), ("$ticket", ticketHash),
                ("$expires", expires.ToUnixTimeMilliseconds()), ("$now", nowMs)))
            {
                insert.Transaction = tx;
                insert.ExecuteNonQuery();
            }
            tx.Commit();
            return new(operationId, machineId, ticket, expires);
        }
    }

    public AgentUpdateTicketResponse RenewAgentUpdateTicket(string machineId, string operationId)
    {
        Identifier(machineId);
        Identifier(operationId);
        lock (gate)
        {
            var now = clock.GetUtcNow();
            using var tx = connection.BeginTransaction();
            string status;
            long? startedAt;
            using (var operation = Command("SELECT machine_id,status,started_at_unix_ms FROM agent_update_operations WHERE operation_id=$id", ("$id", operationId)))
            {
                operation.Transaction = tx;
                using var row = operation.ExecuteReader();
                if (!row.Read() || row.GetString(0) != machineId) throw new KeyNotFoundException();
                status = row.GetString(1);
                startedAt = row.IsDBNull(2) ? null : row.GetInt64(2);
            }
            if (startedAt is null || !ActiveAgentUpdateStatuses.Contains(status, StringComparer.Ordinal))
                throw new InvalidOperationException("Only a started active operation can be recovered");

            var ticket = NewAgentUpdateTicket(out var ticketHash);
            var expires = now.AddMinutes(10);
            using (var renew = Command("UPDATE agent_update_operations SET ticket_hash=$ticket,ticket_expires_at_unix_ms=$expires,ticket_started_at_unix_ms=NULL,updated_at_unix_ms=$now WHERE operation_id=$id AND machine_id=$machine AND status NOT IN ('completed','rolledBack','expired')",
                ("$ticket", ticketHash), ("$expires", expires.ToUnixTimeMilliseconds()), ("$now", now.ToUnixTimeMilliseconds()),
                ("$id", operationId), ("$machine", machineId)))
            {
                renew.Transaction = tx;
                if (renew.ExecuteNonQuery() != 1) throw new InvalidOperationException("Update operation changed during recovery");
            }
            tx.Commit();
            return new(operationId, machineId, ticket, expires);
        }
    }

    public AgentUpdateOperationDto StartAgentUpdate(string machineId, string ticket)
    {
        Identifier(machineId);
        var ticketHash = AgentUpdateTicketHash(ticket) ?? throw new UnauthorizedAccessException("Update ticket is invalid");
        lock (gate)
        {
            var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
            using var tx = connection.BeginTransaction();
            string operationId;
            long expiresAt;
            long? ticketStartedAt;
            using (var operation = Command("SELECT operation_id,ticket_expires_at_unix_ms,ticket_started_at_unix_ms FROM agent_update_operations WHERE ticket_hash=$ticket AND machine_id=$machine AND status NOT IN ('completed','rolledBack','expired')", ("$ticket", ticketHash), ("$machine", machineId)))
            {
                operation.Transaction = tx;
                using var row = operation.ExecuteReader();
                if (!row.Read()) throw new UnauthorizedAccessException("Update ticket is invalid");
                operationId = row.GetString(0);
                expiresAt = row.GetInt64(1);
                ticketStartedAt = row.IsDBNull(2) ? null : row.GetInt64(2);
            }
            if (ticketStartedAt is not null || expiresAt <= nowMs) throw new UnauthorizedAccessException("Update ticket is expired or already used");
            using (var start = Command("UPDATE agent_update_operations SET status='installing',ticket_started_at_unix_ms=$now,started_at_unix_ms=COALESCE(started_at_unix_ms,$now),updated_at_unix_ms=$now WHERE operation_id=$id AND ticket_started_at_unix_ms IS NULL AND ticket_expires_at_unix_ms > $now", ("$now", nowMs), ("$id", operationId)))
            {
                start.Transaction = tx;
                if (start.ExecuteNonQuery() != 1) throw new UnauthorizedAccessException("Update ticket is expired or already used");
            }
            var result = FindAgentUpdateOperation(tx, operationId);
            tx.Commit();
            return result;
        }
    }

    public AgentUpdateOperationDto ReportAgentUpdateStatus(string machineId, string ticket, string status)
    {
        Identifier(machineId);
        if (status is not ("awaitingHeartbeat" or "rollingBack" or "failed")) throw new ArgumentException("Invalid update status");
        var ticketHash = AgentUpdateTicketHash(ticket) ?? throw new UnauthorizedAccessException("Update ticket is invalid");
        lock (gate)
        {
            var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
            using var tx = connection.BeginTransaction();
            string operationId;
            string currentStatus;
            using (var operation = Command("SELECT operation_id,status FROM agent_update_operations WHERE ticket_hash=$ticket AND machine_id=$machine AND ticket_started_at_unix_ms IS NOT NULL AND status NOT IN ('completed','rolledBack','expired')", ("$ticket", ticketHash), ("$machine", machineId)))
            {
                operation.Transaction = tx;
                using var row = operation.ExecuteReader();
                if (!row.Read()) throw new UnauthorizedAccessException("Update ticket is invalid");
                operationId = row.GetString(0);
                currentStatus = row.GetString(1);
            }
            if (currentStatus is not ("installing" or "awaitingHeartbeat" or "rollingBack" or "failed"))
                throw new InvalidOperationException("Update operation cannot accept a status report");
            using (var update = Command("UPDATE agent_update_operations SET status=$status,updated_at_unix_ms=$now WHERE operation_id=$id", ("$status", status), ("$now", nowMs), ("$id", operationId)))
            {
                update.Transaction = tx;
                update.ExecuteNonQuery();
            }
            var result = FindAgentUpdateOperation(tx, operationId);
            tx.Commit();
            return result;
        }
    }

    private void ResolveAgentUpdateFromHeartbeat(SqliteTransaction tx, string machineId, string? reportedVersion, long nowMs)
    {
        if (reportedVersion is null) return;
        using var resolve = Command("UPDATE agent_update_operations SET status=CASE WHEN target_version=$version THEN 'completed' ELSE 'rolledBack' END,updated_at_unix_ms=$now WHERE machine_id=$machine AND started_at_unix_ms IS NOT NULL AND status IN ('installing','awaitingHeartbeat','rollingBack','failed') AND (target_version=$version OR (status='rollingBack' AND previous_version=$version))",
            ("$version", reportedVersion), ("$now", nowMs), ("$machine", machineId));
        resolve.Transaction = tx;
        resolve.ExecuteNonQuery();
    }

    private void ExpireUnstartedAgentUpdates(SqliteTransaction tx, long nowMs)
    {
        using var expire = Command("UPDATE agent_update_operations SET status='expired',updated_at_unix_ms=$now WHERE status='awaitingInstaller' AND started_at_unix_ms IS NULL AND ticket_expires_at_unix_ms <= $now", ("$now", nowMs));
        expire.Transaction = tx;
        expire.ExecuteNonQuery();
    }

    private static string NewAgentUpdateTicket(out string hash)
    {
        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        hash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(ticket)));
        return ticket;
    }

    private static string? AgentUpdateTicketHash(string? ticket)
    {
        if (ticket is null || !System.Text.RegularExpressions.Regex.IsMatch(ticket, @"\A[A-Za-z0-9_-]{43}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(ticket)));
    }

    private AgentUpdateOperationDto FindAgentUpdateOperation(SqliteTransaction tx, string operationId)
    {
        using var operation = Command("SELECT operation_id,machine_id,release_version,target_version,target_package_sha256,previous_version,status,created_at_unix_ms,updated_at_unix_ms FROM agent_update_operations WHERE operation_id=$id", ("$id", operationId));
        operation.Transaction = tx;
        using var row = operation.ExecuteReader();
        if (!row.Read()) throw new KeyNotFoundException();
        return new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4), row.GetString(5),
            row.GetString(6), DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(7)), DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(8)));
    }
}
