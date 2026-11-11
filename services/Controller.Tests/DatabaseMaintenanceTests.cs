using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using KamenRaider.Contracts;
using KamenRaider.Controller.Data;

namespace Controller.Tests;

public class DatabaseMaintenanceTests
{
    [Fact]
    public void PreRestoreRecoverySnapshotRetainsItsOwnArtifactsAndCanBeRestoredOnACleanHost()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "live", "controller.db");
            var backup = Path.Combine(root, "backup.db");
            var destination = Path.Combine(root, "destination", "controller.db");
            var recovered = Path.Combine(root, "recovered", "controller.db");
            var incoming = AddReleaseArtifact(source, "2.0.0", "incoming release");
            var previous = AddReleaseArtifact(destination, "3.0.0", "previous protected release");
            ControllerDatabaseMaintenance.Backup(source, backup);

            var recovery = ControllerDatabaseMaintenance.Restore(backup, destination);
            Assert.NotNull(recovery);
            ControllerDatabaseMaintenance.Restore(recovery!, recovered);

            Assert.Equal("previous protected release", File.ReadAllText(ArtifactPath(recovered, previous)));
            Assert.Equal("incoming release", File.ReadAllText(ArtifactPath(destination, incoming)));
            Assert.Equal("previous protected release", File.ReadAllText(ArtifactPath(destination, previous)));
            using var database = new ControllerDatabase(recovered);
            Assert.Equal("3.0.0", Assert.Single(database.ListAgentReleases()).Version);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void RestoreRejectsRedirectedArtifactDirectoriesWithoutWritingIntoTheTarget()
    {
        var root = NewRoot();
        var link = Path.Combine(root, "destination", "agent-releases");
        try
        {
            var source = Path.Combine(root, "live", "controller.db");
            var backup = Path.Combine(root, "backup.db");
            var destination = Path.Combine(root, "destination", "controller.db");
            AddReleaseArtifact(source, "2.0.0", "release");
            ControllerDatabaseMaintenance.Backup(source, backup);
            using (var database = new ControllerDatabase(destination)) AddMachine(database, "preserved-machine");
            var protectedDirectory = Path.Combine(root, "protected");
            Directory.CreateDirectory(protectedDirectory);
            File.WriteAllText(Path.Combine(protectedDirectory, "keep.txt"), "protected");
            using (var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c mklink /J \"{link}\" \"{protectedDirectory}\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            })!) { junction.WaitForExit(); Assert.Equal(0, junction.ExitCode); }
            var original = File.ReadAllBytes(destination);

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Restore(backup, destination));

            Assert.Equal(original, File.ReadAllBytes(destination));
            Assert.Equal("keep.txt", Assert.Single(Directory.GetFileSystemEntries(protectedDirectory)).Split(Path.DirectorySeparatorChar).Last());
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); DeleteRoot(root); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("conflict")]
    [InlineData("directory")]
    public void RestoreRejectsArtifactFailuresBeforeChangingDatabaseOrOtherArtifacts(string failure)
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "live", "controller.db");
            var backup = Path.Combine(root, "backup", "controller.db");
            var destination = Path.Combine(root, "destination", "controller.db");
            var first = AddReleaseArtifact(source, "2.0.0", "first release bytes");
            var second = AddReleaseArtifact(source, "3.0.0", "second release bytes");
            ControllerDatabaseMaintenance.Backup(source, backup);
            using (var database = new ControllerDatabase(destination)) AddMachine(database, "preserved-machine");
            var original = File.ReadAllBytes(destination);
            var sidecar = Path.Combine(backup + ".agent-releases", "sha256", second[..2].ToLowerInvariant(), second.ToLowerInvariant() + ".zip");
            var conflict = ArtifactPath(destination, second);
            if (failure == "missing") File.Delete(sidecar);
            if (failure == "corrupt") File.WriteAllText(sidecar, "corrupt");
            if (failure == "conflict") { Directory.CreateDirectory(Path.GetDirectoryName(conflict)!); File.WriteAllText(conflict, "protected existing bytes"); }
            if (failure == "directory") Directory.CreateDirectory(conflict);

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Restore(backup, destination));

            Assert.Equal(original, File.ReadAllBytes(destination));
            Assert.False(File.Exists(ArtifactPath(destination, first)));
            Assert.False(Directory.Exists(Path.GetDirectoryName(ArtifactPath(destination, first))));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.pre-restore-*"));
            if (failure == "conflict") Assert.Equal("protected existing bytes", File.ReadAllText(conflict));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void LegacyDatabaseOnlyBackupWithReleasesRequiresAnExplicitVerifiedArtifactSidecar()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "live", "controller.db");
            var legacy = Path.Combine(root, "legacy.db");
            var destination = Path.Combine(root, "destination", "controller.db");
            var hash = AddReleaseArtifact(source, "2.0.0", "legacy immutable release");
            File.Copy(source, legacy);
            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Restore(legacy, destination));
            Assert.False(File.Exists(destination));
            var sidecar = Path.Combine(legacy + ".agent-releases", "sha256", hash[..2].ToLowerInvariant(), hash.ToLowerInvariant() + ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
            File.Copy(ArtifactPath(source, hash), sidecar);

            ControllerDatabaseMaintenance.Restore(legacy, destination);

            Assert.Equal(File.ReadAllBytes(ArtifactPath(source, hash)), File.ReadAllBytes(ArtifactPath(destination, hash)));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void ExistingBackupArtifactDirectoryIsNeverRemovedOrOverwritten()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "live", "controller.db");
            var backup = Path.Combine(root, "backup.db");
            var hash = AddReleaseArtifact(source, "2.0.0", "release bytes");
            var protectedPath = Path.Combine(backup + ".agent-releases", "sha256", hash[..2].ToLowerInvariant(), hash.ToLowerInvariant() + ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(protectedPath)!);
            File.WriteAllText(protectedPath, "protected backup");

            Assert.Throws<IOException>(() => ControllerDatabaseMaintenance.Backup(source, backup));

            Assert.Equal("protected backup", File.ReadAllText(protectedPath));
            Assert.False(File.Exists(backup));
        }
        finally { DeleteRoot(root); }
    }

    private static string AddReleaseArtifact(string databasePath, string version, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var artifact = ArtifactPath(databasePath, hash);
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllBytes(artifact, bytes);
        using var database = new ControllerDatabase(databasePath);
        database.PublishAgentRelease(new(version, "stable", "release", hash, artifact, bytes.Length, "0123456789ab", false, true));
        return hash;
    }

    [Fact]
    public void BackupAndCleanHostRestoreIncludeVerifiedReleaseArtifacts()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "live", "controller.db");
            var backup = Path.Combine(root, "backup", "controller.db");
            var restored = Path.Combine(root, "restored", "controller.db");
            var bytes = System.Text.Encoding.UTF8.GetBytes("immutable release bytes");
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var sourceArtifact = ArtifactPath(source, hash);
            Directory.CreateDirectory(Path.GetDirectoryName(sourceArtifact)!);
            File.WriteAllBytes(sourceArtifact, bytes);
            using (var database = new ControllerDatabase(source))
                database.PublishAgentRelease(new("2.0.0", "stable", "release", hash, sourceArtifact, bytes.Length, "0123456789ab", false, true));

            ControllerDatabaseMaintenance.Backup(source, backup);
            ControllerDatabaseMaintenance.Restore(backup, restored);

            Assert.Equal(bytes, File.ReadAllBytes(ArtifactPath(restored, hash)));
            using var databaseAfterRestore = new ControllerDatabase(restored);
            Assert.Equal(hash, Assert.Single(databaseAfterRestore.ListAgentReleases()).PackageSha256);
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackupRejectsMissingOrCorruptReferencedReleaseWithoutPublishing(bool corrupt)
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "live", "controller.db");
            var backup = Path.Combine(root, "backup", "controller.db");
            var bytes = System.Text.Encoding.UTF8.GetBytes("immutable release bytes");
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var artifact = ArtifactPath(source, hash);
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            if (corrupt) File.WriteAllText(artifact, "corrupt");
            using (var database = new ControllerDatabase(source))
                database.PublishAgentRelease(new("2.0.0", "stable", "release", hash, artifact, bytes.Length, "0123456789ab", false, true));

            Assert.ThrowsAny<Exception>(() => ControllerDatabaseMaintenance.Backup(source, backup));
            Assert.False(File.Exists(backup));
        }
        finally { DeleteRoot(root); }
    }

    private static string ArtifactPath(string database, string hash) => Path.Combine(Path.GetDirectoryName(database)!, "agent-releases", "sha256", hash[..2].ToLowerInvariant(), hash.ToLowerInvariant() + ".zip");
    [Fact]
    public void BackupRejectsVersionTwelveDatabaseMissingAgentUpdateOperations()
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
                command.CommandText = "DROP TABLE agent_update_operations;";
                command.ExecuteNonQuery();
            }

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Backup(databasePath, backupPath));
            Assert.False(File.Exists(backupPath));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void BackupRejectsDatabaseWithoutHeldChainUniqueness()
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
                command.CommandText = "DROP INDEX agent_maintenance_one_held_per_machine; CREATE UNIQUE INDEX agent_maintenance_one_held_per_machine ON agent_maintenance_chains(machine_id) WHERE held=0;";
                command.ExecuteNonQuery();
            }

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Backup(databasePath, backupPath));
            Assert.False(File.Exists(backupPath));
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    public void LegacyReleaseDatabasesCanBeBackedUpAndMigrated(int version)
    {
        var root = NewRoot();
        var source = Path.Combine(root, "legacy.db");
        var backup = Path.Combine(root, "backup.db");
        try
        {
            CreateLegacyReleaseDatabase(source, version);
            ControllerDatabaseMaintenance.Backup(source, backup);
            using var upgraded = new ControllerDatabase(backup);
            Assert.Equal("legacy-pc", Assert.Single(upgraded.List()).MachineId);
            ControllerDatabaseMaintenance.Backup(backup, Path.Combine(root, "upgraded-backup.db"));
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    public void LegacyReleaseAndOperationSurviveBackupRestoreAndMigration(int version)
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "legacy", "controller.db");
            var backup = Path.Combine(root, "backup", "controller.db");
            var destination = Path.Combine(root, "restored", "controller.db");
            var hash = CreateLegacyReleaseWithOperation(source, version, "failed", false);
            ControllerDatabaseMaintenance.Backup(source, backup);
            ControllerDatabaseMaintenance.Restore(backup, destination);
            using (var database = new ControllerDatabase(destination))
            {
                var machine = Assert.Single(database.List());
                Assert.Equal("legacy-op", machine.AgentUpdateOperationId);
                Assert.Equal("failed", machine.AgentUpdateStatus);
                Assert.Equal("approved", machine.ApprovalStatus);
                Assert.Equal("ready", machine.Readiness);
                Assert.False(machine.CanRent);
                Assert.Throws<InvalidOperationException>(() => database.CreateTrialLease("user", "legacy-pc", 60, "rent"));
            }
            Assert.Equal(File.ReadAllBytes(ArtifactPath(source, hash)), File.ReadAllBytes(ArtifactPath(destination, hash)));
            using var connection = OpenDatabase(destination);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT o.operation_id,o.target_version,o.target_package_sha256,o.previous_version,o.ticket_hash,o.ticket_expires_at_unix_ms,o.ticket_started_at_unix_ms,o.started_at_unix_ms,o.created_at_unix_ms,o.updated_at_unix_ms,o.ticket_package_downloaded_at_unix_ms,o.ticket_generation,o.ticket_purpose,c.held,c.inherited,c.current_head_operation_id,r.package_path FROM agent_update_operations o JOIN agent_maintenance_chains c ON c.chain_id=o.chain_id JOIN agent_releases r ON r.version=o.release_version";
            using var row = command.ExecuteReader();
            Assert.True(row.Read());
            Assert.Equal("legacy-op", row.GetString(0));
            Assert.Equal("2.3.4", row.GetString(1));
            Assert.Equal(hash, row.GetString(2));
            Assert.Equal("1.2.3", row.GetString(3));
            Assert.Equal("legacy-ticket", row.GetString(4));
            Assert.Equal(900, row.GetInt64(5));
            Assert.Equal(20, row.GetInt64(6));
            Assert.Equal(30, row.GetInt64(7));
            Assert.Equal(10, row.GetInt64(8));
            Assert.Equal(40, row.GetInt64(9));
            if (version == 13) Assert.Equal(35, row.GetInt64(10)); else Assert.True(row.IsDBNull(10));
            Assert.Equal(0, row.GetInt64(11));
            Assert.Equal("legacy", row.GetString(12));
            Assert.Equal(1, row.GetInt64(13));
            Assert.Equal(1, row.GetInt64(14));
            Assert.Equal("legacy-op", row.GetString(15));
            Assert.Equal(ArtifactPath(destination, hash), row.GetString(16));
            Assert.False(row.Read());
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void InheritedHeldChainSurvivesVersionFourteenBackupAndRestore()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source", "controller.db");
            var backup = Path.Combine(root, "backup", "controller.db");
            var destination = Path.Combine(root, "restored", "controller.db");
            var hash = CreateLegacyReleaseWithOperation(source, 13, "failed", false);
            using (new ControllerDatabase(source)) { }
            ControllerDatabaseMaintenance.Backup(source, backup);
            ControllerDatabaseMaintenance.Restore(backup, destination);
            using var restored = new ControllerDatabase(destination);
            var machine = Assert.Single(restored.List());
            Assert.Equal("legacy-op", machine.AgentUpdateOperationId);
            Assert.Equal("failed", machine.AgentUpdateStatus);
            Assert.Equal("approved", machine.ApprovalStatus);
            Assert.Equal("ready", machine.Readiness);
            Assert.False(machine.CanRent);
            Assert.Throws<InvalidOperationException>(() => restored.CreateTrialLease("user", "legacy-pc", 60, "rent"));
            Assert.Equal(File.ReadAllBytes(ArtifactPath(source, hash)), File.ReadAllBytes(ArtifactPath(destination, hash)));
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData("awaitingInstaller", false, false)]
    [InlineData("installing", true, false)]
    [InlineData("awaitingHeartbeat", true, false)]
    [InlineData("rollingBack", true, false)]
    [InlineData("failed", true, false)]
    [InlineData("expired", true, false)]
    [InlineData("expired", true, true)]
    public void RestoreRejectsClosedChainWithNonterminalOrStartedExpiredHead(string status, bool started, bool held)
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source", "controller.db");
            var destination = Path.Combine(root, "destination", "controller.db");
            var hash = AddReleaseArtifact(source, "2.3.4", "legacy release bytes");
            using (var database = new ControllerDatabase(source)) AddMachine(database, "source-machine");
            using (var connection = OpenDatabase(source))
            using (var tx = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "INSERT INTO agent_maintenance_chains VALUES('chain','source-machine','operation',1,$held,1,1,1); INSERT INTO agent_update_operations(operation_id,machine_id,chain_id,release_version,target_version,target_package_sha256,status,ticket_hash,ticket_generation,ticket_purpose,ticket_expires_at_unix_ms,started_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) VALUES('operation','source-machine','chain','2.3.4','2.3.4',$hash,$status,'ticket',0,'legacy',1,$started,1,1)";
                command.Parameters.AddWithValue("$hash", hash);
                command.Parameters.AddWithValue("$status", status);
                command.Parameters.AddWithValue("$held", held ? 1 : 0);
                command.Parameters.AddWithValue("$started", started ? 1 : DBNull.Value);
                command.ExecuteNonQuery();
                tx.Commit();
            }
            var startError = Record.Exception(() => { using var database = new ControllerDatabase(source); });
            Assert.IsType<InvalidDataException>(startError);
            CopyArtifactToSidecar(source, hash);
            AssertSnapshotRejectedWithoutReplacement(source, destination, Path.Combine(root, "bad-backup.db"));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void RestoreRejectsExpiredHeadWhenEarlierChainWorkStarted()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source", "controller.db");
            var destination = Path.Combine(root, "destination", "controller.db");
            var hash = AddReleaseArtifact(source, "2.3.4", "legacy release bytes");
            using (var database = new ControllerDatabase(source)) AddMachine(database, "source-machine");
            using (var connection = OpenDatabase(source))
            using (var tx = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "INSERT INTO agent_maintenance_chains VALUES('chain','source-machine','head',2,0,0,1,1); INSERT INTO agent_update_operations(operation_id,machine_id,chain_id,release_version,target_version,target_package_sha256,status,ticket_hash,ticket_generation,ticket_purpose,ticket_expires_at_unix_ms,started_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) VALUES('previous','source-machine','chain','2.3.4','2.3.4',$hash,'completed','previous-ticket',1,'apply',1,1,1,1); INSERT INTO agent_update_operations(operation_id,machine_id,chain_id,parent_operation_id,release_version,target_version,target_package_sha256,status,ticket_hash,ticket_generation,ticket_purpose,ticket_expires_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) VALUES('head','source-machine','chain','previous','2.3.4','2.3.4',$hash,'expired','head-ticket',2,'apply',1,1,1)";
                command.Parameters.AddWithValue("$hash", hash);
                command.ExecuteNonQuery();
                tx.Commit();
            }
            CopyArtifactToSidecar(source, hash);
            AssertSnapshotRejectedWithoutReplacement(source, destination, Path.Combine(root, "bad-backup.db"));
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData(12, "awaitingInstaller")]
    [InlineData(12, "installing")]
    [InlineData(12, "awaitingHeartbeat")]
    [InlineData(12, "rollingBack")]
    [InlineData(12, "failed")]
    [InlineData(13, "awaitingInstaller")]
    [InlineData(13, "installing")]
    [InlineData(13, "awaitingHeartbeat")]
    [InlineData(13, "rollingBack")]
    [InlineData(13, "failed")]
    public void LegacyLeaseAndNonterminalUpdateConflictIsRejectedBeforeRestore(int version, string status)
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "legacy", "controller.db");
            var destination = Path.Combine(root, "destination", "controller.db");
            var hash = CreateLegacyReleaseWithOperation(source, version, status, true);
            CopyArtifactToSidecar(source, hash);
            AssertSnapshotRejectedWithoutReplacement(source, destination, Path.Combine(root, "bad-backup.db"));
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData(12, "completed")]
    [InlineData(12, "rolledBack")]
    [InlineData(12, "expired")]
    [InlineData(13, "completed")]
    [InlineData(13, "rolledBack")]
    [InlineData(13, "expired")]
    public void LegacyTerminalUpdateMayCoexistWithHeldLease(int version, string status)
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source", "controller.db");
            var backup = Path.Combine(root, "backup", "controller.db");
            var destination = Path.Combine(root, "destination", "controller.db");
            var hash = CreateLegacyReleaseWithOperation(source, version, status, true);
            ControllerDatabaseMaintenance.Backup(source, backup);
            ControllerDatabaseMaintenance.Restore(backup, destination);
            using var database = new ControllerDatabase(destination);
            var machine = Assert.Single(database.List());
            Assert.Equal(status, machine.AgentUpdateStatus);
            Assert.True(machine.AllocationHeld);
            Assert.False(machine.CanRent);
            Assert.Equal(File.ReadAllBytes(ArtifactPath(source, hash)), File.ReadAllBytes(ArtifactPath(destination, hash)));
            if (status == "expired")
            {
                using var connection = OpenDatabase(destination);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT started_at_unix_ms FROM agent_update_operations WHERE operation_id='legacy-op'";
                Assert.Equal(DBNull.Value, command.ExecuteScalar());
            }
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    public void LegacyExpiredStartedUpdateIsRejectedBeforeBackupRestoreOrStartup(int version)
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "legacy", "controller.db");
            var destination = Path.Combine(root, "destination", "controller.db");
            var hash = CreateLegacyReleaseWithOperation(source, version, "expired", false);
            using (var connection = OpenDatabase(source))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE agent_update_operations SET started_at_unix_ms=30 WHERE operation_id='legacy-op'";
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            CopyArtifactToSidecar(source, hash);
            AssertSnapshotRejectedWithoutReplacement(source, destination, Path.Combine(root, "bad-backup.db"));

            var error = Record.Exception(() => { using var database = new ControllerDatabase(source); });
            Assert.IsType<InvalidDataException>(error);
            using var verify = OpenDatabase(source);
            using var query = verify.CreateCommand();
            query.CommandText = "PRAGMA user_version";
            Assert.Equal(version, Convert.ToInt32(query.ExecuteScalar()));
            query.CommandText = "SELECT started_at_unix_ms FROM agent_update_operations WHERE operation_id='legacy-op'";
            Assert.Equal(30L, query.ExecuteScalar());
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData(12, "ticket")]
    [InlineData(13, "ticket")]
    [InlineData(13, "download")]
    public void LegacyExpiredUpdateWithTicketStartEvidenceIsRejected(int version, string evidence)
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "legacy", "controller.db");
            var destination = Path.Combine(root, "destination", "controller.db");
            var hash = CreateLegacyReleaseWithOperation(source, version, "expired", false);
            using (var connection = OpenDatabase(source))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = evidence == "ticket"
                    ? "UPDATE agent_update_operations SET ticket_started_at_unix_ms=30 WHERE operation_id='legacy-op'"
                    : "UPDATE agent_update_operations SET ticket_package_downloaded_at_unix_ms=35 WHERE operation_id='legacy-op'";
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            CopyArtifactToSidecar(source, hash);
            AssertSnapshotRejectedWithoutReplacement(source, destination, Path.Combine(root, "bad-backup.db"));
            var error = Record.Exception(() => { using var database = new ControllerDatabase(source); });
            Assert.IsType<InvalidDataException>(error);
            using var verify = OpenDatabase(source);
            using var query = verify.CreateCommand();
            query.CommandText = "PRAGMA user_version";
            Assert.Equal(version, Convert.ToInt32(query.ExecuteScalar()));
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData("ticket")]
    [InlineData("download")]
    public void VersionFourteenExpiredChainWithTicketStartEvidenceIsRejected(string evidence)
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source", "controller.db");
            var destination = Path.Combine(root, "destination", "controller.db");
            var hash = AddReleaseArtifact(source, "2.3.4", "legacy release bytes");
            using (var database = new ControllerDatabase(source)) AddMachine(database, "source-machine");
            using (var connection = OpenDatabase(source))
            using (var tx = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "INSERT INTO agent_maintenance_chains VALUES('chain','source-machine','operation',1,0,1,1,1); INSERT INTO agent_update_operations(operation_id,machine_id,chain_id,release_version,target_version,target_package_sha256,status,ticket_hash,ticket_generation,ticket_purpose,ticket_expires_at_unix_ms,ticket_started_at_unix_ms,ticket_package_downloaded_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) VALUES('operation','source-machine','chain','2.3.4','2.3.4',$hash,'expired','ticket',0,'legacy',1,$ticketStarted,$downloaded,1,1)";
                command.Parameters.AddWithValue("$hash", hash);
                command.Parameters.AddWithValue("$ticketStarted", evidence == "ticket" ? 30 : DBNull.Value);
                command.Parameters.AddWithValue("$downloaded", evidence == "download" ? 35 : DBNull.Value);
                command.ExecuteNonQuery();
                tx.Commit();
            }
            CopyArtifactToSidecar(source, hash);
            AssertSnapshotRejectedWithoutReplacement(source, destination, Path.Combine(root, "bad-backup.db"));
            var error = Record.Exception(() => { using var database = new ControllerDatabase(source); });
            Assert.IsType<InvalidDataException>(error);
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData("ticket", false)]
    [InlineData("download", false)]
    [InlineData("ticket", true)]
    [InlineData("download", true)]
    public void AwaitingInstallerWithTicketStartEvidenceNeverExpiresItsHold(string evidence, bool inherited)
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source", "controller.db");
            var hash = AddReleaseArtifact(source, "2.3.4", "legacy release bytes");
            using (var database = new ControllerDatabase(source)) AddMachine(database, "source-machine");
            using (var connection = OpenDatabase(source))
            using (var tx = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "UPDATE machines SET approval_status='approved' WHERE machine_id='source-machine'; INSERT INTO machine_heartbeats(machine_id,last_seen,boot_id,readiness) VALUES('source-machine',$now,'boot','ready'); INSERT INTO machine_config_acks(machine_id,applied_revision) VALUES('source-machine',1); INSERT INTO agent_maintenance_chains VALUES('chain','source-machine','operation',1,1,$inherited,1,1); INSERT INTO agent_update_operations(operation_id,machine_id,chain_id,release_version,target_version,target_package_sha256,status,ticket_hash,ticket_generation,ticket_purpose,ticket_expires_at_unix_ms,ticket_started_at_unix_ms,ticket_package_downloaded_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) VALUES('operation','source-machine','chain','2.3.4','2.3.4',$hash,'awaitingInstaller','ticket',1,'apply',1,$ticketStarted,$downloaded,1,1)";
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$hash", hash);
                command.Parameters.AddWithValue("$inherited", inherited ? 1 : 0);
                command.Parameters.AddWithValue("$ticketStarted", evidence == "ticket" ? 30 : DBNull.Value);
                command.Parameters.AddWithValue("$downloaded", evidence == "download" ? 35 : DBNull.Value);
                command.ExecuteNonQuery();
                tx.Commit();
            }
            using var restored = new ControllerDatabase(source);
            var machine = Assert.Single(restored.List());
            Assert.Equal("ready", machine.Readiness);
            Assert.Equal("awaitingInstaller", machine.AgentUpdateStatus);
            Assert.False(machine.CanRent);
            Assert.Throws<InvalidOperationException>(() => restored.CreateTrialLease("user", "source-machine", 60, "rent"));
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData("started")]
    [InlineData("download")]
    public void AwaitingInstallerWithPriorStartEvidenceCannotStartAgain(string evidence)
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source", "controller.db");
            var hash = AddReleaseArtifact(source, "2.3.4", "legacy release bytes");
            using (var database = new ControllerDatabase(source)) AddMachine(database, "source-machine");
            var ticket = new string('A', 43);
            var ticketHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(ticket)));
            using (var connection = OpenDatabase(source))
            using (var tx = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "INSERT INTO agent_maintenance_chains VALUES('chain','source-machine','operation',1,1,0,1,1); INSERT INTO agent_update_operations(operation_id,machine_id,chain_id,release_version,target_version,target_package_sha256,status,ticket_hash,ticket_generation,ticket_purpose,ticket_expires_at_unix_ms,started_at_unix_ms,ticket_package_downloaded_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) VALUES('operation','source-machine','chain','2.3.4','2.3.4',$hash,'awaitingInstaller',$ticket,1,'apply',9999999999999,$started,$downloaded,1,1)";
                command.Parameters.AddWithValue("$hash", hash);
                command.Parameters.AddWithValue("$ticket", ticketHash);
                command.Parameters.AddWithValue("$started", evidence == "started" ? 30 : DBNull.Value);
                command.Parameters.AddWithValue("$downloaded", evidence == "download" ? 35 : DBNull.Value);
                command.ExecuteNonQuery();
                tx.Commit();
            }
            using var reopened = new ControllerDatabase(source);
            Assert.Throws<UnauthorizedAccessException>(() => reopened.StartAgentUpdate("source-machine", ticket));
            Assert.False(Assert.Single(reopened.List()).CanRent);
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    public void ConflictingLegacyDatabaseCannotStartOrPartiallyMigrate(int version)
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "legacy", "controller.db");
            CreateLegacyReleaseWithOperation(path, version, "failed", true);
            var error = Record.Exception(() => { using var database = new ControllerDatabase(path); });
            Assert.IsType<InvalidDataException>(error);
            using var connection = OpenDatabase(path);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            Assert.Equal(version, Convert.ToInt32(command.ExecuteScalar()));
            command.CommandText = "SELECT COUNT(*) FROM leases WHERE allocation_held=1";
            Assert.Equal(1L, command.ExecuteScalar());
            command.CommandText = "SELECT status FROM agent_update_operations WHERE operation_id='legacy-op'";
            Assert.Equal("failed", command.ExecuteScalar());
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData("nonhead")]
    [InlineData("future-generation")]
    public void RestoreRejectsContradictoryHeldChainRelationships(string mutation)
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source", "controller.db");
            var destination = Path.Combine(root, "destination", "controller.db");
            var hash = AddReleaseArtifact(source, "2.3.4", "legacy release bytes");
            using (var database = new ControllerDatabase(source)) AddMachine(database, "source-machine");
            using (var connection = OpenDatabase(source))
            using (var tx = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "INSERT INTO agent_maintenance_chains VALUES('chain','source-machine','head',1,1,0,1,1); INSERT INTO agent_update_operations(operation_id,machine_id,chain_id,release_version,target_version,target_package_sha256,status,ticket_hash,ticket_generation,ticket_purpose,ticket_expires_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) VALUES('head','source-machine','chain','2.3.4','2.3.4',$hash,'awaitingInstaller','head-ticket',$generation,'apply',9999999999999,1,1)";
                command.Parameters.AddWithValue("$hash", hash);
                command.Parameters.AddWithValue("$generation", mutation == "future-generation" ? 2 : 1);
                command.ExecuteNonQuery();
                if (mutation == "nonhead")
                {
                    command.CommandText = "INSERT INTO agent_update_operations(operation_id,machine_id,chain_id,release_version,target_version,target_package_sha256,status,ticket_hash,ticket_generation,ticket_purpose,ticket_expires_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) VALUES('orphan-active','source-machine','chain','2.3.4','2.3.4',$hash,'failed','other-ticket',1,'apply',1,1,1)";
                    command.ExecuteNonQuery();
                }
                tx.Commit();
            }
            CopyArtifactToSidecar(source, hash);
            AssertSnapshotRejectedWithoutReplacement(source, destination, Path.Combine(root, "bad-backup.db"));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void RevokedOlderGenerationTicketDoesNotInvalidateHeldChainSnapshot()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source", "controller.db");
            var backup = Path.Combine(root, "backup", "controller.db");
            var destination = Path.Combine(root, "destination", "controller.db");
            var hash = AddReleaseArtifact(source, "2.3.4", "legacy release bytes");
            using (var database = new ControllerDatabase(source)) AddMachine(database, "source-machine");
            using (var connection = OpenDatabase(source))
            using (var tx = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "INSERT INTO agent_maintenance_chains VALUES('chain','source-machine','head',2,1,0,1,1); INSERT INTO agent_update_operations(operation_id,machine_id,chain_id,release_version,target_version,target_package_sha256,status,ticket_hash,ticket_generation,ticket_purpose,ticket_expires_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) VALUES('head','source-machine','chain','2.3.4','2.3.4',$hash,'awaitingInstaller','old-ticket',1,'apply',9999999999999,1,1)";
                command.Parameters.AddWithValue("$hash", hash);
                command.ExecuteNonQuery();
                tx.Commit();
            }
            ControllerDatabaseMaintenance.Backup(source, backup);
            ControllerDatabaseMaintenance.Restore(backup, destination);
            using var restored = new ControllerDatabase(destination);
            var machine = Assert.Single(restored.List());
            Assert.Equal("head", machine.AgentUpdateOperationId);
            Assert.False(machine.CanRent);
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData("status")]
    [InlineData("glob")]
    public void SchemaComparisonRejectsAlteredSqlLiteralsInBackupAndRestore(string mutation)
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source", "controller.db");
            var destination = Path.Combine(root, "destination", "controller.db");
            using (var database = new ControllerDatabase(source)) AddMachine(database, "source-machine");
            using (var connection = OpenDatabase(source))
            using (var command = connection.CreateCommand())
            {
                if (mutation == "status")
                {
                    command.CommandText = "SELECT sql FROM sqlite_schema WHERE name='agent_update_superseded_insert_guard'";
                    var definition = (string)command.ExecuteScalar()!;
                    Assert.Contains("'superseded'", definition);
                    command.CommandText = "DROP TRIGGER agent_update_superseded_insert_guard";
                    command.ExecuteNonQuery();
                    command.CommandText = definition.Replace("'superseded'", "'SUPERSEDED'", StringComparison.Ordinal);
                    command.ExecuteNonQuery();
                }
                else
                {
                    command.CommandText = "SELECT sql FROM sqlite_schema WHERE name='agent_update_operations'";
                    var definition = (string)command.ExecuteScalar()!;
                    Assert.Contains("*[^0-9a-fA-F]*", definition);
                    command.CommandText = "PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql=$sql WHERE type='table' AND name='agent_update_operations'; PRAGMA writable_schema=OFF";
                    command.Parameters.AddWithValue("$sql", definition.Replace("*[^0-9a-fA-F]*", "*^0-9a-fA-F*", StringComparison.Ordinal));
                    command.ExecuteNonQuery();
                }
            }
            var startupError = Record.Exception(() => { using var database = new ControllerDatabase(source); });
            Assert.IsType<InvalidDataException>(startupError);
            AssertSnapshotRejectedWithoutReplacement(source, destination, Path.Combine(root, "bad-backup.db"));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void BackupRejectsMissingMaintenanceCloseGuard()
    {
        var root = NewRoot();
        var source = Path.Combine(root, "source.db");
        var backup = Path.Combine(root, "backup.db");
        try
        {
            using (var database = new ControllerDatabase(source)) AddMachine(database, "source-machine");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DROP TRIGGER agent_maintenance_close_guard";
                command.ExecuteNonQuery();
            }
            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Backup(source, backup));
            Assert.False(File.Exists(backup));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void BackupRejectsVersionThirteenDatabaseMissingDurableUpdateDownloadState()
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
                command.CommandText = "ALTER TABLE agent_update_operations DROP COLUMN ticket_package_downloaded_at_unix_ms;";
                command.ExecuteNonQuery();
            }

            Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Backup(databasePath, backupPath));
            Assert.False(File.Exists(backupPath));
        }
        finally { DeleteRoot(root); }
    }

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

    private static SqliteConnection OpenDatabase(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static string CreateLegacyReleaseWithOperation(string path, int version, string status, bool withHeldLease)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        CreateLegacyReleaseDatabase(path, version);
        var bytes = System.Text.Encoding.UTF8.GetBytes("legacy retained artifact");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var artifact = ArtifactPath(path, hash);
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllBytes(artifact, bytes);
        using var connection = OpenDatabase(path);
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE machines SET approval_status='approved' WHERE machine_id='legacy-pc'; INSERT INTO machine_heartbeats(machine_id,last_seen,boot_id,readiness,agent_version) VALUES('legacy-pc',$now,'boot','ready','1.2.3'); INSERT INTO machine_config_acks(machine_id,applied_revision) VALUES('legacy-pc',1); INSERT INTO agent_releases(version,channel,release_notes,package_sha256,package_path,package_size_bytes,source_commit,source_dirty,release_build,package_format,published_at_unix_ms) VALUES('2.3.4','stable','notes',$hash,$path,$size,'commit',0,1,'AgentPackage.zip',1); INSERT INTO agent_update_operations(operation_id,machine_id,release_version,target_version,target_package_sha256,previous_version,status,ticket_hash,ticket_expires_at_unix_ms,ticket_started_at_unix_ms,started_at_unix_ms,created_at_unix_ms,updated_at_unix_ms) VALUES('legacy-op','legacy-pc','2.3.4','2.3.4',$hash,'1.2.3',$status,'legacy-ticket',900,$ticketStarted,$started,10,40)";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$path", artifact);
        command.Parameters.AddWithValue("$size", bytes.Length);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$ticketStarted", status is "awaitingInstaller" or "expired" ? DBNull.Value : 20);
        command.Parameters.AddWithValue("$started", status is "awaitingInstaller" or "expired" ? DBNull.Value : 30);
        command.ExecuteNonQuery();
        if (version == 13 && status is not ("awaitingInstaller" or "expired"))
        {
            command.CommandText = "UPDATE agent_update_operations SET ticket_package_downloaded_at_unix_ms=35 WHERE operation_id='legacy-op'";
            command.ExecuteNonQuery();
        }
        if (withHeldLease)
        {
            command.CommandText = "INSERT INTO leases(id,user_id,machine_id,state,duration_seconds,preparation_deadline,allocation_held) VALUES('held-lease','user','legacy-pc','preparing',60,999999,1)";
            command.ExecuteNonQuery();
        }
        tx.Commit();
        return hash;
    }

    private static void CopyArtifactToSidecar(string snapshot, string hash)
    {
        var sidecar = Path.Combine(snapshot + ".agent-releases", "sha256", hash[..2].ToLowerInvariant(), hash.ToLowerInvariant() + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
        File.Copy(ArtifactPath(snapshot, hash), sidecar);
    }

    private static void AssertSnapshotRejectedWithoutReplacement(string source, string destination, string backup)
    {
        using (var database = new ControllerDatabase(destination)) AddMachine(database, "preserved-machine");
        var original = File.ReadAllBytes(destination);
        var backupError = Record.Exception(() => ControllerDatabaseMaintenance.Backup(source, backup));
        var restoreError = Record.Exception(() => ControllerDatabaseMaintenance.Restore(source, destination));
        Assert.IsType<InvalidDataException>(backupError);
        Assert.IsType<InvalidDataException>(restoreError);
        Assert.False(File.Exists(backup));
        Assert.Equal(original, File.ReadAllBytes(destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.pre-restore-*"));
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

    private static void CreateLegacyReleaseDatabase(string databasePath, int version)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var assembly = typeof(ControllerDatabase).Assembly;
        foreach (var name in assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("KamenRaider.Controller.Data.Migrations.", StringComparison.Ordinal)
                && int.TryParse(name.AsSpan("KamenRaider.Controller.Data.Migrations.".Length, 3), out var number) && number <= version)
            .Order(StringComparer.Ordinal))
        {
            using var resource = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(resource);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
        }
        using var seed = connection.CreateCommand();
        seed.Transaction = transaction;
        seed.CommandText = "INSERT INTO machines(machine_id,public_key,local_ip,mac_address,moonlight_port,sunshine_port) VALUES('legacy-pc','a2V5','10.0.0.2','001122334455',1,2)";
        seed.ExecuteNonQuery();
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
