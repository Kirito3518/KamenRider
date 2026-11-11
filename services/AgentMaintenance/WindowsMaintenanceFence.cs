using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace KamenRaider.AgentMaintenance;

public sealed record AgentMaintenanceFenceRecord(
    int SchemaVersion,
    string State,
    string OperationId,
    string ChainId,
    string HeadOperationId,
    long Generation,
    string Phase)
{
    public AgentMaintenanceFenceRecord Validate()
    {
        if (SchemaVersion != 1 ||
            State is not ("held" or "clear") ||
            !IsSafeIdentifier(OperationId) ||
            !IsSafeIdentifier(ChainId) ||
            !IsSafeIdentifier(HeadOperationId) ||
            Generation <= 0 ||
            !WindowsMaintenanceFence.KnownPhases.Contains(Phase) ||
            (State == "clear" && Phase != "controllerConfirmed"))
            throw new InvalidDataException("The maintenance fence record is invalid.");

        return this;
    }

    private static bool IsSafeIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}

public interface IMaintenanceFenceStore
{
    AgentMaintenanceFenceRecord? Read();
    void Write(AgentMaintenanceFenceRecord record);
}

public interface IWindowsMaintenanceFirewallFence
{
    void EnsureBlocked(string sunshinePath);
    bool IsBlocked(string sunshinePath);
    void RemoveBlock(string sunshinePath);
}

public sealed class WindowsMaintenanceFence(
    IMaintenanceFenceStore store,
    IWindowsMaintenanceFirewallFence firewall)
{
    internal static readonly HashSet<string> KnownPhases = new(StringComparer.Ordinal)
    {
        "inspected",
        "controllerStarted",
        "targetValidated",
        "servicesQuiesced",
        "backupPublished",
        "sourceBound",
        "swapIntent",
        "sourceMoved",
        "targetPlaced",
        "servicesStarted",
        "outcomeReported",
        "controllerConfirmed",
        "legacyImported"
    };

    public void Hold(AgentMaintenanceFenceRecord requested, string sunshinePath)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentException.ThrowIfNullOrWhiteSpace(sunshinePath);
        requested.Validate();
        if (requested.State != "held")
            throw new ArgumentException("The maintenance hold record must have state 'held'.", nameof(requested));

        var executable = Path.GetFullPath(sunshinePath);

        // The firewall rule is deliberately separate from Stream Guard's managed rule.
        // Old installed Guard processes do not know its name and cannot undo it through
        // their ordinary block/allow transitions.
        firewall.EnsureBlocked(executable);
        if (!firewall.IsBlocked(executable))
            throw new IOException("The independent Windows Firewall maintenance block was not verified.");

        var existing = store.Read();
        if (existing is not null)
        {
            existing.Validate();
            if (existing.State == "held")
            {
                if (!SameOperation(existing, requested))
                    throw new InvalidOperationException("A different maintenance operation already owns the held fence.");
                if (PhaseRank(requested.Phase) < PhaseRank(existing.Phase))
                    throw new InvalidOperationException("The maintenance fence phase cannot move backwards.");
            }
            else if (existing.ChainId == requested.ChainId &&
                     existing.Generation >= requested.Generation &&
                     !SameOperation(existing, requested))
            {
                throw new InvalidOperationException("The maintenance fence generation is stale.");
            }
        }

        store.Write(requested);
        var published = store.Read();
        if (published is null || !SameRecord(published, requested) || !firewall.IsBlocked(executable))
            throw new IOException("The protected maintenance hold could not be verified after publication.");
    }

    public void Release(
        AgentMaintenanceFenceRecord expectedHeld,
        AgentMaintenanceFenceRecord confirmedClear,
        string sunshinePath,
        bool controllerConfirmed,
        bool targetServicesVerified)
    {
        ArgumentNullException.ThrowIfNull(expectedHeld);
        ArgumentNullException.ThrowIfNull(confirmedClear);
        ArgumentException.ThrowIfNullOrWhiteSpace(sunshinePath);
        expectedHeld.Validate();
        confirmedClear.Validate();

        if (!controllerConfirmed || !targetServicesVerified)
            throw new InvalidOperationException("The maintenance fence cannot be released before Controller confirmation and verified target services.");
        if (expectedHeld.State != "held" ||
            confirmedClear.State != "clear" ||
            confirmedClear.Phase != "controllerConfirmed" ||
            !SameOperation(expectedHeld, confirmedClear))
            throw new InvalidOperationException("The clear record does not match the confirmed held operation.");

        var executable = Path.GetFullPath(sunshinePath);
        var current = store.Read();
        if (current is null || !SameRecord(current, expectedHeld))
            throw new InvalidOperationException("The current maintenance fence changed before release.");
        if (!firewall.IsBlocked(executable))
            throw new InvalidOperationException("The independent Windows Firewall maintenance block is missing or unreadable.");

        // Keep the independent OS block in place while publishing clear. If the process
        // stops here, networking is still blocked; if firewall removal then fails, put
        // the durable marker back to held before returning.
        store.Write(confirmedClear);
        var published = store.Read();
        if (published is null || !SameRecord(published, confirmedClear))
            throw new IOException("The confirmed clear record was not durably published.");

        try
        {
            firewall.RemoveBlock(executable);
            if (firewall.IsBlocked(executable))
                throw new IOException("The independent Windows Firewall maintenance block remained active.");
        }
        catch (Exception releaseError)
        {
            var failClosed = expectedHeld with { Phase = "controllerConfirmed" };
            Exception? markerRestoreError = null;
            Exception? firewallRestoreError = null;
            try
            {
                store.Write(failClosed);
                var restored = store.Read();
                if (restored is null || !SameRecord(restored, failClosed))
                    throw new IOException("The held maintenance marker could not be restored.");
            }
            catch (Exception restoreError)
            {
                markerRestoreError = restoreError;
            }

            try
            {
                firewall.EnsureBlocked(executable);
                if (!firewall.IsBlocked(executable))
                    throw new IOException("The independent firewall block could not be re-established after release failed.");
            }
            catch (Exception restoreError)
            {
                firewallRestoreError = restoreError;
            }

            if (markerRestoreError is not null || firewallRestoreError is not null)
                throw new AggregateException(
                    "The independent firewall release failed and one or more fail-closed protections could not be restored.",
                    new[] { releaseError, markerRestoreError, firewallRestoreError }.OfType<Exception>());

            throw;
        }
    }

    private static bool SameOperation(AgentMaintenanceFenceRecord first, AgentMaintenanceFenceRecord second) =>
        first.OperationId == second.OperationId &&
        first.ChainId == second.ChainId &&
        first.HeadOperationId == second.HeadOperationId &&
        first.Generation == second.Generation;

    private static bool SameRecord(AgentMaintenanceFenceRecord first, AgentMaintenanceFenceRecord second) =>
        SameOperation(first, second) &&
        first.SchemaVersion == second.SchemaVersion &&
        first.State == second.State &&
        first.Phase == second.Phase;

    private static int PhaseRank(string phase) => phase switch
    {
        "inspected" => 0,
        "controllerStarted" => 1,
        "targetValidated" => 2,
        "servicesQuiesced" => 3,
        "backupPublished" => 4,
        "sourceBound" => 5,
        "swapIntent" => 6,
        "sourceMoved" => 7,
        "targetPlaced" => 8,
        "servicesStarted" => 9,
        "outcomeReported" => 10,
        "controllerConfirmed" => 11,
        "legacyImported" => 0,
        _ => -1
    };
}

[SupportedOSPlatform("windows")]
public sealed class WindowsMaintenanceFenceStore : IMaintenanceFenceStore
{
    private const int MaximumDocumentBytes = 8192;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 4
    };
    private static readonly Regex ServiceSidPattern = new(@"SERVICE SID:\s*(S-1-5-80(?:-\d+){5})", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly string directory;
    private readonly string path;

    public WindowsMaintenanceFenceStore()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Windows maintenance fence store requires Windows.");

        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonData) || !Path.IsPathFullyQualified(commonData))
            throw new InvalidOperationException("The native CommonApplicationData path is unavailable.");
        directory = Path.Combine(Path.GetFullPath(commonData), "KamenRaider.AgentMaintenance");
        path = Path.Combine(directory, "maintenance-fence.json");
    }

    public AgentMaintenanceFenceRecord? Read()
    {
        EnsureAncestorsHaveNoReparsePoints();
        if (!Directory.Exists(directory))
            return null;
        EnsureDirectorySecurity(directory);
        if (!File.Exists(path))
            return null;

        EnsureFileSecurity(path);
        var bytes = ReadSingleLinkFile(path);
        if (bytes.Length is <= 0 or > MaximumDocumentBytes)
            throw new InvalidDataException("The maintenance fence document has an invalid size.");
        try
        {
            return (JsonSerializer.Deserialize<AgentMaintenanceFenceRecord>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The maintenance fence document is empty.")).Validate();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The maintenance fence document is malformed.", ex);
        }
    }

    public void Write(AgentMaintenanceFenceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.Validate();
        EnsureAncestorsHaveNoReparsePoints();
        EnsurePrivateDirectory();
        if (File.Exists(path))
            EnsureFileSecurity(path);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        if (bytes.Length > MaximumDocumentBytes)
            throw new InvalidDataException("The maintenance fence document exceeds its size limit.");
        var temporary = Path.Combine(directory, ".maintenance-fence-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                SetPrivateFileSecurity(temporary);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            EnsureFileSecurity(temporary);
            if (File.Exists(path))
                File.Replace(temporary, path, null, ignoreMetadataErrors: false);
            else
                File.Move(temporary, path);
            EnsureFileSecurity(path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal void EnsureLocationForJournal()
    {
        EnsureAncestorsHaveNoReparsePoints();
        EnsurePrivateDirectory();
    }

    internal static void ValidateLocationForJournal()
    {
        var store = new WindowsMaintenanceFenceStore();
        store.EnsureAncestorsHaveNoReparsePoints();
        EnsureDirectorySecurity(store.directory);
    }

    private void EnsureAncestorsHaveNoReparsePoints()
    {
        foreach (var ancestor in Ancestors(directory))
            if (Directory.Exists(ancestor) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("The maintenance fence path contains a reparse point.");
    }

    private void EnsurePrivateDirectory()
    {
        var parent = Path.GetDirectoryName(directory)
            ?? throw new InvalidOperationException("The maintenance fence directory has no parent.");
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("CommonApplicationData is unavailable.");

        if (!Directory.Exists(directory))
        {
            try
            {
                new DirectoryInfo(directory).Create(CreatePrivateDirectorySecurity());
            }
            catch (IOException) when (Directory.Exists(directory))
            {
            }
        }

        EnsureDirectorySecurity(directory);
    }

    internal static void EnsureDirectorySecurity(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new UnauthorizedAccessException("The maintenance fence directory is not a regular directory.");
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!IsPrivateSecurity(security, ResolveAgentServiceSid(), isDirectory: true))
            throw new UnauthorizedAccessException("The maintenance fence directory ACL or owner is unsafe.");
    }

    private static void EnsureFileSecurity(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new UnauthorizedAccessException("The maintenance fence file is not a regular file.");
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!IsPrivateSecurity(security, ResolveAgentServiceSid(), isDirectory: false))
            throw new UnauthorizedAccessException("The maintenance fence file ACL or owner is unsafe.");
        using var handle = OpenReadHandle(path);
        if (!GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks != 1)
            throw new UnauthorizedAccessException("The maintenance fence file must have exactly one hard link.");
    }

    private static byte[] ReadSingleLinkFile(string path)
    {
        using var handle = OpenReadHandle(path);
        if (!GetFileInformationByHandle(handle, out var info) ||
            info.NumberOfLinks != 1 ||
            (info.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) != 0)
            throw new UnauthorizedAccessException("The maintenance fence must be a regular single-link file.");
        using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        if (stream.Length is <= 0 or > MaximumDocumentBytes)
            return [];
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static SafeFileHandle OpenReadHandle(string path)
    {
        const uint genericRead = 0x80000000;
        const uint shareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
        const uint openExisting = 3;
        const uint openReparsePoint = 0x00200000;
        var handle = CreateFile(path, genericRead, shareReadWriteDelete, IntPtr.Zero, openExisting, openReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("The maintenance fence file could not be opened safely.", new System.ComponentModel.Win32Exception(error));
        }
        return handle;
    }

    private static void SetPrivateFileSecurity(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid());
        security.AddAccessRule(new FileSystemAccessRule(SystemSid(), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid(), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(ResolveAgentServiceSid(), FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static DirectorySecurity CreatePrivateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid());
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(SystemSid(), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid(), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(ResolveAgentServiceSid(), FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static bool IsPrivateSecurity(FileSystemSecurity security, SecurityIdentifier agentServiceSid, bool isDirectory)
    {
        var system = SystemSid();
        var administrators = AdministratorsSid();
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (!security.AreAccessRulesProtected || owner is null || (owner != system && owner != administrators))
            return false;

        var inheritance = isDirectory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        if (rules.Length != 3)
            return false;
        var identities = new HashSet<SecurityIdentifier>();
        foreach (var rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                rule.IsInherited ||
                rule.PropagationFlags != PropagationFlags.None ||
                rule.InheritanceFlags != inheritance ||
                rule.IdentityReference is not SecurityIdentifier sid ||
                !identities.Add(sid))
                return false;
            var rights = sid == system || sid == administrators
                ? FileSystemRights.FullControl
                : sid == agentServiceSid
                    ? FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize
                    : (FileSystemRights)(-1);
            if (rights == (FileSystemRights)(-1) || rule.FileSystemRights != rights)
                return false;
        }

        return identities.SetEquals([system, administrators, agentServiceSid]);
    }

    private static SecurityIdentifier ResolveAgentServiceSid()
    {
        var scPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = scPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("showsid");
        startInfo.ArgumentList.Add("KamenRaider Agent");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Agent service SID lookup could not start.");
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The Agent service SID lookup timed out.");
        }
        var output = process.StandardOutput.ReadToEnd() + Environment.NewLine + process.StandardError.ReadToEnd();
        var match = ServiceSidPattern.Match(output);
        if (process.ExitCode != 0 || !match.Success)
            throw new InvalidOperationException("The Agent service SID could not be resolved.");
        return new SecurityIdentifier(match.Groups[1].Value);
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            yield return current;
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static SecurityIdentifier SystemSid() => new(WellKnownSidType.LocalSystemSid, null);
    private static SecurityIdentifier AdministratorsSid() => new(WellKnownSidType.BuiltinAdministratorsSid, null);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInformation
    {
        public uint Attributes;
        public ulong CreationTime;
        public ulong LastAccessTime;
        public ulong LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}

public sealed record MaintenanceFirewallProfile(string Name, bool Enabled, bool AllowLocalFirewallRules);

public sealed record EffectiveMaintenanceFirewallRule(
    string Name,
    string? ApplicationPath,
    string? ServiceName,
    string PolicySourceType,
    bool Enabled,
    string Direction,
    string Action,
    string Profiles,
    string? Protocol,
    string? LocalPorts,
    string? RemotePorts,
    string? LocalAddresses,
    string? RemoteAddresses,
    string? InterfaceTypes,
    string? Interfaces,
    string? EdgeTraversalPolicy,
    bool OverrideBlockRules);

public sealed record EffectiveMaintenanceFirewallPolicy(
    IReadOnlyList<MaintenanceFirewallProfile> Profiles,
    IReadOnlyList<EffectiveMaintenanceFirewallRule> Rules);

public interface IEffectiveMaintenanceFirewallPolicyReader
{
    EffectiveMaintenanceFirewallPolicy ReadActivePolicy();
}

public static class WindowsMaintenanceFirewallPolicyVerifier
{
    public static void EnsureProfilesUsable(EffectiveMaintenanceFirewallPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var required = new HashSet<string>(["Domain", "Private", "Public"], StringComparer.OrdinalIgnoreCase);
        if (policy.Profiles is null || policy.Profiles.Count != 3 ||
            policy.Profiles.Any(profile => !required.Remove(profile.Name) || !profile.Enabled || !profile.AllowLocalFirewallRules) ||
            required.Count != 0)
            throw new InvalidOperationException("Every effective Windows Firewall profile must be enabled and permit merged local rules.");
    }

    public static void EnsureEffectiveBlock(EffectiveMaintenanceFirewallPolicy policy, string ruleName, string sunshinePath)
    {
        EnsureProfilesUsable(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleName);
        var executable = Path.GetFullPath(sunshinePath);
        if (policy.Rules is null || policy.Rules.Count != 1)
            throw new InvalidOperationException("The maintenance rule is not uniquely present in Windows Firewall ActiveStore.");
        var rule = policy.Rules[0];
        if (!string.Equals(rule.Name, ruleName, StringComparison.Ordinal) ||
            !string.Equals(rule.PolicySourceType, "Local", StringComparison.OrdinalIgnoreCase) ||
            !rule.Enabled || !PathEquals(rule.ApplicationPath, executable) ||
            !string.IsNullOrWhiteSpace(rule.ServiceName) ||
            !string.Equals(rule.Direction, "Inbound", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(rule.Action, "Block", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(rule.Profiles, "Any", StringComparison.OrdinalIgnoreCase) ||
            rule.Protocol is not ("256" or "Any") ||
            !IsAny(rule.LocalPorts) || !IsAny(rule.RemotePorts) ||
            !IsAny(rule.LocalAddresses) || !IsAny(rule.RemoteAddresses) ||
            !IsAny(rule.InterfaceTypes) || !IsAny(rule.Interfaces) ||
            !string.Equals(rule.EdgeTraversalPolicy, "Block", StringComparison.OrdinalIgnoreCase) ||
            rule.OverrideBlockRules)
            throw new InvalidOperationException("The maintenance firewall block is not an exact effective all-profile Sunshine block.");
    }

    private static bool PathEquals(string? value, string expected)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { return string.Equals(Path.GetFullPath(value), expected, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool IsAny(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "*" ||
        string.Equals(value, "Any", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Any,Any", StringComparison.OrdinalIgnoreCase);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsPowerShellMaintenanceFirewallPolicyReader : IEffectiveMaintenanceFirewallPolicyReader
{
    private const int MaximumOutputCharacters = 65536;
    private const string Script = @"
$ErrorActionPreference = 'Stop'
$profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop | ForEach-Object {
    [pscustomobject]@{ name = [string]$_.Name; enabled = ([string]$_.Enabled -eq 'True'); allowLocalFirewallRules = ([string]$_.AllowLocalFirewallRules -eq 'True') }
})
$rules = @(Get-NetFirewallRule -PolicyStore ActiveStore -Name 'KamenRaider Maintenance Fence - Sunshine' -ErrorAction SilentlyContinue)
$activeRules = @(foreach ($rule in $rules) {
    $app = @(Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop)
    $service = @(Get-NetFirewallServiceFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop)
    $port = @(Get-NetFirewallPortFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop)
    $address = @(Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop)
    $interfaceType = @(Get-NetFirewallInterfaceTypeFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop)
    $interface = @(Get-NetFirewallInterfaceFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop)
    if ($app.Count -ne 1 -or $service.Count -ne 1 -or $port.Count -ne 1 -or $address.Count -ne 1 -or $interfaceType.Count -ne 1 -or $interface.Count -ne 1) { throw 'ActiveStore rule filters are ambiguous.' }
    [pscustomobject]@{
        name = [string]$rule.Name
        applicationPath = [string]$app.Program
        serviceName = [string]$service[0].Service
        policySourceType = [string]$rule.PolicyStoreSourceType
        enabled = ([string]$rule.Enabled -eq 'True')
        direction = [string]$rule.Direction
        action = [string]$rule.Action
        profiles = [string]$rule.Profile
        protocol = [string]$port[0].Protocol
        localPorts = (@($port[0].LocalPort) -join ',')
        remotePorts = (@($port[0].RemotePort) -join ',')
        localAddresses = (@($address[0].LocalAddress) -join ',')
        remoteAddresses = (@($address[0].RemoteAddress) -join ',')
        interfaceTypes = (@($interfaceType[0].InterfaceType) -join ',')
        interfaces = (@($interface[0].InterfaceAlias) -join ',')
        edgeTraversalPolicy = [string]$rule.EdgeTraversalPolicy
        overrideBlockRules = ([string]$rule.OverrideBlockRules -eq 'True')
    }
})
[pscustomobject]@{ profiles = $profiles; rules = $activeRules } | ConvertTo-Json -Depth 6 -Compress
";

    public EffectiveMaintenanceFirewallPolicy ReadActivePolicy()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Firewall policy inspection requires Windows.");
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powershell = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            throw new FileNotFoundException("Windows PowerShell required for ActiveStore firewall verification is unavailable.", powershell);
        var startInfo = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(Script);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The ActiveStore firewall inspection could not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The ActiveStore firewall inspection timed out.");
        }
        Task.WaitAll(outputTask, errorTask);
        var output = outputTask.Result;
        if (output.Length is <= 0 or > MaximumOutputCharacters || process.ExitCode != 0)
            throw new InvalidOperationException("The ActiveStore firewall inspection failed or exceeded its bounded output.");
        try
        {
            return JsonSerializer.Deserialize<EffectiveMaintenanceFirewallPolicy>(output, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                MaxDepth = 8
            }) ?? throw new InvalidDataException("ActiveStore firewall inspection returned an empty document.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("ActiveStore firewall inspection returned malformed data.", ex);
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsFirewallMaintenanceFence : IWindowsMaintenanceFirewallFence
{
    public const string RuleName = "KamenRaider Maintenance Fence - Sunshine";
    private const int AllProfiles = 0x7fffffff;
    private const int Inbound = 1;
    private const int Block = 0;
    private const int AnyProtocol = 256;
    private const int RuleNotFoundHResult = unchecked((int)0x80070490);
    private readonly IEffectiveMaintenanceFirewallPolicyReader policyReader;

    public WindowsFirewallMaintenanceFence(IEffectiveMaintenanceFirewallPolicyReader? policyReader = null) =>
        this.policyReader = policyReader ?? new WindowsPowerShellMaintenanceFirewallPolicyReader();

    public void EnsureBlocked(string sunshinePath)
    {
        var executable = ValidateSunshinePath(sunshinePath);
        dynamic policy = CreateComObject("HNetCfg.FwPolicy2");
        object? rulesObject = null;
        object? ruleObject = null;
        try
        {
            EnsurePolicyUsable(policy);
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            try
            {
                ruleObject = rules.Item(RuleName);
            }
            catch (Exception ex) when (ex.HResult == RuleNotFoundHResult || ex is FileNotFoundException)
            {
                dynamic rule = CreateComObject("HNetCfg.FWRule");
                try
                {
                    rule.Name = RuleName;
                    rule.Description = "Independent KamenRaider updater maintenance fence.";
                    rule.ApplicationName = executable;
                    rule.ServiceName = "";
                    rule.Protocol = AnyProtocol;
                    rule.LocalPorts = "";
                    rule.RemotePorts = "";
                    rule.LocalAddresses = "*";
                    rule.RemoteAddresses = "*";
                    rule.InterfaceTypes = "All";
                    rule.Interfaces = null;
                    rule.Direction = Inbound;
                    rule.Action = Block;
                    rule.Profiles = AllProfiles;
                    rule.EdgeTraversal = false;
                    rule.Enabled = true;
                    rules.Add(rule);
                }
                finally { ReleaseComObject(rule); }
            }

            if (ruleObject is not null)
            {
                EnsureExactRule((dynamic)ruleObject, executable);
                if (!(bool)((dynamic)ruleObject).Enabled)
                    ((dynamic)ruleObject).Enabled = true;
            }
        }
        finally
        {
            ReleaseComObject(ruleObject);
            ReleaseComObject(rulesObject);
            ReleaseComObject(policy);
        }

        if (!IsBlocked(executable))
            throw new IOException("The independent Windows Firewall maintenance rule did not become effective.");
    }

    public bool IsBlocked(string sunshinePath)
    {
        var executable = ValidateSunshinePath(sunshinePath);
        dynamic policy = CreateComObject("HNetCfg.FwPolicy2");
        object? rulesObject = null;
        object? ruleObject = null;
        try
        {
            EnsurePolicyUsable(policy);
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            try { ruleObject = rules.Item(RuleName); }
            catch (Exception ex) when (ex.HResult == RuleNotFoundHResult || ex is FileNotFoundException) { return false; }
            if (ruleObject is null)
                return false;
            EnsureExactRule((dynamic)ruleObject, executable);
            if (!(bool)((dynamic)ruleObject).Enabled)
                return false;
            WindowsMaintenanceFirewallPolicyVerifier.EnsureEffectiveBlock(policyReader.ReadActivePolicy(), RuleName, executable);
            return true;
        }
        finally
        {
            ReleaseComObject(ruleObject);
            ReleaseComObject(rulesObject);
            ReleaseComObject(policy);
        }
    }

    public void RemoveBlock(string sunshinePath)
    {
        var executable = ValidateSunshinePath(sunshinePath);
        dynamic policy = CreateComObject("HNetCfg.FwPolicy2");
        object? rulesObject = null;
        object? ruleObject = null;
        try
        {
            EnsurePolicyUsable(policy);
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            try { ruleObject = rules.Item(RuleName); }
            catch (Exception ex) when (ex.HResult == RuleNotFoundHResult || ex is FileNotFoundException) { return; }
            if (ruleObject is not null)
                EnsureExactRule((dynamic)ruleObject, executable);
            rules.Remove(RuleName);
        }
        finally
        {
            ReleaseComObject(ruleObject);
            ReleaseComObject(rulesObject);
            ReleaseComObject(policy);
        }
    }

    private static string ValidateSunshinePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Firewall is available only on Windows.");
        var full = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(full) || (File.GetAttributes(full) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The Sunshine executable path is not a regular absolute file.");
        return full;
    }

    private void EnsurePolicyUsable(dynamic policy)
    {
        if ((int)policy.CurrentProfileTypes == 0 || (int)policy.LocalPolicyModifyState != 0)
            throw new InvalidOperationException("Windows Firewall policy cannot accept a verified local maintenance block.");
        foreach (var profile in new[] { 1, 2, 4 })
            if (!(bool)policy.FirewallEnabled[profile])
                throw new InvalidOperationException("Windows Firewall must be enabled for every profile before maintenance.");
        WindowsMaintenanceFirewallPolicyVerifier.EnsureProfilesUsable(policyReader.ReadActivePolicy());
    }

    private static void EnsureExactRule(dynamic rule, string executable)
    {
        var interfaces = rule.Interfaces;
        var unrestrictedInterfaces = interfaces is null || interfaces is Array array && array.Length == 0;
        var exact = string.Equals((string)rule.Name, RuleName, StringComparison.Ordinal) &&
            PathEquals((string?)rule.ApplicationName, executable) &&
            string.IsNullOrWhiteSpace((string?)rule.ServiceName) &&
            (int)rule.Direction == Inbound &&
            (int)rule.Action == Block &&
            (int)rule.Profiles == AllProfiles &&
            (int)rule.Protocol == AnyProtocol &&
            IsAny((string?)rule.LocalPorts) &&
            IsAny((string?)rule.RemotePorts) &&
            IsAnyAddress((string?)rule.LocalAddresses) &&
            IsAnyAddress((string?)rule.RemoteAddresses) &&
            string.Equals((string?)rule.InterfaceTypes, "All", StringComparison.OrdinalIgnoreCase) &&
            unrestrictedInterfaces &&
            !(bool)rule.EdgeTraversal &&
            !(bool)rule.OverrideBlockRules;
        if (!exact)
            throw new InvalidOperationException("The existing maintenance firewall rule has an unexpected shape.");
    }

    private static bool PathEquals(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual)) return false;
        try { return string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    private static bool IsAny(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "*" || string.Equals(value, "Any", StringComparison.OrdinalIgnoreCase);

    private static bool IsAnyAddress(string? value) =>
        value == "*" || string.Equals(value, "Any", StringComparison.OrdinalIgnoreCase);

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
