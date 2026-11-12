using KamenRaider.Contracts;
using KamenRaider.AgentPackage;
using KamenRaider.Controller.Data;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace KamenRaider.Controller.Agents;

public sealed class AgentReleaseValidationException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class AgentReleaseTooLargeException(string message) : Exception(message);
public sealed class AgentReleaseConflictException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Validates and stores complete Agent ZIPs under immutable SHA-256 paths.</summary>
public sealed class AgentReleaseStore
{
    public const long MaximumCompressedBytes = 100L * 1024 * 1024;
    public const long MaximumExpandedBytes = 500L * 1024 * 1024;
    public const int MaximumEntries = 5_000;
    public const int PackageFormatVersion = 1;
    public const int MaximumManifestBytes = 64 * 1024;
    public const int MaximumReleaseNotesLength = 4_000;

    private static readonly Regex SupportedVersion = new(@"\A(?:\d+\.\d+\.\d+|1\.0\.0\+g[0-9a-f]{12,40})\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly string artifactRoot;

    public AgentReleaseStore(string artifactRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);
        if (!Path.IsPathFullyQualified(artifactRoot)) throw new ArgumentException("Agent release storage must use an absolute path", nameof(artifactRoot));
        this.artifactRoot = Path.GetFullPath(artifactRoot);
        Directory.CreateDirectory(this.artifactRoot);
    }

    /// <summary>Stream a multipart file section into a bounded private temporary file.</summary>
    public async Task<StagedAgentPackage> StageAsync(Stream package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        Directory.CreateDirectory(artifactRoot);
        var temporaryPath = Path.Combine(artifactRoot, ".AgentPackage-" + Guid.NewGuid().ToString("N") + ".upload");
        long size = 0;
        try
        {
            await using var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await package.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0) break;
                size += read;
                if (size > MaximumCompressedBytes) throw new AgentReleaseTooLargeException("Agent package exceeds the 100 MiB compressed limit");
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await target.FlushAsync(cancellationToken);
            target.Flush(flushToDisk: true);
            if (size == 0) throw new AgentReleaseValidationException("Agent package is empty");
            return new StagedAgentPackage(temporaryPath, size);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    /// <summary>Validate staged bytes, atomically retain the content-addressed artifact, then publish its DB metadata.</summary>
    public AgentReleaseDto PublishStaged(
        ControllerDatabase database,
        StagedAgentPackage staged,
        string version,
        string channel,
        string releaseNotes,
        string adminActorId)
        => PublishStaged(database, staged, version, channel, releaseNotes, adminActorId, afterArtifactRetained: null);

    internal AgentReleaseDto PublishStaged(
        ControllerDatabase database,
        StagedAgentPackage staged,
        string version,
        string channel,
        string releaseNotes,
        string adminActorId,
        Action? afterArtifactRetained)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(staged);
        if (database.IsRestoreQuarantined())
            throw new AgentReleaseConflictException("Agent release publication is unavailable while the Controller is globally quarantined after restore");
        if (string.IsNullOrWhiteSpace(adminActorId) || adminActorId.Length > 128)
            throw new AgentReleaseValidationException("Authenticated Admin actor metadata is required");
        if (string.IsNullOrWhiteSpace(version) || version.Length > 64 || !SupportedVersion.IsMatch(version))
            throw new AgentReleaseValidationException("Invalid Agent release version");
        if (channel is not ("stable" or "beta")) throw new AgentReleaseValidationException("Channel must be stable or beta");
        if (string.IsNullOrWhiteSpace(releaseNotes) || releaseNotes.Length > MaximumReleaseNotesLength)
            throw new AgentReleaseValidationException("Release notes are required and must not exceed 4,000 characters");

        if (database.ListAgentReleases().Any(release => release.Version == version))
            throw new AgentReleaseConflictException("This Agent version has already been published");

        PackageInventory inventory;
        try
        {
            inventory = ValidatePackage(staged.FilePath, staged.SizeBytes, version);
        }
        catch (AgentReleaseValidationException)
        {
            throw;
        }
        catch (AgentReleaseTooLargeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or BadImageFormatException)
        {
            throw new AgentReleaseValidationException("Agent package is not a valid supported ZIP archive", exception);
        }

        var sha256 = inventory.ArchiveHash.ToUpperInvariant();
        var packageDirectory = Path.Combine(artifactRoot, "sha256", sha256[..2].ToLowerInvariant());
        Directory.CreateDirectory(packageDirectory);
        var packagePath = Path.Combine(packageDirectory, sha256.ToLowerInvariant() + ".zip");
        var createdArtifact = RetainImmutableArtifact(staged.FilePath, packagePath, sha256, staged.SizeBytes);

        try
        {
            afterArtifactRetained?.Invoke();
            return database.PublishAgentRelease(new AgentReleasePublication(
                version, channel, releaseNotes, sha256, packagePath, staged.SizeBytes,
                inventory.SourceCommit, inventory.SourceDirty, inventory.ReleaseBuild,
                inventory.TargetRuntime, inventory.AgentUpdateChallengeProtocol));
        }
        catch (InvalidOperationException exception)
        {
            if (!database.IsRestoreQuarantined()) throw;
            // PublishAgentRelease repeats the quarantine check inside its write transaction.
            // If quarantine won the race after this staged file was retained, remove only our
            // unreferenced artifact; a concurrent successful publication may share its hash.
            if (createdArtifact && !database.ListAgentReleases().Any(release => string.Equals(release.PackageSha256, sha256, StringComparison.OrdinalIgnoreCase)))
                TryDelete(packagePath);
            throw new AgentReleaseConflictException("Agent release publication is unavailable while the Controller is globally quarantined after restore", exception);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new AgentReleaseConflictException("Agent release metadata is immutable and this version is already published", exception);
        }
    }

    /// <summary>
    /// Opens a published artifact by its ticket-bound SHA-256. Call only after the Controller has
    /// authenticated and consumed the operation ticket; the path is derived internally and never returned.
    /// </summary>
    public AgentPackageReadHandle OpenReadByHash(string sha256)
    {
        if (sha256 is null || !Regex.IsMatch(sha256, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid Agent package SHA-256", nameof(sha256));
        var canonicalHash = Convert.ToHexString(Convert.FromHexString(sha256));
        var packagePath = Path.Combine(artifactRoot, "sha256", canonicalHash[..2].ToLowerInvariant(), canonicalHash.ToLowerInvariant() + ".zip");
        var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        try
        {
            if (stream.Length <= 0 || stream.Length > MaximumCompressedBytes)
                throw new InvalidDataException("Stored Agent package has an invalid size");
            var length = stream.Length;
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actualHash, canonicalHash, StringComparison.Ordinal))
                throw new InvalidDataException("Stored Agent package does not match its content address");
            stream.Position = 0;
            return new AgentPackageReadHandle(stream, canonicalHash, length);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal PackageInventory ValidateRetainedForUpdate(string path, long size, string version, string expectedHash)
    {
        var inventory = ValidatePackage(path, size, version);
        if (!string.Equals(inventory.ArchiveHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new AgentReleaseValidationException("Stored Agent package does not match its content address");
        return inventory;
    }

    private PackageInventory ValidatePackage(string path, long compressedSize, string version)
    {
        if (compressedSize <= 0 || compressedSize > MaximumCompressedBytes)
            throw new AgentReleaseTooLargeException("Agent package exceeds the 100 MiB compressed limit");
        if (version.Length > 64 || !SupportedVersion.IsMatch(version))
            throw new AgentReleaseValidationException("Invalid Agent release version");
        if (!Path.IsPathFullyQualified(path))
            throw new AgentReleaseValidationException("Agent package path must be absolute");

        try
        {
            using var package = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            if (package.Length != compressedSize)
                throw new AgentReleaseValidationException("Stored Agent package size does not match its release metadata");
            var inventory = PackageVerifier.ValidateArchive(package, version);
            if (inventory.AgentUpdateChallengeProtocol != 2 || inventory.TargetRuntime is not ("win-x64" or "win-arm64"))
                throw new AgentReleaseValidationException("Agent package does not support update confirmation; publish a compatible release");
            return inventory;
        }
        catch (PackageValidationException exception) when (exception.Category == "limit")
        {
            throw new AgentReleaseTooLargeException(exception.Message);
        }
        catch (PackageValidationException exception)
        {
            throw new AgentReleaseValidationException(exception.Message, exception);
        }
        catch (AgentReleaseValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or BadImageFormatException)
        {
            throw new AgentReleaseValidationException("Agent package is not a valid supported ZIP archive", exception);
        }
    }
    private static bool RetainImmutableArtifact(string stagedPath, string finalPath, string expectedHash, long expectedSize)
    {
        if (File.Exists(finalPath))
        {
            VerifyExistingArtifact(finalPath, expectedHash, expectedSize);
            File.Delete(stagedPath);
            return false;
        }

        try
        {
            File.Move(stagedPath, finalPath, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            VerifyExistingArtifact(finalPath, expectedHash, expectedSize);
            File.Delete(stagedPath);
            return false;
        }
    }

    private static void VerifyExistingArtifact(string path, string expectedHash, long expectedSize)
    {
        var info = new FileInfo(path);
        if (info.Length != expectedSize) throw new InvalidDataException("Content-addressed Agent package path contains different bytes");
        using var stream = info.OpenRead();
        var existingHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(existingHash, expectedHash, StringComparison.Ordinal))
            throw new InvalidDataException("Content-addressed Agent package path contains different bytes");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public sealed class StagedAgentPackage : IDisposable
    {
        internal StagedAgentPackage(string filePath, long sizeBytes)
        {
            FilePath = filePath;
            SizeBytes = sizeBytes;
        }

        internal string FilePath { get; }
        public long SizeBytes { get; }

        public void Dispose() => TryDelete(FilePath);
    }

    public sealed class AgentPackageReadHandle(Stream content, string sha256, long length) : IDisposable
    {
        public Stream Content { get; } = content;
        public string Sha256 { get; } = sha256;
        public long Length { get; } = length;

        public void Dispose() => Content.Dispose();
    }

}
