using KamenRaider.Controller.Data;
using Microsoft.Data.Sqlite;

namespace Controller.Tests;

public sealed class ReadinessDiagnosticsMigrationTests
{
    [Fact]
    public void UpgradesExistingVersionTenDatabaseAndKeepsTheMigrationIdempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"readiness-migration-{Guid.NewGuid():N}.db");
        try
        {
            CreateVersionTenDatabase(path);

            using (new ControllerDatabase(path)) { }
            using (var verify = Open(path))
            {
                Assert.Equal(11L, Scalar(verify, "PRAGMA user_version"));
                Assert.Equal(0L, Scalar(verify, "SELECT COUNT(*) FROM machine_readiness_diagnostics"));
                Assert.Equal("pc", Scalar(verify, "SELECT machine_id FROM machines"));
                Assert.Equal("machine_id,diagnostics_json,updated_at", string.Join(',', Columns(verify, "machine_readiness_diagnostics")));
            }

            using (new ControllerDatabase(path)) { }
            using var restarted = Open(path);
            Assert.Equal(11L, Scalar(restarted, "PRAGMA user_version"));
            Assert.Equal(1L, Scalar(restarted, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='machine_readiness_diagnostics'"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void CreateVersionTenDatabase(string path)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        foreach (var name in new[]
        {
            "001-machines.sql", "002-leases-commands.sql", "003-pairing.sql", "004-boot-challenges.sql",
            "005-config-acks.sql", "006-enrollment-tickets.sql", "007-admin-vnc-sessions.sql",
            "008-vnc-approved-target.sql", "009-agent-version.sql", "010-completed-leases.sql",
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
            seed.CommandText = "INSERT INTO machines(machine_id,public_key,local_ip,mac_address,moonlight_port,sunshine_port) VALUES('pc','key','10.0.0.2','001122334455',47989,47990); PRAGMA user_version=10;";
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
