using KamenRaider.Controller.Data;
using Microsoft.Data.Sqlite;

namespace Controller.Tests;

public sealed class AgentUpdateMigrationTests
{
    [Fact]
    public void UpgradesVersionElevenDatabaseWithImmutableReleasesAndOneActiveOperationPerMachine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-update-migration-{Guid.NewGuid():N}.db");
        try
        {
            CreateVersionElevenDatabase(path);

            using (new ControllerDatabase(path)) { }
            using var verify = Open(path);

            Assert.Equal(12L, Scalar(verify, "PRAGMA user_version"));
            Assert.Equal("pc", Scalar(verify, "SELECT machine_id FROM machines"));
            Assert.Equal("version,channel,release_notes,package_sha256,package_path,package_size_bytes,source_commit,source_dirty,release_build,package_format,published_at_unix_ms", string.Join(',', Columns(verify, "agent_releases")));
            Assert.Equal("operation_id,machine_id,release_version,target_version,target_package_sha256,previous_version,status,ticket_hash,ticket_expires_at_unix_ms,ticket_started_at_unix_ms,started_at_unix_ms,created_at_unix_ms,updated_at_unix_ms", string.Join(',', Columns(verify, "agent_update_operations")));

            using (var seed = verify.CreateCommand())
            {
                seed.CommandText = "INSERT INTO agent_releases(version,channel,release_notes,package_sha256,package_path,package_size_bytes,source_commit,source_dirty,release_build,package_format,published_at_unix_ms) VALUES('2.0.0','stable','notes',lower(hex(zeroblob(32))),'/private/sha256/package.zip',100,'commit',0,1,'AgentPackage.zip',1); INSERT INTO agent_update_operations(operation_id,machine_id,release_version,target_version,target_package_sha256,previous_version,status,ticket_hash,ticket_expires_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) VALUES('one','pc','2.0.0','2.0.0',lower(hex(zeroblob(32))),'1.0.0','failed','ticket-one',1000,1,1);";
                seed.ExecuteNonQuery();
            }

            using (var duplicateVersion = verify.CreateCommand())
            {
                duplicateVersion.CommandText = "INSERT INTO agent_releases(version,channel,release_notes,package_sha256,package_path,package_size_bytes,source_commit,source_dirty,release_build,package_format,published_at_unix_ms) VALUES('2.0.0','beta','replacement',lower(hex(zeroblob(32))),'/private/other.zip',100,'other',0,1,'AgentPackage.zip',2)";
                Assert.Throws<SqliteException>(() => duplicateVersion.ExecuteNonQuery());
            }

            using (var mutateRelease = verify.CreateCommand())
            {
                mutateRelease.CommandText = "UPDATE agent_releases SET release_notes='replacement' WHERE version='2.0.0'";
                Assert.Throws<SqliteException>(() => mutateRelease.ExecuteNonQuery());
            }

            using (var secondActiveOperation = verify.CreateCommand())
            {
                secondActiveOperation.CommandText = "INSERT INTO agent_update_operations(operation_id,machine_id,release_version,target_version,target_package_sha256,previous_version,status,ticket_hash,ticket_expires_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) VALUES('two','pc','2.0.0','2.0.0',lower(hex(zeroblob(32))),'1.0.0','awaitingInstaller','ticket-two',1000,1,1)";
                Assert.Throws<SqliteException>(() => secondActiveOperation.ExecuteNonQuery());
            }

            using (var releaseLock = verify.CreateCommand())
            {
                releaseLock.CommandText = "UPDATE agent_update_operations SET status='expired' WHERE operation_id='one'; INSERT INTO agent_update_operations(operation_id,machine_id,release_version,target_version,target_package_sha256,previous_version,status,ticket_hash,ticket_expires_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) VALUES('two','pc','2.0.0','2.0.0',lower(hex(zeroblob(32))),'1.0.0','awaitingInstaller','ticket-two',1000,1,1)";
                Assert.Equal(2, releaseLock.ExecuteNonQuery());
            }

            using (new ControllerDatabase(path)) { }
            Assert.Equal(12L, Scalar(verify, "PRAGMA user_version"));
            Assert.Equal(2L, Scalar(verify, "SELECT COUNT(*) FROM agent_update_operations"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
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
            seed.CommandText = "INSERT INTO machines(machine_id,public_key,local_ip,mac_address,moonlight_port,sunshine_port) VALUES('pc','key','10.0.0.2','001122334455',47989,47990); PRAGMA user_version=11;";
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
