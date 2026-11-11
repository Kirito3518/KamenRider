using KamenRaider.AgentMaintenance;
using System.Runtime.Versioning;

namespace Controller.Tests;

[SupportedOSPlatform("windows")]
public sealed class MaintenanceFirewallPolicyTests
{
    private static EffectiveMaintenanceFirewallPolicy ValidPolicy() => new(
        [
            new("Domain", Enabled: true, AllowLocalFirewallRules: true),
            new("Private", Enabled: true, AllowLocalFirewallRules: true),
            new("Public", Enabled: true, AllowLocalFirewallRules: true)
        ],
        [new(
            Name: WindowsFirewallMaintenanceFence.RuleName,
            ApplicationPath: @"C:\Program Files\Sunshine\sunshine.exe",
            ServiceName: null,
            PolicySourceType: "Local",
            Enabled: true,
            Direction: "Inbound",
            Action: "Block",
            Profiles: "Any",
            Protocol: "256",
            LocalPorts: "Any",
            RemotePorts: "Any",
            LocalAddresses: "Any",
            RemoteAddresses: "Any",
            InterfaceTypes: "Any",
            Interfaces: "Any",
            EdgeTraversalPolicy: "Block",
            OverrideBlockRules: false)]);

    [Fact]
    public void RequiresEnabledProfilesAndLocalRuleMergeForAllProfiles()
    {
        WindowsMaintenanceFirewallPolicyVerifier.EnsureEffectiveBlock(
            ValidPolicy(), WindowsFirewallMaintenanceFence.RuleName, @"C:\Program Files\Sunshine\sunshine.exe");

        var missingMerge = ValidPolicy() with
        {
            Profiles =
            [
                new("Domain", true, true),
                new("Private", true, true),
                new("Public", true, false)
            ]
        };
        Assert.Throws<InvalidOperationException>(() => WindowsMaintenanceFirewallPolicyVerifier.EnsureProfilesUsable(missingMerge));
        Assert.Throws<InvalidOperationException>(() => WindowsMaintenanceFirewallPolicyVerifier.EnsureProfilesUsable(
            ValidPolicy() with { Profiles = ValidPolicy().Profiles.Take(2).ToArray() }));
    }

    [Fact]
    public void RejectsRulePresentInLocalStoreButAbsentFromEffectiveActiveStore()
    {
        var policy = ValidPolicy() with { Rules = [] };

        Assert.Throws<InvalidOperationException>(() => WindowsMaintenanceFirewallPolicyVerifier.EnsureEffectiveBlock(
            policy, WindowsFirewallMaintenanceFence.RuleName, @"C:\Program Files\Sunshine\sunshine.exe"));
    }

    [Theory]
    [InlineData(false, "Local", "Block")]
    [InlineData(true, "GroupPolicy", "Block")]
    [InlineData(true, "Local", "Allow")]
    public void RejectsIneffectiveOrUnexpectedActiveBlock(bool enabled, string source, string action)
    {
        var rule = ValidPolicy().Rules.Single() with { Enabled = enabled, PolicySourceType = source, Action = action };
        var policy = ValidPolicy() with { Rules = [rule] };

        Assert.Throws<InvalidOperationException>(() => WindowsMaintenanceFirewallPolicyVerifier.EnsureEffectiveBlock(
            policy, WindowsFirewallMaintenanceFence.RuleName, @"C:\Program Files\Sunshine\sunshine.exe"));
    }

    [Fact]
    public void RejectsUnexpectedApplicationOrOverrideBlockRules()
    {
        var rule = ValidPolicy().Rules.Single() with { ApplicationPath = @"C:\Temp\sunshine.exe", OverrideBlockRules = true };

        Assert.Throws<InvalidOperationException>(() => WindowsMaintenanceFirewallPolicyVerifier.EnsureEffectiveBlock(
            ValidPolicy() with { Rules = [rule] }, WindowsFirewallMaintenanceFence.RuleName, @"C:\Program Files\Sunshine\sunshine.exe"));
    }
}
