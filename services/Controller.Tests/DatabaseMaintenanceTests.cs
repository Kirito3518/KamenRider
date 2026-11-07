using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using KamenRaider.Contracts;
using KamenRaider.Controller.Data;

namespace Controller.Tests;

public class DatabaseMaintenanceTests
{
    [Fact]
    public async Task BackupCapturesConsistentDataWhileControllerIsActive()
    {
        var root = NewRoot();
        var databasePath = Path.Combine(root, "controller.db");
        var backupPath = Path.Combine(root, "backups", "controller.db");
        try
        {
            using var live = new ControllerDatabase(databasePath);
            AddMachine(live, "source-machine");
            using var active = new ControllerDatabaseActivityLock(databasePath);
            await active.StartAsync(default);

            ControllerDatabaseMaintenance.Backup(databasePath, backupPath);

            using var backup = new ControllerDatabase(backupPath);
            Assert.Equal("source-machine", Assert.Single(backup.List()).MachineId);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void BackupRejectsDatabaseWithMissingRequiredColumnWithoutPublishingFile()
    {
        var root = NewRoot();
        var databasePath = Path.Combine(root, "controller.db");
        var backupPath = Path.Combine(root, "backup.db");
        try
        {
            using (var database = new ControllerDatabase(databasePath)) AddMachine(database, "source-machine");
            ReplaceHeartbeatTable(databasePath, "CREATE TABLE machine_heartbeats(machine_id TEXT PRIMARY KEY,last_seen TEXT NOT NULL,boot_id TEXT NOT NULL);");

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Backup(databasePath, backupPath));
            Assert.False(File.Exists(backupPath));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void BackupRejectsVersionElevenDatabaseWithMissingReadinessDiagnosticsTable()
    {
        var root = NewRoot();
        var databasePath = Path.Combine(root, "controller.db");
        var backupPath = Path.Combine(root, "backup.db");
        try
        {
            using (var database = new ControllerDatabase(databasePath)) AddMachine(database, "source-machine");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE machine_readiness_diagnostics; PRAGMA user_version=11";
                command.ExecuteNonQuery();
            }

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Backup(databasePath, backupPath));
            Assert.False(File.Exists(backupPath));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void BackupRejectsVersionElevenDatabaseWithMalformedReadinessDiagnosticsTable()
    {
        var root = NewRoot();
        var databasePath = Path.Combine(root, "controller.db");
        var backupPath = Path.Combine(root, "backup.db");
        try
        {
            using (var database = new ControllerDatabase(databasePath)) AddMachine(database, "source-machine");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE machine_readiness_diagnostics; CREATE TABLE machine_readiness_diagnostics(machine_id TEXT PRIMARY KEY, diagnostics_json TEXT); PRAGMA user_version=11";
                command.ExecuteNonQuery();
            }

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Backup(databasePath, backupPath));
            Assert.False(File.Exists(backupPath));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void BackupAcceptsVersionTenDatabaseWithoutReadinessDiagnosticsTable()
    {
        var root = NewRoot();
        var databasePath = Path.Combine(root, "controller-v10.db");
        var backupPath = Path.Combine(root, "backup-v10.db");
        var destinationPath = Path.Combine(root, "destination.db");
        try
        {
            using (var database = new ControllerDatabase(databasePath)) AddMachine(database, "source-machine");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE IF EXISTS machine_readiness_diagnostics; PRAGMA user_version=10";
                command.ExecuteNonQuery();
            }

            ControllerDatabaseMaintenance.Backup(databasePath, backupPath);
            ControllerDatabaseMaintenance.Restore(backupPath, destinationPath);
            using var restored = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath, Pooling = false }.ToString());
            restored.Open();
            using var version = restored.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            Assert.Equal(10L, version.ExecuteScalar());
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void RestoreReplacesDatabaseWithValidatedBackup()
    {
        var root = NewRoot();
        var sourcePath = Path.Combine(root, "source.db");
        var backupPath = Path.Combine(root, "backup.db");
        var destinationPath = Path.Combine(root, "destination.db");
        try
        {
            using (var source = new ControllerDatabase(sourcePath)) AddMachine(source, "restored-machine");
            ControllerDatabaseMaintenance.Backup(sourcePath, backupPath);
            using (var destination = new ControllerDatabase(destinationPath)) AddMachine(destination, "old-machine");

            var recoveryPath = ControllerDatabaseMaintenance.Restore(backupPath, destinationPath);

            using var restored = new ControllerDatabase(destinationPath);
            Assert.Equal("restored-machine", Assert.Single(restored.List()).MachineId);
            Assert.NotNull(recoveryPath);
            using var recovery = new ControllerDatabase(recoveryPath!);
            Assert.Equal("old-machine", Assert.Single(recovery.List()).MachineId);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void RestoreAcceptsLegacyCommandTableWithUnfilteredRevisionUniqueness()
    {
        var root = NewRoot();
        var backupPath = Path.Combine(root, "legacy-v2.db");
        var destinationPath = Path.Combine(root, "destination.db");
        try
        {
            CreateLegacyVersionTwoDatabase(backupPath);
            using (var destination = new ControllerDatabase(destinationPath)) AddMachine(destination, "preserved-machine");

            var recoveryPath = ControllerDatabaseMaintenance.Restore(backupPath, destinationPath);

            Assert.NotNull(recoveryPath);
            using (var restored = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                restored.Open();
                using var version = restored.CreateCommand();
                version.CommandText = "PRAGMA user_version";
                Assert.Equal(2L, version.ExecuteScalar());
            }
            using var recovery = new ControllerDatabase(recoveryPath!);
            Assert.Equal("preserved-machine", Assert.Single(recovery.List()).MachineId);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void RestoreRejectsLegacyCommandRevisionIndexWithExpressionTerm()
    {
        var root = NewRoot();
        var backupPath = Path.Combine(root, "legacy-expression-index.db");
        var destinationPath = Path.Combine(root, "destination.db");
        try
        {
            CreateLegacyVersionTwoDatabase(backupPath);
            ReplaceLegacyCommandRevisionConstraintWithIndex(backupPath, "rental_id,type,lease_revision,lower(payload)");
            using (var destination = new ControllerDatabase(destinationPath)) AddMachine(destination, "preserved-machine");

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Restore(backupPath, destinationPath));

            using var preserved = new ControllerDatabase(destinationPath);
            Assert.Equal("preserved-machine", Assert.Single(preserved.List()).MachineId);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void RestoreRejectsLegacyCommandRevisionIndexWithExtraTerm()
    {
        var root = NewRoot();
        var backupPath = Path.Combine(root, "legacy-extra-index-term.db");
        var destinationPath = Path.Combine(root, "destination.db");
        try
        {
            CreateLegacyVersionTwoDatabase(backupPath);
            ReplaceLegacyCommandRevisionConstraintWithIndex(backupPath, "rental_id,type,lease_revision,payload");
            using (var destination = new ControllerDatabase(destinationPath)) AddMachine(destination, "preserved-machine");

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Restore(backupPath, destinationPath));

            using var preserved = new ControllerDatabase(destinationPath);
            Assert.Equal("preserved-machine", Assert.Single(preserved.List()).MachineId);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task RestoreRefusesWhileControllerActivityLockIsHeld()
    {
        var root = NewRoot();
        var backupPath = Path.Combine(root, "backup.db");
        var destinationPath = Path.Combine(root, "destination.db");
        try
        {
            using (var backup = new ControllerDatabase(backupPath)) AddMachine(backup, "backup-machine");
            using (var destination = new ControllerDatabase(destinationPath)) AddMachine(destination, "live-machine");
            using var active = new ControllerDatabaseActivityLock(destinationPath);
            await active.StartAsync(default);

            Assert.Throws<InvalidOperationException>(() => ControllerDatabaseMaintenance.Restore(backupPath, destinationPath));
            using var unchanged = new ControllerDatabase(destinationPath);
            Assert.Equal("live-machine", Assert.Single(unchanged.List()).MachineId);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void InvalidBackupDoesNotChangeDestination()
    {
        var root = NewRoot();
        var backupPath = Path.Combine(root, "invalid.db");
        var destinationPath = Path.Combine(root, "destination.db");
        try
        {
            File.WriteAllText(backupPath, "not a sqlite database");
            using (var destination = new ControllerDatabase(destinationPath)) AddMachine(destination, "preserved-machine");

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Restore(backupPath, destinationPath));
            using var preserved = new ControllerDatabase(destinationPath);
            Assert.Equal("preserved-machine", Assert.Single(preserved.List()).MachineId);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void RestoreRejectsSqliteWithRequiredTableNamesButMalformedColumns()
    {
        var root = NewRoot();
        var backupPath = Path.Combine(root, "malformed-schema.db");
        var destinationPath = Path.Combine(root, "destination.db");
        try
        {
            using (var malformed = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString()))
            {
                malformed.Open();
                using var command = malformed.CreateCommand();
                command.CommandText = "CREATE TABLE machines (wrong TEXT); CREATE TABLE machine_heartbeats (wrong TEXT); CREATE TABLE leases (wrong TEXT); CREATE TABLE command_outbox (wrong TEXT);";
                command.ExecuteNonQuery();
            }

            using (var malformed = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                malformed.Open();
                using var integrity = malformed.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check";
                Assert.Equal("ok", integrity.ExecuteScalar()?.ToString());
                using var tables = malformed.CreateCommand();
                tables.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name IN ('machines','machine_heartbeats','leases','command_outbox')";
                Assert.Equal(4L, (long)tables.ExecuteScalar()!);
            }

            using (var destination = new ControllerDatabase(destinationPath)) AddMachine(destination, "preserved-machine");

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Restore(backupPath, destinationPath));

            using var preserved = new ControllerDatabase(destinationPath);
            Assert.Equal("preserved-machine", Assert.Single(preserved.List()).MachineId);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void RestoreRejectsDatabaseMissingARequiredColumn()
    {
        var root = NewRoot();
        var backupPath = Path.Combine(root, "missing-column.db");
        var destinationPath = Path.Combine(root, "destination.db");
        try
        {
            using (var backup = new ControllerDatabase(backupPath)) AddMachine(backup, "backup-machine");
            ReplaceHeartbeatTable(backupPath, "CREATE TABLE machine_heartbeats(machine_id TEXT PRIMARY KEY,last_seen TEXT NOT NULL,boot_id TEXT NOT NULL);");
            using (var destination = new ControllerDatabase(destinationPath)) AddMachine(destination, "preserved-machine");

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Restore(backupPath, destinationPath));

            using var preserved = new ControllerDatabase(destinationPath);
            Assert.Equal("preserved-machine", Assert.Single(preserved.List()).MachineId);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void RestoreCommandDoesNotPrintDatabaseRowValuesOnValidationFailure()
    {
        var root = NewRoot();
        var backupPath = Path.Combine(root, "row-content.db");
        var destinationPath = Path.Combine(root, "destination.db");
        const string rowValue = "task14-private-row-marker";
        try
        {
            using (var backup = new ControllerDatabase(backupPath)) AddMachine(backup, "backup-machine");
            ReplaceHeartbeatTable(backupPath, "CREATE TABLE machine_heartbeats(machine_id TEXT PRIMARY KEY,last_seen TEXT NOT NULL,boot_id TEXT NOT NULL);");
            using (var malformed = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString()))
            {
                malformed.Open();
                using var command = malformed.CreateCommand();
                command.CommandText = "INSERT INTO machine_heartbeats(machine_id,last_seen,boot_id) VALUES ('machine-marker',$rowValue,'boot-marker')";
                command.Parameters.AddWithValue("$rowValue", rowValue);
                command.ExecuteNonQuery();
            }
            using (var destination = new ControllerDatabase(destinationPath)) AddMachine(destination, "preserved-machine");

            var originalError = Console.Error;
            using var error = new StringWriter();
            int exitCode;
            bool handled;
            try
            {
                Console.SetError(error);
                handled = KamenRaider.Controller.ControllerMaintenanceCommand.TryRun(["--restore-database", backupPath, destinationPath, "--controller-stopped"], out exitCode);
            }
            finally { Console.SetError(originalError); }

            Assert.True(handled);
            Assert.Equal(1, exitCode);
            Assert.DoesNotContain(rowValue, error.ToString());
            using var preserved = new ControllerDatabase(destinationPath);
            Assert.Equal("preserved-machine", Assert.Single(preserved.List()).MachineId);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void RestoreRejectsDatabaseMissingARequiredPrimaryKey()
    {
        var root = NewRoot();
        var backupPath = Path.Combine(root, "missing-primary-key.db");
        var destinationPath = Path.Combine(root, "destination.db");
        try
        {
            using (var backup = new ControllerDatabase(backupPath)) AddMachine(backup, "backup-machine");
            ReplaceHeartbeatTable(backupPath, "CREATE TABLE machine_heartbeats(machine_id TEXT NOT NULL,last_seen TEXT NOT NULL,boot_id TEXT NOT NULL,readiness TEXT NOT NULL);");
            using (var destination = new ControllerDatabase(destinationPath)) AddMachine(destination, "preserved-machine");

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Restore(backupPath, destinationPath));

            using var preserved = new ControllerDatabase(destinationPath);
            Assert.Equal("preserved-machine", Assert.Single(preserved.List()).MachineId);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void RestoreRejectsDatabaseWithNarrowedRequiredColumnType()
    {
        var root = NewRoot();
        var backupPath = Path.Combine(root, "narrowed-column-type.db");
        var destinationPath = Path.Combine(root, "destination.db");
        try
        {
            using (var backup = new ControllerDatabase(backupPath)) AddMachine(backup, "backup-machine");
            ReplaceHeartbeatTable(backupPath, "CREATE TABLE machine_heartbeats(machine_id TEXT PRIMARY KEY,last_seen INTEGER NOT NULL,boot_id TEXT NOT NULL,readiness TEXT NOT NULL);");
            using (var destination = new ControllerDatabase(destinationPath)) AddMachine(destination, "preserved-machine");

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Restore(backupPath, destinationPath));

            using var preserved = new ControllerDatabase(destinationPath);
            Assert.Equal("preserved-machine", Assert.Single(preserved.List()).MachineId);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void RestoreRejectsDatabaseWithMalformedLeaseAllocationIndex()
    {
        var root = NewRoot();
        var backupPath = Path.Combine(root, "malformed-index.db");
        var destinationPath = Path.Combine(root, "destination.db");
        try
        {
            using (var backup = new ControllerDatabase(backupPath)) AddMachine(backup, "backup-machine");
            using (var malformed = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString()))
            {
                malformed.Open();
                using var command = malformed.CreateCommand();
                command.CommandText = "DROP INDEX leases_machine_allocation; CREATE INDEX leases_machine_allocation ON leases(machine_id);";
                command.ExecuteNonQuery();
            }

            using (var destination = new ControllerDatabase(destinationPath)) AddMachine(destination, "preserved-machine");

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Restore(backupPath, destinationPath));

            using var preserved = new ControllerDatabase(destinationPath);
            Assert.Equal("preserved-machine", Assert.Single(preserved.List()).MachineId);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void RestoreRejectsNarrowedLeaseAllocationIndexPredicate()
    {
        var root = NewRoot();
        var backupPath = Path.Combine(root, "narrowed-allocation-index.db");
        var destinationPath = Path.Combine(root, "destination.db");
        try
        {
            using (var backup = new ControllerDatabase(backupPath)) AddMachine(backup, "backup-machine");
            using (var malformed = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString()))
            {
                malformed.Open();
                using var command = malformed.CreateCommand();
                command.CommandText = "DROP INDEX leases_machine_allocation; CREATE UNIQUE INDEX leases_machine_allocation ON leases(machine_id) WHERE allocation_held=1 AND state='active';";
                command.ExecuteNonQuery();
            }

            using (var destination = new ControllerDatabase(destinationPath)) AddMachine(destination, "preserved-machine");

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Restore(backupPath, destinationPath));

            using var preserved = new ControllerDatabase(destinationPath);
            Assert.Equal("preserved-machine", Assert.Single(preserved.List()).MachineId);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void RestoreRejectsDatabaseWithNarrowedCommandRevisionIndex()
    {
        var root = NewRoot();
        var backupPath = Path.Combine(root, "narrowed-command-index.db");
        var destinationPath = Path.Combine(root, "destination.db");
        try
        {
            using (var backup = new ControllerDatabase(backupPath)) AddMachine(backup, "backup-machine");
            using (var malformed = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString()))
            {
                malformed.Open();
                using var command = malformed.CreateCommand();
                command.CommandText = "DROP INDEX lease_command_revision; CREATE UNIQUE INDEX lease_command_revision ON command_outbox(rental_id,type,lease_revision) WHERE type!='PairPin' AND status='pending';";
                command.ExecuteNonQuery();
            }

            using (var destination = new ControllerDatabase(destinationPath)) AddMachine(destination, "preserved-machine");

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Restore(backupPath, destinationPath));

            using var preserved = new ControllerDatabase(destinationPath);
            Assert.Equal("preserved-machine", Assert.Single(preserved.List()).MachineId);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void RestoreRejectsMissingCommandRevisionIndexEvenWhenUserVersionWasLowered()
    {
        var root = NewRoot();
        var backupPath = Path.Combine(root, "missing-command-index.db");
        var destinationPath = Path.Combine(root, "destination.db");
        try
        {
            using (var backup = new ControllerDatabase(backupPath)) AddMachine(backup, "backup-machine");
            using (var malformed = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString()))
            {
                malformed.Open();
                using var command = malformed.CreateCommand();
                command.CommandText = "DROP INDEX lease_command_revision; PRAGMA user_version=2;";
                command.ExecuteNonQuery();
            }

            using (var destination = new ControllerDatabase(destinationPath)) AddMachine(destination, "preserved-machine");

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Restore(backupPath, destinationPath));

            using var preserved = new ControllerDatabase(destinationPath);
            Assert.Equal("preserved-machine", Assert.Single(preserved.List()).MachineId);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void BackupDoesNotOverwriteAnExistingSnapshot()
    {
        var root = NewRoot();
        var sourcePath = Path.Combine(root, "source.db");
        var backupPath = Path.Combine(root, "backup.db");
        try
        {
            using (var source = new ControllerDatabase(sourcePath)) AddMachine(source, "source-machine");
            File.WriteAllText(backupPath, "keep existing backup");

            Assert.Throws<IOException>(() => ControllerDatabaseMaintenance.Backup(sourcePath, backupPath));
            Assert.Equal("keep existing backup", File.ReadAllText(backupPath));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void RestorePreservesCorruptDatabaseAndWalBesideTheRepairedDatabase()
    {
        var root = NewRoot();
        var sourcePath = Path.Combine(root, "source.db");
        var backupPath = Path.Combine(root, "backup.db");
        var destinationPath = Path.Combine(root, "destination.db");
        try
        {
            using (var source = new ControllerDatabase(sourcePath)) AddMachine(source, "restored-machine");
            ControllerDatabaseMaintenance.Backup(sourcePath, backupPath);
            File.WriteAllText(destinationPath, "corrupt database contents");
            File.WriteAllText(destinationPath + "-wal", "uncheckpointed bytes");

            var recoveryPath = ControllerDatabaseMaintenance.Restore(backupPath, destinationPath);

            Assert.NotNull(recoveryPath);
            Assert.Equal("corrupt database contents", File.ReadAllText(recoveryPath!));
            Assert.Equal("uncheckpointed bytes", File.ReadAllText(recoveryPath + "-wal"));
            Assert.False(File.Exists(destinationPath + "-wal"));
            using var restored = new ControllerDatabase(destinationPath);
            Assert.Equal("restored-machine", Assert.Single(restored.List()).MachineId);
        }
        finally { DeleteRoot(root); }
    }

    private static void AddMachine(ControllerDatabase database, string machineId)
    {
        using var key = ECDsa.Create();
        database.EnrollWithTicket(new EnrollmentRequest(machineId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "192.0.2.1", "001122334455", 5000, 47989));
    }

    private static void ReplaceHeartbeatTable(string databasePath, string createStatement)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE machine_heartbeats; " + createStatement;
        command.ExecuteNonQuery();
    }

    private static void CreateLegacyVersionTwoDatabase(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var migration in new[] { "001-machines.sql", "002-leases-commands.sql" })
        {
            using var resource = typeof(ControllerDatabase).Assembly.GetManifestResourceStream($"KamenRaider.Controller.Data.Migrations.{migration}")!;
            using var reader = new StreamReader(resource);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
        }
        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "PRAGMA user_version=2";
            version.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void ReplaceLegacyCommandRevisionConstraintWithIndex(string databasePath, string indexTerms)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            DROP TABLE command_outbox;
            CREATE TABLE command_outbox (
                command_id TEXT PRIMARY KEY, type TEXT NOT NULL CHECK(type IN ('PrepareMachine','ApplyLease','PairPin')),
                rental_id TEXT NOT NULL REFERENCES leases(id), machine_id TEXT NOT NULL REFERENCES machines(machine_id),
                lease_revision INTEGER NOT NULL, boot_id TEXT NOT NULL, valid_until INTEGER NOT NULL, payload TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','applied','failed','expired','invalidated')),
                error_code TEXT
            );
            CREATE UNIQUE INDEX altered_command_revision ON command_outbox({indexTerms});
            PRAGMA user_version=2;
            """;
        command.ExecuteNonQuery();
    }

    private static string NewRoot()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "kr-controller-db-maintenance-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteRoot(string path)
    {
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(path);
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(resolved).StartsWith("kr-controller-db-maintenance-", StringComparison.OrdinalIgnoreCase) is false)
            throw new InvalidOperationException("Refusing to remove a path outside this test's temporary directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
