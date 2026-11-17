using System.Security.Cryptography;
using System.Text;
using KamenRaider.AgentMaintenance;
using KamenRaider.Contracts;

namespace Controller.Tests;

public sealed class MaintenanceReconcileCommandTests
{
    private sealed class WorkStore(MaintenanceCommandRequest request) : IMaintenanceWorkStore
    {
        public int RequestReads;
        public int ArchiveOpens;
        public string ExtractionRoot => Path.Combine(Path.GetTempPath(), "kr-reconcile-unused");

        public MaintenanceCommandRequest ReadRequest(string expectedCommand)
        {
            RequestReads++;
            Assert.Equal("apply", expectedCommand);
            return request;
        }

        public Stream OpenArchiveRead()
        {
            ArchiveOpens++;
            throw new InvalidOperationException("Reconciliation must not open the archive before journal eligibility is established.");
        }

        public void CreateExtractionRoot() => throw new InvalidOperationException("Reconciliation must not use the generic extraction root.");
    }

    private sealed class JournalStore(MaintenanceJournalSnapshot? snapshot) : IMaintenanceJournalStore
    {
        public int Reads;

        public MaintenanceJournalSnapshot? Read(string chainId)
        {
            Reads++;
            Assert.Equal("chain-1", chainId);
            return snapshot;
        }

        public void WriteAtomic(string chainId, MaintenanceJournalSnapshot next) =>
            throw new InvalidOperationException("The eligibility check must be read-only.");
    }

    private sealed class ExclusionProvider(IMaintenanceApplyProcessExclusionLease? lease)
        : IMaintenanceApplyProcessExclusionProvider
    {
        public int Acquires;
        public IMaintenanceApplyProcessExclusionLease? Acquire()
        {
            Acquires++;
            return lease;
        }
    }

    private sealed class ExclusionLease : IMaintenanceApplyProcessExclusionLease
    {
        public int Verifications;
        public bool Disposed;

        public void VerifyContinuouslyHeld(string operationId)
        {
            Verifications++;
            Assert.Equal("op-1", operationId);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class ReconcileExecutor : IMaintenanceReconcileExecutor
    {
        public int Calls;
        public MaintenanceCommandRequest? Request;
        public IMaintenanceApplyProcessExclusionLease? Lease;

        public AgentMaintenanceCommandResult Execute(
            MaintenanceCommandRequest request,
            IMaintenanceApplyProcessExclusionLease exclusion)
        {
            Calls++;
            Request = request;
            Lease = exclusion;
            return new("ok", null, null, request.TargetVersion, request.TargetRuntime, null, null, null,
                ApplyOutcome: "reconciled");
        }
    }

    private static MaintenanceCommandRequest ApplyRequest() => new(
        1, "apply", "op-1", "2.3.4", "win-x64", new string('b', 64), 12,
        "chain-1", "op-1", 1);

    [Fact]
    public void ReconcileFailsBeforeReadingOrMutatingWhenContinuousExclusionIsUnavailable()
    {
        var request = ApplyRequest();
        var work = new WorkStore(request);
        var journalStore = new JournalStore(null);
        var exclusion = new ExclusionProvider(null);
        var executor = new ReconcileExecutor();
        var command = new MaintenanceReconcileCommand(new LocalUpdateEngine(work), journalStore, exclusion, executor);

        var result = command.Execute();

        Assert.Equal("unavailable", result.Status);
        Assert.Equal("legacyProcessExclusionUnqualified", result.Category);
        Assert.Equal(1, exclusion.Acquires);
        Assert.Equal(0, work.RequestReads);
        Assert.Equal(0, journalStore.Reads);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public void ReconcileDoesNotTreatLegacyImportedJournalAsACompletedCurrentOperation()
    {
        var request = ApplyRequest();
        var binding = Binding();
        var legacySnapshot = new MaintenanceJournalSnapshot(1, "chain-1", "op-1", 1,
        [
            new(binding, "legacyImported", ["legacyImported"])
        ]);
        var work = new WorkStore(request);
        var journalStore = new JournalStore(legacySnapshot);
        var lease = new ExclusionLease();
        var exclusion = new ExclusionProvider(lease);
        var executor = new ReconcileExecutor();
        var command = new MaintenanceReconcileCommand(new LocalUpdateEngine(work), journalStore, exclusion, executor);

        var result = command.Execute();

        Assert.Equal("unavailable", result.Status);
        Assert.Equal("journalUnverifiable", result.Category);
        Assert.Equal(1, work.RequestReads);
        Assert.Equal(1, journalStore.Reads);
        Assert.Equal(0, lease.Verifications);
        Assert.True(lease.Disposed);
        Assert.Equal(0, executor.Calls);
        Assert.Equal(0, work.ArchiveOpens);
    }

    [Fact]
    public void ReconcilePassesOnlyAValidatedCurrentRecoveryPhaseToTheApplyExecutor()
    {
        var request = ApplyRequest();
        var store = new JournalStore(CreateServicesStartedJournal());
        var work = new WorkStore(request);
        var lease = new ExclusionLease();
        var exclusion = new ExclusionProvider(lease);
        var executor = new ReconcileExecutor();
        var command = new MaintenanceReconcileCommand(new LocalUpdateEngine(work), store, exclusion, executor);

        var result = command.Execute();

        Assert.Equal("ok", result.Status);
        Assert.Equal("reconciled", result.ApplyOutcome);
        Assert.Equal(1, executor.Calls);
        Assert.Equal(request, executor.Request);
        Assert.Same(lease, executor.Lease);
        Assert.Equal(1, lease.Verifications);
        Assert.True(lease.Disposed);
        Assert.Equal(0, work.ArchiveOpens);
    }

    internal static MaintenanceJournalSnapshot CreateServicesStartedJournal(MaintenanceOperationBinding? binding = null)
    {
        var store = new RecordingJournalStore();
        var journal = new MaintenanceJournal(store, "chain-1");
        binding ??= Binding();
        journal.Start(binding);
        journal.Advance("op-1", "controllerStarted");
        journal.Advance("op-1", "targetValidated");
        journal.Advance("op-1", "servicesQuiesced");
        var inventory = new string('c', 64);
        journal.Advance("op-1", "backupPublished",
            new MaintenanceJournalEvidence(BackupOwnerOperationId: "op-1", BackupDigest: new string('d', 64)));

        var challengeRequestId = StableRequestId("source-challenge", "op-1", 0);
        var sourceRequestId = StableRequestId("source-binding", "op-1", 0);
        journal.RecordSourceChallengeAttempt("op-1", new(0, challengeRequestId, new string('e', 64)));
        var evidence = new AgentUpdateSourceEvidence(
            "0123456789abcdef0123456789abcdef", binding.EnrollmentKeyFingerprint,
            binding.OperationId, binding.ChainId, binding.HeadOperationId, binding.Generation,
            binding.TargetVersion, binding.TargetSha256, "0123456789abcdef0123456789abcdef",
            challengeRequestId, sourceRequestId, new string('f', 64), binding.TargetRuntime,
            binding.HelperProtocol, "runtime-1", "1.2.3", "op-1", new string('d', 64),
            inventory, inventory, "native-boot-1", "backupPublished");
        var intent = new MaintenanceSourceBindingIntent(evidence, 0, evidence.ChallengeId,
            "challenge-token", evidence.RuntimeEpoch, DateTimeOffset.Parse("2026-11-12T00:00:00Z"),
            new string('e', 64), Convert.ToBase64String([1]), sourceRequestId, new string('a', 64));
        journal.RecordSourceBindingIntent("op-1", intent);
        journal.Advance("op-1", "sourceBound",
            new MaintenanceJournalEvidence(SourceVersion: "1.2.3", SourceInventoryDigest: inventory));
        journal.Advance("op-1", "swapIntent", new MaintenanceJournalEvidence(TargetInventoryDigest: new string('9', 64)));
        journal.Advance("op-1", "sourceMoved");
        journal.Advance("op-1", "targetPlaced");
        journal.Advance("op-1", "servicesStarted");
        return store.Snapshot!;
    }

    private static MaintenanceOperationBinding Binding() => new(
        "op-1", "chain-1", "op-1", 1, "https://controller.example/",
        new string('a', 64), 1, "win-x64", "2.3.4", new string('b', 64));

    private static string StableRequestId(string purpose, string operationId, int attemptNumber)
    {
        var input = "KamenRaider.AgentMaintenance." + purpose + ".v1\n" + operationId + "\n" +
            attemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return purpose + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private sealed class RecordingJournalStore : IMaintenanceJournalStore
    {
        public MaintenanceJournalSnapshot? Snapshot;
        public MaintenanceJournalSnapshot? Read(string chainId) => Snapshot;
        public void WriteAtomic(string chainId, MaintenanceJournalSnapshot snapshot) => Snapshot = snapshot;
    }
}
