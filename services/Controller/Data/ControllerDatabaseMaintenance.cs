using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using System.Globalization;

namespace KamenRaider.Controller.Data;

public sealed class ControllerDatabaseActivityLock(string databasePath) : IHostedService, IDisposable
{
    private readonly string fullDatabasePath = Normalize(databasePath, nameof(databasePath));
    private FileStream? activeLock;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (activeLock is not null) return Task.CompletedTask;

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
        activeLock?.Dispose();
        activeLock = null;
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
                Directory.Move(stagedArtifacts, artifactDestination);
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

    public static string? Restore(string backupPath, string databasePath)
    {
        var source = ControllerDatabaseActivityLock.Normalize(backupPath, nameof(backupPath));
        var destination = ControllerDatabaseActivityLock.Normalize(databasePath, nameof(databasePath));
        EnsureDifferentFiles(source, destination);
        if (!File.Exists(source)) throw new FileNotFoundException("Controller backup was not found.", source);

        using var operationLock = ControllerDatabaseFileLocks.Acquire(destination, ".maintenance.lock", "Another database maintenance operation is already running.");
        using (ControllerDatabaseFileLocks.Acquire(destination, ".active.lock", "Stop the Controller before restoring its database.")) { }

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
            ValidateDatabase(staged);

            string? recoveryPath = null;
            var recoveryDatabaseValidated = false;
            if (File.Exists(destination))
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

            ControllerReleaseArtifacts.Copy(artifacts, source + ".agent-releases", artifactRoot, requireOriginalPath: false, installedArtifacts);
            if (File.Exists(destination))
            {
                if (recoveryDatabaseValidated) CheckpointWal(destination);
                DeleteSqliteSidecars(destination);
            }
            Publish(staged, destination);
            restorePublished = true;
            return recoveryPath;
        }
        finally
        {
            if (!restorePublished) foreach (var installed in installedArtifacts) if (File.Exists(installed)) File.Delete(installed);
            DeleteSqliteSidecars(staged);
            if (File.Exists(staged)) File.Delete(staged);
        }
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
            result.Add(name, NormalizeSql((string)command.ExecuteScalar()!));
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
            if (command.ExecuteScalar() is not string actual || NormalizeSql(actual) != expected)
                throw new InvalidDataException($"Controller database has an invalid maintenance-chain definition '{name}'.");
        }
        ValidateMaintenanceState(connection, transaction);
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

        var normalizedDefinition = NormalizeSql(sql);
        var expectedClause = NormalizeSql($"ON {table}({string.Join(',', expectedColumns)}) WHERE {predicate}");
        var clauseStart = normalizedDefinition.LastIndexOf(NormalizeSql($"ON {table}("), StringComparison.Ordinal);
        if (clauseStart < 0 || !string.Equals(normalizedDefinition[clauseStart..], expectedClause, StringComparison.Ordinal))
            throw new InvalidDataException($"Controller database index '{name}' has an invalid filter.");
    }

    private static string NormalizeSql(string sql)
    {
        var result = new System.Text.StringBuilder(sql.Length);
        var literal = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var character = sql[i];
            if (literal)
            {
                result.Append(character);
                if (character == '\'')
                {
                    if (i + 1 < sql.Length && sql[i + 1] == '\'') result.Append(sql[++i]);
                    else literal = false;
                }
            }
            else if (character == '\'') { literal = true; result.Append(character); }
            else if (!char.IsWhiteSpace(character) && character != '"' && character != '`' && character != '[' && character != ']')
                result.Append(char.ToLowerInvariant(character));
        }
        return result.ToString();
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

    private static void EnsureDifferentFiles(string first, string second)
    {
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Source and destination database paths must differ.");
    }
}
