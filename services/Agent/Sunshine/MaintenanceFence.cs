using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace KamenRaider.Agent.Sunshine;

public sealed record MaintenanceFenceSnapshot(
    bool IsKnown,
    bool IsHeld,
    string? OperationId,
    string? ChainId,
    string? HeadOperationId,
    long? Generation,
    string? Phase)
{
    public static MaintenanceFenceSnapshot UnknownHeld { get; } =
        new(false, true, null, null, null, null, null);
}

public interface IMaintenanceFenceReader
{
    MaintenanceFenceSnapshot Read();
}

public sealed class WindowsMaintenanceFenceReader : IMaintenanceFenceReader
{
    private const int SchemaVersion = 1;
    private const int MaximumDocumentBytes = 8192;
    private static readonly HashSet<string> KnownPhases = new(StringComparer.Ordinal)
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
        "rollbackIntent",
        "targetMovedToFailed",
        "sourceRestored",
        "rollbackServicesStarted",
        "servicesStarted",
        "outcomeReported",
        "controllerConfirmed",
        "freshInstall",
        "legacyImported"
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 4
    };
    private readonly string path;
    private readonly bool requireWindows;
    private readonly bool requirePrivateSecurity;

    public WindowsMaintenanceFenceReader()
        : this(GetDefaultPath(), requireWindows: true, requirePrivateSecurity: true)
    {
    }

    internal WindowsMaintenanceFenceReader(string path)
        : this(path, requireWindows: false, requirePrivateSecurity: false)
    {
    }

    internal WindowsMaintenanceFenceReader(string path, bool requirePrivateSecurity)
        : this(path, requireWindows: true, requirePrivateSecurity)
    {
    }

    private WindowsMaintenanceFenceReader(string path, bool requireWindows, bool requirePrivateSecurity)
    {
        this.path = Path.GetFullPath(path);
        this.requireWindows = requireWindows;
        this.requirePrivateSecurity = requirePrivateSecurity;
    }

    public static string GetDefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "KamenRaider.AgentMaintenance",
        "maintenance-fence.json");

    public MaintenanceFenceSnapshot Read()
    {
        try
        {
            if (requireWindows && !OperatingSystem.IsWindows())
                return MaintenanceFenceSnapshot.UnknownHeld;

            foreach (var ancestor in Ancestors(path))
            {
                if (!File.Exists(ancestor) && !Directory.Exists(ancestor))
                    continue;

                if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    return MaintenanceFenceSnapshot.UnknownHeld;
            }

            if (!File.Exists(path))
                return MaintenanceFenceSnapshot.UnknownHeld;

            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return MaintenanceFenceSnapshot.UnknownHeld;

            if (requirePrivateSecurity)
            {
                if (!OperatingSystem.IsWindows() || !HasPrivateFenceAcl(path))
                    return MaintenanceFenceSnapshot.UnknownHeld;
            }

            var bytes = ReadSingleLinkFile(path);
            if (bytes.Length is <= 0 or > MaximumDocumentBytes)
                return MaintenanceFenceSnapshot.UnknownHeld;

            var document = JsonSerializer.Deserialize<FenceDocument>(bytes, JsonOptions);
            if (document is null || document.SchemaVersion != SchemaVersion ||
                document.State is not ("held" or "clear") ||
                !IsSafeIdentifier(document.OperationId) ||
                !IsSafeIdentifier(document.ChainId) ||
                !IsSafeIdentifier(document.HeadOperationId) ||
                document.Generation < 0 ||
                document.Phase is null || !KnownPhases.Contains(document.Phase))
                return MaintenanceFenceSnapshot.UnknownHeld;

            if (document.State == "clear" && document.Phase is not ("controllerConfirmed" or "freshInstall"))
                return MaintenanceFenceSnapshot.UnknownHeld;

            return new(
                IsKnown: true,
                IsHeld: document.State == "held",
                OperationId: document.OperationId,
                ChainId: document.ChainId,
                HeadOperationId: document.HeadOperationId,
                Generation: document.Generation,
                Phase: document.Phase);
        }
        catch
        {
            return MaintenanceFenceSnapshot.UnknownHeld;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static bool IsPrivateSecurity(FileSystemSecurity security, SecurityIdentifier agentServiceSid, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(security);
        ArgumentNullException.ThrowIfNull(agentServiceSid);
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (!security.AreAccessRulesProtected ||
            security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            (owner != systemSid && owner != administratorsSid))
            return false;
        var inheritance = isDirectory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier))
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

            var expectedRights = sid == systemSid || sid == administratorsSid
                ? FileSystemRights.FullControl
                : sid == agentServiceSid
                    ? FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize
                    : (FileSystemRights)(-1);
            if (expectedRights == (FileSystemRights)(-1) || rule.FileSystemRights != expectedRights)
                return false;
        }

        return identities.SetEquals([systemSid, administratorsSid, agentServiceSid]);
    }

    [SupportedOSPlatform("windows")]
    private static bool HasPrivateFenceAcl(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;

        var agentServiceSid = ServiceSidResolver.Resolve("KamenRaider Agent");
        var directoryAcl = new DirectoryInfo(directory)
            .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var fileAcl = new FileInfo(path)
            .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        return IsPrivateSecurity(directoryAcl, agentServiceSid, isDirectory: true) &&
            IsPrivateSecurity(fileAcl, agentServiceSid, isDirectory: false);
    }

    private static byte[] ReadSingleLinkFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            return File.ReadAllBytes(path);

        const uint genericRead = 0x80000000;
        const uint shareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
        const uint openExisting = 3;
        const uint openReparsePoint = 0x00200000;
        using var handle = CreateFile(
            path,
            genericRead,
            shareReadWriteDelete,
            IntPtr.Zero,
            openExisting,
            openReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException("The maintenance fence file could not be opened safely.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        if (!GetFileInformationByHandle(handle, out var fileInfo) ||
            fileInfo.NumberOfLinks != 1 ||
            (fileInfo.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) != 0)
            throw new UnauthorizedAccessException("The maintenance fence must be a regular single-link file.");

        using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        if (stream.Length is <= 0 or > MaximumDocumentBytes)
            return [];
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static bool IsSafeIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static IEnumerable<string> Ancestors(string fullPath)
    {
        var current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            yield return current;
            current = Path.GetDirectoryName(current)!;
        }
    }

    private sealed record FenceDocument(
        int SchemaVersion,
        string State,
        string OperationId,
        string ChainId,
        string HeadOperationId,
        long Generation,
        string Phase);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NativeFileInformation
    {
        public uint Attributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
