using KamenRaider.Controller.Data;
using Microsoft.Data.Sqlite;

namespace Controller.Tests;

public sealed class LeaseEndedAtMigrationTests
{
    [Fact]
    public void VersionTwentyFourDatabaseGainsANullEndedAtAndMigratesIdempotently()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lease-ended-at-{Guid.NewGuid():N}.db");
        try
        {
            CreateVersionTwentyFourDatabase(path);
            using (var before = Open(path))
            {
                Assert.Equal(24L, Scalar(before, "PRAGMA user_version"));
                Assert.DoesNotContain("ended_at", Columns(before, "leases"));
            }

            using (new ControllerDatabase(path)) { }
            using (var verify = Open(path))
            {
                Assert.Equal(25L, Scalar(verify, "PRAGMA user_version"));
                Assert.Equal("ended_at", Columns(verify, "leases")[^1]);
                // Existing rows keep their data and gain an unset end instant.
                Assert.Equal(1L, Scalar(verify, "SELECT COUNT(*) FROM leases WHERE ended_at IS NULL"));
                Assert.Equal("active", Scalar(verify, "SELECT state FROM leases WHERE id='live'"));
                Assert.Equal(17L, Scalar(verify, "SELECT rowid FROM leases WHERE id='live'"));
                Assert.Equal(60L, Scalar(verify, "SELECT duration_seconds FROM leases WHERE id='live'"));
                Assert.Equal("pc", Scalar(verify, "SELECT machine_id FROM machines"));
                Assert.Equal(1L, Scalar(verify, "SELECT COUNT(*) FROM command_outbox WHERE command_id='command'"));
                Assert.Equal(1L, Scalar(verify, "SELECT COUNT(*) FROM lease_requests WHERE request_key='create-once'"));
                // Lease indexes are untouched by the additive column.
                foreach (var index in new[] { "leases_machine_allocation", "leases_user", "lease_command_revision", "machines_pending" })
                    Assert.Equal(1L, Scalar(verify, $"SELECT COUNT(*) FROM sqlite_schema WHERE type='index' AND name='{index}'"));
                using var foreignKeys = verify.CreateCommand();
                foreignKeys.CommandText = "PRAGMA foreign_key_check";
                using var rows = foreignKeys.ExecuteReader();
                Assert.False(rows.Read());
                ControllerDatabaseMaintenance.ValidateCanonicalSchema(verify, 25);
            }

            // Reopening an already migrated database must not re-run the additive ALTER TABLE.
            using (new ControllerDatabase(path)) { }
            using var restarted = Open(path);
            Assert.Equal(25L, Scalar(restarted, "PRAGMA user_version"));
            Assert.Equal(1, Columns(restarted, "leases").Count(column => column == "ended_at"));
            Assert.Equal(0L, Scalar(restarted, "SELECT COUNT(*) FROM leases WHERE ended_at IS NOT NULL"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void CreateVersionTwentyFourDatabase(string path)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        foreach (var name in new[]
        {
            "001-machines.sql", "002-leases-commands.sql", "003-pairing.sql", "004-boot-challenges.sql",
            "005-config-acks.sql", "006-enrollment-tickets.sql", "007-admin-vnc-sessions.sql",
            "008-vnc-approved-target.sql", "009-agent-version.sql", "010-completed-leases.sql",
            "011-readiness-diagnostics.sql", "012-agent-releases.sql", "013-agent-update-downloads.sql",
            "014-agent-maintenance-chains.sql", "015-agent-sync-proof.sql", "016-agent-sync-challenge-rate-limit.sql",
            "017-agent-release-contract.sql", "018-agent-update-source-binding.sql", "019-controller-restore-quarantine.sql",
            "020-agent-update-recovery-intents.sql", "021-agent-update-recovery-successors.sql",
            "022-agent-update-confirmations.sql", "023-pc-control-commands.sql", "024-machine-profile-sunshine.sql",
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
                INSERT INTO machines(machine_id,public_key,approval_status,display_name,hourly_price,local_ip,mac_address,moonlight_port,sunshine_port)
                  VALUES('pc','key','approved','Legacy PC',500,'10.0.0.2','001122334455',47989,47990);
                INSERT INTO agent_sync_proof_state(machine_id,key_fingerprint) VALUES('pc',NULL);
                INSERT INTO leases(rowid,id,user_id,machine_id,state,revision,duration_seconds,preparation_deadline,starts_at,expires_at,allocation_held)
                  VALUES(17,'live','user','pc','active',1,60,0,1000,61000,1);
                INSERT INTO lease_requests VALUES('create:user','create-once','fingerprint','live');
                INSERT INTO command_outbox(rowid,command_id,type,rental_id,machine_id,lease_revision,boot_id,valid_until,payload,status)
                  VALUES(42,'command','ApplyLease','live','pc',1,'boot',61000,'{}','applied');
                PRAGMA user_version=24;
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
