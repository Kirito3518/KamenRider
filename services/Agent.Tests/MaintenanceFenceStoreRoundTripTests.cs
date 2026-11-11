using System.Runtime.Versioning;
using System.Security.Principal;
using KamenRaider.Agent.Sunshine;
using KamenRaider.AgentMaintenance;

namespace Agent.Tests;

[SupportedOSPlatform("windows")]
public sealed class ElevatedWindowsFactAttribute : FactAttribute
{
    public ElevatedWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows() ||
            !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            Skip = "This filesystem round-trip requires an elevated Windows test process to create the production fence ACL.";
    }
}

[SupportedOSPlatform("windows")]
public sealed class MaintenanceFenceStoreRoundTripTests
{
    private sealed class FakeFirewall : IWindowsMaintenanceFirewallFence
    {
        public bool Blocked;
        public void EnsureBlocked(string sunshinePath) => Blocked = true;
        public bool IsBlocked(string sunshinePath) => Blocked;
        public void RemoveBlock(string sunshinePath) => Blocked = false;
    }

    [ElevatedWindowsFact]
    public void ProtectedWriterRecordsAreAcceptedByTheStrictProductionReader()
    {
        var root = Path.Combine(Path.GetTempPath(), "kr-fence-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fenceDirectory = Path.Combine(root, "protected");
            var store = new WindowsMaintenanceFenceStore(fenceDirectory);
            var firewall = new FakeFirewall();
            var writer = new WindowsMaintenanceFence(store, firewall);
            var sunshine = Path.Combine(root, "sunshine.exe");
            File.WriteAllBytes(sunshine, [0x4d, 0x5a]);
            var path = Path.Combine(fenceDirectory, "maintenance-fence.json");
            var strictReader = new WindowsMaintenanceFenceReader(path, requirePrivateSecurity: true);
            var held = new AgentMaintenanceFenceRecord(1, "held", "op-roundtrip", "chain-roundtrip", "op-roundtrip", 1, "controllerStarted");
            var clear = held with { State = "clear", Phase = "controllerConfirmed" };

            writer.Hold(held, sunshine);
            var heldSnapshot = strictReader.Read();
            Assert.True(heldSnapshot.IsKnown);
            Assert.True(heldSnapshot.IsHeld);

            writer.Release(held, clear, sunshine, controllerConfirmed: true, targetServicesVerified: true);
            var clearSnapshot = strictReader.Read();
            Assert.True(clearSnapshot.IsKnown);
            Assert.False(clearSnapshot.IsHeld);
            Assert.Equal("controllerConfirmed", clearSnapshot.Phase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
