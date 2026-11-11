using KamenRaider.AgentMaintenance;

namespace Controller.Tests;

public sealed class AgentMaintenanceContractTests
{
    private sealed class FakeFenceStore(List<string> events) : IMaintenanceFenceStore
    {
        public AgentMaintenanceFenceRecord? Current;
        public AgentMaintenanceFenceRecord? Read()
        {
            events.Add("record:read");
            return Current;
        }

        public void Write(AgentMaintenanceFenceRecord record)
        {
            events.Add("record:write:" + record.State);
            Current = record;
        }
    }

    private sealed class FakeFirewallFence(List<string> events) : IWindowsMaintenanceFirewallFence
    {
        public bool Blocked;
        public bool FailRemoval;
        public bool FailAfterRemoving;

        public void EnsureBlocked(string sunshinePath)
        {
            events.Add("firewall:block");
            Blocked = true;
        }

        public bool IsBlocked(string sunshinePath)
        {
            events.Add("firewall:verify");
            return Blocked;
        }

        public void RemoveBlock(string sunshinePath)
        {
            events.Add("firewall:remove");
            if (FailAfterRemoving)
                Blocked = false;
            if (FailRemoval)
                throw new IOException("The OS fence could not be removed.");
            Blocked = false;
        }
    }

    private sealed class Fixture
    {
        public readonly List<string> Events = [];
        public readonly FakeFenceStore Store;
        public readonly FakeFirewallFence Firewall;
        public readonly WindowsMaintenanceFence Fence;
        public readonly string SunshinePath = @"C:\Program Files\KamenRaider\Sunshine\sunshine.exe";

        public Fixture()
        {
            Store = new(Events);
            Firewall = new(Events);
            Fence = new(Store, Firewall);
        }
    }

    private static AgentMaintenanceFenceRecord HeldRecord(string phase = "controllerStarted") =>
        new(1, "held", "op-1", "chain-1", "op-1", 4, phase);

    [Fact]
    public void FreshInstallerClearRecordHasAnExplicitBoundedPhase()
    {
        var operationId = "fresh-0123456789abcdef0123456789abcdef";

        var fresh = new AgentMaintenanceFenceRecord(1, "clear", operationId, operationId, operationId, 1, "freshInstall").Validate();

        Assert.Equal("freshInstall", fresh.Phase);
        Assert.Throws<InvalidDataException>(() => (fresh with { Phase = "servicesStarted" }).Validate());
    }

    [Fact]
    public void HoldVerifiesIndependentOperatingSystemBlockBeforePublishingMarker()
    {
        var f = new Fixture();

        f.Fence.Hold(HeldRecord(), f.SunshinePath);

        Assert.True(f.Firewall.Blocked);
        Assert.Equal("held", f.Store.Current!.State);
        Assert.True(f.Events.IndexOf("firewall:block") < f.Events.IndexOf("record:write:held"));
        Assert.True(f.Events.IndexOf("firewall:verify") < f.Events.IndexOf("record:write:held"));
    }

    [Fact]
    public void ReleaseRequiresControllerConfirmationAndVerifiedTargetServices()
    {
        var f = new Fixture();
        var held = HeldRecord();
        f.Store.Current = held;
        f.Firewall.Blocked = true;
        var clear = held with { State = "clear", Phase = "controllerConfirmed" };

        Assert.Throws<InvalidOperationException>(() =>
            f.Fence.Release(held, clear, f.SunshinePath, controllerConfirmed: false, targetServicesVerified: true));
        Assert.Throws<InvalidOperationException>(() =>
            f.Fence.Release(held, clear, f.SunshinePath, controllerConfirmed: true, targetServicesVerified: false));

        Assert.Equal("held", f.Store.Current!.State);
        Assert.True(f.Firewall.Blocked);
        Assert.DoesNotContain("firewall:remove", f.Events);
    }

    [Fact]
    public void ReleasePersistsConfirmedClearBeforeRemovingIndependentBlock()
    {
        var f = new Fixture();
        var held = HeldRecord();
        var clear = held with { State = "clear", Phase = "controllerConfirmed" };
        f.Store.Current = held;
        f.Firewall.Blocked = true;

        f.Fence.Release(held, clear, f.SunshinePath, controllerConfirmed: true, targetServicesVerified: true);

        Assert.Equal("clear", f.Store.Current!.State);
        Assert.False(f.Firewall.Blocked);
        Assert.True(f.Events.IndexOf("record:write:clear") < f.Events.IndexOf("firewall:remove"));
    }

    [Fact]
    public void FailedFirewallReleaseRestoresHeldMarker()
    {
        var f = new Fixture();
        var held = HeldRecord();
        var clear = held with { State = "clear", Phase = "controllerConfirmed" };
        f.Store.Current = held;
        f.Firewall.Blocked = true;
        f.Firewall.FailRemoval = true;

        Assert.Throws<IOException>(() =>
            f.Fence.Release(held, clear, f.SunshinePath, controllerConfirmed: true, targetServicesVerified: true));

        Assert.Equal("held", f.Store.Current!.State);
        Assert.Equal("controllerConfirmed", f.Store.Current.Phase);
        Assert.True(f.Firewall.Blocked);
    }

    [Fact]
    public void FailedFirewallVerificationReestablishesTheBlockAndHeldMarker()
    {
        var f = new Fixture();
        var held = HeldRecord();
        var clear = held with { State = "clear", Phase = "controllerConfirmed" };
        f.Store.Current = held;
        f.Firewall.Blocked = true;
        f.Firewall.FailAfterRemoving = true;
        f.Firewall.FailRemoval = true;

        Assert.Throws<IOException>(() =>
            f.Fence.Release(held, clear, f.SunshinePath, controllerConfirmed: true, targetServicesVerified: true));

        Assert.Equal("held", f.Store.Current!.State);
        Assert.Equal("controllerConfirmed", f.Store.Current.Phase);
        Assert.True(f.Firewall.Blocked);
        Assert.Contains("firewall:block", f.Events);
    }
}
