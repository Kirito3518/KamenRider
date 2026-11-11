using KamenRaider.AgentMaintenance;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Controller.Tests;

public sealed class WindowsAclPredicateFactAttribute : FactAttribute
{
    public WindowsAclPredicateFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "The source ACL predicate consumes Windows security descriptors.";
    }
}

public sealed class MaintenanceBackupPublisherTests : IDisposable
{
    private sealed class TestBackupSecurity : IMaintenanceBackupSecurity
    {
        public void EnsureSourceRoot(string path) => Assert.True(Directory.Exists(path));
        public void EnsureSourceDirectory(string path) => Assert.True(Directory.Exists(path));
        public void EnsureBackupDirectory(string path, bool create)
        {
            if (create) Directory.CreateDirectory(path);
            Assert.True(Directory.Exists(path));
        }
        public void EnsurePrivateDirectory(string path, bool create)
        {
            if (create) Directory.CreateDirectory(path);
            Assert.True(Directory.Exists(path));
        }
        public Stream OpenSourceFile(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        public Stream OpenPrivateFile(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        public Stream CreateBackupFile(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
        public void EnsurePrivateFile(string path) => Assert.True(File.Exists(path));
        public void FlushDirectory(string path) => Assert.True(Directory.Exists(path));
        public void PublishDirectory(string stagingDirectory, string publishedDirectory) => Directory.Move(stagingDirectory, publishedDirectory);
    }

    private sealed class TestBackupAuthorization : IMaintenanceBackupAuthorization
    {
        public int Checks;
        public bool Allow = true;
        public int? RejectAtCheck;
        public void VerifyHeldAndQuiesced(string operationId)
        {
            Checks++;
            if (!Allow || Checks == RejectAtCheck) throw new InvalidOperationException("Installation is not held and quiesced.");
        }
    }

    private readonly string root = Path.Combine(Path.GetTempPath(), "kr-maint-backup-" + Guid.NewGuid().ToString("N"));
    private readonly string source;
    private readonly string backups;

    public MaintenanceBackupPublisherTests()
    {
        source = Path.Combine(root, "installed", "Agent");
        backups = Path.Combine(root, "backups");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "Agent.dll"), "verified-agent-bytes");
        File.WriteAllBytes(Path.Combine(source, "nested", "data.bin"), [0, 1, 2, 3, 4]);
        Directory.CreateDirectory(Path.Combine(source, "empty"));
    }

    [Fact]
    public void PublishCreatesOwnedImmutableBackupAndExactRetryVerifiesItsDigest()
    {
        var authorization = new TestBackupAuthorization();
        var publisher = new MaintenanceBackupPublisher(source, backups, new TestBackupSecurity(), authorization);

        var backup = publisher.Publish("operation-1");
        var retry = publisher.Publish("operation-1");

        Assert.Equal("operation-1", backup.BackupOwnerOperationId);
        Assert.Equal(backup.BackupDigest, retry.BackupDigest);
        Assert.Equal(backup.InventoryDigest, retry.InventoryDigest);
        Assert.True(File.Exists(Path.Combine(backup.DirectoryPath, "Agent.dll")));
        Assert.True(File.Exists(Path.Combine(backup.DirectoryPath, "nested", "data.bin")));
        Assert.True(Directory.Exists(Path.Combine(backup.DirectoryPath, "empty")));
        Assert.Equal("verified-agent-bytes", File.ReadAllText(Path.Combine(backup.DirectoryPath, "Agent.dll")));
        Assert.Equal(64, backup.BackupDigest.Length);
        Assert.Equal(3, authorization.Checks);
    }

    [Fact]
    public void ExactRetryRejectsChangedPublishedBackupInsteadOfRepairingIt()
    {
        var publisher = new MaintenanceBackupPublisher(source, backups, new TestBackupSecurity(), new TestBackupAuthorization());
        var backup = publisher.Publish("operation-1");
        File.WriteAllText(Path.Combine(backup.DirectoryPath, "Agent.dll"), "modified");

        Assert.Throws<InvalidDataException>(() => publisher.Publish("operation-1"));
        Assert.Equal("modified", File.ReadAllText(Path.Combine(backup.DirectoryPath, "Agent.dll")));
    }

    [Fact]
    public void EmptyInstalledTreeCannotPublishAnEmptyRollbackBackup()
    {
        var emptySource = Path.Combine(root, "empty-source");
        Directory.CreateDirectory(emptySource);
        var publisher = new MaintenanceBackupPublisher(emptySource, backups, new TestBackupSecurity(), new TestBackupAuthorization());

        Assert.Throws<InvalidDataException>(() => publisher.Publish("empty-operation"));
        Assert.False(Directory.Exists(Path.Combine(backups, "empty-operation")));
        Assert.False(Directory.Exists(Path.Combine(backups, ".pending-empty-operation")));
    }

    [Theory]
    [InlineData("..\\outside")]
    [InlineData("bad/operation")]
    [InlineData("")]
    public void PublishRejectsUnsafeOperationIdentifiers(string operationId)
    {
        var publisher = new MaintenanceBackupPublisher(source, backups, new TestBackupSecurity(), new TestBackupAuthorization());

        Assert.Throws<InvalidDataException>(() => publisher.Publish(operationId));
        Assert.False(Directory.Exists(backups));
    }

    [Fact]
    public void BackupRefusesToTouchTheTreeWhenHeldAndQuiescedProofIsUnavailable()
    {
        var authorization = new TestBackupAuthorization { Allow = false };
        var publisher = new MaintenanceBackupPublisher(source, backups, new TestBackupSecurity(), authorization);

        Assert.Throws<InvalidOperationException>(() => publisher.Publish("operation-1"));
        Assert.Equal(1, authorization.Checks);
        Assert.False(Directory.Exists(backups));
    }

    [Fact]
    public void BackupRetainsStagingTreeWhenHeldAndQuiescedProofIsLostBeforePublication()
    {
        var authorization = new TestBackupAuthorization { RejectAtCheck = 2 };
        var publisher = new MaintenanceBackupPublisher(source, backups, new TestBackupSecurity(), authorization);

        Assert.Throws<InvalidOperationException>(() => publisher.Publish("operation-1"));
        Assert.Equal(2, authorization.Checks);
        Assert.False(Directory.Exists(Path.Combine(backups, "operation-1")));
        Assert.True(Directory.Exists(Path.Combine(backups, ".pending-operation-1")));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void WindowsDirectoryFlushUsesTheNativeDirectoryHandle()
    {
        Assert.True(OperatingSystem.IsWindows());
        var path = Path.Combine(Path.GetTempPath(), "kr-maint-flush-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            WindowsMaintenanceBackupSecurity.FlushDirectoryToDisk(path);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [WindowsAclPredicateFact]
    [SupportedOSPlatform("windows")]
    public void ProductionSourceAclPredicateAllowsOnlyPrivilegedOwnersAndRejectsWriteDacAndWriteOwner()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var interactiveUser = WindowsIdentity.GetCurrent().User!;

        Assert.True(WindowsMaintenanceBackupSecurity.IsSourceSecuritySafe(SourceAcl(system, users, FileSystemRights.ReadAndExecute)));
        Assert.True(WindowsMaintenanceBackupSecurity.IsSourceSecuritySafe(SourceAcl(admins, users, FileSystemRights.ReadAndExecute)));
        Assert.True(WindowsMaintenanceBackupSecurity.IsSourceSecuritySafe(SourceDirectoryAcl(admins, users,
            FileSystemRights.ReadAndExecute, includeCreatorOwner: true)));

        foreach (var dangerousRight in new[]
        {
            FileSystemRights.ChangePermissions,
            FileSystemRights.TakeOwnership,
            FileSystemRights.WriteData,
            FileSystemRights.AppendData,
            FileSystemRights.CreateFiles,
            FileSystemRights.CreateDirectories,
            FileSystemRights.Delete,
            FileSystemRights.DeleteSubdirectoriesAndFiles,
            FileSystemRights.WriteAttributes,
            FileSystemRights.WriteExtendedAttributes,
            FileSystemRights.Modify,
            FileSystemRights.FullControl
        })
            Assert.False(WindowsMaintenanceBackupSecurity.IsSourceSecuritySafe(SourceAcl(admins, users, dangerousRight)),
                $"An unprivileged {dangerousRight} allow ACE must invalidate the source snapshot.");

        Assert.False(WindowsMaintenanceBackupSecurity.IsSourceSecuritySafe(SourceAcl(users, users, FileSystemRights.ReadAndExecute)));
        Assert.False(WindowsMaintenanceBackupSecurity.IsSourceSecuritySafe(SourceAcl(admins, interactiveUser, FileSystemRights.FullControl)));
        Assert.False(WindowsMaintenanceBackupSecurity.IsSourceSecuritySafe(SourceDirectoryAcl(admins, users,
            FileSystemRights.DeleteSubdirectoriesAndFiles, includeCreatorOwner: true)));
    }

    [WindowsAclPredicateFact]
    [SupportedOSPlatform("windows")]
    public void ProductionVolumeRootPredicateAllowsOnlyDocumentedWindowsRootMutation()
    {
        var authenticatedUsers = new SecurityIdentifier("S-1-5-11");
        var safe = SourceVolumeRootAcl(authenticatedUsers, FileSystemRights.CreateDirectories);

        Assert.True(WindowsMaintenanceBackupSecurity.IsSystemVolumeRootSecuritySafe(safe));

        foreach (var dangerousRight in new[]
        {
            FileSystemRights.ChangePermissions,
            FileSystemRights.TakeOwnership,
            FileSystemRights.WriteData,
            FileSystemRights.CreateFiles,
            FileSystemRights.Delete,
            FileSystemRights.DeleteSubdirectoriesAndFiles,
            FileSystemRights.WriteAttributes,
            FileSystemRights.WriteExtendedAttributes,
            FileSystemRights.Modify,
            FileSystemRights.FullControl
        })
            Assert.False(WindowsMaintenanceBackupSecurity.IsSystemVolumeRootSecuritySafe(
                SourceVolumeRootAcl(authenticatedUsers, dangerousRight)),
                $"The volume root must reject Authenticated Users {dangerousRight}.");

        var userOwnedRoot = SourceVolumeRootAcl(authenticatedUsers, FileSystemRights.CreateDirectories);
        userOwnedRoot.SetOwner(WindowsIdentity.GetCurrent().User!);
        Assert.False(WindowsMaintenanceBackupSecurity.IsSystemVolumeRootSecuritySafe(userOwnedRoot));

        var interactiveUserWritable = SourceVolumeRootAcl(authenticatedUsers, FileSystemRights.CreateDirectories);
        interactiveUserWritable.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.FullControl, AccessControlType.Allow));
        Assert.False(WindowsMaintenanceBackupSecurity.IsSystemVolumeRootSecuritySafe(interactiveUserWritable));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ProductionVolumeRootPredicateAcceptsTheActualWindowsVolumeRoot()
    {
        var volumeRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        var acl = new DirectoryInfo(volumeRoot).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);

        Assert.True(WindowsMaintenanceBackupSecurity.IsSystemVolumeRootSecuritySafe(acl),
            $"The actual Windows volume-root ACL at {volumeRoot} did not match the documented safe policy.");
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity SourceAcl(SecurityIdentifier owner, SecurityIdentifier untrusted, FileSystemRights untrustedRights)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(owner);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(untrusted, untrustedRights, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity SourceDirectoryAcl(SecurityIdentifier owner, SecurityIdentifier untrusted,
        FileSystemRights untrustedRights, bool includeCreatorOwner)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(owner);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(untrusted, untrustedRights,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (includeCreatorOwner)
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null), FileSystemRights.FullControl,
                inheritance, PropagationFlags.InheritOnly, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity SourceVolumeRootAcl(SecurityIdentifier authenticatedUsers, FileSystemRights mutationRight)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var trustedInstaller = new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
        var dacl = new RawAcl(GenericAcl.AclRevision, 5);
        void AddAllow(SecurityIdentifier sid, int mask, AceFlags flags) =>
            dacl.InsertAce(dacl.Count, new CommonAce(flags, AceQualifier.AccessAllowed, mask, sid, isCallback: false, opaque: null));
        var inherited = AceFlags.ContainerInherit | AceFlags.ObjectInherit;
        AddAllow(system, (int)FileSystemRights.FullControl, inherited);
        AddAllow(admins, (int)FileSystemRights.FullControl, inherited);
        AddAllow(trustedInstaller, (int)FileSystemRights.FullControl, inherited);
        AddAllow(authenticatedUsers, (int)mutationRight, AceFlags.None);
        // This inherited mask is present in the documented Windows volume-root ACL. It is
        // inherit-only and cannot grant these generic rights on the volume root itself.
        AddAllow(authenticatedUsers, -536805376, inherited | AceFlags.InheritOnly);
        var descriptor = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent |
                ControlFlags.DiscretionaryAclProtected | ControlFlags.SelfRelative,
            trustedInstaller, admins, null, dacl);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Owner | AccessControlSections.Access);
        return security;
    }

    public void Dispose()
    {
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(root);
        if (resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("kr-maint-backup-", StringComparison.Ordinal))
            Directory.Delete(resolved, recursive: true);
    }
}
