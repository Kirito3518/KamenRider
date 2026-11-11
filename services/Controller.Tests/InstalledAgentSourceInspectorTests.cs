using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using KamenRaider.AgentMaintenance;

namespace Controller.Tests;

public sealed class InstalledAgentSourceInspectorTests : IDisposable
{
    private sealed class BackupSecurity(string sourceRoot, string backupRoot) : IMaintenanceBackupSecurity
    {
        public void EnsureSourceRoot(string path)
        {
            Assert.Equal(Path.GetFullPath(sourceRoot), Path.GetFullPath(path));
            Assert.True(Directory.Exists(path));
        }

        public void EnsureSourceDirectory(string path) => Assert.True(Directory.Exists(path));

        public void EnsureBackupDirectory(string path, bool create)
        {
            Assert.Equal(Path.GetFullPath(backupRoot), Path.GetFullPath(path));
            if (create) Directory.CreateDirectory(path);
            Assert.True(Directory.Exists(path));
        }

        public void EnsurePrivateDirectory(string path, bool create)
        {
            if (create) Directory.CreateDirectory(path);
            Assert.True(Directory.Exists(path));
        }

        public Stream OpenSourceFile(string path) => File.OpenRead(path);
        public Stream OpenPrivateFile(string path) => File.OpenRead(path);
        public Stream CreateBackupFile(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        public void EnsurePrivateFile(string path) => Assert.True(File.Exists(path));
        public void FlushDirectory(string path) => Assert.True(Directory.Exists(path));
        public void PublishDirectory(string stagingDirectory, string publishedDirectory) => Directory.Move(stagingDirectory, publishedDirectory);
    }

    private sealed class Authorization : IMaintenanceBackupAuthorization
    {
        public int Checks { get; private set; }
        public Action<int>? OnCheck { get; set; }

        public void VerifyHeldAndQuiesced(string operationId)
        {
            Assert.Equal("operation-1", operationId);
            Checks++;
            OnCheck?.Invoke(Checks);
        }
    }

    private sealed class MemoryJournalStore : IMaintenanceJournalStore
    {
        public MaintenanceJournalSnapshot? Snapshot { get; private set; }
        public bool FailNextWrite { get; set; }
        public MaintenanceJournalSnapshot? Read(string chainId) => Snapshot;

        public void WriteAtomic(string chainId, MaintenanceJournalSnapshot snapshot)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("simulated journal flush failure");
            }
            Snapshot = snapshot;
        }
    }

    private readonly string root = Path.Combine(Path.GetTempPath(), "kr-source-inspector-" + Guid.NewGuid().ToString("N"));
    private readonly string sourceRoot;
    private readonly string backupRoot;
    private readonly string fixtureOutputPath;

    public InstalledAgentSourceInspectorTests()
    {
        var metadata = typeof(InstalledAgentSourceInspectorTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "LegacyInstalledAgentOutputPath");
        fixtureOutputPath = Path.GetFullPath(metadata.Value!);
        sourceRoot = Path.Combine(root, "installed", "Agent");
        backupRoot = Path.Combine(root, "backups");
        CopyFixtureTree();
    }

    [Fact]
    public void ProtocolOneInstalledTreeUsesOneDepsEntryAndMatchingAgentAssemblyMetadata()
    {
        var snapshot = new InstalledAgentSourceInspector(sourceRoot, NewSecurity()).Inspect();

        Assert.Equal("1.2.3", snapshot.Version);
        Assert.Equal("1.2.3", snapshot.AssemblyInformationalVersion);
        Assert.Equal(1, snapshot.AgentUpdateChallengeProtocol);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(sourceRoot, "Agent.deps.json")))), snapshot.DepsSha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(sourceRoot, "Agent.dll")))), snapshot.AgentAssemblySha256);
    }

    [Fact]
    public void MultipleAgentProjectEntriesAreUnverifiable()
    {
        AddSecondAgentLibrary("Agent/9.9.9");

        Assert.Throws<InvalidDataException>(() => new InstalledAgentSourceInspector(sourceRoot, NewSecurity()).Inspect());
    }

    [Fact]
    public void DllInformationalVersionMustMatchTheUniqueDepsProjectVersion()
    {
        ReplaceDepsLibraryKey("Agent/1.2.3", "Agent/9.9.9");

        Assert.Throws<InvalidDataException>(() => new InstalledAgentSourceInspector(sourceRoot, NewSecurity()).Inspect());
    }

    [Fact]
    public void BackupRequiresQuiescedJournalAndAtomicallyRecordsPublishedBackupEvidence()
    {
        var journalStore = NewJournalAt("servicesQuiesced");
        var journal = new MaintenanceJournal(journalStore, "chain-1");
        var security = NewSecurity();
        var authorization = new Authorization();
        var publisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, security, authorization);
        var operation = new MaintenanceBackupOperation(sourceRoot, new InstalledAgentSourceInspector(sourceRoot, security), publisher, journal);

        var backup = operation.Publish("operation-1");
        var retry = operation.Publish("operation-1");

        var recorded = journal.Read()!.Operations.Single();
        Assert.Equal("1.2.3", backup.Source.Version);
        Assert.Equal("backupPublished", recorded.Phase);
        Assert.Equal("operation-1", recorded.BackupOwnerOperationId);
        Assert.Equal(backup.Backup.BackupDigest, recorded.BackupDigest);
        Assert.Equal(backup.Backup.BackupDigest, retry.Backup.BackupDigest);
        Assert.Contains(backup.Backup.Nodes, node => node.RelativePath == "Agent.deps.json");
        Assert.Equal(3, authorization.Checks);
    }

    [Fact]
    public void BackupRejectsSameSourceAndTargetVersionBeforeCreatingBackupRoot()
    {
        var journalStore = NewJournalAt("servicesQuiesced", targetVersion: "1.2.3");
        var security = NewSecurity();
        var publisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, security, new Authorization());
        var operation = new MaintenanceBackupOperation(sourceRoot,
            new InstalledAgentSourceInspector(sourceRoot, security), publisher,
            new MaintenanceJournal(journalStore, "chain-1"));

        Assert.Throws<InvalidOperationException>(() => { _ = operation.Publish("operation-1"); });
        Assert.False(Directory.Exists(backupRoot));
    }

    [Fact]
    public void BackupRetryRecoversIfJournalWriteFailsAfterAtomicPublication()
    {
        var journalStore = NewJournalAt("servicesQuiesced");
        journalStore.FailNextWrite = true;
        var security = NewSecurity();
        var authorization = new Authorization();
        var publisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, security, authorization);
        var operation = new MaintenanceBackupOperation(sourceRoot,
            new InstalledAgentSourceInspector(sourceRoot, security), publisher,
            new MaintenanceJournal(journalStore, "chain-1"));

        Assert.Throws<IOException>(() => { _ = operation.Publish("operation-1"); });
        Assert.True(Directory.Exists(Path.Combine(backupRoot, "operation-1")));
        Assert.Equal("servicesQuiesced", journalStore.Snapshot!.Operations.Single().Phase);

        var retry = operation.Publish("operation-1");
        Assert.Equal("backupPublished", journalStore.Snapshot.Operations.Single().Phase);
        Assert.Equal(retry.Backup.BackupDigest, journalStore.Snapshot.Operations.Single().BackupDigest);
    }

    [Fact]
    public void BackupFailsHeldWhenSourceMetadataChangesBetweenInspectionAndSnapshot()
    {
        var journalStore = NewJournalAt("servicesQuiesced");
        var security = NewSecurity();
        var authorization = new Authorization
        {
            OnCheck = check =>
            {
                if (check == 1)
                    File.AppendAllText(Path.Combine(sourceRoot, "Agent.dll"), "changed");
            }
        };
        var publisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, security, authorization);
        var operation = new MaintenanceBackupOperation(sourceRoot,
            new InstalledAgentSourceInspector(sourceRoot, security), publisher,
            new MaintenanceJournal(journalStore, "chain-1"));

        Assert.Throws<InvalidOperationException>(() => { _ = operation.Publish("operation-1"); });
        Assert.Equal("servicesQuiesced", journalStore.Snapshot!.Operations.Single().Phase);
        Assert.True(Directory.Exists(Path.Combine(backupRoot, "operation-1")));
    }

    [Fact]
    public void BackupRequiresTheJournalToBeQuiescedBeforeTouchingBackupStorage()
    {
        var journalStore = NewJournalAt("targetValidated");
        var security = NewSecurity();
        var publisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, security, new Authorization());
        var operation = new MaintenanceBackupOperation(sourceRoot,
            new InstalledAgentSourceInspector(sourceRoot, security), publisher,
            new MaintenanceJournal(journalStore, "chain-1"));

        Assert.Throws<InvalidOperationException>(() => { _ = operation.Publish("operation-1"); });
        Assert.False(Directory.Exists(backupRoot));
    }

    private BackupSecurity NewSecurity() => new(sourceRoot, backupRoot);

    private MemoryJournalStore NewJournalAt(string phase, string targetVersion = "2.3.4")
    {
        var phases = new[] { "inspected", "controllerStarted", "targetValidated", "servicesQuiesced" };
        var targetIndex = Array.IndexOf(phases, phase);
        Assert.True(targetIndex >= 0);
        var store = new MemoryJournalStore();
        var journal = new MaintenanceJournal(store, "chain-1");
        journal.Start(new MaintenanceOperationBinding("operation-1", "chain-1", "operation-1", 1,
            "https://controller.example/", new string('a', 64), 1, "win-x64", targetVersion, new string('b', 64)));
        foreach (var next in phases.Skip(1).Take(targetIndex))
            journal.Advance("operation-1", next);
        return store;
    }

    private void CopyFixtureTree()
    {
        Assert.True(File.Exists(Path.Combine(fixtureOutputPath, "Agent.exe")), "The legacy installed fixture apphost was not built.");
        Assert.True(File.Exists(Path.Combine(fixtureOutputPath, "Agent.dll")), "The legacy installed fixture DLL was not built.");
        Assert.True(File.Exists(Path.Combine(fixtureOutputPath, "Agent.deps.json")), "The legacy installed fixture deps file was not built.");
        Assert.True(File.Exists(Path.Combine(fixtureOutputPath, "Agent.runtimeconfig.json")), "The legacy installed fixture runtime config was not built.");
        Directory.CreateDirectory(sourceRoot);
        foreach (var file in Directory.EnumerateFiles(fixtureOutputPath))
            File.Copy(file, Path.Combine(sourceRoot, Path.GetFileName(file)));
    }

    private void AddSecondAgentLibrary(string key)
    {
        var path = Path.Combine(sourceRoot, "Agent.deps.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document["libraries"]!.AsObject()[key] = new JsonObject { ["type"] = "project" };
        File.WriteAllText(path, document.ToJsonString());
    }

    private void ReplaceDepsLibraryKey(string current, string replacement)
    {
        var path = Path.Combine(sourceRoot, "Agent.deps.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var libraries = document["libraries"]!.AsObject();
        var value = libraries[current]!.DeepClone();
        libraries.Remove(current);
        libraries[replacement] = value;
        var targets = document["targets"]!.AsObject();
        foreach (var target in targets.Select(property => property.Value!.AsObject()))
        {
            if (target[current] is not { } entry) continue;
            target.Remove(current);
            target[replacement] = entry.DeepClone();
        }
        File.WriteAllText(path, document.ToJsonString());
    }

    public void Dispose()
    {
        if (Directory.Exists(root) && Path.GetFileName(root).StartsWith("kr-source-inspector-", StringComparison.Ordinal))
            Directory.Delete(root, recursive: true);
    }
}
