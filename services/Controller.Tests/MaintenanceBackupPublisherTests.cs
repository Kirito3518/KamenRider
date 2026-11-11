using KamenRaider.AgentMaintenance;

namespace Controller.Tests;

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

    public void Dispose()
    {
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(root);
        if (resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("kr-maint-backup-", StringComparison.Ordinal))
            Directory.Delete(resolved, recursive: true);
    }
}
