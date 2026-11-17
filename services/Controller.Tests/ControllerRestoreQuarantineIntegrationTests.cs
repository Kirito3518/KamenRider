using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Reflection;
using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Agents;
using KamenRaider.Controller.Machines;
using Microsoft.Data.Sqlite;
using Controller.Tests.Fixtures;

namespace Controller.Tests;

[SupportedOSPlatform("windows")]
public sealed class ControllerRestoreQuarantineIntegrationTests
{
    [Fact]
    public void RestoreOfSnapshotWithoutMachineOrChainQuarantinesNewAndExistingMachineActionsGlobally()
    {
        using var directory = new ProtectedTestDirectory();
        var source = Path.Combine(directory.Path, "source", "controller.db");
        var backup = Path.Combine(directory.Path, "backup", "controller.db");
        var destination = Path.Combine(directory.Path, "destination", "controller.db");
        directory.CreateProtectedParent(source);
        directory.CreateProtectedParent(backup);
        directory.CreateProtectedParent(destination);

        using (new ControllerDatabase(source)) { }
        ControllerDatabaseMaintenance.Backup(source, backup);
        ControllerDatabaseMaintenance.Restore(backup, destination);

        using var restored = new ControllerDatabase(destination, agentReleaseRemediationEnabled: true);
        Assert.Equal(23L, Scalar(destination, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(destination, "SELECT quarantine_active FROM controller_restore_state WHERE singleton=1"));

        SeedApprovedMachine(restored, "machine-added-after-restore");

        Assert.False(Assert.Single(restored.List()).CanRent);
        Assert.Throws<InvalidOperationException>(() => restored.CreateTrialLease("renter", "machine-added-after-restore", 60, "rent-after-restore"));
        Assert.Throws<InvalidOperationException>(() => restored.ReserveAgentUpdate("machine-added-after-restore", "missing-release", "admin", "reserve-after-restore"));
        Assert.Equal(0L, Scalar(destination, "SELECT COUNT(*) FROM leases WHERE allocation_held=1"));
        Assert.Equal(0L, Scalar(destination, "SELECT COUNT(*) FROM agent_maintenance_chains WHERE held=1"));
    }

    [Fact]
    public void TicketCapturedInBackupCannotStartAfterRestoreAndKeepsItsOriginalHeldState()
    {
        using var directory = new ProtectedTestDirectory();
        var source = Path.Combine(directory.Path, "source", "controller.db");
        var backup = Path.Combine(directory.Path, "backup", "controller.db");
        var destination = Path.Combine(directory.Path, "destination", "controller.db");
        directory.CreateProtectedParent(source);
        directory.CreateProtectedParent(backup);
        directory.CreateProtectedParent(destination);
        const string releaseVersion = AgentPackageCorpus.Version;
        var package = AgentPackageCorpus.Valid();
        var packageHash = Convert.ToHexString(SHA256.HashData(package));
        var artifact = Path.Combine(Path.GetDirectoryName(source)!, "agent-releases", "sha256", packageHash[..2].ToLowerInvariant(), packageHash.ToLowerInvariant() + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllBytes(artifact, package);

        AgentUpdateTicketResponse ticket;
        using (var database = new ControllerDatabase(source, agentReleaseRemediationEnabled: true))
        {
            SeedRentableMachine(database, "machine-with-pre-restore-ticket");
            database.PublishAgentRelease(new(releaseVersion, "stable", "release", packageHash, artifact, package.Length, "0123456789ab", false, true, "win-x64", 2));
            ticket = database.ReserveAgentUpdate("machine-with-pre-restore-ticket", releaseVersion, "admin", "pre-restore-ticket");
        }

        ControllerDatabaseMaintenance.Backup(source, backup);
        ControllerDatabaseMaintenance.Restore(backup, destination);

        using var restored = new ControllerDatabase(destination, agentReleaseRemediationEnabled: true);
        Assert.Throws<InvalidOperationException>(() => restored.StartAgentUpdate("machine-with-pre-restore-ticket", ticket.OperationId,
            ticket.Ticket, ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, "start-old-ticket"));
        Assert.Equal("awaitingInstaller", ScalarText(destination, "SELECT status FROM agent_update_operations WHERE operation_id=$operation", ticket.OperationId));
        Assert.Equal(1L, Scalar(destination, "SELECT held FROM agent_maintenance_chains WHERE current_head_operation_id=$operation", ticket.OperationId));
    }

    [Fact]
    public async Task ExistingDatabaseWithoutRestoreEpochCannotStartExternalAdmission()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = Path.Combine(directory.Path, "controller.db");
        directory.CreateProtectedParent(databasePath);
        using (new ControllerDatabase(databasePath)) { }

        using var activityLock = new ControllerDatabaseActivityLock(databasePath, requireRestoreEpoch: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => activityLock.StartAsync(CancellationToken.None));
        Assert.False(File.Exists(databasePath + ".restore-epoch"));
    }

    [Fact]
    public async Task ExplicitBootstrapSetsDurableQuarantineAndCanBeRetriedSafely()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = Path.Combine(directory.Path, "controller.db");
        directory.CreateProtectedParent(databasePath);
        using (new ControllerDatabase(databasePath)) { }

        Assert.Equal(1L, ControllerDatabaseMaintenance.BootstrapRestoreEpochForExistingDatabase(databasePath));
        Assert.Equal(1L, ControllerDatabaseMaintenance.BootstrapRestoreEpochForExistingDatabase(databasePath));
        Assert.Equal(1L, Scalar(databasePath, "SELECT quarantine_active FROM controller_restore_state WHERE singleton=1"));

        using var activityLock = new ControllerDatabaseActivityLock(databasePath, requireRestoreEpoch: true);
        await activityLock.StartAsync(CancellationToken.None);
        Assert.Equal(2L, activityLock.RestoreEpoch);
    }

    [Fact]
    public async Task FirstInstallCreatesEpochOnlyForAbsentDatabaseAndLifetimeLockCoversInitialization()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = Path.Combine(directory.Path, "controller.db");
        directory.CreateProtectedParent(databasePath);

        using (var activityLock = new ControllerDatabaseActivityLock(databasePath, requireRestoreEpoch: true))
        {
            await activityLock.StartAsync(CancellationToken.None);
            Assert.Equal(1L, activityLock.RestoreEpoch);
            using var database = new ControllerDatabase(databasePath);
            Assert.Equal(0L, Scalar(databasePath, "SELECT quarantine_active FROM controller_restore_state WHERE singleton=1"));
        }

        using var restarted = new ControllerDatabaseActivityLock(databasePath, requireRestoreEpoch: true);
        await restarted.StartAsync(CancellationToken.None);
        Assert.Equal(2L, restarted.RestoreEpoch);
        Assert.Equal(1L, Scalar(databasePath, "SELECT quarantine_active FROM controller_restore_state WHERE singleton=1"));
    }

    [Fact]
    public async Task OlderCleanSnapshotWithMachineAbsentIsQuarantinedWhenExternalEpochHasAdvanced()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = Path.Combine(directory.Path, "controller.db");
        var snapshotPath = Path.Combine(directory.Path, "old-clean-snapshot.db");
        directory.CreateProtectedParent(databasePath);
        directory.CreateProtectedParent(snapshotPath);

        using (var firstStart = new ControllerDatabaseActivityLock(databasePath, requireRestoreEpoch: true))
            await firstStart.StartAsync(CancellationToken.None);
        ControllerDatabaseMaintenance.Backup(databasePath, snapshotPath);
        using (var currentDatabase = new ControllerDatabase(databasePath))
            SeedApprovedMachine(currentDatabase, "machine-present-after-snapshot");

        using (var secondStart = new ControllerDatabaseActivityLock(databasePath, requireRestoreEpoch: true))
            await secondStart.StartAsync(CancellationToken.None);
        Assert.Equal(2L, ControllerRestoreEpoch.ValidateExistingEpoch(databasePath));

        File.Copy(snapshotPath, databasePath, overwrite: true);
        Assert.Equal(0L, Scalar(databasePath, "SELECT COUNT(*) FROM machines"));

        using (var restoredStart = new ControllerDatabaseActivityLock(databasePath, requireRestoreEpoch: true))
            await restoredStart.StartAsync(CancellationToken.None);

        using var restored = new ControllerDatabase(databasePath, agentReleaseRemediationEnabled: true);
        Assert.Equal(1L, Scalar(databasePath, "SELECT quarantine_active FROM controller_restore_state WHERE singleton=1"));
        SeedApprovedMachine(restored, "machine-added-after-snapshot-rollback");
        Assert.False(Assert.Single(restored.List()).CanRent);
        Assert.Throws<InvalidOperationException>(() => restored.CreateTrialLease("renter", "machine-added-after-snapshot-rollback", 60, "rollback-rent"));
        Assert.Throws<InvalidOperationException>(() => restored.ReserveAgentUpdate("machine-added-after-snapshot-rollback", "missing-release", "admin", "rollback-update"));
    }

    [Fact]
    public async Task SameRunSnapshotRollbackIsQuarantinedEvenWhenTheSidecarEpochMatches()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = Path.Combine(directory.Path, "controller.db");
        var snapshotPath = Path.Combine(directory.Path, "same-run-snapshot.db");
        directory.CreateProtectedParent(databasePath);
        directory.CreateProtectedParent(snapshotPath);

        using (var firstStart = new ControllerDatabaseActivityLock(databasePath, requireRestoreEpoch: true))
        {
            await firstStart.StartAsync(CancellationToken.None);
            ControllerDatabaseMaintenance.Backup(databasePath, snapshotPath);
            using var changedDatabase = new ControllerDatabase(databasePath, agentReleaseRemediationEnabled: true);
            SeedRentableMachine(changedDatabase, "machine-added-after-snapshot");
            changedDatabase.CreateTrialLease("renter", "machine-added-after-snapshot", 60, "lease-after-snapshot");
        }

        Assert.Equal(1L, ControllerRestoreEpoch.ValidateExistingEpoch(databasePath));
        File.Copy(snapshotPath, databasePath, overwrite: true);
        Assert.Equal(1L, ControllerRestoreEpoch.ValidateExistingEpoch(databasePath));
        Assert.Equal(0L, Scalar(databasePath, "SELECT COUNT(*) FROM machines"));

        using (var restarted = new ControllerDatabaseActivityLock(databasePath, requireRestoreEpoch: true))
            await restarted.StartAsync(CancellationToken.None);

        using var restored = new ControllerDatabase(databasePath, agentReleaseRemediationEnabled: true);
        Assert.Equal(1L, Scalar(databasePath, "SELECT quarantine_active FROM controller_restore_state WHERE singleton=1"));
        SeedApprovedMachine(restored, "machine-added-after-same-run-rollback");
        Assert.False(Assert.Single(restored.List()).CanRent);
        Assert.Throws<InvalidOperationException>(() => restored.CreateTrialLease("renter", "machine-added-after-same-run-rollback", 60, "same-run-rollback-rent"));
    }

    [Fact]
    public async Task FailedDatabaseEpochBindingAfterSidecarAdvanceQuarantinesOnNextAdmission()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = Path.Combine(directory.Path, "controller.db");
        directory.CreateProtectedParent(databasePath);
        using (var firstStart = new ControllerDatabaseActivityLock(databasePath, requireRestoreEpoch: true))
            await firstStart.StartAsync(CancellationToken.None);

        using (var interruptedDatabase = new ControllerDatabase(databasePath))
        {
            Assert.Throws<IOException>(() => ControllerRestoreEpoch.AdvanceBeforeAdmission(databasePath,
                interruptedDatabase.ReconcileRestoreEpoch,
                _ => throw new IOException("injected crash after sidecar advance")));
            Assert.Equal(1L, Scalar(databasePath, "SELECT database_epoch FROM controller_restore_state WHERE singleton=1"));
        }
        Assert.Equal(2L, ControllerRestoreEpoch.ValidateExistingEpoch(databasePath));

        using (var retry = new ControllerDatabaseActivityLock(databasePath, requireRestoreEpoch: true))
            await retry.StartAsync(CancellationToken.None);

        Assert.Equal(1L, Scalar(databasePath, "SELECT quarantine_active FROM controller_restore_state WHERE singleton=1"));
        Assert.Equal(3L, Scalar(databasePath, "SELECT database_epoch FROM controller_restore_state WHERE singleton=1"));
    }

    [Fact]
    public void QuarantinedMachineListDoesNotExpireOrReleaseAStaleHeldUpdateChain()
    {
        using var directory = new ProtectedTestDirectory();
        var source = Path.Combine(directory.Path, "source", "controller.db");
        var backup = Path.Combine(directory.Path, "backup", "controller.db");
        var destination = Path.Combine(directory.Path, "destination", "controller.db");
        directory.CreateProtectedParent(source);
        directory.CreateProtectedParent(backup);
        directory.CreateProtectedParent(destination);
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var package = AgentPackageCorpus.Valid();
        var packageHash = Convert.ToHexString(SHA256.HashData(package));
        var artifact = Path.Combine(Path.GetDirectoryName(source)!, "agent-releases", "sha256", packageHash[..2].ToLowerInvariant(), packageHash.ToLowerInvariant() + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllBytes(artifact, package);

        string operationId;
        using (var database = new ControllerDatabase(source, clock: time, agentReleaseRemediationEnabled: true))
        {
            SeedRentableMachine(database, "machine-with-stale-held-chain");
            database.PublishAgentRelease(new(AgentPackageCorpus.Version, "stable", "release", packageHash, artifact, package.Length, "0123456789ab", false, true, "win-x64", 2));
            operationId = database.ReserveAgentUpdate("machine-with-stale-held-chain", AgentPackageCorpus.Version, "admin", "stale-held-chain").OperationId;
        }

        ControllerDatabaseMaintenance.Backup(source, backup);
        ControllerDatabaseMaintenance.Restore(backup, destination);
        time.Now = time.Now.AddDays(1);

        using var restored = new ControllerDatabase(destination, clock: time, agentReleaseRemediationEnabled: true);
        Assert.Equal(1L, Scalar(destination, "SELECT held FROM agent_maintenance_chains WHERE current_head_operation_id=$operation", operationId));
        Assert.False(Assert.Single(restored.List()).CanRent);
        Assert.Equal(1L, Scalar(destination, "SELECT held FROM agent_maintenance_chains WHERE current_head_operation_id=$operation", operationId));
        Assert.Equal("awaitingInstaller", ScalarText(destination, "SELECT status FROM agent_update_operations WHERE operation_id=$operation", operationId));
    }

    [Fact]
    public async Task FailedRestorePublicationLeavesDurableQuarantineAndBlocksStartup()
    {
        using var directory = new ProtectedTestDirectory();
        var source = Path.Combine(directory.Path, "source", "controller.db");
        var backup = Path.Combine(directory.Path, "backup", "controller.db");
        var destination = Path.Combine(directory.Path, "destination", "controller.db");
        directory.CreateProtectedParent(source);
        directory.CreateProtectedParent(backup);
        directory.CreateProtectedParent(destination);

        using (new ControllerDatabase(source)) { }
        ControllerDatabaseMaintenance.Backup(source, backup);
        Assert.Equal(1L, ControllerRestoreEpoch.InitializeForNewDatabase(destination));
        using (new ControllerDatabase(destination)) { }

        Assert.Throws<IOException>(() => ControllerDatabaseMaintenance.Restore(backup, destination,
            beforeDatabasePublish: () => throw new IOException("injected database publish failure")));

        Assert.Equal(1L, Scalar(destination, "SELECT quarantine_active FROM controller_restore_state WHERE singleton=1"));
        Assert.Equal(2L, ControllerRestoreEpoch.ValidateExistingEpoch(destination));
        Assert.True(File.Exists(destination + ".restore-pending"));
        using var activityLock = new ControllerDatabaseActivityLock(destination, requireRestoreEpoch: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => activityLock.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MalformedExistingDatabaseAndFailedRestoreRemainBlockedByPendingMarker()
    {
        using var directory = new ProtectedTestDirectory();
        var source = Path.Combine(directory.Path, "source", "controller.db");
        var backup = Path.Combine(directory.Path, "backup", "controller.db");
        var destination = Path.Combine(directory.Path, "destination", "controller.db");
        directory.CreateProtectedParent(source);
        directory.CreateProtectedParent(backup);
        directory.CreateProtectedParent(destination);
        using (new ControllerDatabase(source)) { }
        ControllerDatabaseMaintenance.Backup(source, backup);
        Assert.Equal(1L, ControllerRestoreEpoch.InitializeForNewDatabase(destination));
        using (new ControllerDatabase(destination)) { }
        File.WriteAllText(destination, "malformed existing database");

        Assert.Throws<IOException>(() => ControllerDatabaseMaintenance.Restore(backup, destination,
            beforeDatabasePublish: () => throw new IOException("injected publication failure")));

        Assert.Equal("malformed existing database", File.ReadAllText(destination));
        Assert.True(File.Exists(destination + ".restore-pending"));
        Assert.Equal(2L, ControllerRestoreEpoch.ValidateExistingEpoch(destination));
        using var activityLock = new ControllerDatabaseActivityLock(destination, requireRestoreEpoch: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => activityLock.StartAsync(CancellationToken.None));
    }

    [Fact]
    public void RestoreRejectsIndirectTriggerThatCouldClearGlobalQuarantine()
    {
        using var directory = new ProtectedTestDirectory();
        var source = Path.Combine(directory.Path, "source", "controller.db");
        var backup = Path.Combine(directory.Path, "backup", "controller.db");
        var destination = Path.Combine(directory.Path, "destination", "controller.db");
        directory.CreateProtectedParent(source);
        directory.CreateProtectedParent(backup);
        directory.CreateProtectedParent(destination);
        using (new ControllerDatabase(source)) { }
        File.Copy(source, backup);

        using (var connection = Open(backup))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER malicious_lease_restore_reset AFTER UPDATE OF state ON leases BEGIN UPDATE controller_restore_state SET quarantine_active=0 WHERE singleton=1; END;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Restore(backup, destination));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void ExistingPreRestoreSchemaRejectsIndirectTriggerBeforeMigrationOrRuntimeProofInvalidation()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = Path.Combine(directory.Path, "controller.db");
        directory.CreateProtectedParent(databasePath);
        CreateVersion18Database(databasePath);
        using (var connection = Open(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER malicious_proof_restore_reset AFTER UPDATE ON agent_sync_proof_state BEGIN UPDATE controller_restore_state SET quarantine_active=0 WHERE singleton=1; END;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => new ControllerDatabase(databasePath));
        Assert.Equal(18L, Scalar(databasePath, "PRAGMA user_version"));
        Assert.Equal(0L, Scalar(databasePath, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='controller_restore_state'"));
    }

    [Fact]
    public void VersionZeroDatabasesAtHistoricalMigrationOneAndTwoCheckpointsMigrateIntoQuarantine()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = Path.Combine(directory.Path, "controller.db");
        directory.CreateProtectedParent(databasePath);
        CreateMigrationCheckpoint(databasePath, lastMigrationFileVersion: 2);
        Assert.Equal(0L, Scalar(databasePath, "PRAGMA user_version"));

        using (new ControllerDatabase(databasePath)) { }

        Assert.Equal(23L, Scalar(databasePath, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(databasePath, "SELECT quarantine_active FROM controller_restore_state WHERE singleton=1"));
    }

    [Fact]
    public void VersionFourCheckpointAfterAgentVersionMigrationMigratesIntoQuarantine()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = Path.Combine(directory.Path, "controller.db");
        directory.CreateProtectedParent(databasePath);
        CreateMigrationCheckpoint(databasePath, lastMigrationFileVersion: 9);
        Assert.Equal(4L, Scalar(databasePath, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(databasePath, "SELECT COUNT(*) FROM pragma_table_info('machines') WHERE name='vnc_local_ip'"));
        Assert.Equal(1L, Scalar(databasePath, "SELECT COUNT(*) FROM pragma_table_info('machine_heartbeats') WHERE name='agent_version'"));

        using (new ControllerDatabase(databasePath)) { }

        Assert.Equal(23L, Scalar(databasePath, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(databasePath, "SELECT quarantine_active FROM controller_restore_state WHERE singleton=1"));
    }

    [Fact]
    public void EscapedQuotedIdentifierInKnownTriggerIsRejectedBeforeStartupDml()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = Path.Combine(directory.Path, "controller.db");
        directory.CreateProtectedParent(databasePath);
        using (new ControllerDatabase(databasePath)) { }
        using (var connection = Open(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER agent_update_operation_identity_immutable; CREATE TRIGGER agent_update_operation_identity_immutable BEFORE UPDATE OF operation_id,machine_id,chain_id,\"parent\"\"_operation_id\",release_version,target_version,target_package_sha256,target_runtime,target_protocol,previous_version ON agent_update_operations BEGIN SELECT RAISE(ABORT,'Agent update operation identity is immutable'); END;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => new ControllerDatabase(databasePath));
        Assert.Equal(23L, Scalar(databasePath, "PRAGMA user_version"));
    }

    [Fact]
    public void NonAsciiIdentifierConfusableIsRejectedBeforeMigrationOrStartupDml()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = Path.Combine(directory.Path, "controller.db");
        directory.CreateProtectedParent(databasePath);
        CreateVersion18Database(databasePath);
        using (var connection = Open(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "ALTER TABLE agent_releases RENAME COLUMN package_path TO pac\u212Aage_path;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => new ControllerDatabase(databasePath));
        Assert.Equal(18L, Scalar(databasePath, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(databasePath, "SELECT COUNT(*) FROM pragma_table_info('agent_releases') WHERE name='pac\u212Aage_path'"));
    }

    [Fact]
    public void AlteredCommentInCanonicalTriggerIsRejectedOnStartup()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = Path.Combine(directory.Path, "controller.db");
        directory.CreateProtectedParent(databasePath);
        using (new ControllerDatabase(databasePath)) { }
        using (var connection = Open(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER agent_update_reports_immutable_delete; CREATE TRIGGER agent_update_reports_immutable_delete BEFORE DELETE ON agent_update_reports /* altered schema comment */ BEGIN SELECT RAISE(ABORT,'Agent update reports are append-only'); END;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => new ControllerDatabase(databasePath));
    }

    [Fact]
    public async Task FailedFirstRestoreLeavesAbsentDestinationBlockedFromOrdinaryFirstInstall()
    {
        using var directory = new ProtectedTestDirectory();
        var source = Path.Combine(directory.Path, "source", "controller.db");
        var backup = Path.Combine(directory.Path, "backup", "controller.db");
        var destination = Path.Combine(directory.Path, "destination", "controller.db");
        directory.CreateProtectedParent(source);
        directory.CreateProtectedParent(backup);
        directory.CreateProtectedParent(destination);
        using (new ControllerDatabase(source)) { }
        ControllerDatabaseMaintenance.Backup(source, backup);

        Assert.Throws<IOException>(() => ControllerDatabaseMaintenance.Restore(backup, destination,
            beforeDatabasePublish: () => throw new IOException("injected first-install publish failure")));

        Assert.False(File.Exists(destination));
        Assert.True(File.Exists(destination + ".restore-pending"));
        Assert.True(File.Exists(destination + ".restore-epoch"));
        using var activityLock = new ControllerDatabaseActivityLock(destination, requireRestoreEpoch: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => activityLock.StartAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public async Task RestoreRejectsOrphanSqliteSidecarWhenDestinationDatabaseIsMissing(string suffix)
    {
        using var directory = new ProtectedTestDirectory();
        var source = Path.Combine(directory.Path, "source", "controller.db");
        var backup = Path.Combine(directory.Path, "backup", "controller.db");
        var destination = Path.Combine(directory.Path, "destination", "controller.db");
        directory.CreateProtectedParent(source);
        directory.CreateProtectedParent(backup);
        directory.CreateProtectedParent(destination);
        using (new ControllerDatabase(source)) { }
        ControllerDatabaseMaintenance.Backup(source, backup);
        var orphanContent = $"orphan {suffix} from an interrupted database publication";
        File.WriteAllText(destination + suffix, orphanContent);

        Assert.Throws<InvalidDataException>(() => ControllerDatabaseMaintenance.Restore(backup, destination));

        Assert.False(File.Exists(destination));
        Assert.Equal(orphanContent, File.ReadAllText(destination + suffix));
        Assert.False(File.Exists(destination + ".restore-epoch"));
        Assert.False(File.Exists(destination + ".restore-pending"));
        using var activityLock = new ControllerDatabaseActivityLock(destination, requireRestoreEpoch: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => activityLock.StartAsync(CancellationToken.None));
        Assert.False(File.Exists(destination));
        Assert.Equal(orphanContent, File.ReadAllText(destination + suffix));
    }

    [Fact]
    public void InvalidPendingMarkerBlocksRestoreBeforeChangingDestinationOrEpoch()
    {
        using var directory = new ProtectedTestDirectory();
        var source = Path.Combine(directory.Path, "source", "controller.db");
        var backup = Path.Combine(directory.Path, "backup", "controller.db");
        var destination = Path.Combine(directory.Path, "destination", "controller.db");
        directory.CreateProtectedParent(source);
        directory.CreateProtectedParent(backup);
        directory.CreateProtectedParent(destination);
        using (new ControllerDatabase(source)) { }
        ControllerDatabaseMaintenance.Backup(source, backup);
        Assert.Equal(1L, ControllerRestoreEpoch.InitializeForNewDatabase(destination));
        using (new ControllerDatabase(destination)) { }
        Directory.CreateDirectory(destination + ".restore-pending");

        Assert.ThrowsAny<IOException>(() => ControllerDatabaseMaintenance.Restore(backup, destination));

        Assert.Equal(1L, ControllerRestoreEpoch.ValidateExistingEpoch(destination));
        Assert.Equal(0L, Scalar(destination, "SELECT quarantine_active FROM controller_restore_state WHERE singleton=1"));
    }

    private static void SeedRentableMachine(ControllerDatabase database, string machineId)
    {
        SeedApprovedMachine(database, machineId);
        var machine = Assert.Single(database.List());
        database.Sync(machineId, new("boot", "192.0.2.20", "001122334455", machine.ConfigRevision, "ready", []));
    }

    private static void SeedApprovedMachine(ControllerDatabase database, string machineId)
    {
        using var key = ECDsa.Create();
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        database.EnrollWithTicket(new(machineId, publicKey, "192.0.2.20", "001122334455", 47989, 47990));
        database.Update(machineId, new("PC", 500, 47989, 47990), 1);
        database.Approve(machineId, "admin");
    }

    private static long Scalar(string path, string sql, string? operationId = null)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (operationId is not null) command.Parameters.AddWithValue("$operation", operationId);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ScalarText(string path, string sql, string operationId)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$operation", operationId);
        return (string)command.ExecuteScalar()!;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void CreateVersion18Database(string path)
        => CreateMigrationCheckpoint(path, lastMigrationFileVersion: 18);

    private static void CreateMigrationCheckpoint(string path, int lastMigrationFileVersion)
    {
        using var connection = Open(path);
        var assembly = typeof(ControllerDatabase).Assembly;
        const string prefix = "KamenRaider.Controller.Data.Migrations.";
        foreach (var resourceName in assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal))
        {
            var fileName = resourceName[prefix.Length..^4];
            if (!int.TryParse(fileName.AsSpan(0, 3), out var version) || version > lastMigrationFileVersion) continue;
            using var resource = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(resource);
            using var migration = connection.CreateCommand();
            migration.CommandText = reader.ReadToEnd();
            migration.ExecuteNonQuery();
        }
    }

    private sealed class ProtectedTestDirectory : IDisposable
    {
        private static readonly string testRoot = ResolveTestRoot();
        public string Path { get; } = System.IO.Path.Combine(testRoot, Guid.NewGuid().ToString("N"));

        public ProtectedTestDirectory()
        {
            Directory.CreateDirectory(Path);
            ProtectDirectory(Path);
        }

        public void CreateProtectedParent(string filePath)
        {
            var parent = System.IO.Path.GetDirectoryName(filePath)!;
            Directory.CreateDirectory(parent);
            ProtectDirectory(parent);
        }

        private static void ProtectDirectory(string path)
        {
            var currentUser = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The test process has no Windows user SID.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(currentUser);
            security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }

        private static string ResolveTestRoot()
        {
            var controllerProjectPath = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(attribute => attribute.Key == "ControllerProjectPath")?.Value
                ?? throw new InvalidOperationException("Controller.Tests is missing its ControllerProjectPath metadata.");
            var controllerProjectDirectory = System.IO.Path.GetDirectoryName(controllerProjectPath)!;
            var repositoryDirectory = Directory.GetParent(Directory.GetParent(controllerProjectDirectory)!.FullName)!.FullName;
            var root = System.IO.Path.Combine(repositoryDirectory, ".restore-epoch-tests");
            Directory.CreateDirectory(root);
            return root;
        }
    }
}
