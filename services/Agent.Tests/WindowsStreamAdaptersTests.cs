using KamenRaider.Agent.Sunshine;

namespace Agent.Tests;

public class WindowsStreamAdaptersTests
{
    private const string ServiceImage = @"C:\Program Files\Sunshine\tools\sunshinesvc.exe";
    private const string SunshineImage = @"C:\Program Files\Sunshine\sunshine.exe";

    private sealed class FirewallApi : IWindowsFirewallApi
    {
        public FirewallPolicySnapshot Policy = new(2, true, true, true, 0);
        public FirewallRuleSnapshot? Rule;
        public readonly List<FirewallRuleDefinition> Added = [];
        public readonly List<bool> EnabledChanges = [];

        public FirewallPolicySnapshot ReadPolicyState() => Policy;
        public FirewallRuleSnapshot? FindRule(string name) => Rule is { } rule && rule.Name == name ? rule : null;

        public void AddRule(FirewallRuleDefinition rule)
        {
            Added.Add(rule);
            Rule = FromDefinition(rule);
        }

        public void SetRuleEnabled(string name, bool enabled)
        {
            EnabledChanges.Add(enabled);
            if (Rule?.Name == name) Rule = Rule with { Enabled = enabled };
        }
    }

    private sealed class ServiceApi : IWindowsServiceApi
    {
        public WindowsServiceSnapshot? Record = ValidService(WindowsServiceState.Stopped);
        public bool StartChangesState = true;
        public bool StopChangesState = true;
        public int StartCalls;
        public int StopCalls;

        public WindowsServiceSnapshot? Query(string serviceName) =>
            Record is { } record && string.Equals(record.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase)
                ? record
                : null;

        public void Start(string serviceName)
        {
            StartCalls++;
            if (StartChangesState && Record is { } record)
                Record = record with { State = WindowsServiceState.Running };
        }

        public void Stop(string serviceName)
        {
            StopCalls++;
            if (StopChangesState && Record is { } record)
                Record = record with { State = WindowsServiceState.Stopped };
        }
    }

    [Fact]
    public void QuotedServiceImagePathParserExtractsExecutableBeforeArguments()
    {
        var path = WindowsServiceImagePathParser.ParseExecutablePath($"\"{ServiceImage}\" --service --quiet");

        Assert.Equal(ServiceImage, path);
    }

    [Fact]
    public void UnquotedServiceImagePathWithSpacesIsRejectedAsAmbiguous()
    {
        Assert.Throws<InvalidDataException>(() => WindowsServiceImagePathParser.ParseExecutablePath(ServiceImage + " --service"));
    }

    [Fact]
    public void InstallationDiscoveryUsesServiceWrapperAndExpectedSunshineExecutable()
    {
        var api = new ServiceApi();
        api.Record = ValidService(WindowsServiceState.Running) with { ImagePath = $"\"{ServiceImage}\" --service" };
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ServiceImage, SunshineImage };

        var installation = new WindowsSunshineInstallationDiscovery(api, fileExists: files.Contains).Discover();

        Assert.Equal("SunshineService", installation.ServiceName);
        Assert.Equal(ServiceImage, installation.ServiceImagePath);
        Assert.Equal(SunshineImage, installation.SunshineExecutablePath);
    }

    [Fact]
    public void MissingSunshineServiceIsRejected()
    {
        var api = new ServiceApi { Record = null };
        var discovery = new WindowsSunshineInstallationDiscovery(api, fileExists: _ => true);

        Assert.Throws<InvalidOperationException>(() => discovery.Discover());
    }

    [Fact]
    public void WrongServiceImageIsRejectedDuringInstallationDiscovery()
    {
        var api = new ServiceApi
        {
            Record = ValidService(WindowsServiceState.Running) with { ImagePath = @"""C:\Temp\sunshinesvc.exe""" }
        };

        Assert.Throws<InvalidOperationException>(() => new WindowsSunshineInstallationDiscovery(api, fileExists: _ => true).Discover());
    }

    [Fact]
    public async Task FirewallBlockRuleIsExactAndCoversAllProfilesInterfacesAddressesAndProtocols()
    {
        var firewall = new FirewallApi();
        var policy = new WindowsStreamIngressPolicy(firewall, Installation());

        await policy.SetBlockedAsync(true, default);

        var rule = Assert.Single(firewall.Added);
        Assert.Equal(WindowsStreamIngressPolicy.BlockRuleName, rule.Name);
        Assert.Equal(SunshineImage, rule.ApplicationPath);
        Assert.Equal(WindowsStreamIngressPolicy.InboundDirection, rule.Direction);
        Assert.Equal(WindowsStreamIngressPolicy.BlockAction, rule.Action);
        Assert.Equal(WindowsStreamIngressPolicy.AllProfiles, rule.Profiles);
        Assert.Equal(WindowsStreamIngressPolicy.AnyProtocol, rule.Protocol);
        Assert.Equal("*", rule.LocalAddresses);
        Assert.Equal("*", rule.RemoteAddresses);
        Assert.True(string.IsNullOrEmpty(rule.LocalPorts));
        Assert.True(string.IsNullOrEmpty(rule.RemotePorts));
        Assert.Equal("All", rule.InterfaceTypes);
        Assert.True(rule.InterfacesUnrestricted);
        Assert.True(rule.Enabled);
        Assert.False(rule.OverrideBlockRules);
        Assert.True(await policy.IsBlockedAsync(default));
    }

    [Fact]
    public async Task ConflictingExistingManagedRuleIsRejectedWithoutMutation()
    {
        var firewall = new FirewallApi { Rule = ExpectedRule() with { Protocol = 6, Enabled = false } };
        var policy = new WindowsStreamIngressPolicy(firewall, Installation());

        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.SetBlockedAsync(true, default));

        Assert.Empty(firewall.Added);
        Assert.Empty(firewall.EnabledChanges);
    }

    [Fact]
    public async Task DisabledProfilesAndGroupPolicyOverridesPreventRuleMutation()
    {
        foreach (var state in new[]
        {
            new FirewallPolicySnapshot(2, false, true, true, 0),
            new FirewallPolicySnapshot(2, true, true, true, 1)
        })
        {
            var firewall = new FirewallApi { Policy = state };
            var policy = new WindowsStreamIngressPolicy(firewall, Installation());

            await Assert.ThrowsAsync<InvalidOperationException>(() => policy.SetBlockedAsync(true, default));

            Assert.Empty(firewall.Added);
            Assert.Empty(firewall.EnabledChanges);
        }
    }

    [Fact]
    public async Task UnblockingOnlyDisablesTheVerifiedManagedRule()
    {
        var firewall = new FirewallApi { Rule = ExpectedRule() };
        var policy = new WindowsStreamIngressPolicy(firewall, Installation());

        await policy.SetBlockedAsync(false, default);

        Assert.Equal(new[] { false }, firewall.EnabledChanges);
        Assert.False(firewall.Rule!.Enabled);
    }

    [Fact]
    public async Task MissingRuleReportsUnblockedAndCannotBeUnblocked()
    {
        var firewall = new FirewallApi();
        var policy = new WindowsStreamIngressPolicy(firewall, Installation());

        Assert.False(await policy.IsBlockedAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.SetBlockedAsync(false, default));
        Assert.Empty(firewall.Added);
    }

    [Fact]
    public void WindowsFirewallComApiTreatsMissingRuleAsAbsent()
    {
        if (!OperatingSystem.IsWindows()) return;

        var api = new WindowsFirewallComApi();

        Assert.Null(api.FindRule($"KamenRaider test missing {Guid.NewGuid():N}"));
    }

    [Fact]
    public void WindowsFirewallComApiReadsExistingRulesUsingSupportedComProperties()
    {
        if (!OperatingSystem.IsWindows()) return;

        var api = new WindowsFirewallComApi();
        var rule = api.FindRule("Sunshine");

        if (rule is not null)
        {
            Assert.Equal(SunshineImage, rule.ApplicationPath);
            Assert.Equal(WindowsStreamIngressPolicy.AllProfiles, rule.Profiles);
            Assert.False(rule.OverrideBlockRules);
        }
    }

    [Fact]
    public async Task SunshineServiceControlStartsAndStopsOnlyTheValidatedService()
    {
        var api = new ServiceApi();
        var control = new WindowsSunshineServiceControl(api, Installation());

        await control.StartAndWaitAsync(default);
        Assert.True(await control.IsRunningAsync(default));
        await control.StopAndWaitAsync(default);

        Assert.Equal(1, api.StartCalls);
        Assert.Equal(1, api.StopCalls);
        Assert.False(await control.IsRunningAsync(default));
    }

    [Fact]
    public async Task ServiceControlRejectsMissingServiceAndWrongServiceImage()
    {
        var missing = new ServiceApi { Record = null };
        var missingControl = new WindowsSunshineServiceControl(missing, Installation());
        await Assert.ThrowsAsync<InvalidOperationException>(() => missingControl.StartAndWaitAsync(default));

        var wrong = new ServiceApi { Record = ValidService(WindowsServiceState.Running) with { ImagePath = @"C:\Temp\sunshinesvc.exe" } };
        var wrongControl = new WindowsSunshineServiceControl(wrong, Installation());
        await Assert.ThrowsAsync<InvalidOperationException>(() => wrongControl.StopAndWaitAsync(default));
        Assert.Equal(0, wrong.StopCalls);
    }

    [Fact]
    public async Task SunshineServiceStartAndStopTimeoutsAreReported()
    {
        var slowStartApi = new ServiceApi { StartChangesState = false };
        var slowStart = new WindowsSunshineServiceControl(slowStartApi, Installation(), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(2));
        await Assert.ThrowsAsync<TimeoutException>(() => slowStart.StartAndWaitAsync(default));

        var slowStopApi = new ServiceApi { Record = ValidService(WindowsServiceState.Running), StopChangesState = false };
        var slowStop = new WindowsSunshineServiceControl(slowStopApi, Installation(), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(2));
        await Assert.ThrowsAsync<TimeoutException>(() => slowStop.StopAndWaitAsync(default));
    }

    private static SunshineServiceInstallation Installation() => new("SunshineService", ServiceImage, SunshineImage);

    private static FirewallRuleSnapshot ExpectedRule() => FromDefinition(new(
        WindowsStreamIngressPolicy.BlockRuleName,
        SunshineImage,
        "",
        true,
        WindowsStreamIngressPolicy.InboundDirection,
        WindowsStreamIngressPolicy.BlockAction,
        WindowsStreamIngressPolicy.AllProfiles,
        WindowsStreamIngressPolicy.AnyProtocol,
        "",
        "",
        "*",
        "*",
        "All",
        true,
        false,
        false));

    private static FirewallRuleSnapshot FromDefinition(FirewallRuleDefinition rule) => new(
        rule.Name,
        rule.ApplicationPath,
        rule.ServiceName,
        rule.Enabled,
        rule.Direction,
        rule.Action,
        rule.Profiles,
        rule.Protocol,
        rule.LocalPorts,
        rule.RemotePorts,
        rule.LocalAddresses,
        rule.RemoteAddresses,
        rule.InterfaceTypes,
        rule.InterfacesUnrestricted,
        rule.EdgeTraversal,
        rule.OverrideBlockRules);

    private static WindowsServiceSnapshot ValidService(WindowsServiceState state) =>
        new("SunshineService", $"\"{ServiceImage}\" --service", "LocalSystem", state);
}
