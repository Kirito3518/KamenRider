using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace KamenRaider.AgentMaintenance;

/// <summary>
/// Production source-binding authorization. The durable Controller start acknowledgement and
/// the local lease state are re-proven before the helper validates the target, stops services,
/// publishes the immutable backup or binds the source. Application IDs come from the protected
/// lifecycle request and must match the validated journal binding.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMaintenanceSourceBindingAuthorization : IMaintenanceSourceBindingAuthorization
{
    private readonly IMaintenanceJournalStore journalStore;
    private readonly IMaintenanceLeaseEnvironment environment;
    private readonly object gate = new();
    private MaintenanceLifecycleRequest? boundRequest;

    public WindowsMaintenanceSourceBindingAuthorization()
        : this(new WindowsMaintenanceJournalStore(),
            WindowsMaintenanceApplyLeaseAuthorization.DefaultAgentDataDirectory,
            new WindowsMaintenanceLeaseEnvironment())
    {
    }

    internal WindowsMaintenanceSourceBindingAuthorization(
        IMaintenanceJournalStore journalStore,
        string agentDataDirectory,
        IMaintenanceLeaseEnvironment environment)
    {
        this.journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(agentDataDirectory);
        var directory = Path.GetFullPath(agentDataDirectory);
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("The Agent data directory must be an absolute path.", nameof(agentDataDirectory));
        AgentDataDirectory = directory;
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public string AgentDataDirectory { get; }

    public void VerifyRequestBinding(MaintenanceLifecycleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ChainId) || string.IsNullOrWhiteSpace(request.HeadOperationId) ||
            string.IsNullOrWhiteSpace(request.OperationId) || request.Generation <= 0)
            throw new InvalidOperationException("The source binding request has no maintenance chain identity; the operation remains held.");
        VerifyControllerHoldAndLease(request);
        lock (gate)
            boundRequest = request;
    }

    public void VerifyHeldAndQuiesced(string operationId)
    {
        // Quiescence is verified by the service quiescer. The journal and lease are re-read
        // because the Agent can race the helper between stop and the protected backup copy.
        VerifyHeld(operationId);
    }

    public void VerifyHeld(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        MaintenanceLifecycleRequest request;
        lock (gate)
            request = boundRequest is { } bound && string.Equals(bound.OperationId, operationId, StringComparison.Ordinal)
                ? bound
                : throw new InvalidOperationException(
                    "The maintenance source binding authorization is not bound to this operation; the operation remains held.");
        VerifyControllerHoldAndLease(request);
    }

    private void VerifyControllerHoldAndLease(MaintenanceLifecycleRequest request)
    {
        _ = MaintenanceJournalHoldEvidence.ReadAndVerify(
            journalStore,
            request.ChainId,
            request.HeadOperationId,
            request.Generation,
            request.OperationId,
            request.TargetVersion,
            request.TargetRuntime,
            request.TargetPackageSha256);
        MaintenanceLocalLeaseEvidence.EnsureDrained(AgentDataDirectory, environment);
    }
}

/// <summary>
/// Production independent Windows Firewall maintenance fence. It writes a dedicated Local
/// inbound block for the trusted Sunshine executable (distinct from Stream Guard's managed
/// rule) and verifies the effective ActiveStore policy before reporting success. A host that
/// cannot prove the effective block fails closed; the helper never continues on a partial rule.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerShellMaintenanceFirewallFence : IWindowsMaintenanceFirewallFence
{
    public const string RuleName = "KamenRaider Maintenance Fence - Sunshine";
    private const int MaximumOutputCharacters = 8192;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private readonly IEffectiveMaintenanceFirewallPolicyReader reader;

    public WindowsPowerShellMaintenanceFirewallFence()
        : this(new WindowsPowerShellMaintenanceFirewallPolicyReader())
    {
    }

    internal WindowsPowerShellMaintenanceFirewallFence(IEffectiveMaintenanceFirewallPolicyReader reader)
    {
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public void EnsureBlocked(string sunshinePath)
    {
        var executable = ValidateSunshinePath(sunshinePath);
        try
        {
            _ = RunPowerShell(
                "$ErrorActionPreference='Stop'\n" +
                "$name=" + Quote(RuleName) + "\n" +
                "Remove-NetFirewallRule -Name $name -ErrorAction SilentlyContinue\n" +
                "New-NetFirewallRule -Name $name -DisplayName $name -Description 'KamenRaider maintenance fence' " +
                "-Direction Inbound -Action Block -Program " + Quote(executable) +
                " -Profile Any -Enabled True -EdgeTraversalPolicy Block -ErrorAction Stop | Out-Null\n");
            var policy = reader.ReadActivePolicy();
            WindowsMaintenanceFirewallPolicyVerifier.EnsureEffectiveBlock(policy, RuleName, executable);
        }
        catch
        {
            TryRemoveRule();
            throw;
        }
    }

    public bool IsBlocked(string sunshinePath)
    {
        try
        {
            var executable = ValidateSunshinePath(sunshinePath);
            var policy = reader.ReadActivePolicy();
            WindowsMaintenanceFirewallPolicyVerifier.EnsureEffectiveBlock(policy, RuleName, executable);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            return false;
        }
    }

    public void RemoveBlock(string sunshinePath)
    {
        _ = ValidateSunshinePath(sunshinePath);
        TryRemoveRule();
    }

    private void TryRemoveRule()
    {
        try
        {
            _ = RunPowerShell(
                "$ErrorActionPreference='SilentlyContinue'\n" +
                "Remove-NetFirewallRule -Name " + Quote(RuleName) + " -ErrorAction SilentlyContinue\n");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            // The caller verifies the effective policy afterwards; a failed cleanup must not be
            // reported as a proven clear state.
        }
    }

    private static string ValidateSunshinePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException("The maintenance fence Sunshine path is not absolute.");
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetFileName(fullPath), "Sunshine.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The maintenance fence Sunshine path does not name Sunshine.exe.");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The maintenance fence Sunshine executable is missing.", fullPath);
        return fullPath;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string RunPowerShell(string script)
    {
        var system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0");
        var powershell = Path.Combine(system32, "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("The maintenance firewall command could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)OperationTimeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("The maintenance firewall command timed out.");
        }

        var output = Shorten(stdout.GetAwaiter().GetResult() + Environment.NewLine + stderr.GetAwaiter().GetResult());
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "The maintenance firewall command failed with exit code " + process.ExitCode + ". " + output);
        return output;
    }

    private static string Shorten(string value) =>
        value.Length <= MaximumOutputCharacters ? value : value[..MaximumOutputCharacters];
}
