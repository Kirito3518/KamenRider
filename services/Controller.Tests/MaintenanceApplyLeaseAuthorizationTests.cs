using System.Runtime.Versioning;
using System.Text;
using KamenRaider.AgentMaintenance;

namespace Controller.Tests;

[SupportedOSPlatform("windows")]
public sealed class MaintenanceApplyLeaseAuthorizationTests : IDisposable
{
    private const string ChainId = "chain-lease-1";
    private const string OperationId = "operation-lease-1";
    private const string BootId = "0123456789abcdef0123456789abcdef";
    private static readonly string TargetSha = new('a', 64);
    private static readonly string Fingerprint = new('c', 64);
    private static readonly MaintenanceJournalEvidence BackupEvidence =
        new(BackupOwnerOperationId: OperationId, BackupDigest: new string('b', 64));

    private readonly string root = Path.Combine(Path.GetTempPath(), "kr-apply-lease-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryJournalStore journalStore = new();

    public MaintenanceApplyLeaseAuthorizationTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private MaintenanceCommandRequest NewRequest(int generation = 1, string head = OperationId) => new(
        1, "apply", OperationId, "1.0.0", "win-x64", TargetSha, 1024, ChainId, head, generation);

    private static MaintenanceOperationBinding NewBinding(int generation = 1, string head = OperationId) => new(
        OperationId, ChainId, head, generation, "https://controller:7443/", Fingerprint, 2,
        "win-x64", "1.0.0", TargetSha);

    private MaintenanceJournal BuildJournalThroughServicesQuiesced(int generation = 1, string head = OperationId)
    {
        var journal = new MaintenanceJournal(journalStore, ChainId);
        journal.Start(NewBinding(generation, head));
        journal.Advance(OperationId, "controllerStarted");
        journal.Advance(OperationId, "targetValidated");
        journal.Advance(OperationId, "servicesQuiesced");
        return journal;
    }

    private MaintenanceJournal BuildJournalWithBackup(int generation = 1, string head = OperationId)
    {
        var journal = BuildJournalThroughServicesQuiesced(generation, head);
        journal.Advance(OperationId, "backupPublished", BackupEvidence);
        return journal;
    }

    private string LeasePath => Path.Combine(root, "lease.json");

    private void WriteLease(
        string bootId = BootId,
        long deadlineUptime = 100_000,
        long? recoveryDeadlineUptime = null,
        string rentalId = "rental-1",
        string lastCommandId = "command-1")
    {
        var recovery = recoveryDeadlineUptime is { } value
            ? $",\"RecoveryDeadlineUptimeMilliseconds\":{value}"
            : string.Empty;
        var json = "{\"RentalId\":\"" + rentalId + "\",\"Revision\":2,\"ExpiresAtUtc\":\"2026-11-17T01:00:00+00:00\"," +
            "\"BootId\":\"" + bootId + "\",\"LastCommandId\":\"" + lastCommandId + "\"," +
            "\"DeadlineUptimeMilliseconds\":" + deadlineUptime + recovery + "}";
        File.WriteAllText(LeasePath, json, new UTF8Encoding(false));
    }

    private static FakeEnvironment Environment(long uptime = 1_000) => new(BootId, uptime);

    [Fact]
    public void AuthorizationAcceptsAValidatedJournalAndMissingLeaseFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        BuildJournalWithBackup();
        var authorization = new WindowsMaintenanceApplyLeaseAuthorization(journalStore, root, Environment());

        authorization.VerifyRequestBinding(NewRequest());
        authorization.VerifyHeld(OperationId);
        authorization.VerifyHeldAndQuiesced(OperationId);
    }

    [Fact]
    public void AuthorizationRequiresTheDurableControllerStartAcknowledgement()
    {
        if (!OperatingSystem.IsWindows()) return;
        new MaintenanceJournal(journalStore, ChainId).Start(NewBinding());
        var authorization = new WindowsMaintenanceApplyLeaseAuthorization(journalStore, root, Environment());

        var exception = Assert.Throws<InvalidOperationException>(() => authorization.VerifyRequestBinding(NewRequest()));
        Assert.Contains("owner-start", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthorizationHoldsALiveCurrentBootLease()
    {
        if (!OperatingSystem.IsWindows()) return;
        BuildJournalWithBackup();
        WriteLease(deadlineUptime: 5_000, recoveryDeadlineUptime: 4_000);
        var authorization = new WindowsMaintenanceApplyLeaseAuthorization(journalStore, root, Environment(uptime: 1_000));

        var exception = Assert.Throws<InvalidOperationException>(() => authorization.VerifyRequestBinding(NewRequest()));
        Assert.Contains("still live", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorizationTreatsExpiredAndPreviousBootLeasesAsDrained()
    {
        if (!OperatingSystem.IsWindows()) return;
        BuildJournalWithBackup();

        WriteLease(deadlineUptime: 500, recoveryDeadlineUptime: 400);
        var expired = new WindowsMaintenanceApplyLeaseAuthorization(journalStore, root, Environment(uptime: 1_000));
        expired.VerifyRequestBinding(NewRequest());
        expired.VerifyHeld(OperationId);

        File.Delete(LeasePath);
        WriteLease(bootId: new string('d', 32), deadlineUptime: 100_000);
        var previousBoot = new WindowsMaintenanceApplyLeaseAuthorization(journalStore, root, Environment(uptime: 1_000));
        previousBoot.VerifyRequestBinding(NewRequest());
        previousBoot.VerifyHeld(OperationId);
    }

    [Fact]
    public void AuthorizationHoldsACorruptOrUnreadableLeaseDocument()
    {
        if (!OperatingSystem.IsWindows()) return;
        BuildJournalWithBackup();
        File.WriteAllText(LeasePath, "{\"RentalId\":");
        var corrupt = new WindowsMaintenanceApplyLeaseAuthorization(journalStore, root, Environment());
        Assert.Throws<InvalidDataException>(() => corrupt.VerifyRequestBinding(NewRequest()));

        File.WriteAllText(LeasePath,
            "{\"RentalId\":\"rental-1\",\"Revision\":2,\"ExpiresAtUtc\":\"2026-11-17T01:00:00+00:00\"," +
            "\"BootId\":\"" + BootId + "\",\"LastCommandId\":\"command-1\",\"DeadlineUptimeMilliseconds\":100000,\"Unknown\":1}");
        var unknownField = new WindowsMaintenanceApplyLeaseAuthorization(journalStore, root, Environment());
        Assert.Throws<InvalidDataException>(() => unknownField.VerifyRequestBinding(NewRequest()));
    }

    [Fact]
    public void AuthorizationRejectsAJournalHeadOrGenerationMismatch()
    {
        if (!OperatingSystem.IsWindows()) return;
        BuildJournalWithBackup();
        var authorization = new WindowsMaintenanceApplyLeaseAuthorization(journalStore, root, Environment());

        Assert.Throws<InvalidOperationException>(() => authorization.VerifyRequestBinding(NewRequest(generation: 2)));
        Assert.Throws<InvalidOperationException>(() => authorization.VerifyRequestBinding(NewRequest(head: "other-operation")));
    }

    [Fact]
    public void AuthorizationRejectsAnotherOperationIdAfterBinding()
    {
        if (!OperatingSystem.IsWindows()) return;
        BuildJournalWithBackup();
        var authorization = new WindowsMaintenanceApplyLeaseAuthorization(journalStore, root, Environment());
        authorization.VerifyRequestBinding(NewRequest());

        Assert.Throws<InvalidOperationException>(() => authorization.VerifyHeld("operation-other"));
    }

    [Fact]
    public void AuthorizationRechecksTheLeaseAfterQuiescence()
    {
        if (!OperatingSystem.IsWindows()) return;
        BuildJournalWithBackup();
        var authorization = new WindowsMaintenanceApplyLeaseAuthorization(journalStore, root, Environment());
        authorization.VerifyRequestBinding(NewRequest());

        WriteLease(deadlineUptime: 9_000);
        Assert.Throws<InvalidOperationException>(() => authorization.VerifyHeldAndQuiesced(OperationId));
    }

    private sealed class FakeEnvironment(string bootId, long uptime) : IMaintenanceLeaseEnvironment
    {
        public string ReadBootId() => bootId;

        public long ReadUptimeMilliseconds() => uptime;
    }

    private sealed class MemoryJournalStore : IMaintenanceJournalStore
    {
        private readonly Dictionary<string, MaintenanceJournalSnapshot> snapshots = new(StringComparer.Ordinal);

        public MaintenanceJournalSnapshot? Read(string chainId) =>
            snapshots.TryGetValue(chainId, out var snapshot) ? snapshot : null;

        public void WriteAtomic(string chainId, MaintenanceJournalSnapshot snapshot) =>
            snapshots[chainId] = snapshot;
    }
}
