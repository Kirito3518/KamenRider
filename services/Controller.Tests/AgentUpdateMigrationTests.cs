using KamenRaider.Controller.Data;
using Microsoft.Data.Sqlite;

namespace Controller.Tests;

public sealed class AgentUpdateMigrationTests
{
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
            Assert.Equal(14L, Scalar(verify, "PRAGMA user_version"));
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
            Assert.Equal(14L, Scalar(verify, "PRAGMA user_version"));
            Assert.Equal(1L, Scalar(verify, "SELECT COUNT(*) FROM agent_update_operations"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
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
