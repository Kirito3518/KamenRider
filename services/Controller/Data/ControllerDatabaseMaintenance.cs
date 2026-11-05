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

    public static string Backup(string databasePath, string backupPath)
    {
        var source = ControllerDatabaseActivityLock.Normalize(databasePath, nameof(databasePath));
        var destination = ControllerDatabaseActivityLock.Normalize(backupPath, nameof(backupPath));
        EnsureDifferentFiles(source, destination);
        if (!File.Exists(source)) throw new FileNotFoundException("Controller database was not found.", source);
        if (File.Exists(destination)) throw new IOException("Backup destination already exists; choose a new backup path.");

        using var operationLock = ControllerDatabaseFileLocks.Acquire(source, ".maintenance.lock", "Another database maintenance operation is already running.");
        return WriteVerifiedBackup(source, destination, overwriteDestination: false);
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
        try
        {
            try { CopyDatabase(source, staged); }
            catch (SqliteException exception) { throw new InvalidDataException("The backup file could not be read.", exception); }
            ValidateDatabase(staged);

            string? recoveryPath = null;
            if (File.Exists(destination))
            {
                recoveryPath = NewRecoveryPath(destination);
                PreserveRawDatabaseFiles(destination, recoveryPath);
                var verifiedRecoveryStage = NewTemporaryPath(recoveryPath);
                var recoveryDatabaseValidated = false;
                try
                {
                    _ = WriteVerifiedBackup(destination, verifiedRecoveryStage);
                    Publish(verifiedRecoveryStage, recoveryPath);
                    DeleteSqliteSidecars(recoveryPath);
                    recoveryDatabaseValidated = true;
                }
                catch (InvalidDataException)
                {
                }
                finally
                {
                    DeleteSqliteSidecars(verifiedRecoveryStage);
                    if (File.Exists(verifiedRecoveryStage)) File.Delete(verifiedRecoveryStage);
                }

                if (recoveryDatabaseValidated) CheckpointWal(destination);
                DeleteSqliteSidecars(destination);
            }

            Publish(staged, destination);
            return recoveryPath;
        }
        finally
        {
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
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The file is not a valid Controller database.", exception);
        }
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
