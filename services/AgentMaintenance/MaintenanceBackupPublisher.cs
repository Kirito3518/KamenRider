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
}

public sealed class MaintenanceBackupPublisher
{
    private const int MaximumNodes = 5000;
    private const long MaximumTreeBytes = 524288000;
    private const int MaximumManifestBytes = 3 * 1024 * 1024;
    private const string ManifestName = "backup-manifest.json";
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

        var stagingDirectory = Path.Combine(backupRoot, ".pending-" + operationId);
        if (Directory.Exists(stagingDirectory) || File.Exists(stagingDirectory))
            throw new InvalidOperationException("An incomplete backup staging tree exists and requires reconciliation.");

        var sourceInventory = ScanTree(sourceRoot, isPrivateBackup: false);
        if (sourceInventory.Nodes.Count == 0)
            throw new InvalidDataException("The installed Agent tree is empty and cannot be used as a rollback source.");
        security.EnsurePrivateDirectory(stagingDirectory, create: true);
        CopyTree(sourceInventory, stagingDirectory);
        var copiedInventory = ScanTree(stagingDirectory, isPrivateBackup: true);
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

        var actual = ScanTree(directory, isPrivateBackup: true, excludeRootManifest: true);
        if (actual.TotalBytes != manifest.TotalBytes || !SameNodes(actual.Nodes, expectedNodes) ||
            !FixedDigestEquals(ComputeInventoryDigest(actual.Nodes), inventoryDigest))
            throw new InvalidDataException("The published Agent backup bytes do not match its immutable manifest.");

        security.FlushDirectory(directory);
        security.FlushDirectory(backupRoot);
        return new(operationId, directory, manifest.BackupDigest, manifest.InventoryDigest,
            manifest.TotalBytes, expectedNodes);
    }

    private TreeInventory ScanTree(string root, bool isPrivateBackup, bool excludeRootManifest = false)
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
                if (excludeRootManifest && relativeDirectory.Length == 0 && name == ManifestName)
                    continue;
                ValidateRelativePath(relative);
                var attributes = File.GetAttributes(child);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new UnauthorizedAccessException("The Agent tree contains a reparse point or special file.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Visit(child, relative, includeDirectoryNode: true);
                    continue;
                }

                if (!isPrivateBackup && relative == ManifestName)
                    throw new InvalidDataException("The installed Agent tree contains a reserved backup manifest path.");
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
        EnsureSourceDirectory(path);
    }

    public void EnsureSourceDirectory(string path)
    {
        EnsureWithin(path, sourceRoot);
        EnsureNoReparseAncestors(path);
        if (!Directory.Exists(path) ||
            (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new UnauthorizedAccessException("The Agent source contains a missing, non-directory, or reparse path.");
        EnsureNoUntrustedWriteAce(new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
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

    private static void EnsureNoUntrustedWriteAce(FileSystemSecurity security)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var privileged = new HashSet<SecurityIdentifier> { system, admins };
        try { privileged.Add((SecurityIdentifier)new NTAccount("NT SERVICE\\TrustedInstaller").Translate(typeof(SecurityIdentifier))); }
        catch (IdentityNotMappedException) { }
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !privileged.Contains(owner))
            throw new UnauthorizedAccessException("The Agent tree has an untrusted owner.");
        const FileSystemRights writeRights = FileSystemRights.Write | FileSystemRights.Modify |
            FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.CreateFiles |
            FileSystemRights.CreateDirectories | FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes | FileSystemRights.FullControl;
        foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
            if (rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference is SecurityIdentifier sid && !privileged.Contains(sid) &&
                (rule.FileSystemRights & writeRights) != 0)
                throw new UnauthorizedAccessException("An untrusted principal can modify the installed Agent tree.");
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
