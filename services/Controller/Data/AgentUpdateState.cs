using KamenRaider.Contracts;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using KamenRaider.Controller.Agents;
using KamenRaider.AgentPackage;
using System.Text.Json;

namespace KamenRaider.Controller.Data;

public sealed partial class ControllerDatabase
{

    public AgentReleaseDto PublishAgentRelease(AgentReleasePublication release)
    {
        ArgumentNullException.ThrowIfNull(release);
        Identifier(release.Version);
        if (release.Channel is not ("stable" or "beta") || string.IsNullOrWhiteSpace(release.ReleaseNotes)
            || !Path.IsPathFullyQualified(release.PackagePath) || release.PackageSizeBytes <= 0
            || string.IsNullOrWhiteSpace(release.SourceCommit) || release.SourceDirty || !release.ReleaseBuild
            || release.TargetRuntime is not ("win-x64" or "win-arm64" or "unknown")
            || release.AgentUpdateChallengeProtocol is not (1 or 2))
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
            using (var insert = Command("INSERT INTO agent_releases(version,channel,release_notes,package_sha256,package_path,package_size_bytes,source_commit,source_dirty,release_build,package_format,published_at_unix_ms,target_runtime,agent_update_challenge_protocol) VALUES($version,$channel,$notes,$hash,$path,$size,$commit,0,1,'AgentPackage.zip',$published,$runtime,$protocol)",
                ("$version", release.Version), ("$channel", release.Channel), ("$notes", release.ReleaseNotes), ("$hash", canonicalHash),
                ("$path", release.PackagePath), ("$size", release.PackageSizeBytes), ("$commit", release.SourceCommit),
                ("$published", publishedAt.ToUnixTimeMilliseconds()), ("$runtime", release.TargetRuntime),
                ("$protocol", release.AgentUpdateChallengeProtocol)))
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

    public AgentUpdateTicketResponse ReserveAgentUpdate(string machineId, string releaseVersion, string actorId, string requestId)
    {
        if (!agentReleaseRemediationEnabled) throw new InvalidOperationException("Agent release remediation is disabled");
        Identifier(machineId);
        Identifier(releaseVersion);
        Identifier(actorId);
        Identifier(requestId);
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var nowMs = now.ToUnixTimeMilliseconds();
            using var tx = connection.BeginTransaction();
            ExpireUnstartedAgentUpdates(tx, nowMs);

            using (var prior = Command("SELECT 1 FROM agent_update_audit WHERE actor_id=$actor AND request_id=$request", ("$actor", actorId), ("$request", requestId)))
            {
                prior.Transaction = tx;
                if (prior.ExecuteScalar() is not null) throw new InvalidOperationException("Reservation request was already used");
            }

            using (var machine = Command("SELECT 1 FROM machines WHERE machine_id=$id", ("$id", machineId)))
            {
                machine.Transaction = tx;
                if (machine.ExecuteScalar() is null) throw new KeyNotFoundException();
            }

            using (var allocation = Command("SELECT EXISTS(SELECT 1 FROM leases WHERE machine_id=$id AND allocation_held=1)", ("$id", machineId)))
            {
                allocation.Transaction = tx;
                if (Convert.ToInt64(allocation.ExecuteScalar()) != 0) throw new InvalidOperationException("Machine has a held lease");
            }
            using (var active = Command("SELECT EXISTS(SELECT 1 FROM agent_maintenance_chains WHERE machine_id=$id AND held=1)", ("$id", machineId)))
            {
                active.Transaction = tx;
                if (Convert.ToInt64(active.ExecuteScalar()) != 0) throw new InvalidOperationException("Machine already has an active Agent update");
            }

            string targetVersion;
            string packageHash;
            string packagePath;
            long packageSize;
            string publishedRuntime;
            int publishedChallengeProtocol;
            using (var release = Command("SELECT version,package_sha256,package_path,package_size_bytes,target_runtime,agent_update_challenge_protocol FROM agent_releases WHERE version=$version", ("$version", releaseVersion)))
            {
                release.Transaction = tx;
                using var row = release.ExecuteReader();
                if (!row.Read()) throw new KeyNotFoundException();
                targetVersion = row.GetString(0);
                packageHash = row.GetString(1);
                packagePath = row.GetString(2);
                packageSize = row.GetInt64(3);
                publishedRuntime = row.GetString(4);
                publishedChallengeProtocol = row.GetInt32(5);
            }
            using var artifact = OpenUpdateArtifact(packagePath, packageHash, packageSize, targetVersion, out var inventory);
            if (publishedChallengeProtocol != 2 || inventory.AgentUpdateChallengeProtocol != publishedChallengeProtocol
                || publishedRuntime is not ("win-x64" or "win-arm64") || inventory.TargetRuntime != publishedRuntime)
                throw new InvalidOperationException("The selected release is unavailable, corrupt, or lacks update confirmation support; publish a compatible release.");

            var ticket = NewAgentUpdateTicket(out var ticketHash);
            var operationId = Guid.NewGuid().ToString("N");
            var chainId = Guid.NewGuid().ToString("N");
            var expires = now.AddMinutes(10);
            using (var insert = Command("INSERT INTO agent_maintenance_chains(chain_id,machine_id,current_head_operation_id,generation,held,inherited,created_at_unix_ms,updated_at_unix_ms) VALUES($chain,$machine,$operation,1,1,0,$now,$now); INSERT INTO agent_update_operations(operation_id,machine_id,chain_id,release_version,target_version,target_package_sha256,target_runtime,target_protocol,status,ticket_hash,ticket_generation,ticket_purpose,ticket_expires_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) VALUES($operation,$machine,$chain,$release,$target,$hash,$runtime,2,'awaitingInstaller',$ticket,1,'apply',$expires,$now,$now); INSERT INTO agent_update_audit(audit_id,chain_id,operation_id,actor_id,request_id,event_type,new_binding_json,created_at_unix_ms) VALUES($audit,$chain,$operation,$actor,$request,'reserved',$binding,$now)",
                ("$operation", operationId), ("$machine", machineId), ("$release", releaseVersion), ("$target", targetVersion),
                ("$chain", chainId), ("$hash", packageHash), ("$runtime", inventory.TargetRuntime), ("$ticket", ticketHash),
                ("$audit", Guid.NewGuid().ToString("N")), ("$actor", actorId), ("$request", requestId),
                ("$binding", JsonSerializer.Serialize(new { releaseVersion, targetVersion, packageHash, inventory.TargetRuntime, inventory.InventoryDigest })),
                ("$expires", expires.ToUnixTimeMilliseconds()), ("$now", nowMs)))
            {
                insert.Transaction = tx;
                insert.ExecuteNonQuery();
            }
            tx.Commit();
            return new(operationId, machineId, ticket, expires, releaseVersion, targetVersion, packageHash);
        }
    }

    private FileStream OpenUpdateArtifact(string path, string hash, long size, string version, out PackageInventory inventory)
    {
        try
        {
            var canonical = CanonicalAgentUpdateHash(hash);
            var expected = ControllerReleaseArtifacts.PathFor(agentReleaseRoot, canonical);
            if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase) || size is <= 0 or > 100L * 1024 * 1024)
                throw new InvalidDataException();
            var verified = ControllerReleaseArtifacts.OpenVerified(expected, canonical, size);
            try
            {
                inventory = PackageVerifier.ValidateArchive(verified, version);
                if (!string.Equals(inventory.ArchiveHash, canonical, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                return verified;
            }
            catch { verified.Dispose(); throw; }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or PackageValidationException or NotSupportedException or BadImageFormatException)
        {
            throw new InvalidOperationException("The selected release is unavailable, corrupt, or lacks update confirmation support; publish a compatible release.");
        }
    }

    public AgentUpdateTicketResponse RenewAgentUpdateTicket(string machineId, string operationId)
    {
        Identifier(machineId);
        Identifier(operationId);
        throw new InvalidOperationException("Recovery command issuance requires the audited successor protocol");
    }

    public AgentUpdateOperationDto StartAgentUpdate(string machineId, string ticket)
    {
        var expected = FindAgentUpdateTicketContext(machineId, ticket);
        return StartAgentUpdate(machineId, expected.OperationId, ticket, expected.ReleaseVersion, expected.TargetVersion, expected.TargetPackageSha256);
    }

    public AgentUpdateOperationDto StartAgentUpdate(string machineId, string operationId, string ticket, string releaseVersion, string targetVersion, string targetPackageSha256)
    {
        Identifier(machineId);
        Identifier(operationId);
        var ticketHash = AgentUpdateTicketHash(ticket) ?? throw new UnauthorizedAccessException("Update ticket is invalid");
        Identifier(releaseVersion);
        Identifier(targetVersion);
        var expectedHash = CanonicalAgentUpdateHash(targetPackageSha256);
        lock (gate)
        {
            var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
            using var tx = connection.BeginTransaction();
            long expiresAt;
            long? ticketStartedAt;
            using (var operation = Command("SELECT ticket_expires_at_unix_ms,ticket_started_at_unix_ms FROM agent_update_operations WHERE operation_id=$operation AND ticket_hash=$ticket AND machine_id=$machine AND release_version=$release AND target_version=$target AND upper(target_package_sha256)=upper($hash) AND status='awaitingInstaller' AND started_at_unix_ms IS NULL AND ticket_package_downloaded_at_unix_ms IS NULL AND EXISTS(SELECT 1 FROM agent_maintenance_chains c WHERE c.chain_id=agent_update_operations.chain_id AND c.current_head_operation_id=agent_update_operations.operation_id AND c.held=1 AND c.generation=agent_update_operations.ticket_generation AND agent_update_operations.ticket_purpose='apply')",
                ("$operation", operationId), ("$ticket", ticketHash), ("$machine", machineId), ("$release", releaseVersion), ("$target", targetVersion), ("$hash", expectedHash)))
            {
                operation.Transaction = tx;
                using var row = operation.ExecuteReader();
                if (!row.Read()) throw new UnauthorizedAccessException("Update ticket is invalid");
                expiresAt = row.GetInt64(0);
                ticketStartedAt = row.IsDBNull(1) ? null : row.GetInt64(1);
            }
            if (ticketStartedAt is not null || expiresAt <= nowMs) throw new UnauthorizedAccessException("Update ticket is expired or already used");
            using (var start = Command("UPDATE agent_update_operations SET status='installing',ticket_started_at_unix_ms=$now,started_at_unix_ms=COALESCE(started_at_unix_ms,$now),updated_at_unix_ms=$now WHERE operation_id=$id AND machine_id=$machine AND ticket_hash=$ticket AND release_version=$release AND target_version=$target AND upper(target_package_sha256)=upper($hash) AND ticket_started_at_unix_ms IS NULL AND ticket_expires_at_unix_ms > $now AND status='awaitingInstaller' AND started_at_unix_ms IS NULL AND ticket_package_downloaded_at_unix_ms IS NULL AND EXISTS(SELECT 1 FROM agent_maintenance_chains c WHERE c.chain_id=agent_update_operations.chain_id AND c.current_head_operation_id=agent_update_operations.operation_id AND c.held=1 AND c.generation=agent_update_operations.ticket_generation AND agent_update_operations.ticket_purpose='apply')",
                ("$now", nowMs), ("$id", operationId), ("$machine", machineId), ("$ticket", ticketHash), ("$release", releaseVersion), ("$target", targetVersion), ("$hash", expectedHash)))
            {
                start.Transaction = tx;
                if (start.ExecuteNonQuery() != 1) throw new UnauthorizedAccessException("Update ticket is expired or already used");
            }
            var result = FindAgentUpdateOperation(tx, operationId);
            tx.Commit();
            return result;
        }
    }

    public AgentUpdateOperationDto ConsumeAgentUpdatePackageDownload(string machineId, string operationId, string ticket, string releaseVersion, string targetVersion, string targetPackageSha256)
    {
        Identifier(machineId);
        Identifier(operationId);
        var ticketHash = AgentUpdateTicketHash(ticket) ?? throw new UnauthorizedAccessException("Update ticket is invalid");
        Identifier(releaseVersion);
        Identifier(targetVersion);
        var expectedHash = CanonicalAgentUpdateHash(targetPackageSha256);
        lock (gate)
        {
            var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
            using var tx = connection.BeginTransaction();
            using (var operation = Command("SELECT 1 FROM agent_update_operations WHERE operation_id=$operation AND machine_id=$machine AND ticket_hash=$ticket AND release_version=$release AND target_version=$target AND upper(target_package_sha256)=upper($hash) AND ticket_started_at_unix_ms IS NOT NULL AND ticket_package_downloaded_at_unix_ms IS NULL AND status IN ('installing','awaitingHeartbeat','rollingBack') AND EXISTS(SELECT 1 FROM agent_maintenance_chains c WHERE c.chain_id=agent_update_operations.chain_id AND c.current_head_operation_id=agent_update_operations.operation_id AND c.held=1 AND c.generation=agent_update_operations.ticket_generation AND agent_update_operations.ticket_purpose='apply')",
                ("$operation", operationId), ("$machine", machineId), ("$ticket", ticketHash), ("$release", releaseVersion), ("$target", targetVersion), ("$hash", expectedHash)))
            {
                operation.Transaction = tx;
                if (operation.ExecuteScalar() is null) throw new UnauthorizedAccessException("Update ticket is invalid or has no package download remaining");
            }
            using (var consume = Command("UPDATE agent_update_operations SET ticket_package_downloaded_at_unix_ms=$now,updated_at_unix_ms=$now WHERE operation_id=$operation AND machine_id=$machine AND ticket_hash=$ticket AND release_version=$release AND target_version=$target AND upper(target_package_sha256)=upper($hash) AND ticket_package_downloaded_at_unix_ms IS NULL AND ticket_started_at_unix_ms IS NOT NULL AND status IN ('installing','awaitingHeartbeat','rollingBack') AND EXISTS(SELECT 1 FROM agent_maintenance_chains c WHERE c.chain_id=agent_update_operations.chain_id AND c.current_head_operation_id=agent_update_operations.operation_id AND c.held=1 AND c.generation=agent_update_operations.ticket_generation AND agent_update_operations.ticket_purpose='apply')",
                ("$now", nowMs), ("$operation", operationId), ("$machine", machineId), ("$ticket", ticketHash), ("$release", releaseVersion), ("$target", targetVersion), ("$hash", expectedHash)))
            {
                consume.Transaction = tx;
                if (consume.ExecuteNonQuery() != 1) throw new InvalidOperationException("Update package download was already consumed");
            }
            var result = FindAgentUpdateOperation(tx, operationId);
            tx.Commit();
            return result;
        }
    }

    public AgentUpdateOperationDto ReportAgentUpdateStatus(string machineId, string ticket, string status)
    {
        var expected = FindAgentUpdateTicketContext(machineId, ticket);
        return ReportAgentUpdateStatus(machineId, expected.OperationId, ticket, expected.ReleaseVersion, expected.TargetVersion, expected.TargetPackageSha256, status);
    }

    public AgentUpdateOperationDto ReportAgentUpdateStatus(string machineId, string operationId, string ticket, string releaseVersion, string targetVersion, string targetPackageSha256, string status)
    {
        Identifier(machineId);
        Identifier(operationId);
        Identifier(releaseVersion);
        Identifier(targetVersion);
        if (status is not ("awaitingHeartbeat" or "rollingBack" or "failed")) throw new ArgumentException("Invalid update status");
        var ticketHash = AgentUpdateTicketHash(ticket) ?? throw new UnauthorizedAccessException("Update ticket is invalid");
        var expectedHash = CanonicalAgentUpdateHash(targetPackageSha256);
        lock (gate)
        {
            var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
            using var tx = connection.BeginTransaction();
            var canonicalReport = JsonSerializer.Serialize(new { machineId, operationId, releaseVersion, targetVersion, packageHash = expectedHash, status });
            var reportDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalReport)));
            var reportRequestId = reportDigest;
            string currentStatus;
            using (var operation = Command("SELECT status FROM agent_update_operations WHERE operation_id=$operation AND ticket_hash=$ticket AND machine_id=$machine AND release_version=$release AND target_version=$target AND upper(target_package_sha256)=upper($hash) AND ticket_started_at_unix_ms IS NOT NULL AND status IN ('installing','awaitingHeartbeat','rollingBack','failed') AND EXISTS(SELECT 1 FROM agent_maintenance_chains c WHERE c.chain_id=agent_update_operations.chain_id AND c.current_head_operation_id=agent_update_operations.operation_id AND c.held=1 AND c.generation=agent_update_operations.ticket_generation AND agent_update_operations.ticket_purpose='apply')",
                ("$operation", operationId), ("$ticket", ticketHash), ("$machine", machineId), ("$release", releaseVersion), ("$target", targetVersion), ("$hash", expectedHash)))
            {
                operation.Transaction = tx;
                using var row = operation.ExecuteReader();
                if (!row.Read()) throw new UnauthorizedAccessException("Update ticket is invalid");
                currentStatus = row.GetString(0);
            }
            if (currentStatus is not ("installing" or "awaitingHeartbeat" or "rollingBack" or "failed"))
                throw new InvalidOperationException("Update operation cannot accept a status report");
            var reportAlreadyRecorded = false;
            using (var previousReport = Command("SELECT body_digest,status FROM agent_update_reports WHERE operation_id=$operation AND request_id=$request",
                ("$operation", operationId), ("$request", reportRequestId)))
            {
                previousReport.Transaction = tx;
                using var report = previousReport.ExecuteReader();
                if (report.Read())
                {
                    if (report.GetString(0) != reportDigest || report.GetString(1) != status)
                        throw new InvalidOperationException("Update report request ID was reused with different content");
                    reportAlreadyRecorded = true;
                }
            }
            if (reportAlreadyRecorded)
            {
                var duplicate = FindAgentUpdateOperation(tx, operationId);
                tx.Commit();
                return duplicate;
            }
            if (currentStatus == status)
            {
                AppendAgentUpdateReport(tx, operationId, reportRequestId, reportDigest, status);
                var unchanged = FindAgentUpdateOperation(tx, operationId);
                tx.Commit();
                return unchanged;
            }
            var transitionAllowed = currentStatus switch
            {
                "installing" => status is "awaitingHeartbeat" or "rollingBack" or "failed",
                "awaitingHeartbeat" => status is "rollingBack" or "failed",
                "rollingBack" => status == "failed",
                _ => false,
            };
            if (!transitionAllowed) throw new InvalidOperationException("Update status cannot move backwards");
            using (var update = Command("UPDATE agent_update_operations SET status=$status,updated_at_unix_ms=$now WHERE operation_id=$id AND machine_id=$machine AND ticket_hash=$ticket AND release_version=$release AND target_version=$target AND upper(target_package_sha256)=upper($hash) AND EXISTS(SELECT 1 FROM agent_maintenance_chains c WHERE c.chain_id=agent_update_operations.chain_id AND c.current_head_operation_id=agent_update_operations.operation_id AND c.held=1 AND c.generation=agent_update_operations.ticket_generation AND agent_update_operations.ticket_purpose='apply')",
                ("$status", status), ("$now", nowMs), ("$id", operationId), ("$machine", machineId), ("$ticket", ticketHash), ("$release", releaseVersion), ("$target", targetVersion), ("$hash", expectedHash)))
            {
                update.Transaction = tx;
                if (update.ExecuteNonQuery() != 1) throw new UnauthorizedAccessException("Update ticket changed during status reporting");
            }
            AppendAgentUpdateReport(tx, operationId, reportRequestId, reportDigest, status);
            var result = FindAgentUpdateOperation(tx, operationId);
            tx.Commit();
            return result;
        }
    }

    private void AppendAgentUpdateReport(SqliteTransaction tx, string operationId, string requestId, string bodyDigest, string status)
    {
        using var insert = Command("INSERT INTO agent_update_reports(operation_id,request_id,body_digest,generation,status) VALUES($operation,$request,$digest,(SELECT COALESCE(MAX(generation),0)+1 FROM agent_update_reports WHERE operation_id=$operation),$status)",
            ("$operation", operationId), ("$request", requestId), ("$digest", bodyDigest), ("$status", status));
        insert.Transaction = tx;
        insert.ExecuteNonQuery();
    }

    private (string OperationId, string ReleaseVersion, string TargetVersion, string TargetPackageSha256) FindAgentUpdateTicketContext(string machineId, string ticket)
    {
        Identifier(machineId);
        var ticketHash = AgentUpdateTicketHash(ticket) ?? throw new UnauthorizedAccessException("Update ticket is invalid");
        lock (gate)
        {
            using var operation = Command("SELECT operation_id,release_version,target_version,target_package_sha256 FROM agent_update_operations WHERE ticket_hash=$ticket AND machine_id=$machine AND EXISTS(SELECT 1 FROM agent_maintenance_chains c WHERE c.chain_id=agent_update_operations.chain_id AND c.current_head_operation_id=agent_update_operations.operation_id AND c.held=1 AND c.generation=agent_update_operations.ticket_generation AND agent_update_operations.ticket_purpose='apply')", ("$ticket", ticketHash), ("$machine", machineId));
            using var row = operation.ExecuteReader();
            if (!row.Read()) throw new UnauthorizedAccessException("Update ticket is invalid");
            return (row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3));
        }
    }

    private static string CanonicalAgentUpdateHash(string value)
    {
        try
        {
            var bytes = Convert.FromHexString(value);
            if (bytes.Length != 32) throw new ArgumentException("Invalid Agent package SHA-256");
            return Convert.ToHexString(bytes);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Invalid Agent package SHA-256", exception);
        }
    }

    private void ExpireUnstartedAgentUpdates(SqliteTransaction tx, long nowMs)
    {
        using var expire = Command("UPDATE agent_update_operations SET status='expired',updated_at_unix_ms=$now WHERE status='awaitingInstaller' AND started_at_unix_ms IS NULL AND parent_operation_id IS NULL AND ticket_expires_at_unix_ms <= $now AND EXISTS(SELECT 1 FROM agent_maintenance_chains c WHERE c.chain_id=agent_update_operations.chain_id AND c.current_head_operation_id=agent_update_operations.operation_id AND c.held=1 AND c.inherited=0 AND c.generation=agent_update_operations.ticket_generation AND NOT EXISTS(SELECT 1 FROM agent_update_operations prior WHERE prior.chain_id=c.chain_id AND (prior.started_at_unix_ms IS NOT NULL OR prior.ticket_started_at_unix_ms IS NOT NULL OR prior.ticket_package_downloaded_at_unix_ms IS NOT NULL))); UPDATE agent_maintenance_chains SET held=0,updated_at_unix_ms=$now WHERE held=1 AND inherited=0 AND EXISTS(SELECT 1 FROM agent_update_operations o WHERE o.operation_id=agent_maintenance_chains.current_head_operation_id AND o.status='expired' AND o.parent_operation_id IS NULL AND o.started_at_unix_ms IS NULL AND o.ticket_generation=agent_maintenance_chains.generation) AND NOT EXISTS(SELECT 1 FROM agent_update_operations prior WHERE prior.chain_id=agent_maintenance_chains.chain_id AND (prior.started_at_unix_ms IS NOT NULL OR prior.ticket_started_at_unix_ms IS NOT NULL OR prior.ticket_package_downloaded_at_unix_ms IS NOT NULL))", ("$now", nowMs));
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
        using var operation = Command("SELECT o.operation_id,o.machine_id,o.chain_id,c.current_head_operation_id,c.generation,c.held,o.parent_operation_id,o.release_version,o.target_version,o.target_package_sha256,o.target_runtime,o.target_protocol,o.previous_version,o.source_version,o.source_evidence_digest,o.backup_owner_operation_id,o.backup_digest,o.status,o.created_at_unix_ms,o.updated_at_unix_ms FROM agent_update_operations o JOIN agent_maintenance_chains c ON c.chain_id=o.chain_id WHERE o.operation_id=$id", ("$id", operationId));
        operation.Transaction = tx;
        using var row = operation.ExecuteReader();
        if (!row.Read()) throw new KeyNotFoundException();
        return new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetInt64(4), row.GetInt32(5)!=0,
            row.IsDBNull(6)?null:row.GetString(6), row.GetString(7), row.GetString(8), row.GetString(9),
            row.IsDBNull(10)?null:row.GetString(10), row.IsDBNull(11)?null:row.GetInt32(11), row.IsDBNull(12)?null:row.GetString(12),
            row.IsDBNull(13)?null:row.GetString(13), row.IsDBNull(14)?null:row.GetString(14), row.IsDBNull(15)?null:row.GetString(15),
            row.IsDBNull(16)?null:row.GetString(16), row.GetString(17), DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(18)),
            DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(19)));
    }
}
