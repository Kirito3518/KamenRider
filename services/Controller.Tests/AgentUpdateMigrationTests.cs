using KamenRaider.Controller.Data;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace Controller.Tests;

public sealed class AgentUpdateMigrationTests
{
    [Fact]
    public void VersionSixteenReleaseRowMigratesToUnknownProtocolOneAndCannotBeReserved()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-release-v16-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "controller.db");
        try
        {
            CreateVersionElevenDatabase(path);
            var package = AgentUpdateTestPackage.Create("2.3.4");
            var hash = Convert.ToHexString(SHA256.HashData(package));
            var artifact = Path.Combine(root, "agent-releases", "sha256", hash[..2].ToLowerInvariant(), hash.ToLowerInvariant() + ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            File.WriteAllBytes(artifact, package);

            using (var connection = Open(path))
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var name in new[]
                {
                    "012-agent-releases.sql", "013-agent-update-downloads.sql", "014-agent-maintenance-chains.sql",
                    "015-agent-sync-proof.sql", "016-agent-sync-challenge-rate-limit.sql",
                })
                {
                    using var resource = typeof(ControllerDatabase).Assembly.GetManifestResourceStream($"KamenRaider.Controller.Data.Migrations.{name}")!;
                    using var reader = new StreamReader(resource);
                    using var migration = connection.CreateCommand();
                    migration.Transaction = transaction;
                    migration.CommandText = reader.ReadToEnd();
                    migration.ExecuteNonQuery();
                }
                using var seed = connection.CreateCommand();
                seed.Transaction = transaction;
                seed.CommandText = "INSERT INTO agent_releases(version,channel,release_notes,package_sha256,package_path,package_size_bytes,source_commit,source_dirty,release_build,package_format,published_at_unix_ms) VALUES('2.3.4','stable','legacy row',$hash,$path,$size,'0123456789ab',0,1,'AgentPackage.zip',1); PRAGMA user_version=16";
                seed.Parameters.AddWithValue("$hash", hash);
                seed.Parameters.AddWithValue("$path", artifact);
                seed.Parameters.AddWithValue("$size", package.Length);
                seed.ExecuteNonQuery();
                transaction.Commit();
            }

            using var database = new ControllerDatabase(path, agentReleaseRemediationEnabled: true);
            var listed = Assert.Single(database.ListAgentReleases());
            Assert.Equal("2.3.4", listed.Version);
            Assert.Equal(hash, listed.PackageSha256);
            using (var migrated = Open(path))
            {
                Assert.Equal(18L, Scalar(migrated, "PRAGMA user_version"));
                Assert.Equal("unknown", Scalar(migrated, "SELECT target_runtime FROM agent_releases WHERE version='2.3.4'"));
                Assert.Equal(1L, Scalar(migrated, "SELECT agent_update_challenge_protocol FROM agent_releases WHERE version='2.3.4'"));
                Assert.Equal(1L, Scalar(migrated, "SELECT COUNT(*) FROM agent_releases WHERE version='2.3.4'"));
            }

            var unavailable = Assert.Throws<AgentReleaseUnavailableException>(() => database.ReserveAgentUpdate("pc", "2.3.4", "admin", "migrated-release"));
            Assert.Equal(AgentReleaseUnavailableKind.UnsupportedProtocolOrRuntime, unavailable.Kind);
            using var afterReservation = Open(path);
            Assert.Equal(0L, Scalar(afterReservation, "SELECT COUNT(*) FROM agent_maintenance_chains WHERE held=1"));
            Assert.Equal(0L, Scalar(afterReservation, "SELECT COUNT(*) FROM agent_update_operations"));
            Assert.Equal(0L, Scalar(afterReservation, "SELECT COUNT(*) FROM agent_update_audit"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void StartupAndSnapshotOperationsRejectWeakenedProofSchema()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-proof-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.db");
        var destination = Path.Combine(root, "destination.db");
        var backup = Path.Combine(root, "backup.db");
        try
        {
            using (new ControllerDatabase(source)) { }
            using (new ControllerDatabase(destination)) { }
            using (var connection = Open(source))
            {
                Assert.Equal(18L, Scalar(connection, "PRAGMA user_version"));
                using var weaken = connection.CreateCommand();
                weaken.CommandText = "DROP TRIGGER agent_update_reports_immutable_delete";
                weaken.ExecuteNonQuery();
            }
            var original = File.ReadAllBytes(destination);
            Assert.Throws<InvalidDataException>(() => new ControllerDatabase(source));
            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Backup(source, backup));
            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Restore(source, destination));
            Assert.False(File.Exists(backup));
            Assert.Equal(original, File.ReadAllBytes(destination));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void EveryLegacyNonterminalOperationImportsAsAHeldChain()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-chain-migration-{Guid.NewGuid():N}.db");
        try
        {
            CreateVersionElevenDatabase(path);
            using (var seed = Open(path))
            {
                foreach (var name in new[] { "012-agent-releases.sql", "013-agent-update-downloads.sql" })
                {
                    using var resource = typeof(ControllerDatabase).Assembly.GetManifestResourceStream($"KamenRaider.Controller.Data.Migrations.{name}")!;
                    using var reader = new StreamReader(resource);
                    using var migration = seed.CreateCommand();
                    migration.CommandText = reader.ReadToEnd();
                    migration.ExecuteNonQuery();
                }
                using var command = seed.CreateCommand();
                command.CommandText = "INSERT INTO agent_releases VALUES('2.0.0','stable','notes',lower(hex(zeroblob(32))),'/private/package.zip',100,'commit',0,1,'AgentPackage.zip',1)";
                command.ExecuteNonQuery();
                foreach (var status in new[] { "awaitingInstaller", "installing", "awaitingHeartbeat", "rollingBack", "failed" })
                {
                    command.CommandText = "INSERT INTO machines(machine_id,public_key,approval_status,local_ip,mac_address,moonlight_port,sunshine_port) VALUES($id,'a2V5','approved','10.0.0.2','001122334455',1,2); INSERT INTO machine_heartbeats(machine_id,last_seen,boot_id,readiness,agent_version) VALUES($id,$now,'boot','ready','legacy-hint'); INSERT INTO machine_config_acks(machine_id,applied_revision) VALUES($id,1); INSERT INTO agent_update_operations(operation_id,machine_id,release_version,target_version,target_package_sha256,previous_version,status,ticket_hash,ticket_expires_at_unix_ms,started_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) VALUES($id,$id,'2.0.0','2.0.0',lower(hex(zeroblob(32))),'legacy-hint',$status,$id,1,CASE WHEN $status='awaitingInstaller' THEN NULL ELSE 1 END,1,1)";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("$id", status);
                    command.Parameters.AddWithValue("$status", status);
                    command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                    command.ExecuteNonQuery();
                }
            }

            using (var database = new ControllerDatabase(path))
            {
                foreach (var machine in database.List().Where(machine => machine.MachineId != "pc"))
                {
                    Assert.False(machine.CanRent);
                    Assert.Equal(machine.MachineId, machine.AgentUpdateOperationId);
                    Assert.Equal(machine.MachineId, machine.AgentUpdateStatus);
                    Assert.Throws<InvalidOperationException>(() => database.CreateTrialLease("user", machine.MachineId, 60, "lease-" + machine.MachineId));
                }
            }
            using var verify = Open(path);
            Assert.Equal(1L, Scalar(verify, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='agent_maintenance_chains'"));
            Assert.Equal(5L, Scalar(verify, "SELECT COUNT(*) FROM agent_maintenance_chains WHERE held=1 AND inherited=1"));
            Assert.Equal(5L, Scalar(verify, "SELECT COUNT(*) FROM agent_update_operations WHERE previous_version='legacy-hint'"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void UpgradesVersionElevenAndEnforcesImmutableReleasesAndOneHeldChain()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-update-migration-{Guid.NewGuid():N}.db");
        try
        {
            CreateVersionElevenDatabase(path);
            using (new ControllerDatabase(path)) { }
            using var verify = Open(path);
            Assert.Equal(18L, Scalar(verify, "PRAGMA user_version"));
            Assert.Equal("pc", Scalar(verify, "SELECT machine_id FROM machines"));
            using (var seed = verify.CreateCommand())
            {
                seed.CommandText = "INSERT INTO agent_releases(version,channel,release_notes,package_sha256,package_path,package_size_bytes,source_commit,source_dirty,release_build,package_format,published_at_unix_ms) VALUES('2.0.0','stable','notes',lower(hex(zeroblob(32))),'/private/sha256/package.zip',100,'commit',0,1,'AgentPackage.zip',1)";
                seed.ExecuteNonQuery();
                seed.CommandText = "UPDATE agent_releases SET release_notes='replacement' WHERE version='2.0.0'";
                Assert.Throws<SqliteException>(() => seed.ExecuteNonQuery());
            }
            using (var tx = verify.BeginTransaction())
            using (var seed = verify.CreateCommand())
            {
                seed.Transaction = tx;
                seed.CommandText = "INSERT INTO agent_maintenance_chains VALUES('chain-one','pc','one',1,1,0,1,1); INSERT INTO agent_update_operations(operation_id,machine_id,chain_id,release_version,target_version,target_package_sha256,status,ticket_hash,ticket_generation,ticket_purpose,ticket_expires_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) VALUES('one','pc','chain-one','2.0.0','2.0.0',lower(hex(zeroblob(32))),'failed','ticket-one',1,'apply',1000,1,1)";
                seed.ExecuteNonQuery();
                tx.Commit();
            }
            using (var duplicate = verify.CreateCommand())
            {
                duplicate.CommandText = "INSERT INTO agent_maintenance_chains VALUES('chain-two','pc','two',1,1,0,1,1)";
                Assert.Throws<SqliteException>(() => duplicate.ExecuteNonQuery());
            }
            using (new ControllerDatabase(path)) { }
            Assert.Equal(18L, Scalar(verify, "PRAGMA user_version"));
            Assert.Equal(1L, Scalar(verify, "SELECT COUNT(*) FROM agent_update_operations"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void VersionFourteenReceiptMigratesAsUnprovenAndCannotRent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-proof-v14-{Guid.NewGuid():N}.db");
        try
        {
            CreateVersionElevenDatabase(path);
            using (var connection = Open(path))
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var name in new[] { "012-agent-releases.sql", "013-agent-update-downloads.sql", "014-agent-maintenance-chains.sql" })
                {
                    using var resource = typeof(ControllerDatabase).Assembly.GetManifestResourceStream($"KamenRaider.Controller.Data.Migrations.{name}")!;
                    using var reader = new StreamReader(resource);
                    using var migration = connection.CreateCommand();
                    migration.Transaction = transaction;
                    migration.CommandText = reader.ReadToEnd();
                    migration.ExecuteNonQuery();
                }
                using var seed = connection.CreateCommand();
                seed.Transaction = transaction;
                seed.CommandText = "UPDATE machines SET approval_status='approved'; INSERT INTO machine_config_acks(machine_id,applied_revision) VALUES('pc',1); INSERT INTO machine_heartbeats(machine_id,last_seen,boot_id,readiness,agent_version) VALUES('pc',$now,'legacy-boot','ready','legacy-hint');";
                seed.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                seed.ExecuteNonQuery();
                transaction.Commit();
            }

            using (var source = Open(path)) Assert.Equal(14L, Scalar(source, "PRAGMA user_version"));
            using (var database = new ControllerDatabase(path))
            {
                using (var migrated = Open(path)) Assert.Equal(18L, Scalar(migrated, "PRAGMA user_version"));
                var machine = Assert.Single(database.List());
                Assert.False(machine.CanRent);
                Assert.Null(machine.AgentVersion);
                Assert.Equal("legacy-boot", machine.BootId);
            }

            using var verify = Open(path);
            Assert.Equal(1L, Scalar(verify, "SELECT COUNT(*) FROM agent_sync_proof_state WHERE machine_id='pc' AND mode='legacyUnproven' AND sequence=0 AND token_sequence IS NULL AND last_proven_sequence IS NULL AND last_proven_version IS NULL AND legacy_reported_version='legacy-hint'"));
            Assert.Equal(1L, Scalar(verify, "SELECT COUNT(*) FROM machine_heartbeats WHERE machine_id='pc' AND agent_version IS NULL"));
            Assert.Equal(0L, Scalar(verify, "SELECT COUNT(*) FROM agent_signed_request_scopes"));
            Assert.Equal(0L, Scalar(verify, "SELECT COUNT(*) FROM agent_signed_request_nonces"));
            Assert.Equal(0L, Scalar(verify, "SELECT COUNT(*) FROM agent_update_evidence_challenges"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void VersionFifteenFutureSyncNonceSurvivesBackupRestoreAndRateLimitMigration()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-proof-v15-future-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "source.db");
        var backup = Path.Combine(root, "backup.db");
        var restored = Path.Combine(root, "restored.db");
        try
        {
            CreateVersionElevenDatabase(path);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("key")));
            var nonceDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("ambiguous-v15-sync-nonce")));
            const long futureSecond = 253402300799;
            using (var connection = Open(path))
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var name in new[] { "012-agent-releases.sql", "013-agent-update-downloads.sql", "014-agent-maintenance-chains.sql", "015-agent-sync-proof.sql" })
                {
                    using var resource = typeof(ControllerDatabase).Assembly.GetManifestResourceStream($"KamenRaider.Controller.Data.Migrations.{name}")!;
                    using var reader = new StreamReader(resource);
                    using var migration = connection.CreateCommand();
                    migration.Transaction = transaction;
                    migration.CommandText = reader.ReadToEnd();
                    migration.ExecuteNonQuery();
                }
                using var seed = connection.CreateCommand();
                seed.Transaction = transaction;
                seed.CommandText = "INSERT INTO agent_signed_request_scopes(machine_id,key_fingerprint,path,cutoff_second) VALUES('pc',$fingerprint,'/agent/sync',$cutoff); INSERT INTO agent_signed_request_nonces(machine_id,key_fingerprint,path,nonce_digest,signed_second) VALUES('pc',$fingerprint,'/agent/sync',$nonce,$future)";
                seed.Parameters.AddWithValue("$fingerprint", fingerprint);
                seed.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120);
                seed.Parameters.AddWithValue("$nonce", nonceDigest);
                seed.Parameters.AddWithValue("$future", futureSecond);
                seed.ExecuteNonQuery();
                transaction.Commit();
            }

            using (var source = Open(path)) Assert.Equal(15L, Scalar(source, "PRAGMA user_version"));
            ControllerDatabaseMaintenance.Backup(path, backup);
            ControllerDatabaseMaintenance.Restore(backup, restored);
            using (var restoredV15 = Open(restored))
            {
                Assert.Equal(15L, Scalar(restoredV15, "PRAGMA user_version"));
                Assert.Equal(1L, Scalar(restoredV15, $"SELECT COUNT(*) FROM agent_signed_request_nonces WHERE nonce_digest='{nonceDigest}' AND signed_second={futureSecond}"));
            }

            using (new ControllerDatabase(restored)) { }
            using var migrated = Open(restored);
            Assert.Equal(18L, Scalar(migrated, "PRAGMA user_version"));
            Assert.Equal(1L, Scalar(migrated, $"SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id='pc' AND key_fingerprint='{fingerprint}' AND path='/agent/sync' AND nonce_digest='{nonceDigest}' AND signed_second={futureSecond}"));
            Assert.Equal(futureSecond, Scalar(migrated, $"SELECT retention_second FROM agent_signed_request_nonces WHERE machine_id='pc' AND nonce_digest='{nonceDigest}'"));
            Assert.Equal(0L, Scalar(migrated, "SELECT challenge_response_count FROM agent_sync_proof_state WHERE machine_id='pc'"));
            Assert.Equal(0L, Scalar(migrated, "SELECT COUNT(*) FROM agent_signed_request_nonces WHERE machine_id='pc' AND path='/agent/sync#protocol2'"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void CreateVersionElevenDatabase(string path)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        foreach (var name in new[]
        {
            "001-machines.sql", "002-leases-commands.sql", "003-pairing.sql", "004-boot-challenges.sql",
            "005-config-acks.sql", "006-enrollment-tickets.sql", "007-admin-vnc-sessions.sql",
            "008-vnc-approved-target.sql", "009-agent-version.sql", "010-completed-leases.sql",
            "011-readiness-diagnostics.sql",
        })
        {
            using var resource = typeof(ControllerDatabase).Assembly
                .GetManifestResourceStream($"KamenRaider.Controller.Data.Migrations.{name}")!;
            using var reader = new StreamReader(resource);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
        }

        using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = "INSERT INTO machines(machine_id,public_key,local_ip,mac_address,moonlight_port,sunshine_port) VALUES('pc','a2V5','10.0.0.2','001122334455',47989,47990); PRAGMA user_version=11;";
            seed.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string[] Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var rows = command.ExecuteReader();
        var columns = new List<string>();
        while (rows.Read()) columns.Add(rows.GetString(1));
        return columns.ToArray();
    }
}
