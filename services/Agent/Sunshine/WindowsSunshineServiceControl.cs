using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KamenRaider.Agent.Sunshine;

public enum WindowsServiceState
{
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7
}

public sealed record WindowsServiceSnapshot(
    string ServiceName,
    string ImagePath,
    string AccountName,
    WindowsServiceState State);

public interface IWindowsServiceApi
{
    WindowsServiceSnapshot? Query(string serviceName);
    void Start(string serviceName);
    void Stop(string serviceName);
}

public sealed record SunshineServiceInstallation(
    string ServiceName,
    string ServiceImagePath,
    string SunshineExecutablePath);

public static class WindowsServiceImagePathParser
{
    public static string ParseExecutablePath(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var value = imagePath.Trim();
        if (value.StartsWith('"'))
        {
            var endQuote = value.IndexOf('"', 1);
            if (endQuote < 0)
                throw new InvalidDataException("The quoted Windows service image path is missing its closing quote.");

            var trailing = value[(endQuote + 1)..];
            if (trailing.Length > 0 && !char.IsWhiteSpace(trailing[0]))
                throw new InvalidDataException("The quoted Windows service image path has invalid text after the executable.");

            var executable = value[1..endQuote];
            if (string.IsNullOrWhiteSpace(executable))
                throw new InvalidDataException("The Windows service image path does not contain an executable.");
            return executable;
        }

        var exeEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeEnd < 0)
            throw new InvalidDataException("The unquoted Windows service image path does not contain an .exe executable.");
        exeEnd += 4;
        if (exeEnd < value.Length && !char.IsWhiteSpace(value[exeEnd]))
            throw new InvalidDataException("The unquoted Windows service image path has invalid text after the executable.");

        var unquotedExecutable = value[..exeEnd];
        if (unquotedExecutable.Any(char.IsWhiteSpace))
            throw new InvalidDataException("An unquoted Windows service image path containing spaces is ambiguous.");
        return unquotedExecutable;
    }
}

public sealed class WindowsSunshineInstallationDiscovery(
    IWindowsServiceApi services,
    Func<string, bool>? fileExists = null)
{
    public const string SunshineServiceName = "SunshineService";
    private readonly Func<string, bool> exists = fileExists ?? File.Exists;

    public SunshineServiceInstallation Discover()
    {
        ArgumentNullException.ThrowIfNull(services);
        var service = services.Query(SunshineServiceName)
            ?? throw new InvalidOperationException($"Windows service '{SunshineServiceName}' is not installed.");
        if (!string.Equals(service.ServiceName, SunshineServiceName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Sunshine service query returned an unexpected service name.");

        string serviceImage;
        try
        {
            serviceImage = NormalizeImagePath(service.ImagePath);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException)
        {
            throw new InvalidOperationException("The Sunshine service has an invalid or ambiguous executable path.", ex);
        }

        if (!string.Equals(Path.GetFileName(serviceImage), "sunshinesvc.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Sunshine service is not configured to run sunshinesvc.exe.");

        var toolsDirectory = Path.GetDirectoryName(serviceImage)
            ?? throw new InvalidOperationException("The Sunshine service executable has no installation directory.");
        if (!string.Equals(Path.GetFileName(toolsDirectory), "tools", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Sunshine service executable is not in Sunshine's tools directory.");

        var sunshineDirectory = Path.GetDirectoryName(toolsDirectory)
            ?? throw new InvalidOperationException("The Sunshine service has no parent installation directory.");
        var sunshineImage = Path.GetFullPath(Path.Combine(sunshineDirectory, "sunshine.exe"));
        if (!exists(serviceImage) || !exists(sunshineImage))
            throw new InvalidOperationException("The Sunshine service executable or sibling sunshine.exe is missing.");

        return new SunshineServiceInstallation(SunshineServiceName, serviceImage, sunshineImage);
    }

    internal static string NormalizeImagePath(string imagePath)
    {
        var parsed = WindowsServiceImagePathParser.ParseExecutablePath(imagePath);
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(parsed));
    }
}

public sealed class WindowsSunshineServiceControl : ISunshineServiceControl
{
    private readonly IWindowsServiceApi services;
    private readonly SunshineServiceInstallation installation;
    private readonly TimeSpan operationTimeout;
    private readonly TimeSpan pollInterval;

    public WindowsSunshineServiceControl(
        IWindowsServiceApi services,
        SunshineServiceInstallation installation,
        TimeSpan? operationTimeout = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(installation);
        if (!string.Equals(installation.ServiceName, WindowsSunshineInstallationDiscovery.SunshineServiceName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only the SunshineService service can be controlled.", nameof(installation));

        var expectedServiceImage = Path.GetFullPath(installation.ServiceImagePath);
        var expectedSunshineImage = Path.GetFullPath(installation.SunshineExecutablePath);
        var toolsDirectory = Path.GetDirectoryName(expectedServiceImage);
        var sunshineDirectory = toolsDirectory is null ? null : Path.GetDirectoryName(toolsDirectory);
        if (!string.Equals(Path.GetFileName(expectedServiceImage), "sunshinesvc.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(toolsDirectory), "tools", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(expectedSunshineImage), "sunshine.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expectedSunshineImage, Path.Combine(sunshineDirectory ?? string.Empty, "sunshine.exe"), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The Sunshine installation paths do not identify the service wrapper and its sibling sunshine.exe.", nameof(installation));

        this.services = services;
        this.installation = installation with
        {
            ServiceImagePath = expectedServiceImage,
            SunshineExecutablePath = expectedSunshineImage
        };
        this.operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30);
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
        if (this.operationTimeout <= TimeSpan.Zero || this.pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(operationTimeout), "Service operation timeout and polling interval must be positive.");
    }

    public Task StartAndWaitAsync(CancellationToken cancellationToken) => ChangeStateAsync(start: true, cancellationToken);
    public Task StopAndWaitAsync(CancellationToken cancellationToken) => ChangeStateAsync(start: false, cancellationToken);

    public Task<bool> IsRunningAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(QueryValidated().State == WindowsServiceState.Running);
    }

    private async Task ChangeStateAsync(bool start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = start ? WindowsServiceState.Running : WindowsServiceState.Stopped;
        var current = QueryValidated();
        if (current.State == target) return;

        if (start)
            services.Start(installation.ServiceName);
        else
            services.Stop(installation.ServiceName);

        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < operationTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (QueryValidated().State == target) return;
            var remaining = operationTimeout - timer.Elapsed;
            await Task.Delay(remaining < pollInterval ? remaining : pollInterval, cancellationToken).ConfigureAwait(false);
        }

        if (QueryValidated().State == target) return;
        throw new TimeoutException($"Sunshine service '{installation.ServiceName}' did not reach {target} within {operationTimeout}.");
    }

    private WindowsServiceSnapshot QueryValidated()
    {
        var service = services.Query(installation.ServiceName)
            ?? throw new InvalidOperationException($"Windows service '{installation.ServiceName}' is no longer installed.");
        if (!string.Equals(service.ServiceName, installation.ServiceName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The SCM returned a different service than the configured Sunshine service.");

        string actualImage;
        try
        {
            actualImage = WindowsSunshineInstallationDiscovery.NormalizeImagePath(service.ImagePath);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException)
        {
            throw new InvalidOperationException("The Sunshine service executable path became invalid or ambiguous.", ex);
        }

        if (!string.Equals(actualImage, installation.ServiceImagePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Sunshine service executable changed after installation discovery; refusing to control it.");
        return service;
    }
}

public sealed class WindowsServiceApi : IWindowsServiceApi
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStartAccess = 0x0010;
    private const uint ServiceStopAccess = 0x0020;
    private const uint ErrorServiceDoesNotExist = 1060;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorServiceAlreadyRunning = 1056;
    private const uint ErrorServiceNotActive = 1062;

    public WindowsServiceSnapshot? Query(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        using var manager = OpenManager();
        using var service = OpenService(manager, serviceName, ServiceQueryConfig | ServiceQueryStatus, missingIsNull: true);
        if (service is null) return null;

        var imagePath = QueryImagePath(service);
        var accountName = QueryStartName(service);
        var status = QueryStatus(service);
        return new WindowsServiceSnapshot(serviceName, imagePath, accountName, ConvertState(status.CurrentState));
    }

    public void Start(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        using var manager = OpenManager();
        using var service = OpenService(manager, serviceName, ServiceQueryConfig | ServiceQueryStatus | ServiceStartAccess, missingIsNull: false)!;
        if (!Native.StartService(service, 0, IntPtr.Zero))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorServiceAlreadyRunning) throw new Win32Exception(error, $"Could not start service '{serviceName}'.");
        }
    }

    public void Stop(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        using var manager = OpenManager();
        using var service = OpenService(manager, serviceName, ServiceQueryConfig | ServiceQueryStatus | ServiceStopAccess, missingIsNull: false)!;
        if (!Native.ControlService(service, Native.ServiceControlStop, out _))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorServiceNotActive) throw new Win32Exception(error, $"Could not stop service '{serviceName}'.");
        }
    }

    private static SafeServiceHandle OpenManager()
    {
        EnsureWindows();
        var manager = Native.OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open the Service Control Manager.");
        return manager;
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

    private static string QueryImagePath(SafeServiceHandle service)
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
            return Marshal.PtrToStringUni(config.BinaryPathName)
                ?? throw new InvalidDataException("The Windows service has no executable path.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string QueryStartName(SafeServiceHandle service)
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
            return Marshal.PtrToStringUni(config.ServiceStartName) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Native.ServiceStatusProcess QueryStatus(SafeServiceHandle service)
    {
        var status = new Native.ServiceStatusProcess();
        if (!Native.QueryServiceStatusEx(service, Native.ServiceStatusProcessInfo, ref status, (uint)Marshal.SizeOf<Native.ServiceStatusProcess>(), out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the Windows service state.");
        return status;
    }

    private static WindowsServiceState ConvertState(uint state) => state switch
    {
        1 => WindowsServiceState.Stopped,
        2 => WindowsServiceState.StartPending,
        3 => WindowsServiceState.StopPending,
        4 => WindowsServiceState.Running,
        5 => WindowsServiceState.ContinuePending,
        6 => WindowsServiceState.PausePending,
        7 => WindowsServiceState.Paused,
        _ => throw new InvalidDataException($"SCM returned unknown service state {state}.")
    };

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Windows Service Control Manager is available only on Windows.");
    }

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

        [DllImport("advapi32.dll", EntryPoint = "StartServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool StartService(SafeServiceHandle service, uint argumentCount, IntPtr arguments);

        [DllImport("advapi32.dll", EntryPoint = "ControlService", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ControlService(SafeServiceHandle service, uint control, out ServiceStatus status);

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
    }
}
