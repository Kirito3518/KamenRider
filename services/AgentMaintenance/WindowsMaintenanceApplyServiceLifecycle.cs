using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace KamenRaider.AgentMaintenance;

[SupportedOSPlatform("windows")]
public sealed class WindowsMaintenanceApplyServiceLifecycle : IMaintenanceApplyServiceLifecycle
{
    private const string AgentAccount = "LocalSystem";
    private const string LocalSystemAccount = "LocalSystem";
    private static readonly Regex VersionPattern = new("\\A(?:[0-9]+\\.[0-9]+\\.[0-9]+|1\\.0\\.0\\+g[0-9a-f]{12,40})\\z", RegexOptions.CultureInvariant);

    private readonly IMaintenanceWindowsServiceApi services;
    private readonly IMaintenanceProcessInspector processes;
    private readonly IMaintenanceApplyAuthorization authorization;
    private readonly MaintenanceServiceLayout layout;
    private readonly Func<string, string> readInstalledVersion;
    private readonly TimeSpan timeout;
    private readonly TimeSpan interval;

    public WindowsMaintenanceApplyServiceLifecycle(
        IMaintenanceWindowsServiceApi services,
        IMaintenanceProcessInspector processes,
        IMaintenanceApplyAuthorization authorization,
        MaintenanceServiceLayout layout,
        Func<string, string> readInstalledVersion,
        TimeSpan? operationTimeout = null,
        TimeSpan? pollInterval = null)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.processes = processes ?? throw new ArgumentNullException(nameof(processes));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        this.layout = NormalizeLayout(layout ?? throw new ArgumentNullException(nameof(layout)));
        this.readInstalledVersion = readInstalledVersion ?? throw new ArgumentNullException(nameof(readInstalledVersion));
        timeout = operationTimeout ?? TimeSpan.FromSeconds(30);
        interval = pollInterval ?? TimeSpan.FromMilliseconds(200);
        if (timeout <= TimeSpan.Zero || interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(operationTimeout), "Service timeout and polling interval must be positive.");
    }

    public async Task EnsureStoppedAndVerifyQuiescedAsync(string operationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        authorization.VerifyHeld(operationId);
        var definitions = Definitions().ToArray();

        // Validate every service configuration before the first stop control.
        foreach (var definition in definitions)
            _ = QueryAndValidate(definition);

        foreach (var definition in StopOrder())
        {
            cancellationToken.ThrowIfCancellationRequested();
            authorization.VerifyHeld(operationId);
            await StopAndVerifyAsync(definition, operationId, cancellationToken).ConfigureAwait(false);
        }

        authorization.VerifyHeld(operationId);
        var remaining = processes.FindByExecutablePath(
            [layout.AgentExecutablePath, layout.SunshineServiceExecutablePath, layout.SunshineExecutablePath]);
        if (remaining.Count != 0)
            throw new InvalidOperationException("An Agent or Sunshine process remains; installation files must remain held.");

        foreach (var definition in definitions)
        {
            var stopped = QueryAndValidate(definition);
            if (stopped.State != MaintenanceWindowsServiceState.Stopped || stopped.ProcessId != 0)
                throw new InvalidOperationException($"Service '{definition.Name}' restarted or retained a process after the stop operation.");
        }

        authorization.VerifyHeldAndQuiesced(operationId);
    }

    public async Task StartAndVerifyAsync(
        string operationId,
        string installDirectory,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        if (!VersionPattern.IsMatch(expectedVersion ?? string.Empty) ||
            !PathEquals(Path.GetFullPath(installDirectory), Path.GetDirectoryName(layout.AgentExecutablePath)!))
            throw new InvalidDataException("The target service start request does not match the fixed Agent installation layout.");
        var installedVersion = readInstalledVersion(Path.GetFullPath(installDirectory));
        if (!string.Equals(installedVersion, expectedVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The protected Agent tree version does not match the target journal binding.");

        authorization.VerifyHeld(operationId);
        var definitions = Definitions().ToDictionary(definition => definition.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions.Values)
        {
            var current = QueryAndValidate(definition);
            if (current.State != MaintenanceWindowsServiceState.Stopped || current.ProcessId != 0)
                throw new InvalidOperationException("All Agent and Sunshine services must be stopped before target service start.");
        }

        foreach (var definition in StartOrder(definitions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            authorization.VerifyHeld(operationId);
            services.Start(definition.Name);
            await WaitForRunningAndVerifyImageAsync(definition, operationId, cancellationToken).ConfigureAwait(false);
        }

        authorization.VerifyHeld(operationId);
        foreach (var definition in definitions.Values)
        {
            var running = QueryAndValidate(definition);
            if (running.State != MaintenanceWindowsServiceState.Running || running.ProcessId == 0)
                throw new InvalidOperationException($"Service '{definition.Name}' did not remain running after startup.");
            VerifyProcessImage(running, definition);
        }
    }

    public Task VerifyStartedAsync(
        string operationId,
        string installDirectory,
        string expectedVersion,
        Action verifyHeld,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentNullException.ThrowIfNull(verifyHeld);
        var normalizedInstallDirectory = Path.GetFullPath(installDirectory);
        if (!VersionPattern.IsMatch(expectedVersion ?? string.Empty) ||
            !PathEquals(normalizedInstallDirectory, Path.GetDirectoryName(layout.AgentExecutablePath)!))
            throw new InvalidDataException("The terminal service verification request does not match the fixed Agent installation layout.");
        var installedVersion = readInstalledVersion(normalizedInstallDirectory);
        if (!string.Equals(installedVersion, expectedVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The protected Agent tree version does not match the terminal journal outcome.");

        var definitions = Definitions().ToArray();
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            authorization.VerifyHeld(operationId);
            verifyHeld();
            var running = QueryAndValidate(definition);
            if (running.State != MaintenanceWindowsServiceState.Running || running.ProcessId == 0)
                throw new InvalidOperationException($"Required service '{definition.Name}' is not running in the verified terminal state.");
        }

        authorization.VerifyHeld(operationId);
        verifyHeld();
        return Task.CompletedTask;
    }

    private async Task StopAndVerifyAsync(ServiceDefinition definition, string operationId, CancellationToken cancellationToken)
    {
        var initial = QueryAndValidate(definition);
        if (initial.State == MaintenanceWindowsServiceState.Stopped)
        {
            if (initial.ProcessId != 0)
                throw new InvalidOperationException($"Stopped service '{definition.Name}' reports a nonzero process ID.");
            return;
        }
        if (initial.State == MaintenanceWindowsServiceState.Running)
        {
            authorization.VerifyHeld(operationId);
            services.Stop(definition.Name);
        }
        else if (initial.State != MaintenanceWindowsServiceState.StopPending)
        {
            throw new InvalidOperationException($"Service '{definition.Name}' is in an ambiguous state while stopping.");
        }

        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            authorization.VerifyHeld(operationId);
            var current = QueryAndValidate(definition, allowPending: true);
            if (current.State == MaintenanceWindowsServiceState.Stopped && current.ProcessId == 0)
                return;
            if (current.State is not (MaintenanceWindowsServiceState.Running or MaintenanceWindowsServiceState.StopPending))
                throw new InvalidOperationException($"Service '{definition.Name}' entered an ambiguous state while stopping.");
            await DelayWithinTimeoutAsync(timer, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"Service '{definition.Name}' did not stop within {timeout}.");
    }

    private async Task WaitForRunningAndVerifyImageAsync(
        ServiceDefinition definition,
        string operationId,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            authorization.VerifyHeld(operationId);
            var current = QueryAndValidate(definition, allowPending: true);
            if (current.State == MaintenanceWindowsServiceState.Running)
            {
                if (current.ProcessId == 0)
                    throw new InvalidOperationException($"Running service '{definition.Name}' reports process ID zero.");
                VerifyProcessImage(current, definition);
                return;
            }
            if (current.State != MaintenanceWindowsServiceState.StartPending)
                throw new InvalidOperationException($"Service '{definition.Name}' did not enter the expected running state.");
            await DelayWithinTimeoutAsync(timer, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"Service '{definition.Name}' did not reach Running within {timeout}.");
    }

    private async Task DelayWithinTimeoutAsync(Stopwatch timer, CancellationToken cancellationToken)
    {
        var remaining = timeout - timer.Elapsed;
        if (remaining <= TimeSpan.Zero) return;
        await Task.Delay(remaining < interval ? remaining : interval, cancellationToken).ConfigureAwait(false);
    }

    private void VerifyProcessImage(MaintenanceWindowsServiceSnapshot snapshot, ServiceDefinition definition)
    {
        if (snapshot.ProcessId is 0 or > int.MaxValue)
            throw new InvalidOperationException($"Service '{definition.Name}' reports an invalid process ID.");
        var actual = processes.GetExecutablePath((int)snapshot.ProcessId);
        if (!MaintenanceServiceQuiescer.PathEquals(actual, definition.ExecutablePath))
            throw new InvalidOperationException($"Service '{definition.Name}' process image does not match its fixed executable path.");
    }

    private MaintenanceWindowsServiceSnapshot QueryAndValidate(ServiceDefinition definition, bool allowPending = false)
    {
        var snapshot = services.Query(definition.Name)
            ?? throw new InvalidOperationException($"Required service '{definition.Name}' is missing.");
        if (!string.Equals(snapshot.ServiceName, definition.Name, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.AccountName, definition.AccountName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Service '{definition.Name}' has an unexpected identity.");
        if (snapshot.ServiceType != MaintenanceWindowsServiceTypes.Win32OwnProcess)
            throw new InvalidOperationException($"Service '{definition.Name}' is not a Win32 own-process service.");

        var (imagePath, arguments) = MaintenanceServiceQuiescer.ParseImageCommandLine(snapshot.ImagePath);
        var argumentsMatch = definition.HasContentRootArgument
            ? MaintenanceServiceQuiescer.ContentRootArgumentsMatch(arguments, definition.ExecutablePath, definition.Arguments)
            : definition.AllowedArguments is not null
                ? definition.AllowedArguments.Contains(arguments, StringComparer.Ordinal)
                : string.Equals(arguments, definition.Arguments, StringComparison.Ordinal);
        if (!MaintenanceServiceQuiescer.PathEquals(imagePath, definition.ExecutablePath) || !argumentsMatch)
            throw new InvalidOperationException($"Service '{definition.Name}' executable path or arguments changed.");
        if (definition.ExpectedDependencies is not null &&
            !snapshot.Dependencies.SequenceEqual(definition.ExpectedDependencies, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Service '{definition.Name}' has an unexpected dependency list.");
        if (snapshot.Dependencies.Any(string.IsNullOrWhiteSpace) ||
            snapshot.Dependencies.Distinct(StringComparer.OrdinalIgnoreCase).Count() != snapshot.Dependencies.Count)
            throw new InvalidOperationException($"Service '{definition.Name}' has an invalid dependency list.");

        if (snapshot.State == MaintenanceWindowsServiceState.Stopped)
        {
            if (snapshot.ProcessId != 0)
                throw new InvalidOperationException($"Stopped service '{definition.Name}' reports a nonzero process ID.");
        }
        else if (snapshot.State == MaintenanceWindowsServiceState.Running)
        {
            VerifyProcessImage(snapshot, definition);
        }
        else if (!allowPending || snapshot.State is not (MaintenanceWindowsServiceState.StartPending or MaintenanceWindowsServiceState.StopPending))
        {
            throw new InvalidOperationException($"Service '{definition.Name}' is in an ambiguous state.");
        }
        else if (snapshot.ProcessId != 0)
        {
            VerifyProcessImage(snapshot, definition);
        }
        return snapshot;
    }

    private IEnumerable<ServiceDefinition> Definitions()
    {
        // The Agent is a single LocalSystem service with no service dependencies.
        yield return new(MaintenanceServiceQuiescer.AgentServiceName, layout.AgentExecutablePath, AgentAccount, string.Empty, true,
            null, []);
        // Preserve vendor dependencies; only the two forms observed in supported Sunshine installs are accepted.
        yield return new(MaintenanceServiceQuiescer.SunshineServiceName, layout.SunshineServiceExecutablePath,
            LocalSystemAccount, string.Empty, false, [string.Empty, "--service"], null);
    }

    private IEnumerable<ServiceDefinition> StopOrder()
    {
        var definitions = Definitions().ToDictionary(definition => definition.Name, StringComparer.OrdinalIgnoreCase);
        yield return definitions[MaintenanceServiceQuiescer.AgentServiceName];
        yield return definitions[MaintenanceServiceQuiescer.SunshineServiceName];
    }

    private static IEnumerable<ServiceDefinition> StartOrder(IReadOnlyDictionary<string, ServiceDefinition> definitions)
    {
        // The Agent is started after Sunshine because it attaches to the running stream host.
        yield return definitions[MaintenanceServiceQuiescer.SunshineServiceName];
        yield return definitions[MaintenanceServiceQuiescer.AgentServiceName];
    }

    private static MaintenanceServiceLayout NormalizeLayout(MaintenanceServiceLayout value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var agent = Path.GetFullPath(value.AgentExecutablePath);
        var sunshineService = Path.GetFullPath(value.SunshineServiceExecutablePath);
        var sunshine = Path.GetFullPath(value.SunshineExecutablePath);
        if (!Path.GetFileName(agent).Equals("Agent.exe", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(sunshineService).Equals("sunshinesvc.exe", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(sunshine).Equals("sunshine.exe", StringComparison.OrdinalIgnoreCase) ||
            !PathEquals(Path.GetDirectoryName(sunshineService)!, Path.Combine(Path.GetDirectoryName(sunshine)!, "tools")))
            throw new InvalidDataException("The service layout does not match the supported Agent/Sunshine installation shape.");
        return new(agent, sunshineService, sunshine);
    }

    private static bool PathEquals(string first, string second) =>
        MaintenanceServiceQuiescer.PathEquals(first, second);

    private sealed record ServiceDefinition(
        string Name,
        string ExecutablePath,
        string AccountName,
        string Arguments,
        bool HasContentRootArgument,
        IReadOnlyList<string>? AllowedArguments,
        IReadOnlyList<string>? ExpectedDependencies);
}
