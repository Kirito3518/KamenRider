using KamenRaider.AgentMaintenance;
using KamenRaider.Contracts;
using System.Security.Cryptography;
using System.Text;

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

        Assert.Throws<InvalidOperationException>(() => journal.Advance("operation-1", "sourceBound", new MaintenanceJournalEvidence(
            SourceVersion: "1.0.0",
            SourceInventoryDigest: new string('e', 64))));
        Assert.Equal("backupPublished", journal.Read()!.Operations.Single().Phase);
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

    [Fact]
    public void SwapAndRollbackHistoriesAreDurableAndBindExactTargetAndOriginalBackup()
    {
        var store = new MemoryStore();
        var journal = new MaintenanceJournal(store, "chain-1");
        AdvanceToSourceBound(journal);
        var targetDigest = new string('f', 64);
        var backupDigest = new string('d', 64);

        Assert.Throws<InvalidOperationException>(() => journal.Advance("operation-1", "swapIntent"));
        journal.Advance("operation-1", "swapIntent", new MaintenanceJournalEvidence(TargetInventoryDigest: targetDigest));
        journal.Advance("operation-1", "sourceMoved");
        journal.Advance("operation-1", "targetPlaced");
        journal.Advance("operation-1", "rollbackIntent", new MaintenanceJournalEvidence(
            BackupDigest: backupDigest,
            TargetInventoryDigest: targetDigest,
            RollbackReason: "targetStartFailed"));
        Assert.Throws<InvalidOperationException>(() => journal.Advance("operation-1", "sourceRestored",
            new MaintenanceJournalEvidence(RestoredBackupDigest: new string('e', 64))));
        journal.Advance("operation-1", "targetMovedToFailed");
        journal.Advance("operation-1", "sourceRestored", new MaintenanceJournalEvidence(RestoredBackupDigest: backupDigest));
        journal.Advance("operation-1", "rollbackServicesStarted");
        journal.Advance("operation-1", "outcomeReported");
        var confirmed = journal.Advance("operation-1", "controllerConfirmed");

        Assert.Equal(new[]
        {
            "inspected", "controllerStarted", "targetValidated", "servicesQuiesced", "backupPublished",
            "sourceBound", "swapIntent", "sourceMoved", "targetPlaced", "rollbackIntent", "targetMovedToFailed",
            "sourceRestored", "rollbackServicesStarted", "outcomeReported", "controllerConfirmed"
        }, confirmed.Operations.Single().PhaseHistory);
        Assert.Equal(targetDigest, confirmed.Operations.Single().TargetInventoryDigest);
        Assert.Equal("targetStartFailed", confirmed.Operations.Single().RollbackReason);
        Assert.Equal(backupDigest, confirmed.Operations.Single().RestoredBackupDigest);
    }

    private static void AdvanceToSourceBound(MaintenanceJournal journal)
    {
        var binding = Binding();
        journal.Start(binding);
        journal.Advance(binding.OperationId, "controllerStarted");
        journal.Advance(binding.OperationId, "targetValidated");
        journal.Advance(binding.OperationId, "servicesQuiesced");
        journal.Advance(binding.OperationId, "backupPublished", new MaintenanceJournalEvidence(
            BackupOwnerOperationId: binding.OperationId, BackupDigest: new string('d', 64)));
        var challengeId = Guid.NewGuid().ToString("N");
        var challengeRequestId = StableSourceRequestId("source-challenge", binding.OperationId, 0);
        var sourceRequestId = StableSourceRequestId("source-binding", binding.OperationId, 0);
        journal.RecordSourceChallengeAttempt(binding.OperationId, new MaintenanceSourceChallengeAttempt(
            0, challengeRequestId, new string('e', 64)));
        var evidence = new AgentUpdateSourceEvidence(
            MachineId: "00112233445566778899aabbccddeeff",
            KeyFingerprint: binding.EnrollmentKeyFingerprint,
            OperationId: binding.OperationId,
            ChainId: binding.ChainId,
            HeadOperationId: binding.HeadOperationId,
            Generation: binding.Generation,
            TargetVersion: binding.TargetVersion,
            TargetPackageSha256: binding.TargetSha256,
            ChallengeId: challengeId,
            ChallengeRequestId: challengeRequestId,
            SourceRequestId: sourceRequestId,
            HelperBuildSha256: new string('c', 64),
            HelperRuntime: binding.TargetRuntime,
            HelperProtocol: binding.HelperProtocol,
            RuntimeEpoch: "runtime-1",
            SourceVersion: "1.0.0",
            BackupOwnerOperationId: binding.OperationId,
            BackupDigest: new string('d', 64),
            InventoryDigest: new string('b', 64),
            InstalledTreeDigest: new string('b', 64),
            NativeBootId: "boot-1",
            JournalPhase: "backupPublished");
        var intent = new MaintenanceSourceBindingIntent(evidence, 0, challengeId, "source-challenge", "runtime-1",
            DateTimeOffset.UtcNow.AddMinutes(5), new string('e', 64), Convert.ToBase64String(new byte[64]),
            sourceRequestId, new string('a', 64));
        _ = journal.RecordSourceBindingIntent(binding.OperationId, intent);
        journal.Advance(binding.OperationId, "sourceBound", new MaintenanceJournalEvidence(
            SourceVersion: evidence.SourceVersion, SourceInventoryDigest: evidence.InventoryDigest));
    }

    private static string StableSourceRequestId(string purpose, string operationId, int attemptNumber)
    {
        var bytes = Encoding.UTF8.GetBytes("KamenRaider.AgentMaintenance." + purpose + ".v1\n" + operationId + "\n" +
            attemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return purpose + "-" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
