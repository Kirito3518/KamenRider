using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace KamenRaider.Controller.Data;

// Release bytes are immutable, so a verified SQLite snapshot defines the exact artifact set.
internal static class ControllerReleaseArtifacts
{
    internal sealed record Artifact(string Version, string Hash, string OriginalPath, long Size);
    private const long MaximumSize = 100L * 1024 * 1024;

    internal static List<Artifact> Read(string database)
    {
        using var connection = Open(database, SqliteOpenMode.ReadOnly);
        using var table = connection.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='agent_releases'";
        if (Convert.ToInt64(table.ExecuteScalar()) == 0) return [];
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT version,package_sha256,package_path,package_size_bytes FROM agent_releases ORDER BY version";
        using var rows = query.ExecuteReader();
        var result = new List<Artifact>();
        while (rows.Read())
        {
            var hash = rows.GetString(1);
            var size = rows.GetInt64(3);
            if (!Regex.IsMatch(hash, @"\A[0-9A-Fa-f]{64}\z", RegexOptions.CultureInvariant) || size is <= 0 or > MaximumSize)
                throw new InvalidDataException("Release artifact metadata is invalid.");
            var path = rows.GetString(2);
            if (!Path.IsPathFullyQualified(path) || !HasExpectedSuffix(path, hash))
                throw new InvalidDataException("Release artifact metadata is inconsistent.");
            result.Add(new(rows.GetString(0), hash.ToUpperInvariant(), path, size));
        }
        return result;
    }

    internal static string PathFor(string root, string hash) => Path.Combine(root, "sha256", hash[..2].ToLowerInvariant(), hash.ToLowerInvariant() + ".zip");

    private static bool HasExpectedSuffix(string path, string hash)
    {
        var parent = Path.GetDirectoryName(path);
        var shaDirectory = parent is null ? null : Path.GetDirectoryName(parent);
        var root = shaDirectory is null ? null : Path.GetDirectoryName(shaDirectory);
        return string.Equals(Path.GetFileName(path), hash + ".zip", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(parent), hash[..2], StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(shaDirectory), "sha256", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(root), "agent-releases", StringComparison.OrdinalIgnoreCase);
    }

    internal static void Verify(string path, string hash, long size)
    {
        using var input = OpenVerified(path, hash, size);
    }

    internal static FileStream OpenVerified(string path, string hash, long size)
    {
        FileStream? input = null;
        try
        {
            EnsureRegularPath(path);
            input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || input.Length != size ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(input)), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A release artifact is missing or corrupt.");
            input.Position = 0;
            return input;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            input?.Dispose();
            throw new InvalidDataException("A release artifact is missing or corrupt.", ex);
        }
    }

    internal static void Copy(List<Artifact> artifacts, string sourceRoot, string destinationRoot, bool requireOriginalPath, List<string>? installed = null)
    {
        ValidateCopy(artifacts, sourceRoot, destinationRoot, requireOriginalPath);
        foreach (var artifact in artifacts)
        {
            var destination = PathFor(destinationRoot, artifact.Hash);
            if (File.Exists(destination)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(PathFor(sourceRoot, artifact.Hash), temporary, overwrite: false);
                Verify(temporary, artifact.Hash, artifact.Size);
                File.Move(temporary, destination, overwrite: false);
                installed?.Add(destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal static void ValidateCopy(List<Artifact> artifacts, string sourceRoot, string destinationRoot, bool requireOriginalPath)
    {
        // Validate the entire source set and all existing destinations before publishing any file.
        foreach (var artifact in artifacts)
        {
            var source = PathFor(sourceRoot, artifact.Hash);
            if (requireOriginalPath && !string.Equals(Path.GetFullPath(artifact.OriginalPath), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Release artifact metadata is inconsistent.");
            Verify(source, artifact.Hash, artifact.Size);
            var destination = PathFor(destinationRoot, artifact.Hash);
            EnsureRegularPath(destination);
            if (File.Exists(destination)) Verify(destination, artifact.Hash, artifact.Size);
        }
    }

    private static void EnsureRegularPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (current == fullPath ? (attributes & FileAttributes.Directory) != 0 : (attributes & FileAttributes.Directory) == 0))
                throw new InvalidDataException("Release artifact paths must be regular files in non-redirected directories.");
        }
    }

    internal static void RemoveCreated(List<Artifact> artifacts, string root)
    {
        foreach (var artifact in artifacts)
        {
            var path = PathFor(root, artifact.Hash);
            if (File.Exists(path)) File.Delete(path);
            var prefix = Path.GetDirectoryName(path)!;
            if (Directory.Exists(prefix) && !Directory.EnumerateFileSystemEntries(prefix).Any()) Directory.Delete(prefix);
        }
        var sha = Path.Combine(root, "sha256");
        if (Directory.Exists(sha) && !Directory.EnumerateFileSystemEntries(sha).Any()) Directory.Delete(sha);
        if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
    }

    internal static void RebasePaths(string database, List<Artifact> artifacts, string destinationRoot)
    {
        if (artifacts.Count == 0) return;
        using var connection = Open(database, SqliteOpenMode.ReadWrite);
        using var tx = connection.BeginTransaction();
        using (var trigger = connection.CreateCommand())
        {
            trigger.Transaction = tx;
            trigger.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='trigger' AND name='agent_releases_immutable_update' AND tbl_name='agent_releases'";
            if (Convert.ToInt64(trigger.ExecuteScalar()) != 1) throw new InvalidDataException("Release immutability guard is missing.");
            trigger.CommandText = "DROP TRIGGER agent_releases_immutable_update";
            trigger.ExecuteNonQuery();
        }
        foreach (var artifact in artifacts)
        {
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE agent_releases SET package_path=$path WHERE version=$version";
            update.Parameters.AddWithValue("$path", PathFor(destinationRoot, artifact.Hash));
            update.Parameters.AddWithValue("$version", artifact.Version);
            if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("Release metadata changed during restore.");
        }
        using (var trigger = connection.CreateCommand())
        {
            trigger.Transaction = tx;
            trigger.CommandText = "CREATE TRIGGER agent_releases_immutable_update BEFORE UPDATE ON agent_releases BEGIN SELECT RAISE(ABORT, 'Agent releases are immutable'); END";
            trigger.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
