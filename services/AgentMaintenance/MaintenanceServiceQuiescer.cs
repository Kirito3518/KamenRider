using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KamenRaider.AgentMaintenance;

public enum MaintenanceWindowsServiceState
{
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7
}

public sealed record MaintenanceWindowsServiceSnapshot(
    string ServiceName,
    string ImagePath,
    string AccountName,
    IReadOnlyList<string> Dependencies,
    MaintenanceWindowsServiceState State,
    uint ProcessId,
    uint ServiceType);

public static class MaintenanceWindowsServiceTypes
{
    public const uint Win32OwnProcess = 0x00000010;
}

public sealed record MaintenanceServiceLayout(
    string AgentExecutablePath,
    string SunshineServiceExecutablePath,
    string SunshineExecutablePath);

public interface IMaintenanceWindowsServiceApi
{
    MaintenanceWindowsServiceSnapshot? Query(string serviceName);
    void Stop(string serviceName);
    void Start(string serviceName);
}

public interface IMaintenanceProcessInspector
{
    IReadOnlyList<int> FindByExecutablePath(IReadOnlyCollection<string> exactPaths);
    string GetExecutablePath(int processId);
}

public interface IMaintenanceStopPrerequisites
{
    Task VerifyBeforeStopAsync(CancellationToken cancellationToken);
    Task VerifyStillHeldAsync(CancellationToken cancellationToken);
    Task MarkServicesQuiescedAsync(CancellationToken cancellationToken);
}

public sealed class MaintenanceServiceQuiescer(
    IMaintenanceWindowsServiceApi services,
    IMaintenanceProcessInspector processes,
    IMaintenanceStopPrerequisites prerequisites,
    TimeSpan? operationTimeout = null,
    TimeSpan? pollInterval = null)
{
    public const string AgentServiceName = "KamenRaider Agent";
    public const string GuardServiceName = "KamenRaider Stream Guard";
    public const string SunshineServiceName = "SunshineService";
    private const string AgentServiceAccount = "NT SERVICE\\KamenRaider Agent";
    private const string LocalSystemAccount = "LocalSystem";
    private readonly TimeSpan timeout = operationTimeout ?? TimeSpan.FromSeconds(30);
    private readonly TimeSpan interval = pollInterval ?? TimeSpan.FromMilliseconds(200);

    public async Task QuiesceAsync(MaintenanceServiceLayout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(prerequisites);
        ArgumentNullException.ThrowIfNull(layout);
        if (timeout <= TimeSpan.Zero || interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(operationTimeout), "Service timeout and polling interval must be positive.");

        var normalized = NormalizeLayout(layout);
        await prerequisites.VerifyBeforeStopAsync(cancellationToken).ConfigureAwait(false);

        // Validate every service record before the first Stop control. A wrong target
        // path, identity, or dependency can otherwise create a partial stop.
        foreach (var definition in Definitions(normalized))
            _ = QueryAndValidate(definition);

        foreach (var definition in StopOrder(normalized))
        {
            await prerequisites.VerifyStillHeldAsync(cancellationToken).ConfigureAwait(false);
            await StopAndVerifyAsync(definition, cancellationToken).ConfigureAwait(false);
        }

        await prerequisites.VerifyStillHeldAsync(cancellationToken).ConfigureAwait(false);
        var remaining = processes.FindByExecutablePath(normalized.ProcessImagePaths);
        if (remaining.Count != 0)
            throw new InvalidOperationException("A KamenRaider or Sunshine process remains after service stop; the installation tree must not be mutated.");

        foreach (var definition in Definitions(normalized))
        {
            var stopped = QueryAndValidate(definition);
            if (stopped.State != MaintenanceWindowsServiceState.Stopped || stopped.ProcessId != 0)
                throw new InvalidOperationException($"Service '{definition.Name}' restarted or retained a process after quiescence.");
        }

        await prerequisites.VerifyStillHeldAsync(cancellationToken).ConfigureAwait(false);
        await prerequisites.MarkServicesQuiescedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StopAndVerifyAsync(ServiceDefinition definition, CancellationToken cancellationToken)
    {
        var initial = QueryAndValidate(definition);
        if (initial.State == MaintenanceWindowsServiceState.Stopped)
        {
            if (initial.ProcessId != 0)
                throw new InvalidOperationException($"Stopped service '{definition.Name}' still reports a process ID.");
            return;
        }
        if (initial.State != MaintenanceWindowsServiceState.Running)
            throw new InvalidOperationException($"Service '{definition.Name}' is in an ambiguous transitional state.");

        await prerequisites.VerifyStillHeldAsync(cancellationToken).ConfigureAwait(false);
        services.Stop(definition.Name);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await prerequisites.VerifyStillHeldAsync(cancellationToken).ConfigureAwait(false);
            var current = QueryAndValidate(definition, allowPending: true);
            if (current.State == MaintenanceWindowsServiceState.Stopped && current.ProcessId == 0)
                return;
            if (current.State is not (MaintenanceWindowsServiceState.Running or MaintenanceWindowsServiceState.StopPending))
                throw new InvalidOperationException($"Service '{definition.Name}' entered an ambiguous state while stopping.");

            var remaining = timeout - timer.Elapsed;
            await Task.Delay(remaining < interval ? remaining : interval, cancellationToken).ConfigureAwait(false);
        }

        var last = QueryAndValidate(definition, allowPending: true);
        if (last.State != MaintenanceWindowsServiceState.Stopped || last.ProcessId != 0)
            throw new TimeoutException($"Service '{definition.Name}' did not stop with process ID zero within {timeout}.");
    }

    private MaintenanceWindowsServiceSnapshot QueryAndValidate(ServiceDefinition definition, bool allowPending = false)
    {
        var snapshot = services.Query(definition.Name)
            ?? throw new InvalidOperationException($"Required service '{definition.Name}' is missing.");
        if (!string.Equals(snapshot.ServiceName, definition.Name, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.AccountName, definition.Account, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Service '{definition.Name}' has an unexpected identity.");
        if (snapshot.ServiceType != MaintenanceWindowsServiceTypes.Win32OwnProcess)
            throw new InvalidOperationException($"Service '{definition.Name}' is not a Win32 own-process service.");

        var (imagePath, arguments) = ParseImageCommandLine(snapshot.ImagePath);
        var argumentsMatch = definition.HasContentRootArgument
            ? ContentRootArgumentsMatch(arguments, definition.ImagePath, definition.Arguments)
            : definition.AllowedArguments is not null
                ? definition.AllowedArguments.Contains(arguments, StringComparer.Ordinal)
                : string.Equals(arguments, definition.Arguments, StringComparison.Ordinal);
        if (!PathEquals(imagePath, definition.ImagePath) || !argumentsMatch)
            throw new InvalidOperationException($"Service '{definition.Name}' executable or arguments do not match the trusted installation layout.");

        if (definition.ExpectedDependencies is not null &&
            !snapshot.Dependencies.SequenceEqual(definition.ExpectedDependencies, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Service '{definition.Name}' has an unexpected dependency list.");
        if (snapshot.Dependencies.Any(string.IsNullOrWhiteSpace) ||
            snapshot.Dependencies.Distinct(StringComparer.OrdinalIgnoreCase).Count() != snapshot.Dependencies.Count)
            throw new InvalidOperationException($"Service '{definition.Name}' has an invalid dependency list.");
        if (definition.RequiredDependency is not null &&
            snapshot.Dependencies.Count(dependency => string.Equals(dependency, definition.RequiredDependency, StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidOperationException($"Service '{definition.Name}' is missing its unique required dependency.");

        if (snapshot.State == MaintenanceWindowsServiceState.Stopped)
        {
            if (snapshot.ProcessId != 0)
                throw new InvalidOperationException($"Stopped service '{definition.Name}' reports a nonzero process ID.");
        }
        else if (snapshot.State == MaintenanceWindowsServiceState.Running)
        {
            VerifyServiceProcess(snapshot, definition);
        }
        else if (!allowPending || snapshot.State is not (MaintenanceWindowsServiceState.StartPending or MaintenanceWindowsServiceState.StopPending))
        {
            throw new InvalidOperationException($"Service '{definition.Name}' is in an ambiguous state.");
        }
        else if (snapshot.ProcessId != 0)
        {
            VerifyServiceProcess(snapshot, definition);
        }
        return snapshot;
    }

    private void VerifyServiceProcess(MaintenanceWindowsServiceSnapshot snapshot, ServiceDefinition definition)
    {
        if (snapshot.ProcessId is 0 or > int.MaxValue)
            throw new InvalidOperationException($"Service '{definition.Name}' reports an invalid process ID.");
        var actual = processes.GetExecutablePath((int)snapshot.ProcessId);
        if (!PathEquals(actual, definition.ImagePath))
            throw new InvalidOperationException($"Service '{definition.Name}' process image does not match its configured executable.");
    }

    private static NormalizedLayout NormalizeLayout(MaintenanceServiceLayout layout)
    {
        var agent = NormalizeFilePath(layout.AgentExecutablePath);
        var sunshineService = NormalizeFilePath(layout.SunshineServiceExecutablePath);
        var sunshine = NormalizeFilePath(layout.SunshineExecutablePath);
        if (!string.Equals(Path.GetFileName(agent), "Agent.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(sunshineService), "sunshinesvc.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(sunshine), "sunshine.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(sunshineService), Path.Combine(Path.GetDirectoryName(sunshine) ?? string.Empty, "tools"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The trusted service layout does not match the supported Agent and Sunshine installation shape.");
        return new(agent, sunshineService, sunshine, [agent, sunshineService, sunshine]);
    }

    private static string NormalizeFilePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(full))
            throw new InvalidDataException("A service image path is not absolute.");
        return full;
    }

    private static IEnumerable<ServiceDefinition> Definitions(NormalizedLayout layout)
    {
        yield return new(AgentServiceName, layout.AgentExecutablePath, AgentServiceAccount, string.Empty, true, null, [GuardServiceName], null);
        yield return new(GuardServiceName, layout.AgentExecutablePath, LocalSystemAccount, "--stream-guard", true, null, [], null);
        // Sunshine keeps vendor dependencies; the managed stream gate adds exactly one Guard dependency.
        yield return new(SunshineServiceName, layout.SunshineServiceExecutablePath, LocalSystemAccount, string.Empty, false,
            [string.Empty, "--service"], null, GuardServiceName);
    }

    private static IEnumerable<ServiceDefinition> StopOrder(NormalizedLayout layout)
    {
        var definitions = Definitions(layout).ToDictionary(definition => definition.Name, StringComparer.OrdinalIgnoreCase);
        yield return definitions[AgentServiceName];
        yield return definitions[SunshineServiceName];
        yield return definitions[GuardServiceName];
    }

    internal static (string Executable, string Arguments) ParseImageCommandLine(string commandLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        var value = commandLine.Trim();
        if (value.StartsWith('"'))
        {
            var close = value.IndexOf('"', 1);
            if (close < 0)
                throw new InvalidDataException("The quoted service image path has no closing quote.");
            var trailing = value[(close + 1)..];
            if (trailing.Length > 0 && !char.IsWhiteSpace(trailing[0]))
                throw new InvalidDataException("The quoted service image path has invalid trailing text.");
            return (value[1..close], trailing.Trim());
        }

        var exeEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeEnd < 0)
            throw new InvalidDataException("The service image path does not identify an executable.");
        exeEnd += 4;
        if (exeEnd < value.Length && !char.IsWhiteSpace(value[exeEnd]))
            throw new InvalidDataException("The service image path has invalid trailing text.");
        var executable = value[..exeEnd];
        if (executable.Any(char.IsWhiteSpace))
            throw new InvalidDataException("An unquoted service executable path containing spaces is ambiguous.");
        return (executable, value[exeEnd..].Trim());
    }

    internal static bool PathEquals(string first, string second)
    {
        try { return string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(first)), second, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    internal static bool ContentRootArgumentsMatch(string arguments, string executablePath, string trailingArguments)
    {
        const string prefix = "--contentRoot ";
        if (!arguments.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var value = arguments[prefix.Length..];
        if (value.Length < 3 || value[0] != '"')
            return false;
        var closingQuote = value.IndexOf('"', 1);
        if (closingQuote < 0)
            return false;
        var contentRoot = value[1..closingQuote];
        var trailing = value[(closingQuote + 1)..].Trim();
        var expectedRoot = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        return !string.IsNullOrWhiteSpace(expectedRoot) && PathEquals(contentRoot, expectedRoot) &&
            string.Equals(trailing, trailingArguments, StringComparison.Ordinal);
    }

    private sealed record ServiceDefinition(
        string Name,
        string ImagePath,
        string Account,
        string Arguments,
        bool HasContentRootArgument,
        IReadOnlyList<string>? AllowedArguments,
        IReadOnlyList<string>? ExpectedDependencies,
        string? RequiredDependency);
    private sealed record NormalizedLayout(string AgentExecutablePath, string SunshineServiceExecutablePath, string SunshineExecutablePath, IReadOnlyList<string> ProcessImagePaths);
}

public sealed class WindowsMaintenanceProcessInspector : IMaintenanceProcessInspector
{
    public string GetExecutablePath(int processId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Process image verification requires Windows.");
        try
        {
            using var process = Process.GetProcessById(processId);
            return Path.GetFullPath(process.MainModule?.FileName
                ?? throw new InvalidOperationException("A service process has no readable executable path."));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            throw new InvalidOperationException("The service process image could not be verified.", ex);
        }
    }

    public IReadOnlyList<int> FindByExecutablePath(IReadOnlyCollection<string> exactPaths)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Process quiescence verification requires Windows.");
        var expected = exactPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = expected.Select(path => Path.GetFileNameWithoutExtension(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var remaining = new List<int>();
        foreach (var name in names)
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                string actual;
                try
                {
                    actual = process.MainModule?.FileName
                        ?? throw new InvalidOperationException("A candidate process has no readable executable path.");
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    throw new InvalidOperationException("A same-name process could not be inspected; the installation tree must remain held.", ex);
                }
                if (expected.Contains(Path.GetFullPath(actual)))
                    remaining.Add(process.Id);
            }
        }
        return remaining;
    }
}

public sealed class WindowsMaintenanceServiceApi : IMaintenanceWindowsServiceApi
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStartAccess = 0x0010;
    private const uint ServiceStopAccess = 0x0020;
    private const uint ErrorServiceDoesNotExist = 1060;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorServiceNotActive = 1062;
    private const uint ErrorServiceAlreadyRunning = 1056;

    public MaintenanceWindowsServiceSnapshot? Query(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        EnsureWindows();
        using var manager = Native.OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open the Service Control Manager.");
        using var service = OpenService(manager, serviceName, ServiceQueryConfig | ServiceQueryStatus, missingIsNull: true);
        if (service is null) return null;
        var config = QueryConfig(service);
        var status = QueryStatus(service);
        if (config.ServiceType != MaintenanceWindowsServiceTypes.Win32OwnProcess ||
            status.ServiceType != MaintenanceWindowsServiceTypes.Win32OwnProcess)
            throw new InvalidOperationException($"Service '{serviceName}' is not a Win32 own-process service.");
        return new(serviceName, config.BinaryPath, config.StartName, config.Dependencies,
            ConvertState(status.CurrentState), status.ProcessId, config.ServiceType);
    }

    public void Stop(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        EnsureWindows();
        using var manager = Native.OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open the Service Control Manager.");
        using var service = OpenService(manager, serviceName, ServiceQueryStatus | ServiceStopAccess, missingIsNull: false)!;
        if (!Native.ControlService(service, Native.ServiceControlStop, out _))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorServiceNotActive)
                throw new Win32Exception(error, $"Could not stop service '{serviceName}'.");
        }
    }

    public void Start(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        EnsureWindows();
        using var manager = Native.OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open the Service Control Manager.");
        using var service = OpenService(manager, serviceName, ServiceStartAccess, missingIsNull: false)!;
        if (!Native.StartService(service, 0, IntPtr.Zero))
        {
            var error = Marshal.GetLastPInvokeError();
            if ((uint)error != ErrorServiceAlreadyRunning)
                throw new Win32Exception(error, $"Could not start service '{serviceName}'.");
        }
    }

    private static SafeServiceHandle? OpenService(SafeServiceHandle manager, string name, uint access, bool missingIsNull)
    {
        var service = Native.OpenService(manager, name, access);
        if (!service.IsInvalid) return service;
        var error = Marshal.GetLastPInvokeError();
        service.Dispose();
        if (missingIsNull && (uint)error == ErrorServiceDoesNotExist) return null;
        throw new Win32Exception(error, $"Could not open service '{name}'.");
    }

    private static ServiceConfiguration QueryConfig(SafeServiceHandle service)
    {
        var size = 0;
        _ = Native.QueryServiceConfig(service, IntPtr.Zero, 0, ref size);
        var error = Marshal.GetLastPInvokeError();
        if ((uint)error != ErrorInsufficientBuffer || size <= 0)
            throw new Win32Exception(error, "Could not read the Windows service configuration size.");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!Native.QueryServiceConfig(service, buffer, size, ref size))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the Windows service configuration.");
            var config = Marshal.PtrToStructure<Native.ServiceConfig>(buffer);
            return new(
                Marshal.PtrToStringUni(config.BinaryPathName) ?? throw new InvalidDataException("The service has no image path."),
                Marshal.PtrToStringUni(config.ServiceStartName) ?? string.Empty,
                config.ServiceType,
                ReadMultiString(config.Dependencies));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IReadOnlyList<string> ReadMultiString(IntPtr value)
    {
        if (value == IntPtr.Zero) return [];
        var items = new List<string>();
        var current = value;
        while (true)
        {
            var item = Marshal.PtrToStringUni(current);
            if (string.IsNullOrEmpty(item)) break;
            items.Add(item);
            current = IntPtr.Add(current, checked((item.Length + 1) * sizeof(char)));
            if (items.Count > 256)
                throw new InvalidDataException("The service configuration has too many dependencies.");
        }
        return items;
    }

    private static Native.ServiceStatusProcess QueryStatus(SafeServiceHandle service)
    {
        var status = new Native.ServiceStatusProcess();
        if (!Native.QueryServiceStatusEx(service, Native.ServiceStatusProcessInfo, ref status,
            (uint)Marshal.SizeOf<Native.ServiceStatusProcess>(), out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the Windows service state.");
        return status;
    }

    private static MaintenanceWindowsServiceState ConvertState(uint state) => state switch
    {
        1 => MaintenanceWindowsServiceState.Stopped,
        2 => MaintenanceWindowsServiceState.StartPending,
        3 => MaintenanceWindowsServiceState.StopPending,
        4 => MaintenanceWindowsServiceState.Running,
        5 => MaintenanceWindowsServiceState.ContinuePending,
        6 => MaintenanceWindowsServiceState.PausePending,
        7 => MaintenanceWindowsServiceState.Paused,
        _ => throw new InvalidDataException($"SCM returned unknown service state {state}.")
    };

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Service Control Manager access is available only on Windows.");
    }

    private sealed record ServiceConfiguration(string BinaryPath, string StartName, uint ServiceType, IReadOnlyList<string> Dependencies);

    private sealed class SafeServiceHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => Native.CloseServiceHandle(handle);
    }

    private static class Native
    {
        internal const int ServiceStatusProcessInfo = 0;
        internal const uint ServiceControlStop = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ServiceConfig
        {
            public uint ServiceType;
            public uint StartType;
            public uint ErrorControl;
            public IntPtr BinaryPathName;
            public IntPtr LoadOrderGroup;
            public uint TagId;
            public IntPtr Dependencies;
            public IntPtr ServiceStartName;
            public IntPtr DisplayName;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ServiceStatusProcess
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
            public uint ProcessId;
            public uint ServiceFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ServiceStatus
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
        }

        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeServiceHandle OpenService(SafeServiceHandle manager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", EntryPoint = "CloseServiceHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceConfig(SafeServiceHandle service, IntPtr config, int bufferSize, ref int bytesNeeded);

        [DllImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceStatusEx(SafeServiceHandle service, int infoLevel, ref ServiceStatusProcess status, uint bufferSize, out uint bytesNeeded);

        [DllImport("advapi32.dll", EntryPoint = "ControlService", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ControlService(SafeServiceHandle service, uint control, out ServiceStatus status);

        [DllImport("advapi32.dll", EntryPoint = "StartServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool StartService(SafeServiceHandle service, uint argumentCount, IntPtr arguments);
    }
}
