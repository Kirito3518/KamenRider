using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    private static readonly string[] RequiredFiles =
    [
        "Agent/Agent.exe",
        "Agent/Agent.dll",
        "Agent/Agent.deps.json",
        "Install-AgentPackage.ps1",
        "Install-Service.ps1",
        "Install-TightVncServer.ps1",
        "Prepare-SunshineStreamGate.ps1",
        "PrivateInstallFiles.ps1",
        "ServiceInstallTransaction.ps1",
    ];
    private static readonly Regex SupportedVersion = new(@"\A(?:\d+\.\d+\.\d+|1\.0\.0\+g[0-9a-f]{12,40}(?:\.dirty\.\d{14})?)\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SourceCommit = new(@"\A[0-9a-f]{12,40}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);
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
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(staged);
        if (string.IsNullOrWhiteSpace(adminActorId) || adminActorId.Length > 128)
            throw new AgentReleaseValidationException("Authenticated Admin actor metadata is required");
        if (string.IsNullOrWhiteSpace(version) || version.Length > 64 || !SupportedVersion.IsMatch(version))
            throw new AgentReleaseValidationException("Invalid Agent release version");
        if (channel is not ("stable" or "beta")) throw new AgentReleaseValidationException("Channel must be stable or beta");
        if (string.IsNullOrWhiteSpace(releaseNotes) || releaseNotes.Length > MaximumReleaseNotesLength)
            throw new AgentReleaseValidationException("Release notes are required and must not exceed 4,000 characters");

        if (database.ListAgentReleases().Any(release => release.Version == version))
            throw new AgentReleaseConflictException("This Agent version has already been published");

        string sha256;
        AgentPackageManifest manifest;
        try
        {
            (sha256, manifest) = ValidatePackage(staged.FilePath, staged.SizeBytes, version);
        }
        catch (AgentReleaseValidationException)
        {
            throw;
        }
        catch (AgentReleaseTooLargeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or JsonException or DecoderFallbackException or NotSupportedException)
        {
            throw new AgentReleaseValidationException("Agent package is not a valid supported ZIP archive", exception);
        }

        var packageDirectory = Path.Combine(artifactRoot, "sha256", sha256[..2].ToLowerInvariant());
        Directory.CreateDirectory(packageDirectory);
        var packagePath = Path.Combine(packageDirectory, sha256.ToLowerInvariant() + ".zip");
        RetainImmutableArtifact(staged.FilePath, packagePath, sha256, staged.SizeBytes);

        try
        {
            return database.PublishAgentRelease(new AgentReleasePublication(
                version, channel, releaseNotes, sha256, packagePath, staged.SizeBytes,
                manifest.SourceCommit, manifest.SourceDirty, manifest.ReleaseBuild));
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

    private static (string Sha256, AgentPackageManifest Manifest) ValidatePackage(string path, long compressedSize, string version)
    {
        if (compressedSize <= 0 || compressedSize > MaximumCompressedBytes)
            throw new AgentReleaseTooLargeException("Agent package exceeds the 100 MiB compressed limit");

        using var package = File.OpenRead(path);
        var hashBytes = SHA256.HashData(package);
        var sha256 = Convert.ToHexString(hashBytes);
        package.Position = 0;
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaximumEntries)
            throw new AgentReleaseTooLargeException("Agent package exceeds the 5,000 ZIP entry limit");

        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var pathsWithDescendants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ZipArchiveEntry? manifestEntry = null;
        long declaredExpanded = 0;
        foreach (var entry in archive.Entries)
        {
            var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            var normalized = ValidateEntryPath(entry.FullName, isDirectory);
            if (!paths.TryAdd(normalized, isDirectory))
                throw new AgentReleaseValidationException("Agent package contains duplicate ZIP entry paths");
            RejectPathCollisions(paths, pathsWithDescendants, normalized, isDirectory);
            RejectUnsupportedEntryType(entry);

            if (entry.Length < 0 || entry.Length > MaximumExpandedBytes - declaredExpanded)
                throw new AgentReleaseTooLargeException("Agent package exceeds the 500 MiB declared expanded-size limit");
            declaredExpanded += entry.Length;
            if (isDirectory && entry.Length != 0)
                throw new AgentReleaseValidationException("ZIP directory entries must not contain file data");
            if (entry.FullName.Equals("AgentPackage.manifest.json", StringComparison.Ordinal)) manifestEntry = entry;
            if (entry.FullName.Equals("Agent/appsettings.json", StringComparison.OrdinalIgnoreCase))
                throw new AgentReleaseValidationException("Agent package must not include the protected appsettings.json configuration");
            if (Regex.IsMatch(entry.FullName, @"(?i)(^|/)tightvnc[^/]*\.msi\z", RegexOptions.CultureInvariant))
                throw new AgentReleaseValidationException("The separately licensed TightVNC MSI must not be included in the Agent package");
        }

        foreach (var required in RequiredFiles)
            if (!paths.TryGetValue(required, out var isDirectory) || isDirectory)
                throw new AgentReleaseValidationException($"Agent package is missing required file {required}");
        if (manifestEntry is null) throw new AgentReleaseValidationException("Agent package is missing AgentPackage.manifest.json");
        if (manifestEntry.Length is <= 0 or > MaximumManifestBytes)
            throw new AgentReleaseValidationException("Agent package manifest must not exceed 64 KiB");

        long actualExpanded = 0;
        var buffer = new byte[64 * 1024];
        foreach (var entry in archive.Entries)
        {
            long actualEntry = 0;
            using var entryStream = entry.Open();
            while (true)
            {
                var read = entryStream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                actualEntry += read;
                actualExpanded += read;
                if (actualExpanded > MaximumExpandedBytes)
                    throw new AgentReleaseTooLargeException("Agent package exceeds the 500 MiB expanded-size limit");
                if (actualEntry > entry.Length)
                    throw new AgentReleaseValidationException("ZIP entry expanded beyond its declared length");
            }
            if (actualEntry != entry.Length)
                throw new AgentReleaseValidationException("ZIP entry data does not match its declared length");
        }

        var manifest = ReadManifest(manifestEntry);
        if (manifest.PackageFormatVersion != PackageFormatVersion)
            throw new AgentReleaseValidationException("Unsupported Agent package format version");
        if (!string.Equals(manifest.AgentInformationalVersion, version, StringComparison.Ordinal))
            throw new AgentReleaseValidationException("Release version does not match the Agent informational version in the package manifest");
        ValidateAgentDependencyVersion(paths, archive, version);
        if (!SourceCommit.IsMatch(manifest.SourceCommit))
            throw new AgentReleaseValidationException("Agent package manifest sourceCommit is invalid");
        if (manifest.SourceDirty || !manifest.ReleaseBuild)
            throw new AgentReleaseValidationException("Dirty or non-release Agent packages cannot be published");
        return (sha256, manifest);
    }

    private static void ValidateAgentDependencyVersion(Dictionary<string, bool> paths, ZipArchive archive, string version)
    {
        const string dependencyPath = "Agent/Agent.deps.json";
        var dependencyEntry = archive.GetEntry(dependencyPath);
        if (!paths.TryGetValue(dependencyPath, out var isDirectory) || isDirectory || dependencyEntry is null)
            throw new AgentReleaseValidationException("Agent package is missing Agent/Agent.deps.json");
        if (dependencyEntry.Length is <= 0 or > 16 * 1024 * 1024)
            throw new AgentReleaseValidationException("Agent dependency manifest has an invalid size");
        using var stream = dependencyEntry.Open();
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
        if (!document.RootElement.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object
            || !libraries.TryGetProperty("Agent/" + version, out var agentProject)
            || agentProject.ValueKind != JsonValueKind.Object
            || !agentProject.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "project", StringComparison.Ordinal))
            throw new AgentReleaseValidationException("Agent package version does not match the published Agent project identity");
    }

    private static string ValidateEntryPath(string entryName, bool isDirectory)
    {
        if (string.IsNullOrEmpty(entryName) || entryName.Contains('\\') || entryName[0] == '/' || entryName.Contains('\0'))
            throw new AgentReleaseValidationException("Agent package contains an absolute or non-portable ZIP path");
        if (isDirectory) entryName = entryName[..^1];
        if (entryName.Length == 0) throw new AgentReleaseValidationException("Agent package contains an empty ZIP path");
        var segments = entryName.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.'))
                throw new AgentReleaseValidationException("Agent package contains a path traversal or ambiguous ZIP path");
            foreach (var character in segment)
                if (character < 32 || character is '<' or '>' or ':' or '"' or '|' or '?' or '*')
                    throw new AgentReleaseValidationException("Agent package contains a non-portable ZIP path");
            var deviceName = segment.Split('.')[0];
            if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) || deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) || deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(deviceName, @"\A(?:COM|LPT)[1-9]\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new AgentReleaseValidationException("Agent package contains a reserved Windows path name");
        }
        return entryName;
    }

    private static void RejectPathCollisions(Dictionary<string, bool> paths, HashSet<string> pathsWithDescendants, string path, bool isDirectory)
    {
        var separator = path.IndexOf('/');
        while (separator >= 0)
        {
            var parent = path[..separator];
            if (paths.TryGetValue(parent, out var parentIsDirectory) && !parentIsDirectory)
                throw new AgentReleaseValidationException("Agent package ZIP entries collide as file and directory paths");
            pathsWithDescendants.Add(parent);
            separator = path.IndexOf('/', separator + 1);
        }
        if (!isDirectory && pathsWithDescendants.Contains(path))
            throw new AgentReleaseValidationException("Agent package ZIP entries collide as file and directory paths");
    }

    private static void RejectUnsupportedEntryType(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType is not (0 or 0x8000 or 0x4000))
            throw new AgentReleaseValidationException("Agent package contains an unsupported ZIP entry type");
    }

    private static AgentPackageManifest ReadManifest(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream((int)entry.Length);
        stream.CopyTo(memory);
        using var document = JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new AgentReleaseValidationException("Agent package manifest must be a JSON object");
        var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!propertyNames.Add(property.Name))
                throw new AgentReleaseValidationException("Agent package manifest contains duplicate fields");
        var expected = new HashSet<string>(["packageFormatVersion", "agentInformationalVersion", "sourceCommit", "sourceDirty", "releaseBuild"], StringComparer.Ordinal);
        if (!propertyNames.SetEquals(expected))
            throw new AgentReleaseValidationException("Agent package manifest has unexpected or missing fields");
        var manifest = JsonSerializer.Deserialize<AgentPackageManifest>(document.RootElement.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.AgentInformationalVersion) || string.IsNullOrWhiteSpace(manifest.SourceCommit))
            throw new AgentReleaseValidationException("Agent package manifest is incomplete");
        return manifest;
    }

    private static void RetainImmutableArtifact(string stagedPath, string finalPath, string expectedHash, long expectedSize)
    {
        if (File.Exists(finalPath))
        {
            VerifyExistingArtifact(finalPath, expectedHash, expectedSize);
            File.Delete(stagedPath);
            return;
        }

        try
        {
            File.Move(stagedPath, finalPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            VerifyExistingArtifact(finalPath, expectedHash, expectedSize);
            File.Delete(stagedPath);
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

    private sealed record AgentPackageManifest(int PackageFormatVersion, string AgentInformationalVersion, string SourceCommit, bool SourceDirty, bool ReleaseBuild);

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
