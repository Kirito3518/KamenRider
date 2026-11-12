using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using System.Globalization;
using System.Collections.Concurrent;

namespace KamenRaider.Controller.Data;

public sealed class ControllerDatabaseActivityLock(string databasePath, bool requireRestoreEpoch = false) : IHostedService, IDisposable
{
    private readonly string fullDatabasePath = Normalize(databasePath, nameof(databasePath));
    private readonly bool requiresProtectedRestoreEpoch = requireRestoreEpoch;
    private FileStream? activeLock;
    private ControllerRestoreEpoch.ControllerLifetimeLease? restoreEpochLease;

    public long? RestoreEpoch => OperatingSystem.IsWindows() ? restoreEpochLease?.Epoch : null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (activeLock is not null || restoreEpochLease is not null) return Task.CompletedTask;

        if (requiresProtectedRestoreEpoch)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Protected restore epoch admission requires Windows.");
            if (File.Exists(fullDatabasePath) || Directory.Exists(fullDatabasePath))
            {
                ControllerDatabase? startupDatabase = null;
                try
                {
                    using var admission = ControllerRestoreEpoch.AdvanceBeforeAdmission(fullDatabasePath,
                        currentEpoch =>
                        {
                            startupDatabase = new ControllerDatabase(fullDatabasePath);
                            startupDatabase.ReconcileRestoreEpoch(currentEpoch);
                        },
                        nextEpoch => (startupDatabase ?? throw new InvalidDataException("Controller epoch reconciliation did not open its database."))
                            .PersistRestoreEpoch(nextEpoch));
                    restoreEpochLease = admission.TakeLifetimeLease();
                }
                finally { startupDatabase?.Dispose(); }
            }
            else
            {
                if (HasRestoreArtifactsWithoutDatabase(fullDatabasePath))
                    throw new InvalidDataException("The Controller database is absent but restore artifacts remain; refusing first-install epoch creation.");
                using var admission = ControllerRestoreEpoch.InitializeForNewDatabaseAdmission(fullDatabasePath);
                using (var startupDatabase = new ControllerDatabase(fullDatabasePath))
                    startupDatabase.PersistRestoreEpoch(admission.Epoch, requireUninitialized: true);
                restoreEpochLease = admission.TakeLifetimeLease();
            }

            return Task.CompletedTask;
        }

        using var operationLock = ControllerDatabaseFileLocks.Acquire(fullDatabasePath, ".maintenance.lock", "A database maintenance operation is already running.");
        activeLock = ControllerDatabaseFileLocks.Acquire(fullDatabasePath, ".active.lock", "Another Controller is already using this database.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows()) restoreEpochLease?.Dispose();
        restoreEpochLease = null;
        activeLock?.Dispose();
        activeLock = null;
    }

    private static bool HasRestoreArtifactsWithoutDatabase(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath)!;
        Directory.CreateDirectory(directory);
        var databaseName = Path.GetFileName(databasePath);
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                if (name.Equals(databaseName + ".restore-epoch", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(databaseName + ".restore-pending", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(databaseName + ".pre-restore-", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(databaseName + "-wal", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(databaseName + "-shm", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("." + databaseName + ".", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("agent-releases", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The Controller database directory could not be checked for interrupted restore artifacts.", exception);
        }

        return false;
    }

    internal static string Normalize(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A database path is required.", parameterName);
        return Path.GetFullPath(path);
    }
}

internal static class ControllerDatabaseFileLocks
{
    public static FileStream Acquire(string databasePath, string suffix, string conflictMessage)
    {
        var directory = Path.GetDirectoryName(databasePath)!;
        Directory.CreateDirectory(directory);
        try
        {
            return new FileStream(databasePath + suffix, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(conflictMessage, exception);
        }
    }
}

public static class ControllerDatabaseMaintenance
{
    private sealed record SchemaObject(string Type, string Name, string TableName, string? Sql);
    private static readonly ConcurrentDictionary<long, IReadOnlyList<IReadOnlyDictionary<string, SchemaObject>>> expectedSchemas = new();
    private static readonly string[] requiredTables = ["machines", "machine_heartbeats", "leases", "command_outbox"];
    private sealed record RequiredColumn(string Name, string Type, bool NotNull = false);
    private static readonly IReadOnlyDictionary<string, RequiredColumn[]> requiredColumns = new Dictionary<string, RequiredColumn[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["machines"] = [
            new("machine_id", "TEXT"), new("public_key", "TEXT", true), new("approval_status", "TEXT", true),
            new("display_name", "TEXT", true), new("hourly_price", "INTEGER", true), new("local_ip", "TEXT", true),
            new("mac_address", "TEXT", true), new("moonlight_port", "INTEGER", true), new("sunshine_port", "INTEGER", true),
            new("revision", "INTEGER", true), new("approved_by", "TEXT"), new("approved_at", "TEXT")
        ],
        ["machine_heartbeats"] = [
            new("machine_id", "TEXT"), new("last_seen", "TEXT", true), new("boot_id", "TEXT", true), new("readiness", "TEXT", true)
        ],
        ["machine_readiness_diagnostics"] = [
            new("machine_id", "TEXT"), new("diagnostics_json", "TEXT", true), new("updated_at", "TEXT", true)
        ],
        ["agent_releases"] = [
            new("version", "TEXT"), new("channel", "TEXT", true), new("release_notes", "TEXT", true),
            new("package_sha256", "TEXT", true), new("package_path", "TEXT", true), new("package_size_bytes", "INTEGER", true),
            new("source_commit", "TEXT", true), new("source_dirty", "INTEGER", true), new("release_build", "INTEGER", true),
            new("package_format", "TEXT", true), new("published_at_unix_ms", "INTEGER", true)
        ],
        ["agent_update_operations"] = [
            new("operation_id", "TEXT"), new("machine_id", "TEXT", true), new("release_version", "TEXT", true),
            new("target_version", "TEXT", true), new("target_package_sha256", "TEXT", true), new("previous_version", "TEXT", true),
            new("status", "TEXT", true), new("ticket_hash", "TEXT", true), new("ticket_expires_at_unix_ms", "INTEGER", true),
            new("ticket_started_at_unix_ms", "INTEGER"), new("started_at_unix_ms", "INTEGER"),
            new("created_at_unix_ms", "INTEGER", true), new("updated_at_unix_ms", "INTEGER", true)
        ],
        ["leases"] = [
            new("id", "TEXT"), new("user_id", "TEXT", true), new("machine_id", "TEXT", true), new("state", "TEXT", true),
            new("revision", "INTEGER", true), new("pending_revision", "INTEGER"), new("duration_seconds", "INTEGER", true),
            new("preparation_deadline", "INTEGER", true), new("starts_at", "INTEGER"), new("expires_at", "INTEGER"),
            new("pending_expires_at", "INTEGER"), new("boot_id", "TEXT"), new("allocation_held", "INTEGER", true)
        ],
        ["command_outbox"] = [
            new("command_id", "TEXT"), new("type", "TEXT", true), new("rental_id", "TEXT", true), new("machine_id", "TEXT", true),
            new("lease_revision", "INTEGER", true), new("boot_id", "TEXT", true), new("valid_until", "INTEGER", true),
            new("payload", "TEXT", true), new("status", "TEXT", true), new("error_code", "TEXT")
        ]
    };
    private static readonly IReadOnlyDictionary<string, string[]> requiredPrimaryKeys = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["machines"] = ["machine_id"],
        ["machine_heartbeats"] = ["machine_id"],
        ["machine_readiness_diagnostics"] = ["machine_id"],
        ["agent_releases"] = ["version"],
        ["agent_update_operations"] = ["operation_id"],
        ["leases"] = ["id"],
        ["command_outbox"] = ["command_id"]
    };

    public static string Backup(string databasePath, string backupPath)
    {
        var source = ControllerDatabaseActivityLock.Normalize(databasePath, nameof(databasePath));
        var destination = ControllerDatabaseActivityLock.Normalize(backupPath, nameof(backupPath));
        EnsureDifferentFiles(source, destination);
        if (!File.Exists(source)) throw new FileNotFoundException("Controller database was not found.", source);
        if (File.Exists(destination)) throw new IOException("Backup destination already exists; choose a new backup path.");

        using var operationLock = ControllerDatabaseFileLocks.Acquire(source, ".maintenance.lock", "Another database maintenance operation is already running.");
        var staged = NewTemporaryPath(destination);
        var artifactDestination = destination + ".agent-releases";
        var stagedArtifacts = artifactDestination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        if (Directory.Exists(artifactDestination) || File.Exists(artifactDestination)) throw new IOException("Backup artifact destination already exists.");
        var artifactsPublished = false;
        List<ControllerReleaseArtifacts.Artifact> artifacts = [];
        try
        {
            _ = WriteVerifiedBackup(source, staged, overwriteDestination: false);
            artifacts = ControllerReleaseArtifacts.Read(staged);
            if (artifacts.Count > 0)
            {
                ControllerReleaseArtifacts.Copy(artifacts, Path.Combine(Path.GetDirectoryName(source)!, "agent-releases"), stagedArtifacts, requireOriginalPath: true);
                MoveDirectoryNoOverwriteWithRetry(stagedArtifacts, artifactDestination);
                artifactsPublished = true;
            }
            File.Move(staged, destination);
            return destination;
        }
        catch
        {
            if (artifactsPublished) ControllerReleaseArtifacts.RemoveCreated(artifacts, artifactDestination);
            throw;
        }
        finally
        {
            ControllerReleaseArtifacts.RemoveCreated(artifacts, stagedArtifacts);
            DeleteSqliteSidecars(staged);
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    internal static void MoveDirectoryNoOverwriteWithRetry(string source, string destination,
        Action<string, string>? moveDirectory = null, Action<TimeSpan>? wait = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        moveDirectory ??= Directory.Move;
        wait ??= duration => Thread.Sleep(duration);
        TimeSpan[] delays = [TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)];

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                moveDirectory(source, destination);
                return;
            }
            catch (Exception exception) when (attempt < delays.Length && IsTransientDirectoryMoveFailure(exception))
            {
                wait(delays[attempt]);
            }
        }
    }

    private static bool IsTransientDirectoryMoveFailure(Exception exception) =>
        (exception.HResult & 0xFFFF) is 5 or 32;

    public static string? Restore(string backupPath, string databasePath)
        => Restore(backupPath, databasePath, beforeDatabasePublish: null);

    internal static string? Restore(string backupPath, string databasePath, Action? beforeDatabasePublish)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Protected restore epoch handling requires Windows.");
        var source = ControllerDatabaseActivityLock.Normalize(backupPath, nameof(backupPath));
        var destination = ControllerDatabaseActivityLock.Normalize(databasePath, nameof(databasePath));
        EnsureDifferentFiles(source, destination);
        if (!File.Exists(source)) throw new FileNotFoundException("Controller backup was not found.", source);

        using var operationLock = ControllerDatabaseFileLocks.Acquire(destination, ".maintenance.lock", "Another database maintenance operation is already running.");
        using (ControllerDatabaseFileLocks.Acquire(destination, ".active.lock", "Stop the Controller before restoring its database.")) { }

        var destinationExists = File.Exists(destination);
        if (Directory.Exists(destination)) throw new InvalidDataException("The Controller restore destination must be a database file path.");
        if (!destinationExists && HasOrphanSqliteSidecars(destination))
            throw new InvalidDataException("SQLite sidecars exist without the Controller restore destination; refusing to publish a database that could replay stale WAL state.");
        var restoreAlreadyPending = ControllerRestoreEpoch.HasValidRestorePendingMarker(destination);
        var epochSidecar = destination + ".restore-epoch";
        long? previousEpoch = null;
        if (destinationExists)
        {
            if (Directory.Exists(epochSidecar))
                throw new InvalidDataException("The Controller restore epoch entry is not a regular protected sidecar.");
            if (File.Exists(epochSidecar))
                previousEpoch = ControllerRestoreEpoch.ValidateExistingEpoch(destination);
        }
        else if ((File.Exists(epochSidecar) || Directory.Exists(epochSidecar)) && !restoreAlreadyPending)
        {
            throw new InvalidDataException("The restore epoch exists without its database; refusing to treat an interrupted restore as a new deployment.");
        }

        var staged = NewTemporaryPath(destination);
        var installedArtifacts = new List<string>();
        var restorePublished = false;
        try
        {
            try { CopyDatabase(source, staged); }
            catch (SqliteException exception) { throw new InvalidDataException("The backup file could not be read.", exception); }
            ValidateDatabase(staged);
            var artifacts = ControllerReleaseArtifacts.Read(staged);
            var artifactRoot = Path.Combine(Path.GetDirectoryName(destination)!, "agent-releases");
            ControllerReleaseArtifacts.ValidateCopy(artifacts, source + ".agent-releases", artifactRoot, requireOriginalPath: false);
            ControllerReleaseArtifacts.RebasePaths(staged, artifacts, artifactRoot);

            using (var stagedDatabase = new ControllerDatabase(staged, policy: null, clock: null, agentReleaseRemediationEnabled: false, invalidateRuntimeProofsOnOpen: false))
                stagedDatabase.ActivateRestoreQuarantine("database-restore", 0);
            ValidateDatabase(staged);

            string? recoveryPath = null;
            var recoveryDatabaseValidated = false;
            if (destinationExists)
            {
                recoveryPath = NewRecoveryPath(destination);
                PreserveRawDatabaseFiles(destination, recoveryPath);
                var verifiedRecoveryStage = NewTemporaryPath(recoveryPath);
                try
                {
                    try { _ = WriteVerifiedBackup(destination, verifiedRecoveryStage); }
                    catch (InvalidDataException) { /* Preserve the raw corrupt database and its sidecars. */ }
                    if (File.Exists(verifiedRecoveryStage))
                    {
                        var recoveryArtifacts = ControllerReleaseArtifacts.Read(verifiedRecoveryStage);
                        ControllerReleaseArtifacts.Copy(recoveryArtifacts, artifactRoot, recoveryPath + ".agent-releases", requireOriginalPath: true);
                        Publish(verifiedRecoveryStage, recoveryPath);
                        DeleteSqliteSidecars(recoveryPath);
                        recoveryDatabaseValidated = true;
                    }
                }
                finally
                {
                    DeleteSqliteSidecars(verifiedRecoveryStage);
                    if (File.Exists(verifiedRecoveryStage)) File.Delete(verifiedRecoveryStage);
                }

            }

            ControllerRestoreEpoch.BeginRestoreWithMaintenanceLockHeld(destination, operationLock);

            if (destinationExists)
            {
                // Make the live database fail closed before advancing the external epoch. If a later
                // publication step fails, startup still observes the durable global hold.
                try
                {
                    using var currentDatabase = new ControllerDatabase(destination);
                    currentDatabase.ActivateRestoreQuarantine("database-restore-in-progress", previousEpoch ?? 0);
                }
                catch (Exception exception) when (exception is SqliteException or InvalidDataException)
                {
                    // The protected pending marker blocks startup if a malformed existing image
                    // cannot accept the SQLite quarantine update before repair publication.
                }
            }

            var restoreEpoch = destinationExists
                ? previousEpoch is null
                    ? ControllerRestoreEpoch.ProvisionQuarantinedExistingDatabaseWithMaintenanceLockHeld(destination, operationLock)
                    : ControllerRestoreEpoch.AdvanceWithMaintenanceLockHeld(destination, operationLock)
                : ControllerRestoreEpoch.InitializeForRestoreWithMaintenanceLockHeld(destination, operationLock);
            using (var stagedDatabase = new ControllerDatabase(staged, policy: null, clock: null, agentReleaseRemediationEnabled: false, invalidateRuntimeProofsOnOpen: false))
            {
                stagedDatabase.ActivateRestoreQuarantine("database-restored", restoreEpoch);
                stagedDatabase.PersistRestoreEpoch(restoreEpoch);
            }
            ValidateDatabase(staged);

            beforeDatabasePublish?.Invoke();

            ControllerReleaseArtifacts.Copy(artifacts, source + ".agent-releases", artifactRoot, requireOriginalPath: false, installedArtifacts);
            if (destinationExists)
            {
                if (recoveryDatabaseValidated) CheckpointWal(destination);
                DeleteSqliteSidecars(destination);
            }
            Publish(staged, destination);
            restorePublished = true;
            ControllerRestoreEpoch.CompleteRestoreWithMaintenanceLockHeld(destination, operationLock);
            return recoveryPath;
        }
        finally
        {
            if (!restorePublished) foreach (var installed in installedArtifacts) if (File.Exists(installed)) File.Delete(installed);
            DeleteSqliteSidecars(staged);
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    public static long BootstrapRestoreEpochForExistingDatabase(string databasePath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Protected restore epoch handling requires Windows.");
        var destination = ControllerDatabaseActivityLock.Normalize(databasePath, nameof(databasePath));
        if (!File.Exists(destination)) throw new FileNotFoundException("An existing Controller database is required for restore epoch bootstrap.", destination);

        using var operationLock = ControllerDatabaseFileLocks.Acquire(destination, ".maintenance.lock", "Another database maintenance operation is already running.");
        using (ControllerDatabaseFileLocks.Acquire(destination, ".active.lock", "Stop the Controller before bootstrapping its restore epoch.")) { }

        using var database = new ControllerDatabase(destination);
        database.ActivateRestoreQuarantine("explicit-existing-database-bootstrap", 0);
        var epoch = ControllerRestoreEpoch.ProvisionQuarantinedExistingDatabaseWithMaintenanceLockHeld(destination, operationLock);
        database.PersistRestoreEpoch(epoch);
        return epoch;
    }

    private static string WriteVerifiedBackup(string source, string destination, bool overwriteDestination = true)
    {
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var staged = NewTemporaryPath(destination);
        try
        {
            CopyDatabase(source, staged);
            ValidateDatabase(staged);
            if (overwriteDestination) Publish(staged, destination);
            else File.Move(staged, destination);
            return destination;
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The database could not be backed up or failed SQLite integrity checks.", exception);
        }
        finally
        {
            DeleteSqliteSidecars(staged);
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    private static void CopyDatabase(string source, string destination)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("Database source was not found.", source);
        var sourceString = new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadWrite, ForeignKeys = true, Pooling = false }.ToString();
        var destinationString = new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true, Pooling = false }.ToString();
        using var sourceConnection = new SqliteConnection(sourceString);
        using var destinationConnection = new SqliteConnection(destinationString);
        sourceConnection.Open();
        destinationConnection.Open();
        sourceConnection.BackupDatabase(destinationConnection);
    }

    private static void ValidateDatabase(string path)
    {
        try
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, ForeignKeys = true, Pooling = false }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check";
                if (!string.Equals(integrity.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SQLite integrity_check did not return ok.");
            }

            using (var foreignKeys = connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_key_check";
                using var rows = foreignKeys.ExecuteReader();
                if (rows.Read()) throw new InvalidDataException("SQLite foreign_key_check found a violation.");
            }

            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "SELECT name FROM sqlite_schema WHERE type='table'";
                using var rows = schema.ExecuteReader();
                while (rows.Read()) tables.Add(rows.GetString(0));
            }
            if (requiredTables.Any(table => !tables.Contains(table)))
                throw new InvalidDataException("The database is missing required Controller tables.");

            foreach (var table in requiredTables)
                ValidateColumns(connection, table, requiredColumns[table], requiredPrimaryKeys[table]);

            using var userVersionCommand = connection.CreateCommand();
            userVersionCommand.CommandText = "PRAGMA user_version";
            var userVersion = Convert.ToInt64(userVersionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            ValidateCanonicalSchema(connection, userVersion);
            if (userVersion >= 11)
            {
                const string diagnosticsTable = "machine_readiness_diagnostics";
                if (!tables.Contains(diagnosticsTable))
                    throw new InvalidDataException("The database is missing the version 11 readiness diagnostics table.");
                ValidateColumns(connection, diagnosticsTable, requiredColumns[diagnosticsTable], requiredPrimaryKeys[diagnosticsTable]);
            }
            if (userVersion >= 12)
            {
                foreach (var table in new[] { "agent_releases", "agent_update_operations" })
                {
                    if (!tables.Contains(table))
                        throw new InvalidDataException($"The database is missing the version 12 {table} table.");
                    if (table != "agent_update_operations" || userVersion < 14)
                        ValidateColumns(connection, table, requiredColumns[table], requiredPrimaryKeys[table]);
                }
                if (userVersion < 14)
                {
                    ValidateIndex(connection, "agent_update_operations", "agent_update_one_active_per_machine", ["machine_id"], "status NOT IN ('completed','rolledBack','expired')", required: true);
                    ValidateLegacyMaintenanceState(connection, userVersion);
                }
            }
            if (userVersion >= 13 && userVersion < 14)
                ValidateColumns(connection, "agent_update_operations", [
                    new("operation_id", "TEXT"), new("machine_id", "TEXT", true), new("release_version", "TEXT", true),
                    new("target_version", "TEXT", true), new("target_package_sha256", "TEXT", true), new("previous_version", "TEXT", true),
                    new("status", "TEXT", true), new("ticket_hash", "TEXT", true), new("ticket_expires_at_unix_ms", "INTEGER", true),
                    new("ticket_started_at_unix_ms", "INTEGER"), new("started_at_unix_ms", "INTEGER"),
                    new("created_at_unix_ms", "INTEGER", true), new("updated_at_unix_ms", "INTEGER", true),
                    new("ticket_package_downloaded_at_unix_ms", "INTEGER")
                ], ["operation_id"]);
            if (userVersion >= 14) ValidateMaintenanceChainSchema(connection);
            if (userVersion >= 15) ValidateSyncProofSchema(connection);
            if (userVersion >= 19) ValidateRestoreStateSchema(connection);
            // These uniqueness constraints enforce allocation and command-revision invariants.
            ValidateIndex(connection, "leases", "leases_machine_allocation", ["machine_id"], "allocation_held=1", required: true);
            var hasCommandRevisionIndex = HasIndex(connection, "command_outbox", "lease_command_revision");
            if (userVersion >= 3 || hasCommandRevisionIndex)
                ValidateIndex(connection, "command_outbox", "lease_command_revision", ["rental_id", "type", "lease_revision"], "type!='PairPin'", required: true);
            else
                // Before pairing, this same uniqueness rule is a table-level UNIQUE constraint.
                ValidateLegacyCommandRevisionUniqueness(connection);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The file is not a valid Controller database.", exception);
        }
    }

    private static readonly Lazy<IReadOnlyDictionary<string, string>> maintenanceSchema = new(() =>
    {
        using var reference = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        reference.Open();
        using var transaction = reference.BeginTransaction();
        var assembly = typeof(ControllerDatabase).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("KamenRaider.Controller.Data.Migrations.", StringComparison.Ordinal)
                && int.TryParse(name.AsSpan("KamenRaider.Controller.Data.Migrations.".Length, 3), out var version) && version <= 14)
            .Order(StringComparer.Ordinal))
        {
            using var resource = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(resource);
            using var migration = reference.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = reader.ReadToEnd();
            migration.ExecuteNonQuery();
        }
        transaction.Commit();
        var names = new[] { "agent_maintenance_chains", "agent_update_operations", "agent_update_audit",
            "agent_maintenance_one_held_per_machine", "agent_update_unstarted_expiry", "agent_update_audit_immutable_update",
            "agent_update_audit_immutable_delete", "agent_update_operation_identity_immutable", "agent_update_operation_provenance_immutable",
            "agent_update_start_immutable", "agent_update_superseded_insert_guard",
            "agent_maintenance_chain_identity_immutable", "agent_maintenance_generation_guard", "agent_maintenance_close_guard", "agent_update_supersession_guard" };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = reference.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name=$name";
        var parameter = command.Parameters.Add("$name", SqliteType.Text);
        foreach (var name in names)
        {
            parameter.Value = name;
            result.Add(name, NormalizeCanonicalSql((string)command.ExecuteScalar()!));
        }
        return result;
    });

    internal static void ValidateMaintenanceChainSchema(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name=$name";
        var parameter = command.Parameters.Add("$name", SqliteType.Text);
        foreach (var (name, expected) in maintenanceSchema.Value)
        {
            parameter.Value = name;
            if (command.ExecuteScalar() is not string actual || NormalizeCanonicalSql(actual) != expected)
                throw new InvalidDataException($"Controller database has an invalid maintenance-chain definition '{name}'.");
        }
        ValidateMaintenanceState(connection, transaction);
    }

    private static readonly Lazy<IReadOnlyDictionary<string, string>> syncProofSchema = new(() =>
    {
        using var reference = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        reference.Open();
        using var transaction = reference.BeginTransaction();
        var assembly = typeof(ControllerDatabase).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("KamenRaider.Controller.Data.Migrations.", StringComparison.Ordinal)
                && int.TryParse(name.AsSpan("KamenRaider.Controller.Data.Migrations.".Length, 3), out var version) && version <= 16)
            .Order(StringComparer.Ordinal))
        {
            using var resource = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(resource);
            using var migration = reference.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = reader.ReadToEnd();
            migration.ExecuteNonQuery();
        }
        transaction.Commit();
        var names = new[] { "agent_sync_proof_state", "agent_signed_request_scopes", "agent_signed_request_nonces",
            "agent_signed_request_nonce_pruning", "agent_update_evidence_challenges", "agent_update_evidence_nonce",
            "agent_update_reports", "agent_update_reports_immutable_update", "agent_update_reports_immutable_delete" };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = reference.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name=$name";
        var parameter = command.Parameters.Add("$name", SqliteType.Text);
        foreach (var name in names)
        {
            parameter.Value = name;
            result.Add(name, NormalizeCanonicalSql((string)command.ExecuteScalar()!));
        }
        return result;
    });

    private static readonly Lazy<IReadOnlyDictionary<string, string>> syncProofSchemaV18 = new(() =>
    {
        using var reference = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        reference.Open();
        using var transaction = reference.BeginTransaction();
        var assembly = typeof(ControllerDatabase).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("KamenRaider.Controller.Data.Migrations.", StringComparison.Ordinal)
                && int.TryParse(name.AsSpan("KamenRaider.Controller.Data.Migrations.".Length, 3), out var version) && version <= 18)
            .Order(StringComparer.Ordinal))
        {
            using var resource = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(resource);
            using var migration = reference.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = reader.ReadToEnd();
            migration.ExecuteNonQuery();
        }
        transaction.Commit();
        var names = new[] { "agent_sync_proof_state", "agent_signed_request_scopes", "agent_signed_request_nonces",
            "agent_signed_request_nonce_pruning", "agent_update_evidence_challenges", "agent_update_evidence_nonce",
            "agent_update_reports", "agent_update_reports_immutable_update", "agent_update_reports_immutable_delete",
            "agent_update_start_requests", "agent_update_start_request_immutable_update", "agent_update_start_request_immutable_delete",
            "agent_update_ticket_runtime_epochs", "agent_update_ticket_runtime_epoch_immutable_update", "agent_update_ticket_runtime_epoch_immutable_delete",
            "agent_update_evidence_challenge_binding", "agent_update_evidence_challenge_request" };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = reference.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name=$name";
        var parameter = command.Parameters.Add("$name", SqliteType.Text);
        foreach (var name in names)
        {
            parameter.Value = name;
            result.Add(name, NormalizeCanonicalSql((string)command.ExecuteScalar()!));
        }
        return result;
    });

    private static readonly Lazy<IReadOnlyDictionary<string, string>> syncProofSchemaV15 = new(() =>
    {
        using var reference = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        reference.Open();
        using var transaction = reference.BeginTransaction();
        var assembly = typeof(ControllerDatabase).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("KamenRaider.Controller.Data.Migrations.", StringComparison.Ordinal)
                && int.TryParse(name.AsSpan("KamenRaider.Controller.Data.Migrations.".Length, 3), out var version) && version <= 15)
            .Order(StringComparer.Ordinal))
        {
            using var resource = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(resource);
            using var migration = reference.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = reader.ReadToEnd();
            migration.ExecuteNonQuery();
        }
        transaction.Commit();
        var names = new[] { "agent_sync_proof_state", "agent_signed_request_scopes", "agent_signed_request_nonces",
            "agent_signed_request_nonce_pruning", "agent_update_evidence_challenges", "agent_update_evidence_nonce",
            "agent_update_reports", "agent_update_reports_immutable_update", "agent_update_reports_immutable_delete" };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = reference.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name=$name";
        var parameter = command.Parameters.Add("$name", SqliteType.Text);
        foreach (var name in names)
        {
            parameter.Value = name;
            result.Add(name, NormalizeCanonicalSql((string)command.ExecuteScalar()!));
        }
        return result;
    });

    internal static void ValidateSyncProofSchema(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        long userVersion;
        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "PRAGMA user_version";
            userVersion = Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        var expectedSchema = userVersion >= 18 ? syncProofSchemaV18.Value
            : userVersion >= 16 ? syncProofSchema.Value
            : syncProofSchemaV15.Value;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name=$name";
        var parameter = command.Parameters.Add("$name", SqliteType.Text);
        foreach (var (name, expected) in expectedSchema)
        {
            parameter.Value = name;
            if (command.ExecuteScalar() is not string actual || NormalizeCanonicalSql(actual) != expected)
                throw new InvalidDataException($"Controller database has an invalid Agent proof definition '{name}'.");
        }
        ValidateSyncProofState(connection, transaction, userVersion >= 16, userVersion >= 18);
    }

    private static void ValidateSyncProofState(SqliteConnection connection, SqliteTransaction? transaction, bool hasChallengeRateLimit, bool hasSourceBindingSchema)
    {
        using var integrity = connection.CreateCommand();
        integrity.Transaction = transaction;
        var rateLimitIntegrity = hasChallengeRateLimit
            ? "EXISTS(SELECT 1 FROM agent_sync_proof_state WHERE challenge_response_count<0 OR challenge_response_count>2 OR ((challenge_response_count=0)!=(challenge_window_tick IS NULL))) OR "
            : "";
        var nonceRetentionIntegrity = hasChallengeRateLimit ? "n.retention_second" : "n.signed_second";
        var sourceBindingIntegrity = hasSourceBindingSchema
            ? "EXISTS(SELECT 1 FROM agent_update_start_requests WHERE length(body_digest)!=64 OR body_digest GLOB '*[^0-9A-Fa-f]*') OR EXISTS(SELECT 1 FROM agent_update_start_requests r LEFT JOIN agent_update_ticket_runtime_epochs e USING(operation_id) WHERE e.operation_id IS NULL) OR EXISTS(SELECT 1 FROM agent_update_ticket_runtime_epochs WHERE length(controller_runtime_epoch)!=32 OR controller_runtime_epoch GLOB '*[^0-9a-f]*' OR issued_monotonic_tick<0) OR EXISTS(SELECT 1 FROM agent_update_evidence_challenges WHERE (challenge_binding_digest IS NULL)!=(challenge_request_id IS NULL) OR (challenge_binding_digest IS NOT NULL AND (length(challenge_binding_digest)!=64 OR challenge_binding_digest GLOB '*[^0-9A-Fa-f]*' OR length(challenge_request_id)<1 OR length(challenge_request_id)>128 OR challenge_issued_at_unix_ms IS NULL OR challenge_issued_monotonic_tick IS NULL OR challenge_issued_monotonic_tick<0 OR length(challenge_runtime_epoch)!=32 OR challenge_runtime_epoch GLOB '*[^0-9a-f]*')) OR (challenge_issued_at_unix_ms IS NULL)!=(challenge_issued_monotonic_tick IS NULL) OR (challenge_issued_monotonic_tick IS NULL)!=(challenge_runtime_epoch IS NULL)) OR "
            : "";
        integrity.CommandText = "SELECT " + rateLimitIntegrity +
            sourceBindingIntegrity +
            "EXISTS(SELECT machine_id FROM machines EXCEPT SELECT machine_id FROM agent_sync_proof_state) OR " +
            "EXISTS(SELECT machine_id FROM agent_sync_proof_state EXCEPT SELECT machine_id FROM machines) OR " +
            "EXISTS(SELECT 1 FROM agent_sync_proof_state WHERE sequence<0 OR (token_sequence IS NOT NULL AND (token_sequence>sequence OR token_sequence<=COALESCE(last_proven_sequence,0))) OR (last_proven_sequence IS NOT NULL AND (last_proven_sequence<0 OR last_proven_sequence>sequence)) OR (key_fingerprint IS NOT NULL AND (length(key_fingerprint)!=64 OR key_fingerprint GLOB '*[^0-9A-Fa-f]*')) OR (token_digest IS NOT NULL AND (length(token_digest)!=64 OR token_digest GLOB '*[^0-9A-Fa-f]*'))) OR " +
            $"EXISTS(SELECT 1 FROM agent_signed_request_nonces n JOIN agent_signed_request_scopes s USING(machine_id,key_fingerprint,path) WHERE {nonceRetentionIntegrity}<s.cutoff_second) OR " +
            "EXISTS(SELECT 1 FROM agent_update_evidence_challenges WHERE length(key_fingerprint)!=64 OR key_fingerprint GLOB '*[^0-9A-Fa-f]*' OR length(nonce_digest)!=64 OR nonce_digest GLOB '*[^0-9A-Fa-f]*')";
        if (Convert.ToInt64(integrity.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidDataException("Controller database has inconsistent Agent proof, replay or evidence state.");
    }

    private static void ValidateLegacyMaintenanceState(SqliteConnection connection, long version)
    {
        using var conflict = connection.CreateCommand();
        // Ticket start and download markers also record work, even if a legacy start timestamp is missing.
        var downloadEvidence = version >= 13 ? " OR o.ticket_package_downloaded_at_unix_ms IS NOT NULL" : "";
        conflict.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_update_operations o JOIN leases l ON l.machine_id=o.machine_id WHERE l.allocation_held=1 AND o.status NOT IN ('completed','rolledBack','expired')) OR EXISTS(SELECT 1 FROM agent_update_operations o WHERE o.status='expired' AND (o.started_at_unix_ms IS NOT NULL OR o.ticket_started_at_unix_ms IS NOT NULL" + downloadEvidence + "))";
        if (Convert.ToInt64(conflict.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidDataException("Controller database contains an inconsistent legacy Agent update or a conflicting held lease.");
    }

    internal static void ValidateMaintenanceState(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var conflict = connection.CreateCommand();
        conflict.Transaction = transaction;
        conflict.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM agent_maintenance_chains c
                LEFT JOIN agent_update_operations h ON h.operation_id=c.current_head_operation_id AND h.chain_id=c.chain_id AND h.machine_id=c.machine_id
                WHERE h.operation_id IS NULL OR h.status='superseded' OR h.ticket_generation < 0 OR h.ticket_generation > c.generation
                   OR (c.held=0 AND h.status NOT IN ('completed','rolledBack','expired'))
                   OR (h.status='expired' AND EXISTS(SELECT 1 FROM agent_update_operations started WHERE started.chain_id=c.chain_id AND (started.started_at_unix_ms IS NOT NULL OR started.ticket_started_at_unix_ms IS NOT NULL OR started.ticket_package_downloaded_at_unix_ms IS NOT NULL)))
                   OR (c.held=1 AND EXISTS(SELECT 1 FROM leases l WHERE l.machine_id=c.machine_id AND l.allocation_held=1))
                   OR EXISTS(SELECT 1 FROM agent_update_operations o WHERE o.chain_id=c.chain_id AND o.operation_id<>c.current_head_operation_id AND o.status NOT IN ('completed','rolledBack','expired','superseded'))
            ) OR EXISTS(
                SELECT 1 FROM agent_update_operations o LEFT JOIN agent_maintenance_chains c ON c.chain_id=o.chain_id AND c.machine_id=o.machine_id WHERE c.chain_id IS NULL
            )
            """;
        if (Convert.ToInt64(conflict.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidDataException("Controller database contains an inconsistent Agent maintenance chain or a conflicting held lease.");
    }

    internal static void ValidateRestoreStateSchema(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        ValidateCanonicalSchema(connection, 19, transaction);
        ValidateColumns(connection, "controller_restore_state", [
            new("singleton", "INTEGER"), new("quarantine_active", "INTEGER", true),
            new("quarantine_reason", "TEXT", true), new("quarantine_epoch", "INTEGER", true),
            new("database_epoch", "INTEGER", true),
            new("quarantined_at_unix_ms", "INTEGER", true), new("updated_at_unix_ms", "INTEGER", true)
        ], ["singleton"]);

        using var integrity = connection.CreateCommand();
        integrity.Transaction = transaction;
        integrity.CommandText = "SELECT COUNT(*) FROM controller_restore_state WHERE singleton<>1 OR quarantine_active NOT IN (0,1) OR length(trim(quarantine_reason)) NOT BETWEEN 1 AND 128 OR quarantine_epoch<0 OR database_epoch<0 OR quarantined_at_unix_ms<0 OR updated_at_unix_ms<0";
        if (Convert.ToInt64(integrity.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidDataException("Controller database has an invalid restore quarantine marker.");
        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM controller_restore_state";
        if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new InvalidDataException("Controller database must contain exactly one restore quarantine marker.");

    }

    internal static void ValidateCanonicalSchema(SqliteConnection connection, long expectedVersion, SqliteTransaction? transaction = null)
    {
        if (expectedVersion is < 0 or > 19)
            throw new InvalidDataException("Controller database version is outside the supported migration range.");

        var checkpoints = expectedSchemas.GetOrAdd(expectedVersion, BuildCanonicalSchemas);
        var actual = ReadSchemaObjects(connection, transaction);
        if (checkpoints.Any(expected => SchemaMatches(actual, expected))) return;
        if (expectedVersion is 0 or 2 && checkpoints.Any(expected => MatchesKnownLegacyUnfilteredCommandRevisionSchema(connection, actual, expected))) return;
        var difference = checkpoints.Select(expected => DescribeSchemaDifference(actual, expected)).FirstOrDefault(value => value is not null);
        throw new InvalidDataException($"Controller database schema does not match canonical migration version {expectedVersion}.{(difference is null ? string.Empty : " " + difference)}");
    }

    private static string? DescribeSchemaDifference(IReadOnlyDictionary<string, SchemaObject> actual, IReadOnlyDictionary<string, SchemaObject> expected)
    {
        foreach (var pair in expected)
        {
            if (!actual.TryGetValue(pair.Key, out var observed)) return $"Missing schema object '{pair.Key.Replace('\0', '/')}'.";
            if (!string.Equals(observed.Type, pair.Value.Type, StringComparison.Ordinal)
                || !string.Equals(observed.TableName, pair.Value.TableName, StringComparison.Ordinal)
                || !string.Equals(observed.Sql, pair.Value.Sql, StringComparison.Ordinal))
                return $"Schema object '{pair.Key.Replace('\0', '/')}' differs from its canonical definition.";
        }
        var extra = actual.Keys.FirstOrDefault(key => !expected.ContainsKey(key));
        return extra is null ? null : $"Unexpected schema object '{extra.Replace('\0', '/')}'.";
    }

    private static bool SchemaMatches(IReadOnlyDictionary<string, SchemaObject> actual, IReadOnlyDictionary<string, SchemaObject> expected)
        => actual.Count == expected.Count && !expected.Any(pair => !actual.TryGetValue(pair.Key, out var schema)
            || !string.Equals(schema.Type, pair.Value.Type, StringComparison.Ordinal)
            || !string.Equals(schema.TableName, pair.Value.TableName, StringComparison.Ordinal)
            || !string.Equals(schema.Sql, pair.Value.Sql, StringComparison.Ordinal));

    private static bool MatchesKnownLegacyUnfilteredCommandRevisionSchema(SqliteConnection connection,
        IReadOnlyDictionary<string, SchemaObject> actual, IReadOnlyDictionary<string, SchemaObject> expected)
    {
        const string commandTableName = "command_outbox";
        var tableKey = "table\0" + commandTableName;
        if (!actual.TryGetValue(tableKey, out var actualTable) || !expected.TryGetValue(tableKey, out var expectedTable)
            || actualTable.Sql is null || expectedTable.Sql is null)
            return false;

        const string tableUniqueClause = ", UNIQUE(rental_id,type,lease_revision)";
        if (!string.Equals(actualTable.Sql, expectedTable.Sql.Replace(tableUniqueClause, "", StringComparison.OrdinalIgnoreCase), StringComparison.Ordinal))
            return false;

        var actualWithoutCommandIndexes = actual.Where(pair => pair.Value.Type != "index"
            || !string.Equals(pair.Value.TableName, commandTableName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var expectedWithoutCommandIndexes = expected.Where(pair => pair.Value.Type != "index"
            || !string.Equals(pair.Value.TableName, commandTableName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (!SchemaMatches(actualWithoutCommandIndexes, expectedWithoutCommandIndexes)) return false;

        var uniqueIndexes = new List<(string Name, string Origin, bool Partial)>();
        using (var indexes = connection.CreateCommand())
        {
            indexes.CommandText = "PRAGMA index_list('command_outbox')";
            using var rows = indexes.ExecuteReader();
            while (rows.Read())
            {
                if (rows.GetInt32(2) != 1) return false;
                uniqueIndexes.Add((rows.GetString(1), rows.GetString(3), rows.GetInt32(4) != 0));
            }
        }

        if (uniqueIndexes.Count != 2 || uniqueIndexes.Count(index => index.Origin == "pk" && !index.Partial) != 1
            || uniqueIndexes.Count(index => index.Origin == "c" && !index.Partial) != 1)
            return false;
        var userIndex = uniqueIndexes.Single(index => index.Origin == "c");
        var indexName = userIndex.Name.Replace("'", "''", StringComparison.Ordinal);
        using var columns = connection.CreateCommand();
        columns.CommandText = $"PRAGMA index_info('{indexName}')";
        using var columnRows = columns.ExecuteReader();
        var indexedColumns = new List<string>();
        while (columnRows.Read())
        {
            if (columnRows.IsDBNull(2)) return false;
            indexedColumns.Add(columnRows.GetString(2));
        }
        return indexedColumns.SequenceEqual(["rental_id", "type", "lease_revision"], StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, SchemaObject>> BuildCanonicalSchemas(long version)
    {
        if (version is not (0 or 2 or 3 or >= 4 and <= 19))
            return [];

        var connectionString = new SqliteConnectionStringBuilder { DataSource = ":memory:", ForeignKeys = true, Pooling = false }.ToString();
        using var canonical = new SqliteConnection(connectionString);
        canonical.Open();
        var checkpoints = new List<IReadOnlyDictionary<string, SchemaObject>>();
        if (version == 0) checkpoints.Add(ReadSchemaObjects(canonical, transaction: null));
        var assembly = typeof(ControllerDatabase).Assembly;
        var prefix = "KamenRaider.Controller.Data.Migrations.";
        var lastMigrationNumber = version switch { 0 or 2 => 2, 3 => 3, 4 => 9, _ => checked((int)version) };
        foreach (var resourceName in assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal))
        {
            var fileName = resourceName[prefix.Length..^4];
            if (fileName.Length < 3 || !int.TryParse(fileName.AsSpan(0, 3), out var migrationVersion) || migrationVersion > lastMigrationNumber)
                continue;
            using var resource = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException("A Controller migration resource could not be loaded.");
            using var reader = new StreamReader(resource);
            using var migration = canonical.CreateCommand();
            migration.CommandText = reader.ReadToEnd();
            migration.ExecuteNonQuery();

            var isCheckpoint = version switch
            {
                0 => migrationVersion is 1 or 2,
                2 => migrationVersion == 2,
                3 => migrationVersion == 3,
                4 => migrationVersion is >= 4 and <= 9,
                _ => migrationVersion == version,
            };
            if (isCheckpoint) checkpoints.Add(ReadSchemaObjects(canonical, transaction: null));
        }

        return checkpoints;
    }

    private static Dictionary<string, SchemaObject> ReadSchemaObjects(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var schemaObjects = new Dictionary<string, SchemaObject>(StringComparer.Ordinal);
        using (var triggers = connection.CreateCommand())
        {
            triggers.Transaction = transaction;
            triggers.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_schema";
            using var rows = triggers.ExecuteReader();
            while (rows.Read())
            {
                var type = rows.GetString(0);
                var name = rows.GetString(1);
                var tableName = rows.GetString(2);
                var sql = rows.IsDBNull(3) ? null : NormalizeCanonicalSql(rows.GetString(3));
                if (!schemaObjects.TryAdd(type + "\0" + name, new(type, name, tableName, sql)))
                    throw new InvalidDataException("Controller database contains duplicate schema objects.");
            }
        }

        return schemaObjects;
    }

    // sqlite_schema preserves the canonical migration SQL. Compare it verbatim so comments,
    // quoting and SQLite's ASCII tokenizer semantics cannot be changed by Unicode normalization.
    private static string NormalizeCanonicalSql(string sql) => sql;

    private static void ValidateColumns(SqliteConnection connection, string table, RequiredColumn[] expectedColumns, string[] expectedPrimaryKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var rows = command.ExecuteReader();
        var columns = new Dictionary<string, (string Type, bool NotNull)>(StringComparer.OrdinalIgnoreCase);
        var primaryKey = new SortedDictionary<int, string>();
        while (rows.Read())
        {
            var name = rows.GetString(1);
            columns[name] = (rows.GetString(2), rows.GetInt32(3) != 0);
            var primaryKeyOrder = rows.GetInt32(5);
            if (primaryKeyOrder > 0) primaryKey.Add(primaryKeyOrder, name);
        }

        if (expectedColumns.Any(column => !columns.ContainsKey(column.Name)))
            throw new InvalidDataException($"Controller database table '{table}' is missing required columns.");
        foreach (var expected in expectedColumns)
        {
            var actual = columns[expected.Name];
            if (!string.Equals(actual.Type.Trim(), expected.Type, StringComparison.OrdinalIgnoreCase) || (expected.NotNull && !actual.NotNull))
                throw new InvalidDataException($"Controller database table '{table}' has an invalid required column definition.");
        }
        if (!primaryKey.Values.SequenceEqual(expectedPrimaryKey, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"Controller database table '{table}' has an invalid primary key.");
    }

    private static bool HasIndex(SqliteConnection connection, string table, string name)
    {
        using var indexes = connection.CreateCommand();
        indexes.CommandText = $"PRAGMA index_list('{table}')";
        using var rows = indexes.ExecuteReader();
        while (rows.Read())
            if (string.Equals(rows.GetString(1), name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static void ValidateLegacyCommandRevisionUniqueness(SqliteConnection connection)
    {
        var candidateIndexes = new List<string>();
        using var indexes = connection.CreateCommand();
        indexes.CommandText = "PRAGMA index_list('command_outbox')";
        using (var rows = indexes.ExecuteReader())
        {
            while (rows.Read())
                if (rows.GetInt32(2) != 0 && rows.GetInt32(4) == 0 && string.Equals(rows.GetString(3), "u", StringComparison.OrdinalIgnoreCase))
                    candidateIndexes.Add(rows.GetString(1));
        }

        foreach (var candidate in candidateIndexes)
        {
            var indexName = candidate.Replace("'", "''", StringComparison.Ordinal);
            using var info = connection.CreateCommand();
            info.CommandText = $"PRAGMA index_info('{indexName}')";
            using var columns = info.ExecuteReader();
            var actualColumns = new List<string>();
            var hasExpressionTerm = false;
            while (columns.Read())
            {
                if (columns.IsDBNull(2))
                {
                    hasExpressionTerm = true;
                    break;
                }
                actualColumns.Add(columns.GetString(2));
            }
            if (!hasExpressionTerm && actualColumns.SequenceEqual(["rental_id", "type", "lease_revision"], StringComparer.OrdinalIgnoreCase)) return;
        }
        throw new InvalidDataException("Controller database is missing command-revision uniqueness.");
    }

    private static void ValidateIndex(SqliteConnection connection, string table, string name, string[] expectedColumns, string predicate, bool required)
    {
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";
            ValidateCanonicalSchema(connection, Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture));
        }
        var found = false;
        var isUnique = false;
        var isPartial = false;
        using (var indexes = connection.CreateCommand())
        {
            indexes.CommandText = $"PRAGMA index_list('{table}')";
            using var rows = indexes.ExecuteReader();
            while (rows.Read())
            {
                if (!string.Equals(rows.GetString(1), name, StringComparison.OrdinalIgnoreCase)) continue;
                found = true;
                isUnique = rows.GetInt32(2) != 0;
                isPartial = rows.GetInt32(4) != 0;
                break;
            }
        }

        if (!found)
        {
            if (required) throw new InvalidDataException($"Controller database is missing required index '{name}'.");
            return;
        }
        if (!isUnique || !isPartial)
            throw new InvalidDataException($"Controller database index '{name}' must be unique and partial.");

        var columns = new List<string>();
        using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA index_info('{name}')";
            using var rows = info.ExecuteReader();
            while (rows.Read())
            {
                if (rows.IsDBNull(2)) throw new InvalidDataException($"Controller database index '{name}' cannot contain expression columns.");
                columns.Add(rows.GetString(2));
            }
        }
        if (!columns.SequenceEqual(expectedColumns, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"Controller database index '{name}' has invalid columns.");

        using var definition = connection.CreateCommand();
        definition.CommandText = "SELECT sql FROM sqlite_schema WHERE type='index' AND name=$name";
        definition.Parameters.AddWithValue("$name", name);
        var sql = definition.ExecuteScalar()?.ToString();
        if (sql is null)
            throw new InvalidDataException($"Controller database index '{name}' has an invalid filter.");
        // The versioned full-schema comparison above validates the exact canonical index SQL.
    }

    private static void CheckpointWal(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, ForeignKeys = true, Pooling = false }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var modeCommand = connection.CreateCommand();
        modeCommand.CommandText = "PRAGMA journal_mode";
        if (!string.Equals(modeCommand.ExecuteScalar()?.ToString(), "wal", StringComparison.OrdinalIgnoreCase)) return;

        using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var rows = checkpoint.ExecuteReader();
        if (!rows.Read() || rows.GetInt32(0) != 0) throw new IOException("Controller database WAL checkpoint did not complete.");
    }

    private static void PreserveRawDatabaseFiles(string source, string recoveryPath)
    {
        File.Copy(source, recoveryPath, overwrite: false);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = source + suffix;
            if (File.Exists(sidecar)) File.Copy(sidecar, recoveryPath + suffix, overwrite: false);
        }
    }

    private static string NewTemporaryPath(string destination) => Path.Combine(Path.GetDirectoryName(destination)!, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");

    private static string NewRecoveryPath(string destination) => destination + ".pre-restore-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".db";

    private static void Publish(string staged, string destination)
    {
        if (File.Exists(destination)) File.Replace(staged, destination, null);
        else File.Move(staged, destination);
    }

    private static void DeleteSqliteSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = path + suffix;
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }
    }

    private static bool HasOrphanSqliteSidecars(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath)!;
        var databaseName = Path.GetFileName(databasePath);
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).Any(entry =>
            {
                var name = Path.GetFileName(entry);
                return name.Equals(databaseName + "-wal", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(databaseName + "-shm", StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The Controller restore destination directory could not be checked for orphan SQLite sidecars.", exception);
        }
    }

    private static void EnsureDifferentFiles(string first, string second)
    {
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Source and destination database paths must differ.");
    }
}
