using KamenRaider.AgentMaintenance;

namespace Controller.Tests;

public sealed class MaintenanceJournalTests
{
    private sealed class MemoryStore : IMaintenanceJournalStore
    {
        public MaintenanceJournalSnapshot? Current;
        public int Writes;

        public MaintenanceJournalSnapshot? Read(string chainId) => Current;

        public void WriteAtomic(string chainId, MaintenanceJournalSnapshot snapshot)
        {
            Writes++;
            Current = snapshot;
        }
    }

    private static MaintenanceOperationBinding Binding() => new(
        OperationId: "operation-1",
        ChainId: "chain-1",
        HeadOperationId: "operation-1",
        Generation: 1,
        ControllerOrigin: "https://controller.example/",
        EnrollmentKeyFingerprint: new string('a', 64),
        HelperProtocol: 1,
        TargetRuntime: "win-x64",
        TargetVersion: "2.0.0",
        TargetSha256: new string('b', 64));

    [Fact]
    public void StartPersistsBoundOperationAtInspectedAndExactRetryIsIdempotent()
    {
        var store = new MemoryStore();
        var journal = new MaintenanceJournal(store, "chain-1");

        var created = journal.Start(Binding());
        var retried = journal.Start(Binding());

        Assert.Equal("inspected", created.Operations.Single().Phase);
        Assert.Equal(new[] { "inspected" }, created.Operations.Single().PhaseHistory);
        Assert.Equal(created, retried);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public void StartRejectsSameOperationIdWithDifferentImmutableBinding()
    {
        var store = new MemoryStore();
        var journal = new MaintenanceJournal(store, "chain-1");
        journal.Start(Binding());

        Assert.Throws<InvalidOperationException>(() =>
            journal.Start(Binding() with { TargetSha256 = new string('c', 64) }));
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public void AdvanceRequiresDurableConsecutivePhasesAndBackupBeforeSourceBinding()
    {
        var store = new MemoryStore();
        var journal = new MaintenanceJournal(store, "chain-1");
        journal.Start(Binding());

        Assert.Throws<InvalidOperationException>(() => journal.Advance("operation-1", "targetValidated"));
        journal.Advance("operation-1", "controllerStarted");
        journal.Advance("operation-1", "targetValidated");
        journal.Advance("operation-1", "servicesQuiesced");
        Assert.Throws<InvalidOperationException>(() => journal.Advance("operation-1", "backupPublished"));
        journal.Advance("operation-1", "backupPublished", new MaintenanceJournalEvidence(
            BackupOwnerOperationId: "operation-1",
            BackupDigest: new string('d', 64)));
        Assert.Throws<InvalidOperationException>(() => journal.Advance("operation-1", "sourceBound"));

        var bound = journal.Advance("operation-1", "sourceBound", new MaintenanceJournalEvidence(
            SourceVersion: "1.0.0",
            SourceInventoryDigest: new string('e', 64)));

        Assert.Equal(new[] { "inspected", "controllerStarted", "targetValidated", "servicesQuiesced", "backupPublished", "sourceBound" },
            bound.Operations.Single().PhaseHistory);
        Assert.Equal(new string('d', 64), bound.Operations.Single().BackupDigest);
        Assert.Equal(new string('e', 64), bound.Operations.Single().SourceInventoryDigest);
    }

    [Fact]
    public void AdvanceRejectsSourceEqualToTargetAndDoesNotPublishIt()
    {
        var store = new MemoryStore();
        var journal = new MaintenanceJournal(store, "chain-1");
        journal.Start(Binding());
        journal.Advance("operation-1", "controllerStarted");
        journal.Advance("operation-1", "targetValidated");
        journal.Advance("operation-1", "servicesQuiesced");
        journal.Advance("operation-1", "backupPublished", new MaintenanceJournalEvidence(
            BackupOwnerOperationId: "operation-1",
            BackupDigest: new string('d', 64)));
        var writesBefore = store.Writes;

        Assert.Throws<InvalidOperationException>(() => journal.Advance("operation-1", "sourceBound", new MaintenanceJournalEvidence(
            SourceVersion: "2.0.0",
            SourceInventoryDigest: new string('e', 64))));

        Assert.Equal(writesBefore, store.Writes);
        Assert.Equal("backupPublished", store.Current!.Operations.Single().Phase);
    }
}
