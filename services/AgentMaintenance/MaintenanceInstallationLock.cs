using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KamenRaider.AgentMaintenance;

/// <summary>
/// Protected installation-wide lock paths. The lock lives beside the maintenance journal and
/// fence under CommonApplicationData so both the PowerShell bootstrap and the native helper
/// resolve the same path regardless of 32/64-bit shell bitness.
/// </summary>
public static class MaintenanceInstallationLockPath
{
    public const string DirectoryName = "KamenRaider.AgentMaintenance";
    public const string LockFileName = "maintenance-installation.lock";

    public static string DefaultDirectory
    {
        get
        {
            var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(commonData) || !Path.IsPathFullyQualified(commonData))
                throw new InvalidOperationException("The native CommonApplicationData path is unavailable.");
            return Path.Combine(Path.GetFullPath(commonData), DirectoryName);
        }
    }
}

/// <summary>
/// Production exclusion provider for the maintenance helper. Every new updater invocation
/// acquires one installation-wide exclusive lock before reading archives or mutating services,
/// and holds the handle through apply, report and reconcile. A busy lock means another new
/// updater is still active and is reported as unqualified rather than waited on or killed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMaintenanceInstallationLockProvider : IMaintenanceApplyProcessExclusionProvider
{
    private readonly string directory;

    public WindowsMaintenanceInstallationLockProvider()
        : this(MaintenanceInstallationLockPath.DefaultDirectory, enforceProtectedSecurity: true)
    {
    }

    internal WindowsMaintenanceInstallationLockProvider(string directory, bool enforceProtectedSecurity = true)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The maintenance installation lock requires Windows.");
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
        if (!Path.IsPathFullyQualified(this.directory))
            throw new ArgumentException("The maintenance lock directory must be an absolute path.", nameof(directory));
        this.EnforceProtectedSecurity = enforceProtectedSecurity;
    }

    internal bool EnforceProtectedSecurity { get; }

    public IMaintenanceApplyProcessExclusionLease? Acquire() =>
        WindowsMaintenanceInstallationLock.TryAcquire(directory, EnforceProtectedSecurity);
}

/// <summary>
/// Held installation-wide exclusive lock. The protected file is opened once with
/// FileShare.None; the OS refuses a second updater until the handle is disposed. Every
/// destructive transition rechecks that the same handle, file identity and protected
/// directory are still held so a replaced/deleted lock cannot silently reopen the path.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsMaintenanceInstallationLock : IMaintenanceApplyProcessExclusionLease
{
    internal const int LockProtocol = 1;
    private const int MaximumHolderBytes = 2048;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 4
    };
    private static readonly Regex OperationIdPattern =
        new(@"\A[A-Za-z0-9._-]{1,128}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly FileStream stream;
    private readonly string directory;
    private readonly string path;
    private readonly bool enforceProtectedSecurity;
    private readonly byte[] holder;
    private readonly uint volumeSerialNumber;
    private readonly ulong fileIndex;
    private string? operationId;
    private bool disposed;

    private WindowsMaintenanceInstallationLock(
        FileStream stream,
        string directory,
        string path,
        byte[] holder,
        uint volumeSerialNumber,
        ulong fileIndex,
        bool enforceProtectedSecurity)
    {
        this.stream = stream;
        this.directory = directory;
        this.path = path;
        this.holder = holder;
        this.volumeSerialNumber = volumeSerialNumber;
        this.fileIndex = fileIndex;
        this.enforceProtectedSecurity = enforceProtectedSecurity;
    }

    internal static WindowsMaintenanceInstallationLock? TryAcquire(string directoryPath, bool enforceProtectedSecurity)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The maintenance installation lock requires Windows.");
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var directory = Path.GetFullPath(directoryPath);
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("The maintenance lock directory must be an absolute path.", nameof(directoryPath));

        MaintenanceInstallationLockSecurity.EnsureAncestorsHaveNoReparsePoints(directory);
        MaintenanceInstallationLockSecurity.EnsureDirectory(directory, enforceProtectedSecurity);
        MaintenanceInstallationLockSecurity.EnsureNotReparsePoint(directory, "maintenance lock directory");
        if (enforceProtectedSecurity)
            MaintenanceInstallationLockSecurity.ValidateProtectedDirectory(directory);

        var path = Path.Combine(directory, MaintenanceInstallationLockPath.LockFileName);
        MaintenanceInstallationLockSecurity.CreateLockFileIfMissing(path, enforceProtectedSecurity);
        try
        {
            MaintenanceInstallationLockSecurity.EnsureNotReparsePoint(path, "maintenance lock file");
        }
        catch (IOException exception) when (IsSharingConflict(exception))
        {
            // Another updater holds the file with no delete/read metadata sharing.
            return null;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
        }
        catch (IOException exception) when (IsSharingConflict(exception))
        {
            // Another new updater still holds the shared installation lock. Never wait,
            // kill it, or infer its death from elapsed time.
            return null;
        }

        try
        {
            var identity = MaintenanceInstallationLockSecurity.QueryIdentity(stream.SafeFileHandle);
            MaintenanceInstallationLockSecurity.ValidateHeldFileIdentity(identity);

            var holder = MaintenanceInstallationLockSecurity.SerializeHolder();
            stream.SetLength(0);
            stream.Write(holder);
            stream.Flush(flushToDisk: true);

            return new(
                stream,
                directory,
                path,
                holder,
                identity.VolumeSerialNumber,
                identity.FileIndex,
                enforceProtectedSecurity);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void VerifyContinuouslyHeld(string operationId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (!OperationIdPattern.IsMatch(operationId))
            throw new ArgumentException("The maintenance operation ID is invalid.", nameof(operationId));
        if (this.operationId is null)
            this.operationId = operationId;
        else if (!string.Equals(this.operationId, operationId, StringComparison.Ordinal))
            throw new InvalidOperationException("The installation lock belongs to another maintenance operation.");
        VerifyHeld();
    }

    public void VerifyHeld()
    {
        ThrowIfDisposed();
        var handle = stream.SafeFileHandle;
        if (handle.IsClosed || handle.IsInvalid)
            throw new InvalidOperationException("The installation lock handle is no longer held.");

        var identity = MaintenanceInstallationLockSecurity.QueryIdentity(handle);
        MaintenanceInstallationLockSecurity.ValidateHeldFileIdentity(identity);
        if (identity.VolumeSerialNumber != volumeSerialNumber ||
            identity.FileIndex != fileIndex)
            throw new InvalidOperationException("The installation lock file identity changed while held.");

        MaintenanceInstallationLockSecurity.EnsureNotReparsePoint(directory, "maintenance lock directory");
        if (enforceProtectedSecurity)
            MaintenanceInstallationLockSecurity.ValidateProtectedDirectory(directory);

        var actual = new byte[holder.Length];
        stream.Position = 0;
        var read = 0;
        while (read < actual.Length)
        {
            var chunk = stream.Read(actual, read, actual.Length - read);
            if (chunk <= 0)
                throw new InvalidOperationException("The installation lock holder record was truncated while held.");
            read += chunk;
        }
        if (!actual.AsSpan().SequenceEqual(holder))
            throw new InvalidOperationException("The installation lock holder record changed while held.");
        if (stream.Length > holder.Length)
            throw new InvalidOperationException("The installation lock holder record changed while held.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stream.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static bool IsSharingConflict(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code is ErrorSharingViolation or ErrorLockViolation;
    }
}

[SupportedOSPlatform("windows")]
internal static class MaintenanceInstallationLockSecurity
{
    private const int MaximumAncestorDepth = 64;

    public static void EnsureAncestorsHaveNoReparsePoints(string anywhere)
    {
        var current = Directory.GetParent(anywhere);
        var depth = 0;
        while (current is not null && depth++ < MaximumAncestorDepth)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A maintenance lock ancestor is a reparse point.");
            current = current.Parent;
        }
    }

    public static void EnsureDirectory(string directory, bool enforceProtectedSecurity)
    {
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            if (enforceProtectedSecurity)
                ApplyProtectedDirectorySecurity(directory);
        }
    }

    public static void CreateLockFileIfMissing(string path, bool enforceProtectedSecurity)
    {
        if (File.Exists(path))
            return;
        try
        {
            using (var creation = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 128,
                FileOptions.None))
            {
            }
            if (enforceProtectedSecurity)
                ApplyProtectedFileSecurity(path);
        }
        catch (IOException)
        {
            // Another updater created the shared lock file first; the exclusive open below
            // decides which one is allowed to hold it.
        }
    }

    public static void EnsureNotReparsePoint(string path, string description)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException($"The {description} is unavailable.", path);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"The {description} must not be a reparse point.");
    }

    public static void EnsureSingleLink(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The maintenance lock file is unavailable.", path);
        using var probe = OpenReadHandle(path);
        var info = QueryIdentity(probe);
        if (info.NumberOfLinks != 1)
            throw new InvalidDataException("The maintenance lock file must be a single-link regular file.");
    }

    public static void ValidateProtectedDirectory(string directory) =>
        // The lock shares the protected maintenance directory with the fence/journal stores and
        // must accept exactly the same SYSTEM + Administrators + Agent-service-read shape.
        WindowsMaintenanceFenceStore.EnsureDirectorySecurity(directory);



    public static byte[] SerializeHolder()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var envelope = new HolderEnvelope(
            WindowsMaintenanceInstallationLock.LockProtocol,
            Environment.ProcessId,
            process.StartTime.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            Environment.MachineName,
            DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, HolderJsonOptions);
        if (bytes.Length is <= 0 or > 2048)
            throw new InvalidDataException("The maintenance lock holder record exceeds its size limit.");
        return bytes;
    }

    private sealed record HolderEnvelope(int Protocol, int ProcessId, string ProcessStartTimeUtc, string MachineName, string AcquiredAtUtc);

    private static readonly JsonSerializerOptions HolderJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 4
    };

    internal readonly record struct FileIdentity(uint VolumeSerialNumber, ulong FileIndex, uint NumberOfLinks, uint FileAttributes);

    public static void ValidateHeldFileIdentity(FileIdentity identity)
    {
        if (identity.NumberOfLinks != 1)
            throw new InvalidDataException("The maintenance lock file must be a single-link regular file.");
        if ((identity.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The maintenance lock file must not be a reparse point.");
    }

    public static FileIdentity QueryIdentity(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not query the maintenance lock identity.");
        var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return new(info.VolumeSerialNumber, index, info.NumberOfLinks, info.FileAttributes);
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenReadHandle(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private static void ApplyProtectedDirectorySecurity(string directory)
    {
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var agentServiceSid = WindowsMaintenanceFenceStore.ResolveAgentServiceSid();
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administratorsSid);
        security.AddAccessRule(new FileSystemAccessRule(systemSid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(administratorsSid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(agentServiceSid, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static void ApplyProtectedFileSecurity(string path)
    {
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var agentServiceSid = WindowsMaintenanceFenceStore.ResolveAgentServiceSid();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administratorsSid);
        security.AddAccessRule(new FileSystemAccessRule(systemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(administratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(agentServiceSid, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
