using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace KamenRaider.AgentMaintenance;

public sealed record MaintenanceBackupNode(string RelativePath, bool IsDirectory, long Length, string? Sha256);

public sealed record MaintenanceBackupResult(
    string BackupOwnerOperationId,
    string DirectoryPath,
    string BackupDigest,
    string InventoryDigest,
    long TotalBytes,
    IReadOnlyList<MaintenanceBackupNode> Nodes);

public interface IMaintenanceBackupSecurity
{
    void EnsureSourceRoot(string path);
    void EnsureSourceDirectory(string path);
    void EnsureBackupDirectory(string path, bool create);
    void EnsurePrivateDirectory(string path, bool create);
    Stream OpenSourceFile(string path);
    Stream OpenPrivateFile(string path);
    Stream CreateBackupFile(string path);
    void EnsurePrivateFile(string path);
    void FlushDirectory(string path);
    void PublishDirectory(string stagingDirectory, string publishedDirectory);
}

public interface IMaintenanceBackupAuthorization
{
    void VerifyHeldAndQuiesced(string operationId);
    void VerifyHeld(string operationId) => VerifyHeldAndQuiesced(operationId);
}

public sealed class MaintenanceBackupPublisher
{
    private const int MaximumNodes = 5000;
    private const long MaximumTreeBytes = 524288000;
    private const int MaximumManifestBytes = 3 * 1024 * 1024;
    private const string ManifestName = "backup-manifest.json";
    private const string PendingOwnerName = ".pending-owner.json";
    private const int MaximumPendingOwnerBytes = 4096;
    private static readonly Regex OperationIdPattern = new("\\A[A-Za-z0-9._-]{1,128}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex DeviceNamePattern = new("\\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        PropertyNameCaseInsensitive = false
    };

    private readonly string sourceRoot;
    private readonly string backupRoot;
    private readonly IMaintenanceBackupSecurity security;
    private readonly IMaintenanceBackupAuthorization authorization;

    public MaintenanceBackupPublisher(string sourceRoot, string backupRoot, IMaintenanceBackupSecurity security,
        IMaintenanceBackupAuthorization authorization)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        this.sourceRoot = Path.GetFullPath(sourceRoot);
        this.backupRoot = Path.GetFullPath(backupRoot);
        this.security = security ?? throw new ArgumentNullException(nameof(security));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
    }

    public string SourceRoot => sourceRoot;
    public string BackupRoot => backupRoot;

    public MaintenanceBackupResult Publish(string operationId)
    {
        if (operationId is null || !OperationIdPattern.IsMatch(operationId))
            throw new InvalidDataException("The backup operation ID is invalid.");

        authorization.VerifyHeldAndQuiesced(operationId);
        security.EnsureSourceRoot(sourceRoot);
        security.EnsureBackupDirectory(backupRoot, create: true);
        var finalDirectory = Path.Combine(backupRoot, operationId);
        if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
            return ReadAndVerifyPublished(operationId, finalDirectory);

        var sourceInventory = ScanTree(sourceRoot, isPrivateBackup: false);
        if (sourceInventory.Nodes.Count == 0)
            throw new InvalidDataException("The installed Agent tree is empty and cannot be used as a rollback source.");

        var stagingDirectory = Path.Combine(backupRoot, ".pending-" + operationId);
        if (Directory.Exists(stagingDirectory) || File.Exists(stagingDirectory))
        {
            RecoverOwnedPendingStage(stagingDirectory, operationId, ComputeInventoryDigest(sourceInventory.Nodes));
            authorization.VerifyHeldAndQuiesced(operationId);
        }

        security.EnsurePrivateDirectory(stagingDirectory, create: true);
        WritePendingOwner(stagingDirectory, operationId, ComputeInventoryDigest(sourceInventory.Nodes));
        CopyTree(sourceInventory, stagingDirectory);
        var copiedInventory = ScanTree(stagingDirectory, isPrivateBackup: true, excludeRootPendingOwner: true);
        if (!SameInventory(sourceInventory, copiedInventory))
            throw new InvalidDataException("The copied Agent tree does not match the quiesced source snapshot.");

        var inventoryDigest = ComputeInventoryDigest(copiedInventory.Nodes);
        var backupDigest = ComputeBackupDigest(operationId, inventoryDigest);
        var manifest = new BackupManifest(1, operationId, backupDigest, inventoryDigest,
            copiedInventory.TotalBytes, copiedInventory.Nodes.ToList());
        WriteManifest(Path.Combine(stagingDirectory, ManifestName), manifest);

        var finalSourceInventory = ScanTree(sourceRoot, isPrivateBackup: false);
        if (!SameInventory(sourceInventory, finalSourceInventory))
            throw new InvalidDataException("The installed Agent tree changed while its backup was being published.");

        security.FlushDirectory(stagingDirectory);
        authorization.VerifyHeldAndQuiesced(operationId);
        security.PublishDirectory(stagingDirectory, finalDirectory);
        security.FlushDirectory(backupRoot);
        return ReadAndVerifyPublished(operationId, finalDirectory);
    }

    public MaintenanceBackupResult VerifyPublished(string operationId)
    {
        if (operationId is null || !OperationIdPattern.IsMatch(operationId))
            throw new InvalidDataException("The backup operation ID is invalid.");
        authorization.VerifyHeldAndQuiesced(operationId);
        security.EnsureBackupDirectory(backupRoot, create: false);
        var finalDirectory = Path.Combine(backupRoot, operationId);
        if (!Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
            throw new DirectoryNotFoundException("The immutable Agent backup has not been published.");
        return ReadAndVerifyPublished(operationId, finalDirectory);
    }

    public MaintenanceBackupResult VerifyPublishedUnderHold(string operationId, Action verifyHeld)
    {
        ArgumentNullException.ThrowIfNull(verifyHeld);
        if (operationId is null || !OperationIdPattern.IsMatch(operationId))
            throw new InvalidDataException("The backup operation ID is invalid.");
        verifyHeld();
        authorization.VerifyHeld(operationId);
        security.EnsureBackupDirectory(backupRoot, create: false);
        var finalDirectory = Path.Combine(backupRoot, operationId);
        if (!Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
            throw new DirectoryNotFoundException("The immutable Agent backup has not been published.");
        var backup = ReadAndVerifyPublished(operationId, finalDirectory);
        authorization.VerifyHeld(operationId);
        verifyHeld();
        return backup;
    }

    private MaintenanceBackupResult ReadAndVerifyPublished(string operationId, string directory)
    {
        security.EnsurePrivateDirectory(directory, create: false);
        var manifestPath = Path.Combine(directory, ManifestName);
        security.EnsurePrivateFile(manifestPath);
        var manifest = ParseManifest(ReadPrivateFile(manifestPath, MaximumManifestBytes));
        if (manifest.SchemaVersion != 1 || manifest.OperationId != operationId || manifest.Nodes is null ||
            !IsDigest(manifest.BackupDigest) || !IsDigest(manifest.InventoryDigest) ||
            manifest.Nodes.Count is < 1 or > MaximumNodes || manifest.TotalBytes is < 0 or > MaximumTreeBytes)
            throw new InvalidDataException("The published Agent backup manifest is invalid.");

        var expectedNodes = ValidateManifestNodes(manifest.Nodes);
        var inventoryDigest = ComputeInventoryDigest(expectedNodes);
        if (!FixedDigestEquals(inventoryDigest, manifest.InventoryDigest) ||
            !FixedDigestEquals(ComputeBackupDigest(operationId, inventoryDigest), manifest.BackupDigest))
            throw new InvalidDataException("The published Agent backup manifest digest is invalid.");

        var pendingOwnerPath = Path.Combine(directory, PendingOwnerName);
        var hasPendingOwner = File.Exists(pendingOwnerPath) || Directory.Exists(pendingOwnerPath);
        var actual = ScanTree(directory, isPrivateBackup: true, excludeRootManifest: true,
            excludeRootPendingOwner: true);
        if (actual.TotalBytes != manifest.TotalBytes || !SameNodes(actual.Nodes, expectedNodes) ||
            !FixedDigestEquals(ComputeInventoryDigest(actual.Nodes), inventoryDigest))
            throw new InvalidDataException("The published Agent backup bytes do not match its immutable manifest.");

        if (hasPendingOwner)
        {
            var pendingOwner = ReadPendingOwner(directory);
            if (pendingOwner.OperationId != operationId ||
                !FixedDigestEquals(pendingOwner.SourceInventoryDigest, inventoryDigest))
                throw new InvalidDataException("The published Agent backup has a mismatched pending owner marker.");
            DeletePrivateFile(pendingOwnerPath);
            security.FlushDirectory(directory);
        }

        security.FlushDirectory(directory);
        security.FlushDirectory(backupRoot);
        return new(operationId, directory, manifest.BackupDigest, manifest.InventoryDigest,
            manifest.TotalBytes, expectedNodes);
    }

    private TreeInventory ScanTree(string root, bool isPrivateBackup, bool excludeRootManifest = false,
        bool excludeRootPendingOwner = false)
    {
        var nodes = new List<MaintenanceBackupNode>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        Visit(root, string.Empty, includeDirectoryNode: false);
        nodes.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        return new(nodes, totalBytes);

        void Visit(string directory, string relativeDirectory, bool includeDirectoryNode)
        {
            if (isPrivateBackup)
                security.EnsurePrivateDirectory(directory, create: false);
            else
                security.EnsureSourceDirectory(directory);

            if (includeDirectoryNode)
                AddNode(new(relativeDirectory, true, 0, null));

            var children = Directory.EnumerateFileSystemEntries(directory)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                var relative = relativeDirectory.Length == 0 ? name : relativeDirectory + "/" + name;
                ValidateRelativePath(relative);
                var attributes = File.GetAttributes(child);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new UnauthorizedAccessException("The Agent tree contains a reparse point or special file.");
                if (relativeDirectory.Length == 0 && name == PendingOwnerName && excludeRootPendingOwner)
                {
                    if ((attributes & FileAttributes.Directory) != 0)
                        throw new UnauthorizedAccessException("The pending backup owner marker is not a regular file.");
                    continue;
                }
                if (excludeRootManifest && relativeDirectory.Length == 0 && name == ManifestName)
                {
                    if ((attributes & FileAttributes.Directory) != 0)
                        throw new UnauthorizedAccessException("The backup manifest is not a regular file.");
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Visit(child, relative, includeDirectoryNode: true);
                    continue;
                }

                if (!isPrivateBackup && relative == ManifestName)
                    throw new InvalidDataException("The installed Agent tree contains a reserved backup manifest path.");
                if (!isPrivateBackup && relative == PendingOwnerName)
                    throw new InvalidDataException("The installed Agent tree contains a reserved pending backup owner path.");
                using var file = isPrivateBackup ? security.OpenPrivateFile(child) : security.OpenSourceFile(child);
                if (!file.CanRead || !file.CanSeek || file.Length < 0 || file.Length > MaximumTreeBytes - totalBytes)
                    throw new InvalidDataException("The Agent tree exceeds its size limit or contains an unreadable file.");
                var length = file.Length;
                var digest = HashFile(file);
                totalBytes = checked(totalBytes + length);
                AddNode(new(relative, false, length, digest));
            }
        }

        void AddNode(MaintenanceBackupNode node)
        {
            if (nodes.Count >= MaximumNodes || !paths.Add(node.RelativePath))
                throw new InvalidDataException("The Agent tree exceeds its node limit or has colliding paths.");
            nodes.Add(node);
        }
    }

    private void CopyTree(TreeInventory inventory, string destinationRoot)
    {
        foreach (var node in inventory.Nodes.Where(node => node.IsDirectory)
                     .OrderBy(node => node.RelativePath.Count(character => character == '/'))
                     .ThenBy(node => node.RelativePath, StringComparer.Ordinal))
            security.EnsurePrivateDirectory(LocalPath(destinationRoot, node.RelativePath), create: true);

        foreach (var node in inventory.Nodes.Where(node => !node.IsDirectory))
        {
            var source = LocalPath(sourceRoot, node.RelativePath);
            var destination = LocalPath(destinationRoot, node.RelativePath);
            using var input = security.OpenSourceFile(source);
            string copiedHash;
            using (var output = security.CreateBackupFile(destination))
            {
                copiedHash = CopyAndHash(input, output, node.Length);
                if (output is FileStream fileStream)
                    fileStream.Flush(flushToDisk: true);
                else
                    output.Flush();
            }
            if (!FixedDigestEquals(copiedHash, node.Sha256!))
                throw new InvalidDataException("An installed Agent file changed while its backup copy was written.");
            security.EnsurePrivateFile(destination);
        }
    }

    private void WriteManifest(string path, BackupManifest manifest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (bytes.Length is <= 0 or > MaximumManifestBytes)
            throw new InvalidDataException("The Agent backup manifest exceeds its size limit.");
        using (var output = security.CreateBackupFile(path))
        {
            output.Write(bytes);
            if (output is FileStream fileStream)
                fileStream.Flush(flushToDisk: true);
            else
                output.Flush();
        }
        security.EnsurePrivateFile(path);
    }

    private void WritePendingOwner(string stagingDirectory, string operationId, string sourceInventoryDigest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new PendingBackupOwner(1, operationId, sourceInventoryDigest), JsonOptions);
        if (bytes.Length is <= 0 or > MaximumPendingOwnerBytes)
            throw new InvalidDataException("The pending backup owner marker exceeds its size limit.");
        var path = Path.Combine(stagingDirectory, PendingOwnerName);
        using (var output = security.CreateBackupFile(path))
        {
            output.Write(bytes);
            if (output is FileStream fileStream)
                fileStream.Flush(flushToDisk: true);
            else
                output.Flush();
        }
        security.EnsurePrivateFile(path);
        security.FlushDirectory(stagingDirectory);
    }

    private PendingBackupOwner ReadPendingOwner(string directory)
    {
        var path = Path.Combine(directory, PendingOwnerName);
        security.EnsurePrivateFile(path);
        var bytes = ReadPrivateFile(path, MaximumPendingOwnerBytes);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 2,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            ValidateObjectFields(document.RootElement, ["schemaVersion", "operationId", "sourceInventoryDigest"]);
            return JsonSerializer.Deserialize<PendingBackupOwner>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The pending backup owner marker is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The pending backup owner marker is malformed.", exception);
        }
    }

    private void RecoverOwnedPendingStage(string stagingDirectory, string operationId, string sourceInventoryDigest)
    {
        if (!Directory.Exists(stagingDirectory) || File.Exists(stagingDirectory))
            throw new UnauthorizedAccessException("The pending backup staging path is not a directory.");
        security.EnsurePrivateDirectory(stagingDirectory, create: false);
        var owner = ReadPendingOwner(stagingDirectory);
        if (owner.SchemaVersion != 1 || owner.OperationId != operationId ||
            !IsDigest(owner.SourceInventoryDigest) ||
            !FixedDigestEquals(owner.SourceInventoryDigest, sourceInventoryDigest))
            throw new InvalidDataException("The pending backup staging tree is not owned by this operation and source snapshot.");

        authorization.VerifyHeldAndQuiesced(operationId);
        var discardedDirectory = Path.Combine(backupRoot,
            ".discard-" + operationId + "-" + Guid.NewGuid().ToString("N"));
        security.PublishDirectory(stagingDirectory, discardedDirectory);
        security.FlushDirectory(backupRoot);
        DeletePendingTreeContents(discardedDirectory, string.Empty);
        Directory.Delete(discardedDirectory, recursive: false);
        security.FlushDirectory(backupRoot);
    }

    private void DeletePendingTreeContents(string directory, string relativeDirectory)
    {
        var entries = Directory.EnumerateFileSystemEntries(directory)
            .OrderBy(path => string.Equals(Path.GetFileName(path), PendingOwnerName, StringComparison.Ordinal)
                ? 1
                : 0)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            var relative = relativeDirectory.Length == 0 ? name : relativeDirectory + "/" + name;
            ValidateRelativePath(relative);
            var attributes = File.GetAttributes(entry);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new UnauthorizedAccessException("The pending backup staging tree contains a reparse point or special file.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                security.EnsurePrivateDirectory(entry, create: false);
                DeletePendingTreeContents(entry, relative);
                Directory.Delete(entry, recursive: false);
                continue;
            }

            DeletePrivateFile(entry);
        }
    }

    private void DeletePrivateFile(string path)
    {
        security.EnsurePrivateFile(path);
        using (security.OpenPrivateFile(path)) { }
        File.Delete(path);
    }

    private static BackupManifest ParseManifest(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 8,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            ValidateObjectFields(document.RootElement, ["schemaVersion", "operationId", "backupDigest", "inventoryDigest", "totalBytes", "nodes"]);
            var manifest = JsonSerializer.Deserialize<BackupManifest>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The Agent backup manifest is empty.");
            if (document.RootElement.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                foreach (var node in nodes.EnumerateArray())
                    ValidateObjectFields(node, ["relativePath", "isDirectory", "length", "sha256"]);
            return manifest;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The Agent backup manifest is malformed.", ex);
        }
    }

    private static void ValidateObjectFields(JsonElement element, string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The Agent backup manifest has an invalid object.");
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name) || !found.Add(property.Name))
                throw new InvalidDataException("The Agent backup manifest contains unknown or duplicate fields.");
        if (found.Count != names.Length)
            throw new InvalidDataException("The Agent backup manifest is incomplete.");
    }

    private static List<MaintenanceBackupNode> ValidateManifestNodes(IReadOnlyList<MaintenanceBackupNode> nodes)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (node is null)
                throw new InvalidDataException("The Agent backup manifest contains an empty node.");
            ValidateRelativePath(node.RelativePath);
            if (!paths.Add(node.RelativePath) ||
                (node.IsDirectory && (node.Length != 0 || node.Sha256 is not null)) ||
                (!node.IsDirectory && (node.Length < 0 || !IsDigest(node.Sha256))))
                throw new InvalidDataException("The Agent backup manifest contains an invalid or colliding node.");
        }
        return nodes.OrderBy(node => node.RelativePath, StringComparer.Ordinal).ToList();
    }

    private byte[] ReadPrivateFile(string path, int maximumBytes)
    {
        using var input = security.OpenPrivateFile(path);
        if (input.Length is <= 0 || input.Length > maximumBytes)
            throw new InvalidDataException("The Agent backup manifest has an invalid size.");
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static bool SameInventory(TreeInventory first, TreeInventory second) =>
        first.TotalBytes == second.TotalBytes && SameNodes(first.Nodes, second.Nodes);

    private static bool SameNodes(IReadOnlyList<MaintenanceBackupNode> first, IReadOnlyList<MaintenanceBackupNode> second) =>
        first.Count == second.Count && first.Zip(second).All(pair => pair.First == pair.Second);

    private static string ComputeInventoryDigest(IEnumerable<MaintenanceBackupNode> nodes)
    {
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, new UTF8Encoding(false, true), leaveOpen: true))
            foreach (var node in nodes.OrderBy(node => node.RelativePath, StringComparer.Ordinal))
            {
                writer.Write(node.RelativePath);
                writer.Write(node.IsDirectory);
                writer.Write(node.Length);
                writer.Write(node.Sha256 ?? string.Empty);
            }
        return Convert.ToHexStringLower(SHA256.HashData(memory.ToArray()));
    }

    private static string ComputeBackupDigest(string operationId, string inventoryDigest) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "KamenRaider.AgentMaintenance.Backup.v1\n" + operationId + "\n" + inventoryDigest)));

    private static string HashFile(Stream stream)
    {
        stream.Position = 0;
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string CopyAndHash(Stream input, Stream output, long expectedLength)
    {
        input.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total = checked(total + read);
            if (total > expectedLength || total > MaximumTreeBytes)
                throw new InvalidDataException("An installed Agent file changed length during backup.");
            output.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
        }
        if (total != expectedLength)
            throw new InvalidDataException("An installed Agent file changed length during backup.");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string LocalPath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static void ValidateRelativePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath.Length > 240 || relativePath.Contains('\\') || relativePath.StartsWith('/'))
            throw new InvalidDataException("The Agent tree contains an invalid relative path.");
        var parts = relativePath.Split('/');
        if (parts.Length > 16)
            throw new InvalidDataException("The Agent tree path exceeds its depth limit.");
        foreach (var part in parts)
            if (part.Length is < 1 or > 100 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                DeviceNamePattern.IsMatch(part.Split('.')[0]) ||
                part.Any(character => character < 32 || character > 126 || character is '<' or '>' or ':' or '"' or '|' or '?' or '*'))
                throw new InvalidDataException("The Agent tree contains a non-portable path segment.");
    }

    private static bool IsDigest(string? digest) => digest is { Length: 64 } && digest.All(Uri.IsHexDigit);

    private static bool FixedDigestEquals(string first, string second) =>
        first.Length == second.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(first.ToLowerInvariant()), Encoding.ASCII.GetBytes(second.ToLowerInvariant()));

    private sealed record TreeInventory(List<MaintenanceBackupNode> Nodes, long TotalBytes);
    private sealed record BackupManifest(int SchemaVersion, string OperationId, string BackupDigest,
        string InventoryDigest, long TotalBytes, List<MaintenanceBackupNode> Nodes);
    private sealed record PendingBackupOwner(int SchemaVersion, string OperationId, string SourceInventoryDigest);
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsMaintenanceBackupSecurity : IMaintenanceBackupSecurity
{
    private readonly string sourceRoot;
    private readonly string backupRoot;

    public WindowsMaintenanceBackupSecurity()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Agent backup publication requires Windows ACLs and file identity checks.");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programFiles) || string.IsNullOrWhiteSpace(commonData) ||
            !Path.IsPathFullyQualified(programFiles) || !Path.IsPathFullyQualified(commonData))
            throw new InvalidOperationException("The native Program Files or CommonApplicationData path is unavailable.");
        sourceRoot = Path.GetFullPath(Path.Combine(programFiles, "KamenRaider", "Agent"));
        backupRoot = Path.GetFullPath(Path.Combine(commonData, "KamenRaider.AgentMaintenance", "backups"));
    }

    public string SourceRoot => sourceRoot;
    public string BackupRoot => backupRoot;

    public void EnsureSourceRoot(string path)
    {
        if (!PathEquals(path, sourceRoot))
            throw new UnauthorizedAccessException("The source tree does not match the canonical Agent installation path.");
        EnsureNoReparseAncestors(path);
        EnsureSourceAncestors(path);
        EnsureSourceDirectory(path);
    }

    public void EnsureSourceDirectory(string path)
    {
        EnsureWithin(path, sourceRoot);
        EnsureNoReparseAncestors(path);
        if (!Directory.Exists(path) ||
            (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new UnauthorizedAccessException("The Agent source contains a missing, non-directory, or reparse path.");
        EnsureSourceAncestors(path);
    }

    public void EnsureBackupDirectory(string path, bool create)
    {
        if (!PathEquals(path, backupRoot))
            throw new UnauthorizedAccessException("The backup root does not match the canonical protected path.");
        EnsureNoReparseAncestors(path);
        var parent = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent) && create)
            new WindowsMaintenanceFenceStore().EnsureLocationForJournal();
        WindowsMaintenanceFenceStore.ValidateLocationForJournal();
        EnsurePrivateDirectory(path, create);
    }

    public void EnsurePrivateDirectory(string path, bool create)
    {
        EnsureWithin(path, backupRoot);
        if (!Directory.Exists(path) && create)
        {
            var parent = Path.GetDirectoryName(path);
            if (parent is null || !Directory.Exists(parent))
                throw new DirectoryNotFoundException("The protected backup parent is unavailable.");
            try { new DirectoryInfo(path).Create(CreateDirectorySecurity()); }
            catch (IOException) when (Directory.Exists(path)) { }
        }
        EnsureNoReparseAncestors(path);
        if (!Directory.Exists(path) ||
            (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new UnauthorizedAccessException("The backup tree contains a missing, non-directory, or reparse path.");
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!IsPrivateSecurity(security, isDirectory: true))
            throw new UnauthorizedAccessException("The backup directory owner or ACL is unsafe.");
    }

    public Stream OpenSourceFile(string path)
    {
        EnsureWithin(path, sourceRoot);
        EnsureNoReparseAncestors(path);
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new UnauthorizedAccessException("The Agent source file has no parent directory.");
        EnsureSourceAncestors(parent);
        if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new UnauthorizedAccessException("The Agent source file is missing or is not a regular file.");
        EnsureNoUntrustedWriteAce(new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
        return OpenRegularSingleLinkFile(path);
    }

    public Stream OpenPrivateFile(string path)
    {
        EnsureWithin(path, backupRoot);
        EnsurePrivateFile(path);
        return OpenRegularSingleLinkFile(path);
    }

    public Stream CreateBackupFile(string path)
    {
        EnsureWithin(path, backupRoot);
        using (new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough)) { }
        SetPrivateFileSecurity(path);
        EnsurePrivateFile(path);
        return new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
    }

    public void EnsurePrivateFile(string path)
    {
        EnsureWithin(path, backupRoot);
        if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new UnauthorizedAccessException("The backup file is missing or unsafe.");
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!IsPrivateSecurity(security, isDirectory: false))
            throw new UnauthorizedAccessException("The backup file owner or ACL is unsafe.");
        using var stream = OpenRegularSingleLinkFile(path);
    }

    public void FlushDirectory(string path) => FlushDirectoryToDisk(path);

    internal static void FlushDirectoryToDisk(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Directory durability requires Windows file handles.");
        const uint genericReadWrite = 0x80000000 | 0x40000000;
        const uint shareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
        const uint openExisting = 3;
        const uint flags = 0x02000000 | 0x00200000;
        using var handle = CreateFile(path, genericReadWrite, shareReadWriteDelete, IntPtr.Zero,
            openExisting, flags, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException("The backup directory could not be opened for a durability flush.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        if (!GetFileInformationByHandle(handle, out var info) ||
            (info.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) != (uint)FileAttributes.Directory)
            throw new UnauthorizedAccessException("The backup durability flush path is not a regular directory.");
        if (!FlushFileBuffers(handle))
            throw new IOException("The backup directory durability flush failed.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    }

    public void PublishDirectory(string stagingDirectory, string publishedDirectory)
    {
        EnsureWithin(stagingDirectory, backupRoot);
        EnsureWithin(publishedDirectory, backupRoot);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(stagingDirectory)), backupRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(publishedDirectory)), backupRoot, StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(publishedDirectory) || File.Exists(publishedDirectory))
            throw new IOException("The backup publication paths are not an unused sibling pair under the canonical root.");
        Directory.Move(stagingDirectory, publishedDirectory);
        EnsurePrivateDirectory(publishedDirectory, create: false);
    }

    private static Stream OpenRegularSingleLinkFile(string path)
    {
        const uint genericRead = 0x80000000;
        const uint shareRead = 0x00000001;
        const uint openExisting = 3;
        const uint openReparsePoint = 0x00200000;
        var handle = CreateFile(path, genericRead, shareRead, IntPtr.Zero, openExisting, openReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks != 1 ||
            (info.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) != 0)
        {
            handle.Dispose();
            throw new UnauthorizedAccessException("The Agent tree contains a reparse point, hard link, or non-file node.");
        }
        return new FileStream(handle, FileAccess.Read, 65536, isAsync: false);
    }

    private void EnsureSourceAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(current)
            ?? throw new UnauthorizedAccessException("The Agent source path has no volume root.");
        while (!string.IsNullOrEmpty(current))
        {
            if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("The Agent source ancestor is missing or is a reparse point.");
            var security = new DirectoryInfo(current).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (PathEquals(current, volumeRoot))
            {
                if (!IsSystemVolumeRootSecuritySafe(security))
                    throw new UnauthorizedAccessException("The system volume root owner or writable access is unsafe.");
            }
            else EnsureNoUntrustedWriteAce(security);
            if (PathEquals(current, volumeRoot)) break;
            current = Path.GetDirectoryName(current)
                ?? throw new UnauthorizedAccessException("The Agent source ancestor chain is incomplete.");
        }
    }

    internal static bool IsSourceSecuritySafe(FileSystemSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var trustedInstaller = TrustedInstallerSid();
        var privilegedOwners = new HashSet<SecurityIdentifier> { system, admins, trustedInstaller };
        // Creator Owner is safe only as an ACE because the actual owner is checked
        // independently against the privileged owner set above.
        var privilegedAces = new HashSet<SecurityIdentifier>(privilegedOwners)
        {
            new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null)
        };
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !privilegedOwners.Contains(owner)) return false;
        const FileSystemRights writeRights = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.WriteData |
            FileSystemRights.AppendData | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
            if (rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference is SecurityIdentifier sid && !privilegedAces.Contains(sid) &&
                (rule.FileSystemRights & writeRights) != 0)
                return false;
        return true;
    }

    internal static bool IsSystemVolumeRootSecuritySafe(DirectorySecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var trustedInstaller = TrustedInstallerSid();
        var creatorOwner = new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null);
        var authenticatedUsers = new SecurityIdentifier("S-1-5-11");
        var privilegedOwners = new HashSet<SecurityIdentifier> { system, admins, trustedInstaller };
        var privilegedAces = new HashSet<SecurityIdentifier>(privilegedOwners) { creatorOwner };
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != trustedInstaller)
            return false;

        const FileSystemRights writeRights = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.WriteData |
            FileSystemRights.AppendData | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        const int inheritOnlyReadMask = -536805376;
        var allowedCreateDirectories = 0;
        var allowedReadInheritance = 0;
        foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference is not SecurityIdentifier sid ||
                privilegedAces.Contains(sid))
                continue;

            if (sid == authenticatedUsers)
            {
                if ((int)rule.FileSystemRights == inheritOnlyReadMask &&
                    rule.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) &&
                    rule.PropagationFlags == PropagationFlags.InheritOnly && !rule.IsInherited)
                {
                    allowedReadInheritance++;
                    continue;
                }

                var rightsWithoutSynchronize = rule.FileSystemRights & ~FileSystemRights.Synchronize;
                if (rightsWithoutSynchronize == FileSystemRights.CreateDirectories &&
                    rule.InheritanceFlags == InheritanceFlags.None && rule.PropagationFlags == PropagationFlags.None &&
                    !rule.IsInherited)
                {
                    allowedCreateDirectories++;
                    continue;
                }
            }

            if ((rule.FileSystemRights & writeRights) != 0)
                return false;
        }

        return allowedCreateDirectories == 1 && allowedReadInheritance == 1;
    }

    private static SecurityIdentifier TrustedInstallerSid() =>
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private static void EnsureNoUntrustedWriteAce(FileSystemSecurity security)
    {
        if (!IsSourceSecuritySafe(security))
            throw new UnauthorizedAccessException("An untrusted principal can alter the installed Agent tree or its ACL.");
    }

    private static bool IsPrivateSecurity(FileSystemSecurity security, bool isDirectory)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (!security.AreAccessRulesProtected || owner is null || (owner != system && owner != admins)) return false;
        var inheritance = isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (rules.Length != 2) return false;
        var identities = new HashSet<SecurityIdentifier>();
        foreach (var rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IsInherited ||
                rule.PropagationFlags != PropagationFlags.None || rule.InheritanceFlags != inheritance ||
                rule.IdentityReference is not SecurityIdentifier sid || !identities.Add(sid) ||
                rule.FileSystemRights != FileSystemRights.FullControl) return false;
        }
        return identities.SetEquals([system, admins]);
    }

    private static DirectorySecurity CreateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(AdministratorsSid());
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(SystemSid(), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid(), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static void SetPrivateFileSecurity(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(AdministratorsSid());
        security.AddAccessRule(new FileSystemAccessRule(SystemSid(), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid(), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void EnsureNoReparseAncestors(string path)
    {
        foreach (var ancestor in Ancestors(path))
            if ((Directory.Exists(ancestor) || File.Exists(ancestor)) &&
                (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("The backup path contains a reparse point.");
    }

    private static void EnsureWithin(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!string.Equals(full, canonicalRoot, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("A maintenance backup path escaped its fixed root.");
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            yield return current;
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static SecurityIdentifier SystemSid() => new(WellKnownSidType.LocalSystemSid, null);
    private static SecurityIdentifier AdministratorsSid() => new(WellKnownSidType.BuiltinAdministratorsSid, null);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out WindowsFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);
}
