using System.Security.Cryptography;
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
