using KamenRaider.Controller.Data;
using Microsoft.Data.Sqlite;

namespace Controller.Tests;

public sealed class LeaseCompletionMigrationTests
{
    [Fact]
    public void ExistingVersionNineDatabaseAddsCompletedStateAndPreservesLeaseReferences()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lease-completion-{Guid.NewGuid():N}.db");
        try
        {
            CreateVersionNineDatabase(path);

            using (new ControllerDatabase(path)) { }
            using (var verify = Open(path))
            {
                using (var command = verify.CreateCommand())
                {
                    command.CommandText = "UPDATE leases SET state='completed' WHERE id='expired'";
                    Assert.Equal(1, command.ExecuteNonQuery());
                }

                Assert.Equal("completed", Scalar(verify, "SELECT state FROM leases WHERE id='expired'"));
                Assert.Equal(17L, Scalar(verify, "SELECT rowid FROM leases WHERE id='expired'"));
                Assert.Equal("extend-once", Scalar(verify, "SELECT request_key FROM lease_requests WHERE rental_id='expired'"));
                Assert.Equal(42L, Scalar(verify, "SELECT rowid FROM command_outbox WHERE command_id='command'"));
                Assert.Equal("pair-once", Scalar(verify, "SELECT request_key FROM pairing_requests WHERE rental_id='expired'"));
                Assert.Equal(18L, Scalar(verify, "PRAGMA user_version"));

                using var foreignKeys = verify.CreateCommand();
                foreignKeys.CommandText = "PRAGMA foreign_key_check";
                using var rows = foreignKeys.ExecuteReader();
                Assert.False(rows.Read());

                using var marker = verify.CreateCommand();
                marker.CommandText = "CREATE TRIGGER migration_restart_marker AFTER UPDATE ON leases BEGIN SELECT 1; END";
                marker.ExecuteNonQuery();
            }

            using (new ControllerDatabase(path)) { }
            using (var restarted = Open(path))
            {
                Assert.Equal(18L, Scalar(restarted, "PRAGMA user_version"));
                Assert.Equal(1L, Scalar(restarted, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='migration_restart_marker'"));
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void CreateVersionNineDatabase(string path)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        foreach (var name in new[]
        {
            "001-machines.sql", "002-leases-commands.sql", "003-pairing.sql", "004-boot-challenges.sql",
            "005-config-acks.sql", "006-enrollment-tickets.sql", "007-admin-vnc-sessions.sql",
            "008-vnc-approved-target.sql", "009-agent-version.sql",
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
            seed.CommandText = """
                INSERT INTO machines(machine_id,public_key,local_ip,mac_address,moonlight_port,sunshine_port)
                  VALUES('pc','key','10.0.0.2','001122334455',47989,47990);
                INSERT INTO leases(rowid,id,user_id,machine_id,state,revision,duration_seconds,preparation_deadline,allocation_held)
                  VALUES(17,'expired','user','pc','ending',1,3600,0,0);
                INSERT INTO lease_requests VALUES('extend:expired','extend-once','fingerprint','expired');
                INSERT INTO command_outbox(rowid,command_id,type,rental_id,machine_id,lease_revision,boot_id,valid_until,payload,status)
                  VALUES(42,'command','ApplyLease','expired','pc',1,'boot',1,'{}','expired');
                INSERT INTO pairing_requests VALUES('expired','pair-once','slot','command');
                PRAGMA user_version=9;
                """;
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
}
