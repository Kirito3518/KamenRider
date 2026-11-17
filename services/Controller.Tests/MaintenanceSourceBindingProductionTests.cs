using System.Runtime.Versioning;
using System.Text;
using KamenRaider.AgentMaintenance;

namespace Controller.Tests;

[SupportedOSPlatform("windows")]
public sealed class MaintenanceSourceBindingProductionTests : IDisposable
{
    private const string ChainId = "chain-bind-1";
    private const string OperationId = "operation-bind-1";
    private const string BootId = "0123456789abcdef0123456789abcdef";
    private const string TargetSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Fingerprint = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private readonly string root = Path.Combine(Path.GetTempPath(), "kr-source-bind-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryJournalStore journalStore = new();

    public MaintenanceSourceBindingProductionTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static MaintenanceLifecycleRequest NewRequest(long generation = 1, string head = OperationId) => new(
        1, "bind-source", OperationId, ChainId, head, generation,
        "https://controller:7443/", Fingerprint, 2, "1.0.0", "win-x64", TargetSha, "release-1", "ticket-1");

    private static MaintenanceOperationBinding NewBinding(long generation = 1, string head = OperationId) => new(
        OperationId, ChainId, head, generation, "https://controller:7443/", Fingerprint, 2,
        "win-x64", "1.0.0", TargetSha);

    private MaintenanceJournal BuildJournalThroughServicesQuiesced(long generation = 1, string head = OperationId)
    {
        var journal = new MaintenanceJournal(journalStore, ChainId);
        journal.Start(NewBinding(generation, head));
        journal.Advance(OperationId, "controllerStarted");
        journal.Advance(OperationId, "targetValidated");
        journal.Advance(OperationId, "servicesQuiesced");
        return journal;
    }

    private void WriteLease(string bootId = BootId, long deadlineUptime = 100_000)
    {
        var json = "{\"RentalId\":\"rental-1\",\"Revision\":2,\"ExpiresAtUtc\":\"2026-11-17T01:00:00+00:00\"," +
            "\"BootId\":\"" + bootId + "\",\"LastCommandId\":\"command-1\",\"DeadlineUptimeMilliseconds\":" + deadlineUptime + "}";
        File.WriteAllText(Path.Combine(root, "lease.json"), json, new UTF8Encoding(false));
    }

    private WindowsMaintenanceSourceBindingAuthorization NewAuthorization(long uptime = 1_000) =>
        new(journalStore, root, new FakeEnvironment(BootId, uptime));

    [Fact]
    public void SourceBindingAuthorizationAcceptsAStartedJournalAndMissingLease()
    {
        if (!OperatingSystem.IsWindows()) return;
        BuildJournalThroughServicesQuiesced();
        var authorization = NewAuthorization();

        authorization.VerifyRequestBinding(NewRequest());
        authorization.VerifyHeld(OperationId);
        authorization.VerifyHeldAndQuiesced(OperationId);
    }

    [Fact]
    public void SourceBindingAuthorizationRequiresTheDurableControllerStartAcknowledgement()
    {
        if (!OperatingSystem.IsWindows()) return;
        new MaintenanceJournal(journalStore, ChainId).Start(NewBinding());
        var authorization = NewAuthorization();

        var exception = Assert.Throws<InvalidOperationException>(() => authorization.VerifyRequestBinding(NewRequest()));
        Assert.Contains("owner-start", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourceBindingAuthorizationHoldsALiveLeaseAndAcceptsADrainedOrPreviousBootLease()
    {
        if (!OperatingSystem.IsWindows()) return;
        BuildJournalThroughServicesQuiesced();

        WriteLease(deadlineUptime: 5_000);
        var live = NewAuthorization(uptime: 1_000);
        Assert.Throws<InvalidOperationException>(() => live.VerifyRequestBinding(NewRequest()));

        File.Delete(Path.Combine(root, "lease.json"));
        WriteLease(deadlineUptime: 500);
        var expired = NewAuthorization(uptime: 1_000);
        expired.VerifyRequestBinding(NewRequest());
        expired.VerifyHeld(OperationId);

        File.Delete(Path.Combine(root, "lease.json"));
        WriteLease(bootId: new string('d', 32), deadlineUptime: 100_000);
        var previousBoot = NewAuthorization(uptime: 1_000);
        previousBoot.VerifyRequestBinding(NewRequest());
        previousBoot.VerifyHeld(OperationId);
    }

    [Fact]
    public void SourceBindingAuthorizationHoldsACorruptLease()
    {
        if (!OperatingSystem.IsWindows()) return;
        BuildJournalThroughServicesQuiesced();
        File.WriteAllText(Path.Combine(root, "lease.json"), "{not-json");

        Assert.Throws<InvalidDataException>(() => NewAuthorization().VerifyRequestBinding(NewRequest()));
    }

    [Fact]
    public void SourceBindingAuthorizationRejectsHeadGenerationAndOperationMismatches()
    {
        if (!OperatingSystem.IsWindows()) return;
        BuildJournalThroughServicesQuiesced();

        Assert.Throws<InvalidOperationException>(() => NewAuthorization().VerifyRequestBinding(NewRequest(generation: 2)));
        Assert.Throws<InvalidOperationException>(() => NewAuthorization().VerifyRequestBinding(NewRequest(head: "other-operation")));

        var bound = NewAuthorization();
        bound.VerifyRequestBinding(NewRequest());
        Assert.Throws<InvalidOperationException>(() => bound.VerifyHeld("operation-other"));
    }

    [Fact]
    public void FirewallFenceValidatesTheSunshinePathBeforeAnyMutation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fence = new WindowsPowerShellMaintenanceFirewallFence();

        Assert.Throws<FileNotFoundException>(() =>
            fence.EnsureBlocked(Path.Combine(root, "Sunshine", "Sunshine.exe")));

        var misnamed = Path.Combine(root, "Sunshine", "NotSunshine.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(misnamed)!);
        File.WriteAllText(misnamed, "fixture");
        Assert.Throws<InvalidDataException>(() => fence.EnsureBlocked(misnamed));
        Assert.False(fence.IsBlocked(misnamed));
    }

    [ElevatedWindowsWorkStoreFact]
    public void FirewallFenceCreatesVerifiesAndRemovesTheEffectiveBlock()
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = Path.Combine(root, "Sunshine", "Sunshine.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "fixture");
        var fence = new WindowsPowerShellMaintenanceFirewallFence();

        // The effective-policy verifier requires every firewall profile enabled and local rules
        // allowed; skip the mutation when the host policy cannot satisfy the plan contract.
        try
        {
            WindowsMaintenanceFirewallPolicyVerifier.EnsureProfilesUsable(
                new WindowsPowerShellMaintenanceFirewallPolicyReader().ReadActivePolicy());
        }
        catch (Exception)
        {
            return;
        }

        try
        {
            fence.EnsureBlocked(executable);
            Assert.True(fence.IsBlocked(executable));
        }
        finally
        {
            fence.RemoveBlock(executable);
        }

        Assert.False(fence.IsBlocked(executable));
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
