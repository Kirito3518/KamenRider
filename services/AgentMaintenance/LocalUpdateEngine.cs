using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using KamenRaider.AgentPackage;
using Microsoft.Win32.SafeHandles;

namespace KamenRaider.AgentMaintenance;

public sealed record MaintenanceCommandRequest(
    int SchemaVersion,
    string Command,
    string OperationId,
    string TargetVersion,
    string TargetRuntime,
    string TargetPackageSha256,
    long TargetPackageLength);

public static class MaintenanceCommandRequestParser
{
    private const int MaximumRequestBytes = 16 * 1024;
    private static readonly HashSet<string> RequiredFields = new(StringComparer.Ordinal)
    {
        "schemaVersion", "command", "operationId", "targetVersion", "targetRuntime", "targetPackageSha256", "targetPackageLength"
    };
    private static readonly HashSet<string> RequiredFieldsIgnoreCase = new(RequiredFields, StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 4
    };

    public static MaintenanceCommandRequest Parse(byte[] bytes, string expectedCommand)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length is <= 0 or > MaximumRequestBytes || expectedCommand is not ("validate-archive" or "extract"))
            throw new InvalidDataException("The fixed maintenance request exceeds its limit or names an unsupported operation.");

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 4,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The fixed maintenance request must be a JSON object.");
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!RequiredFields.Contains(property.Name) || !found.Add(property.Name))
                    throw new InvalidDataException("The fixed maintenance request contains an unknown or duplicate field.");
            if (!found.SetEquals(RequiredFieldsIgnoreCase))
                throw new InvalidDataException("The fixed maintenance request is incomplete.");

            var request = JsonSerializer.Deserialize<MaintenanceCommandRequest>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The fixed maintenance request is empty.");
            return LocalUpdateEngineRequestValidation.Validate(request, expectedCommand);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The fixed maintenance request is malformed.", ex);
        }
    }
}

public sealed record AgentMaintenanceCommandResult(
    string Status,
    string? Category,
    string? ArchiveSha256,
    string? Version,
    string? TargetRuntime,
    string? InventoryDigest,
    long? TotalBytes,
    IReadOnlyList<PackageNode>? Nodes);

public interface IMaintenanceWorkStore
{
    MaintenanceCommandRequest ReadRequest(string expectedCommand);
    Stream OpenArchiveRead();
    string ExtractionRoot { get; }
    void CreateExtractionRoot();
}

public sealed class LocalUpdateEngine(IMaintenanceWorkStore workStore)
{
    public PackageInventory ValidateArchive()
    {
        var (_, archive, inventory) = OpenVerifiedArchive("validate-archive");
        archive.Dispose();
        return inventory;
    }

    public PackageInventory Extract()
    {
        var (_, archive, inventory) = OpenVerifiedArchive("extract");
        using (archive)
        {
            workStore.CreateExtractionRoot();
            archive.Position = 0;
            return PackageVerifier.Extract(archive, workStore.ExtractionRoot, inventory);
        }
    }

    private (MaintenanceCommandRequest Request, Stream Archive, PackageInventory Inventory) OpenVerifiedArchive(string command)
    {
        ArgumentNullException.ThrowIfNull(workStore);
        var request = ValidateRequest(workStore.ReadRequest(command), command);
        var archive = workStore.OpenArchiveRead();
        try
        {
            if (!archive.CanRead || !archive.CanSeek || archive.Length != request.TargetPackageLength)
                throw new InvalidOperationException("The target archive length does not match its typed request.");
            archive.Position = 0;
            var inventory = PackageVerifier.ValidateArchive(archive, request.TargetVersion);
            if (!string.Equals(inventory.ArchiveHash, request.TargetPackageSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(inventory.TargetRuntime, request.TargetRuntime, StringComparison.Ordinal))
                throw new InvalidOperationException("The target archive bytes or native runtime do not match the typed request.");
            return (request, archive, inventory);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private static MaintenanceCommandRequest ValidateRequest(MaintenanceCommandRequest? request, string expectedCommand)
    {
        if (request is null)
            throw new InvalidDataException("The fixed maintenance request is missing.");
        return LocalUpdateEngineRequestValidation.Validate(request, expectedCommand);
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsMaintenanceWorkStore : IMaintenanceWorkStore
{
    private const int MaximumRequestBytes = 16 * 1024;
    private readonly string directory;

    public WindowsMaintenanceWorkStore()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The maintenance work store requires Windows ACLs.");
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonData) || !Path.IsPathFullyQualified(commonData))
            throw new InvalidOperationException("The native CommonApplicationData path is unavailable.");
        directory = Path.Combine(Path.GetFullPath(commonData), "KamenRaider.AgentMaintenance", "work");
    }

    internal WindowsMaintenanceWorkStore(string workDirectoryPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The maintenance work store requires Windows ACLs.");
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectoryPath);
        directory = Path.GetFullPath(workDirectoryPath);
    }

    public string ExtractionRoot => Path.Combine(directory, "target");

    public MaintenanceCommandRequest ReadRequest(string expectedCommand)
    {
        if (expectedCommand is not ("validate-archive" or "extract"))
            throw new InvalidOperationException("This maintenance command is not implemented by the local archive engine.");
        EnsureWorkDirectory(create: false);
        var path = Path.Combine(directory, "request.json");
        EnsurePrivateFile(path);
        var bytes = ReadSingleLinkFile(path, MaximumRequestBytes);
        return MaintenanceCommandRequestParser.Parse(bytes, expectedCommand);
    }

    public Stream OpenArchiveRead()
    {
        EnsureWorkDirectory(create: false);
        var path = Path.Combine(directory, "target.zip");
        EnsurePrivateFile(path);
        return OpenArchiveStream(path);
    }

    internal static Stream OpenArchiveStream(string path)
    {
        var handle = OpenFile(path, genericRead: true, shareRead: true);
        try
        {
            if (!GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks != 1 ||
                (info.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) != 0)
                throw new UnauthorizedAccessException("The target archive must be a regular single-link file.");
            // The returned FileStream owns the SafeFileHandle from this point forward.
            return new FileStream(handle, FileAccess.Read, 65536, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void CreateExtractionRoot()
    {
        EnsureWorkDirectory(create: true);
        if (Directory.Exists(ExtractionRoot) || File.Exists(ExtractionRoot))
            throw new IOException("The target extraction directory already exists.");
        try
        {
            new DirectoryInfo(ExtractionRoot).Create(CreatePrivateDirectorySecurity());
        }
        catch (IOException) when (Directory.Exists(ExtractionRoot) || File.Exists(ExtractionRoot))
        {
            throw new IOException("The target extraction directory was created by another process.");
        }
        EnsurePrivateDirectory(ExtractionRoot);
    }

    private void EnsureWorkDirectory(bool create)
    {
        foreach (var ancestor in Ancestors(directory))
            if ((Directory.Exists(ancestor) || File.Exists(ancestor)) &&
                (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("The maintenance work path contains a reparse point.");

        var maintenanceDirectory = Path.GetDirectoryName(directory)
            ?? throw new InvalidOperationException("The protected maintenance work directory has no parent.");
        if (!Directory.Exists(maintenanceDirectory))
        {
            if (!create)
                throw new DirectoryNotFoundException("The protected maintenance directory is unavailable.");
            new WindowsMaintenanceFenceStore(maintenanceDirectory).EnsureLocationForJournal();
        }
        WindowsMaintenanceFenceStore.ValidateLocationForJournal(maintenanceDirectory);

        if (!Directory.Exists(directory) && create)
        {
            try { new DirectoryInfo(directory).Create(CreatePrivateDirectorySecurity()); }
            catch (IOException) when (Directory.Exists(directory)) { }
        }
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("The protected maintenance work directory is unavailable.");
        EnsurePrivateDirectory(directory);
    }

    private static void EnsurePrivateDirectory(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new UnauthorizedAccessException("The maintenance work path is not a regular directory.");
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!HasPrivateSecurity(security, isDirectory: true))
            throw new UnauthorizedAccessException("The maintenance work directory owner or ACL is unsafe.");
    }

    private static void EnsurePrivateFile(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new FileNotFoundException("The protected maintenance work file is missing or unsafe.", path);
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!HasPrivateSecurity(security, isDirectory: false))
            throw new UnauthorizedAccessException("The maintenance work file owner or ACL is unsafe.");
        using var handle = OpenFile(path, genericRead: true, shareRead: true);
        if (!GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks != 1 ||
            (info.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) != 0)
            throw new UnauthorizedAccessException("The maintenance work file must be a regular single-link file.");
    }

    private static byte[] ReadSingleLinkFile(string path, int maximumBytes)
    {
        using var handle = OpenFile(path, genericRead: true, shareRead: true);
        if (!GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks != 1 ||
            (info.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) != 0)
            throw new UnauthorizedAccessException("The maintenance request must be a regular single-link file.");
        using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("The fixed maintenance request exceeds its size limit.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static bool HasPrivateSecurity(FileSystemSecurity security, bool isDirectory)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (!security.AreAccessRulesProtected || owner is null || (owner != system && owner != admins))
            return false;
        var inheritance = isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (rules.Length != 2)
            return false;
        var identities = new HashSet<SecurityIdentifier>();
        foreach (var rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IsInherited ||
                rule.PropagationFlags != PropagationFlags.None || rule.InheritanceFlags != inheritance ||
                rule.IdentityReference is not SecurityIdentifier sid || !identities.Add(sid) ||
                rule.FileSystemRights != FileSystemRights.FullControl)
                return false;
        }
        return identities.SetEquals([system, admins]);
    }

    internal static DirectorySecurity CreatePrivateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(AdministratorsSid());
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(SystemSid(), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid(), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    internal static void SetPrivateFileSecurity(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(AdministratorsSid());
        security.AddAccessRule(new FileSystemAccessRule(SystemSid(), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid(), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static SafeFileHandle OpenFile(string path, bool genericRead, bool shareRead)
    {
        var access = genericRead ? 0x80000000u : 0u;
        var share = shareRead ? 0x00000001u : 0u;
        var handle = CreateFile(path, access, share, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("The maintenance work file could not be opened safely.", new System.ComponentModel.Win32Exception(error));
        }
        return handle;
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
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsFileInformation fileInformation);
}

internal static class LocalUpdateEngineRequestValidation
{
    private static readonly Regex VersionPattern = new("\\A(?:[0-9]+\\.[0-9]+\\.[0-9]+|1\\.0\\.0\\+g[0-9a-f]{12,40})\\z", RegexOptions.CultureInvariant);

    public static MaintenanceCommandRequest Validate(MaintenanceCommandRequest request, string expectedCommand)
    {
        if (request.SchemaVersion != 1 || request.Command != expectedCommand ||
            string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 128 ||
            request.OperationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')) ||
            string.IsNullOrWhiteSpace(request.TargetVersion) || request.TargetVersion.Length > 64 || !VersionPattern.IsMatch(request.TargetVersion) ||
            request.TargetRuntime is not ("win-x64" or "win-arm64") ||
            request.TargetPackageSha256 is null || request.TargetPackageSha256.Length != 64 ||
            request.TargetPackageSha256.Any(character => !Uri.IsHexDigit(character)) ||
            request.TargetPackageLength is < 1 or > 104857600)
            throw new InvalidDataException("The fixed maintenance request is malformed or does not match the requested operation.");
        return request;
    }
}
