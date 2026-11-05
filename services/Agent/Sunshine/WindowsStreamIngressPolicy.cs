using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KamenRaider.Agent.Sunshine;

public sealed record FirewallPolicySnapshot(
    int CurrentProfileTypes,
    bool DomainEnabled,
    bool PrivateEnabled,
    bool PublicEnabled,
    int LocalPolicyModifyState);

public sealed record FirewallRuleSnapshot(
    string Name,
    string? ApplicationPath,
    string? ServiceName,
    bool Enabled,
    int Direction,
    int Action,
    int Profiles,
    int Protocol,
    string? LocalPorts,
    string? RemotePorts,
    string? LocalAddresses,
    string? RemoteAddresses,
    string? InterfaceTypes,
    bool InterfacesUnrestricted,
    bool EdgeTraversal,
    bool OverrideBlockRules);

public sealed record FirewallRuleDefinition(
    string Name,
    string ApplicationPath,
    string? ServiceName,
    bool Enabled,
    int Direction,
    int Action,
    int Profiles,
    int Protocol,
    string? LocalPorts,
    string? RemotePorts,
    string? LocalAddresses,
    string? RemoteAddresses,
    string? InterfaceTypes,
    bool InterfacesUnrestricted,
    bool EdgeTraversal,
    bool OverrideBlockRules);

public interface IWindowsFirewallApi
{
    FirewallPolicySnapshot ReadPolicyState();
    FirewallRuleSnapshot? FindRule(string name);
    void AddRule(FirewallRuleDefinition rule);
    void SetRuleEnabled(string name, bool enabled);
}

public sealed class WindowsStreamIngressPolicy : IStreamIngressPolicy
{
    public const string BlockRuleName = "KamenRaider Stream Guard - Sunshine";
    public const int InboundDirection = 1;
    public const int BlockAction = 0;
    public const int AnyProtocol = 256;
    public const int AllProfiles = 0x7fffffff;
    private const int DomainProfile = 1;
    private const int PrivateProfile = 2;
    private const int PublicProfile = 4;
    private readonly IWindowsFirewallApi firewall;
    private readonly string sunshinePath;

    public WindowsStreamIngressPolicy(IWindowsFirewallApi firewall, SunshineServiceInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(firewall);
        ArgumentNullException.ThrowIfNull(installation);
        this.firewall = firewall;
        sunshinePath = Path.GetFullPath(installation.SunshineExecutablePath);
    }

    public Task SetBlockedAsync(bool blocked, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => SetBlocked(blocked), cancellationToken);
    }

    public Task<bool> IsBlockedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(IsBlocked, cancellationToken);
    }

    private void SetBlocked(bool blocked)
    {
        EnsureFirewallUsable();
        var current = firewall.FindRule(BlockRuleName);
        if (current is null)
        {
            if (!blocked)
                throw new InvalidOperationException("The managed Sunshine block rule does not exist.");
            firewall.AddRule(ExpectedRule(enabled: true));
        }
        else
        {
            EnsureExactRule(current);
            if (current.Enabled != blocked)
                firewall.SetRuleEnabled(BlockRuleName, blocked);
        }

        EnsureFirewallUsable();
        var verified = firewall.FindRule(BlockRuleName);
        if (verified is null)
            throw new IOException("The managed Sunshine firewall rule disappeared during an update.");
        EnsureExactRule(verified);
        if (verified.Enabled != blocked)
            throw new IOException($"The managed Sunshine firewall rule did not reach Enabled={blocked}.");
    }

    private bool IsBlocked()
    {
        EnsureFirewallUsable();
        var rule = firewall.FindRule(BlockRuleName);
        if (rule is null) return false;
        EnsureExactRule(rule);
        return rule.Enabled;
    }

    private void EnsureFirewallUsable()
    {
        var state = firewall.ReadPolicyState();
        if (state.CurrentProfileTypes == 0 ||
            !state.DomainEnabled ||
            !state.PrivateEnabled ||
            !state.PublicEnabled ||
            state.LocalPolicyModifyState != 0)
        {
            throw new InvalidOperationException("Windows Firewall profiles or local policy do not permit a verified Sunshine fence.");
        }
    }

    private FirewallRuleDefinition ExpectedRule(bool enabled) => new(
        BlockRuleName,
        sunshinePath,
        "",
        enabled,
        InboundDirection,
        BlockAction,
        AllProfiles,
        AnyProtocol,
        "",
        "",
        "*",
        "*",
        "All",
        InterfacesUnrestricted: true,
        EdgeTraversal: false,
        OverrideBlockRules: false);

    private void EnsureExactRule(FirewallRuleSnapshot rule)
    {
        var exact =
            rule.Name == BlockRuleName &&
            PathsEqual(rule.ApplicationPath, sunshinePath) &&
            string.IsNullOrWhiteSpace(rule.ServiceName) &&
            rule.Direction == InboundDirection &&
            rule.Action == BlockAction &&
            rule.Profiles == AllProfiles &&
            rule.Protocol == AnyProtocol &&
            IsUnrestricted(rule.LocalPorts) &&
            IsUnrestricted(rule.RemotePorts) &&
            IsUnrestrictedAddress(rule.LocalAddresses) &&
            IsUnrestrictedAddress(rule.RemoteAddresses) &&
            string.Equals(rule.InterfaceTypes, "All", StringComparison.OrdinalIgnoreCase) &&
            rule.InterfacesUnrestricted &&
            !rule.EdgeTraversal &&
            !rule.OverrideBlockRules;

        if (!exact)
            throw new InvalidOperationException("The existing KamenRaider Sunshine firewall rule has a conflicting shape; refusing to change it.");
    }

    private static bool PathsEqual(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsUnrestricted(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "*" || string.Equals(value, "Any", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnrestrictedAddress(string? value) =>
        value == "*" || string.Equals(value, "Any", StringComparison.OrdinalIgnoreCase);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsFirewallComApi : IWindowsFirewallApi
{
    private const int ProfileDomain = 1;
    private const int ProfilePrivate = 2;
    private const int ProfilePublic = 4;
    private const int RuleNotFoundHResult = unchecked((int)0x80070002);

    public FirewallPolicySnapshot ReadPolicyState()
    {
        dynamic policy = CreateComObject("HNetCfg.FwPolicy2");
        try
        {
            return new FirewallPolicySnapshot(
                (int)policy.CurrentProfileTypes,
                (bool)policy.FirewallEnabled(ProfileDomain),
                (bool)policy.FirewallEnabled(ProfilePrivate),
                (bool)policy.FirewallEnabled(ProfilePublic),
                (int)policy.LocalPolicyModifyState);
        }
        finally
        {
            ReleaseComObject(policy);
        }
    }

    public FirewallRuleSnapshot? FindRule(string name)
    {
        dynamic policy = CreateComObject("HNetCfg.FwPolicy2");
        object? rulesObject = null;
        object? ruleObject = null;
        try
        {
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            try
            {
                ruleObject = rules.Item(name);
            }
            catch (Exception ex) when (ex.HResult == RuleNotFoundHResult && (ex is COMException or FileNotFoundException))
            {
                return null;
            }

            if (ruleObject is null) return null;
            return ReadRule((dynamic)ruleObject);
        }
        finally
        {
            ReleaseComObject(ruleObject);
            ReleaseComObject(rulesObject);
            ReleaseComObject(policy);
        }
    }

    public void AddRule(FirewallRuleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        dynamic policy = CreateComObject("HNetCfg.FwPolicy2");
        dynamic rule = CreateComObject("HNetCfg.FWRule");
        object? rulesObject = null;
        try
        {
            rule.Name = definition.Name;
            rule.Description = "Managed by KamenRaider Stream Guard.";
            rule.ApplicationName = definition.ApplicationPath;
            rule.ServiceName = definition.ServiceName ?? "";
            rule.Protocol = definition.Protocol;
            rule.LocalPorts = definition.LocalPorts ?? "";
            rule.RemotePorts = definition.RemotePorts ?? "";
            rule.LocalAddresses = definition.LocalAddresses ?? "*";
            rule.RemoteAddresses = definition.RemoteAddresses ?? "*";
            rule.InterfaceTypes = definition.InterfaceTypes ?? "All";
            rule.Interfaces = definition.InterfacesUnrestricted ? null : Array.Empty<string>();
            rule.Direction = definition.Direction;
            rule.Action = definition.Action;
            rule.Profiles = definition.Profiles;
            rule.EdgeTraversal = definition.EdgeTraversal;
            rule.OverrideBlockRules = definition.OverrideBlockRules;
            rule.Enabled = definition.Enabled;

            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            rules.Add(rule);
        }
        finally
        {
            ReleaseComObject(rulesObject);
            ReleaseComObject(rule);
            ReleaseComObject(policy);
        }
    }

    public void SetRuleEnabled(string name, bool enabled)
    {
        dynamic policy = CreateComObject("HNetCfg.FwPolicy2");
        object? rulesObject = null;
        object? ruleObject = null;
        try
        {
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            ruleObject = rules.Item(name);
            dynamic rule = ruleObject;
            rule.Enabled = enabled;
        }
        finally
        {
            ReleaseComObject(ruleObject);
            ReleaseComObject(rulesObject);
            ReleaseComObject(policy);
        }
    }

    private static FirewallRuleSnapshot ReadRule(dynamic rule)
    {
        object? interfaces = rule.Interfaces;
        var interfacesUnrestricted = interfaces is null || interfaces is Array array && array.Length == 0;
        return new FirewallRuleSnapshot(
            (string)rule.Name,
            (string?)rule.ApplicationName,
            (string?)rule.ServiceName,
            (bool)rule.Enabled,
            (int)rule.Direction,
            (int)rule.Action,
            (int)rule.Profiles,
            (int)rule.Protocol,
            (string?)rule.LocalPorts,
            (string?)rule.RemotePorts,
            (string?)rule.LocalAddresses,
            (string?)rule.RemoteAddresses,
            (string?)rule.InterfaceTypes,
            interfacesUnrestricted,
            (bool)rule.EdgeTraversal,
            (bool)rule.OverrideBlockRules);
    }

    private static object CreateComObject(string progId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Firewall COM is available only on Windows.");
        var type = Type.GetTypeFromProgID(progId, throwOnError: true)
            ?? throw new InvalidOperationException($"Windows Firewall COM type '{progId}' is unavailable.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Windows Firewall COM type '{progId}' could not be created.");
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }
}
