using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;
using KamenRaider.AgentPackage;
using Microsoft.Win32.SafeHandles;

namespace KamenRaider.AgentMaintenance;

[SupportedOSPlatform("windows")]
public sealed class WindowsMaintenanceApplyFileSystem : IMaintenanceApplyFileSystem
{
    private const int MaximumNodes = 5000;
    private const long MaximumTreeBytes = 524288000;
    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private static readonly Regex OperationIdPattern = new("\\A[A-Za-z0-9._-]{1,128}\\z", RegexOptions.CultureInvariant);

    private readonly string installDirectory;
    private readonly string installParent;
    private readonly string backupRoot;
    private readonly string extractedTargetRoot;
    private readonly SecurityIdentifier agentServiceSid;

    public WindowsMaintenanceApplyFileSystem(
        WindowsMaintenanceBackupSecurity security,
        string extractedTargetRoot)
    {
        ArgumentNullException.ThrowIfNull(security);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractedTargetRoot);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Agent apply filesystem operations require Windows ACLs and native handles.");

        installDirectory = Path.GetFullPath(security.SourceRoot);
        installParent = Path.GetDirectoryName(installDirectory)
            ?? throw new InvalidOperationException("The canonical Agent path has no parent directory.");
        backupRoot = Path.GetFullPath(security.BackupRoot);
        this.extractedTargetRoot = Path.GetFullPath(extractedTargetRoot);
        var fixedExtractionRoot = new WindowsMaintenanceWorkStore().ExtractionRoot;
        if (!PathEquals(this.extractedTargetRoot, fixedExtractionRoot))
            throw new UnauthorizedAccessException("The target extraction root must be the fixed protected maintenance work directory.");
        agentServiceSid = WindowsMaintenanceFenceStore.ResolveAgentServiceSid();
    }

    public void EnsureProtectedInstallParent(string path)
    {
        EnsureInstallPath(path);
        EnsureTrustedAncestors(installParent);
    }

    public bool DirectoryExists(string path)
    {
        EnsureInstallSibling(path, allowInstallDirectory: true, allowStage: true, allowPrior: true,
            allowFailed: true, allowRestore: true);
        EnsureNoReparseAncestors(path);
        if (TryGetAttributes(path, out var attributes))
        {
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("A maintenance swap path is a reparse point.");
            if ((attributes & FileAttributes.Directory) == 0)
                throw new InvalidDataException("A maintenance swap directory path is occupied by a non-directory entry.");
            return true;
        }
        return false;
    }

    public void PrepareTargetInstallTree(string extractedTree, string destination, PackageInventory expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        EnsureExtractedPath(extractedTree);
        EnsureInstallSibling(destination, allowInstallDirectory: false, requiredPrefix: ".Agent-stage-");
        EnsureProtectedInstallParent(installDirectory);
        EnsureSafeTree(extractedTargetRoot);
        PackageVerifier.ValidateExtracted(extractedTargetRoot, expected);
        var installInventory = PackageVerifier.CreateAgentInstallInventory(expected);
        EnsureAbsent(destination);

        CreatePrivateDirectory(destination);
        foreach (var node in installInventory.Nodes.Where(node => node.IsDirectory)
                     .OrderBy(node => node.Path.Count(character => character == '/'))
                     .ThenBy(node => node.Path, StringComparer.Ordinal))
        {
            var directory = LocalPath(destination, node.Path);
            CreatePrivateDirectory(directory);
        }

        foreach (var node in installInventory.Nodes.Where(node => !node.IsDirectory))
        {
            var source = LocalPath(extractedTargetRoot, "Agent/" + node.Path);
            var target = LocalPath(destination, node.Path);
            CopyVerifiedFile(source, target, node.Length, node.Sha256!);
        }

        FlushTreeDirectories(destination);
        ValidateTargetInstallTree(destination, installInventory);
    }

    public void ValidateTargetInstallTree(string path, PackageInventory expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        EnsureInstallSibling(path, allowInstallDirectory: true, allowStage: true, allowFailed: true);
        PackageVerifier.ValidateExtracted(path, expected);
        EnsurePrivateTree(path, includeAgentService: true);
    }

    public void EnsureOwnedInstallTree(string path)
    {
        EnsureInstallSibling(path, allowInstallDirectory: true, allowStage: true, allowPrior: true,
            allowFailed: true, allowRestore: true);
        _ = ScanTree(path, requirePrivateAcl: true, includeAgentService: true);
    }

    public void VerifyBackupTree(string path, MaintenanceBackupResult backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        EnsureInstallSibling(path, allowInstallDirectory: true, allowPrior: true, allowStage: true,
            allowFailed: true, allowRestore: true);
        var actual = ScanTree(path, requirePrivateAcl: false);
        var expected = backup.Nodes.OrderBy(node => node.RelativePath, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected))
            throw new InvalidDataException("The installed or restored Agent tree differs from the immutable backup inventory.");
    }

    public void PrepareBackupInstallTree(string backupPath, string destination, MaintenanceBackupResult backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        var expectedBackupPath = Path.Combine(backupRoot, backup.BackupOwnerOperationId);
        if (!PathEquals(backupPath, expectedBackupPath) || !OperationIdPattern.IsMatch(backup.BackupOwnerOperationId))
            throw new UnauthorizedAccessException("The restore source is not the operation-owned protected backup directory.");
        EnsureInstallSibling(destination, allowInstallDirectory: false, requiredPrefix: ".Agent-restore-");
        EnsureNoReparseAncestors(backupPath);
        EnsureTrustedAncestors(Path.GetDirectoryName(backupPath)!);
        EnsurePrivateTree(backupPath, includeAgentService: false);
        VerifyBackupSourceNodes(backupPath, backup);
        EnsureAbsent(destination);

        CreatePrivateDirectory(destination);
        foreach (var node in backup.Nodes.Where(node => node.IsDirectory)
                     .OrderBy(node => node.RelativePath.Count(character => character == '/'))
                     .ThenBy(node => node.RelativePath, StringComparer.Ordinal))
            CreatePrivateDirectory(LocalPath(destination, node.RelativePath));

        foreach (var node in backup.Nodes.Where(node => !node.IsDirectory))
            CopyVerifiedFile(LocalPath(backupPath, node.RelativePath), LocalPath(destination, node.RelativePath),
                node.Length, node.Sha256!);

        FlushTreeDirectories(destination);
        VerifyBackupTree(destination, backup);
    }

    public void MoveDirectory(string source, string destination)
    {
        EnsureInstallSibling(source, allowInstallDirectory: true, allowStage: true, allowPrior: true,
            allowFailed: true, allowRestore: true);
        EnsureInstallSibling(destination, allowInstallDirectory: true, allowStage: true, allowPrior: true,
            allowFailed: true, allowRestore: true);
        if (!PathEquals(Path.GetDirectoryName(Path.GetFullPath(source))!, installParent) ||
            !PathEquals(Path.GetDirectoryName(Path.GetFullPath(destination))!, installParent))
            throw new UnauthorizedAccessException("A swap move must remain within the fixed Agent installation parent.");
        EnsureNoReparseAncestors(source);
        EnsureNoReparseAncestors(destination);
        if (!DirectoryExists(source))
            throw new DirectoryNotFoundException("A required Agent swap source directory is missing.");
        _ = ScanTree(source, requirePrivateAcl: false);
        EnsureAbsent(destination);
        Directory.Move(source, destination);
    }

    public void FlushParent(string path)
    {
        EnsureInstallSibling(path, allowInstallDirectory: true, allowStage: true, allowPrior: true,
            allowFailed: true, allowRestore: true);
        WindowsMaintenanceBackupSecurity.FlushDirectoryToDisk(installParent);
    }

    private void VerifyBackupSourceNodes(string root, MaintenanceBackupResult backup)
    {
        var actual = ScanTree(root, requirePrivateAcl: true, excludeRootManifest: true, includeAgentService: false);
        var expected = backup.Nodes.OrderBy(node => node.RelativePath, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected))
            throw new InvalidDataException("The immutable backup directory does not match its published manifest inventory.");
    }

    private void EnsurePrivateTree(string root, bool includeAgentService) =>
        _ = ScanTree(root, requirePrivateAcl: true, includeAgentService: includeAgentService);

    private void EnsureSafeTree(string root) => _ = ScanTree(root, requirePrivateAcl: false);

    private IReadOnlyList<MaintenanceBackupNode> ScanTree(
        string root,
        bool requirePrivateAcl,
        bool excludeRootManifest = false,
        bool includeAgentService = false)
    {
        EnsureNoReparseAncestors(root);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new UnauthorizedAccessException("An Agent tree path is missing or is not a regular directory.");

        var nodes = new List<MaintenanceBackupNode>();
        long totalBytes = 0;
        Visit(root, string.Empty, includeDirectoryNode: false);
        nodes.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        return nodes;

        void Visit(string directory, string relativeDirectory, bool includeDirectoryNode)
        {
            EnsureDirectorySecurity(directory, requirePrivateAcl, includeAgentService);
            if (includeDirectoryNode)
                AddNode(new(relativeDirectory, true, 0, null));

            var entries = Directory.EnumerateFileSystemEntries(directory)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                var relative = relativeDirectory.Length == 0 ? name : relativeDirectory + "/" + name;
                if (excludeRootManifest && relativeDirectory.Length == 0 && name == "backup-manifest.json")
                    continue;
                ValidateRelativePath(relative);
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new UnauthorizedAccessException("An Agent tree contains a reparse point or special file.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Visit(entry, relative, includeDirectoryNode: true);
                    continue;
                }

                EnsureFileSecurity(entry, requirePrivateAcl, includeAgentService);
                using var stream = OpenRegularSingleLinkFile(entry);
                if (stream.Length < 0 || stream.Length > MaximumTreeBytes - totalBytes)
                    throw new InvalidDataException("An Agent tree exceeds the supported inventory size.");
                var length = stream.Length;
                var digest = Convert.ToHexStringLower(SHA256.HashData(stream));
                totalBytes = checked(totalBytes + length);
                AddNode(new(relative, false, length, digest));
            }
        }

        void AddNode(MaintenanceBackupNode node)
        {
            if (nodes.Count >= MaximumNodes)
                throw new InvalidDataException("An Agent tree exceeds the supported inventory node count.");
            nodes.Add(node);
        }
    }

    private void CopyVerifiedFile(string source, string destination, long expectedLength, string expectedSha256)
    {
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("A staged Agent file has no parent directory.");
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("A staged Agent file parent is missing.");

        using var input = OpenRegularSingleLinkFile(source);
        using (var create = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { }
        SetInstallFileSecurity(destination);

        using var output = new FileStream(destination, FileMode.Open, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long copied = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            copied = checked(copied + read);
            if (copied > expectedLength)
                throw new InvalidDataException("A source file grew while it was copied to the Agent stage.");
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        if (copied != expectedLength || Convert.ToHexStringLower(hash.GetHashAndReset()) != expectedSha256)
            throw new InvalidDataException("A staged Agent file differs from its verified source digest.");
        output.Flush(flushToDisk: true);
        EnsureFileSecurity(destination, requirePrivateAcl: true, includeAgentService: true);
    }

    private static FileStream OpenRegularSingleLinkFile(string path)
    {
        var handle = CreateFile(path, GenericRead, ShareRead, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks != 1 ||
            (info.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) != 0)
        {
            handle.Dispose();
            throw new UnauthorizedAccessException("An Agent tree contains a reparse point, hard link, or non-file node.");
        }
        return new FileStream(handle, FileAccess.Read, 65536, isAsync: false);
    }

    private void CreatePrivateDirectory(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException("A protected Agent stage directory already exists and requires reconciliation.");
        try
        {
            new DirectoryInfo(path).Create(CreateInstallDirectorySecurity());
        }
        catch (IOException) when (Directory.Exists(path) || File.Exists(path))
        {
            throw new IOException("A protected Agent stage directory was created by another process.");
        }
        EnsureDirectorySecurity(path, requirePrivateAcl: true, includeAgentService: true);
    }

    private static void FlushTreeDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            WindowsMaintenanceBackupSecurity.FlushDirectoryToDisk(directory);
        WindowsMaintenanceBackupSecurity.FlushDirectoryToDisk(root);
    }

    private void EnsureDirectorySecurity(string path, bool requirePrivateAcl, bool includeAgentService)
    {
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (requirePrivateAcl ? !IsPrivateSecurity(security, isDirectory: true, includeAgentService) : !WindowsMaintenanceBackupSecurity.IsSourceSecuritySafe(security))
            throw new UnauthorizedAccessException("An Agent directory owner or ACL is unsafe.");
    }

    private void EnsureFileSecurity(string path, bool requirePrivateAcl, bool includeAgentService)
    {
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (requirePrivateAcl ? !IsPrivateSecurity(security, isDirectory: false, includeAgentService) : !WindowsMaintenanceBackupSecurity.IsSourceSecuritySafe(security))
            throw new UnauthorizedAccessException("An Agent file owner or ACL is unsafe.");
    }

    private bool IsPrivateSecurity(FileSystemSecurity security, bool isDirectory, bool includeAgentService)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (!security.AreAccessRulesProtected || owner is null || (owner != system && owner != admins))
            return false;
        var inheritance = isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (rules.Length != (includeAgentService ? 3 : 2)) return false;
        var identities = new HashSet<SecurityIdentifier>();
        foreach (var rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IsInherited ||
                rule.PropagationFlags != PropagationFlags.None || rule.InheritanceFlags != inheritance ||
                rule.IdentityReference is not SecurityIdentifier sid || !identities.Add(sid))
                return false;
            var rights = sid == system || sid == admins
                ? FileSystemRights.FullControl
                : includeAgentService && sid == agentServiceSid
                    ? FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize
                    : (FileSystemRights)(-1);
            if (rights == (FileSystemRights)(-1) || rule.FileSystemRights != rights)
                return false;
        }
        return includeAgentService
            ? identities.SetEquals([system, admins, agentServiceSid])
            : identities.SetEquals([system, admins]);
    }

    private DirectorySecurity CreateInstallDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(agentServiceSid,
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private void SetInstallFileSecurity(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(agentServiceSid,
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private void EnsureInstallPath(string path)
    {
        if (!PathEquals(path, installDirectory))
            throw new UnauthorizedAccessException("The apply install path is not the canonical Agent installation directory.");
    }

    private void EnsureExtractedPath(string path)
    {
        if (!PathEquals(path, extractedTargetRoot))
            throw new UnauthorizedAccessException("The target extraction path does not match the fixed protected work directory.");
        EnsureNoReparseAncestors(path);
        EnsureTrustedAncestors(Path.GetDirectoryName(path)!);
    }

    private void EnsureInstallSibling(
        string path,
        bool allowInstallDirectory,
        bool allowStage = false,
        bool allowPrior = false,
        bool allowFailed = false,
        bool allowRestore = false,
        string? requiredPrefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (!PathEquals(Path.GetDirectoryName(full)!, installParent))
            throw new UnauthorizedAccessException("An Agent swap path escaped the canonical installation parent.");
        EnsureTrustedAncestors(installParent);
        var name = Path.GetFileName(full);
        if (allowInstallDirectory && name.Equals(Path.GetFileName(installDirectory), StringComparison.OrdinalIgnoreCase))
            return;
        var prefixes = new List<string>();
        if (allowStage) prefixes.Add(".Agent-stage-");
        if (allowPrior) prefixes.Add(".Agent-prior-");
        if (allowFailed) prefixes.Add(".Agent-failed-");
        if (allowRestore) prefixes.Add(".Agent-restore-");
        if (requiredPrefix is not null) prefixes = [requiredPrefix];
        if (!prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal) &&
                                   OperationIdPattern.IsMatch(name[prefix.Length..])))
            throw new UnauthorizedAccessException("An Agent swap path does not match a fixed operation-owned sibling name.");
    }

    private void EnsureTrustedAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(current)
            ?? throw new UnauthorizedAccessException("The Agent install parent has no volume root.");
        while (!string.IsNullOrEmpty(current))
        {
            if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("The Agent install ancestor is missing or is a reparse point.");
            var security = new DirectoryInfo(current).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            var safe = PathEquals(current, volumeRoot)
                ? WindowsMaintenanceBackupSecurity.IsSystemVolumeRootSecuritySafe((DirectorySecurity)security)
                : WindowsMaintenanceBackupSecurity.IsSourceSecuritySafe(security);
            if (!safe)
                throw new UnauthorizedAccessException("An untrusted principal can alter the Agent installation parent or its ACL.");
            if (PathEquals(current, volumeRoot)) break;
            current = Path.GetDirectoryName(current)
                ?? throw new UnauthorizedAccessException("The Agent install ancestor chain is incomplete.");
        }
    }

    private static void EnsureAbsent(string path)
    {
        EnsureNoReparseAncestors(path);
        if (TryGetAttributes(path, out _))
            throw new IOException("A fixed Agent recovery sibling already exists and requires reconciliation.");
    }

    private static void EnsureNoReparseAncestors(string path)
    {
        foreach (var ancestor in Ancestors(path))
            if (TryGetAttributes(ancestor, out var attributes) && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("An Agent apply path contains a reparse point.");
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

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { attributes = default; return false; }
        catch (DirectoryNotFoundException) { attributes = default; return false; }
    }

    private static string LocalPath(string root, string relativePath)
    {
        ValidateRelativePath(relativePath);
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!full.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An Agent inventory path escaped its fixed tree root.");
        return full;
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') ||
            path.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException("An Agent inventory contains an invalid relative path.");
    }

    private static bool PathEquals(string first, string second) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct WindowsFileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out WindowsFileInformation information);
}
