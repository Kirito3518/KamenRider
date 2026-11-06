using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using KamenRaider.Agent.Leases;

namespace KamenRaider.Agent.Sunshine;

public interface IStreamGrantHistoryStore
{
    StreamGrantHistorySnapshot Load();
    void Save(StreamGrantHistorySnapshot snapshot);
}

public sealed record StreamLeaseKey(string BootId, string RentalId);
public sealed record StreamGrantHistorySnapshot(StreamLeaseGrant[] Grants, StreamLeaseKey? CurrentLease);

[SupportedOSPlatform("windows")]
public sealed class WindowsStreamGrantHistoryStore : IStreamGrantHistoryStore
{
    private const int CurrentVersion = 2;
    private const int MaximumGrantCount = 10000;
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private readonly string directory;
    private readonly string filePath;

    public WindowsStreamGrantHistoryStore()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Stream Guard grant history store requires Windows.");

        directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "KamenRaider.StreamGuard");
        filePath = Path.Combine(directory, "grant-history.json");
    }

    public StreamGrantHistorySnapshot Load()
    {
        EnsurePrivateDirectory();
        if (!File.Exists(filePath)) return new([], null);
        EnsurePrivateFile(filePath);

        var info = new FileInfo(filePath);
        if (info.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("The Stream Guard grant history has an invalid size.");

        GrantHistoryDocument document;
        try
        {
            document = JsonSerializer.Deserialize<GrantHistoryDocument>(File.ReadAllBytes(filePath))
                ?? throw new InvalidDataException("The Stream Guard grant history is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The Stream Guard grant history is malformed.", ex);
        }

        if (document.Version != CurrentVersion || document.Grants is null || document.Grants.Length > MaximumGrantCount)
            throw new InvalidDataException("The Stream Guard grant history version or entry count is unsupported.");

        var keys = new HashSet<(string BootId, string RentalId)>();
        foreach (var grant in document.Grants)
        {
            ValidateGrant(grant);
            if (!keys.Add((grant.BootId, grant.RentalId)))
                throw new InvalidDataException("The Stream Guard grant history contains duplicate lease entries.");
        }

        if (document.CurrentLease is { } currentLease)
        {
            ValidateLeaseKey(currentLease);
            if (!keys.Contains((currentLease.BootId, currentLease.RentalId)))
                throw new InvalidDataException("The Stream Guard grant history's current lease has no accepted grant.");
        }

        return new(document.Grants, document.CurrentLease);
    }

    public void Save(StreamGrantHistorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Grants);
        EnsurePrivateDirectory();
        if (snapshot.Grants.Length > MaximumGrantCount)
            throw new InvalidDataException("The Stream Guard grant history exceeds its entry limit.");

        var entries = snapshot.Grants
            .OrderBy(grant => grant.BootId, StringComparer.Ordinal)
            .ThenBy(grant => grant.RentalId, StringComparer.Ordinal)
            .ToArray();
        foreach (var grant in entries) ValidateGrant(grant);
        if (snapshot.CurrentLease is { } currentLease)
        {
            ValidateLeaseKey(currentLease);
            if (!entries.Any(grant => grant.BootId == currentLease.BootId && grant.RentalId == currentLease.RentalId))
                throw new InvalidDataException("The Stream Guard grant history's current lease has no accepted grant.");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new GrantHistoryDocument(CurrentVersion, entries, snapshot.CurrentLease));
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("The Stream Guard grant history exceeds its file size limit.");

        var stagingPath = Path.Combine(directory, $"grant-history.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                SetPrivateFileSecurity(stagingPath);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(stagingPath, filePath, overwrite: true);
            EnsurePrivateFile(filePath);
        }
        finally
        {
            if (File.Exists(stagingPath)) File.Delete(stagingPath);
        }
    }

    private void EnsurePrivateDirectory()
    {
        foreach (var path in Ancestors(directory))
        {
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("The Stream Guard grant history path contains a reparse point.");
        }

        var parentDirectory = Path.GetDirectoryName(directory)
            ?? throw new InvalidOperationException("The Stream Guard grant history has no parent directory.");
        if (!Directory.Exists(parentDirectory))
            throw new DirectoryNotFoundException("The CommonApplicationData directory is unavailable.");

        if (!Directory.Exists(directory))
            new DirectoryInfo(directory).Create(CreatePrivateDirectorySecurity());

        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("The Stream Guard grant history directory is a reparse point.");

        EnsurePrivateSecurity(new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), directory);
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

    private static void EnsurePrivateFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("The Stream Guard grant history file is a reparse point.");

        EnsurePrivateSecurity(new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), path);
    }

    internal static DirectorySecurity CreatePrivateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(SystemSid());
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(SystemSid(), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid(), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static void SetPrivateFileSecurity(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(SystemSid());
        security.AddAccessRule(new FileSystemAccessRule(SystemSid(), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid(), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void EnsurePrivateSecurity(FileSystemSecurity security, string path)
    {
        if (!security.AreAccessRulesProtected || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != SystemSid())
            throw new UnauthorizedAccessException($"The Stream Guard grant history ACL or owner is not private: '{path}'.");

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        if (rules.Length != 2 || rules.Any(rule =>
                rule.AccessControlType != AccessControlType.Allow ||
                !rule.FileSystemRights.HasFlag(FileSystemRights.FullControl) ||
                rule.IdentityReference.Value is not ("S-1-5-18" or "S-1-5-32-544")))
            throw new UnauthorizedAccessException($"The Stream Guard grant history grants unexpected access: '{path}'.");

        if (!rules.Any(rule => rule.IdentityReference.Value == SystemSid().Value) ||
            !rules.Any(rule => rule.IdentityReference.Value == AdministratorsSid().Value))
            throw new UnauthorizedAccessException($"The Stream Guard grant history ACL is missing a required principal: '{path}'.");
    }

    private static SecurityIdentifier SystemSid() => new(WellKnownSidType.LocalSystemSid, null);
    private static SecurityIdentifier AdministratorsSid() => new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static void ValidateGrant(StreamLeaseGrant grant)
    {
        if (grant is null || string.IsNullOrWhiteSpace(grant.BootId) || string.IsNullOrWhiteSpace(grant.RentalId) ||
            grant.BootId.Length > 128 || grant.RentalId.Length > 128 || grant.Revision <= 0 ||
            grant.ExpiresAtUtc == default || grant.DeadlineUptimeMilliseconds <= 0)
            throw new InvalidDataException("The Stream Guard grant history contains an invalid lease entry.");
    }

    private static void ValidateLeaseKey(StreamLeaseKey key)
    {
        if (string.IsNullOrWhiteSpace(key.BootId) || string.IsNullOrWhiteSpace(key.RentalId) ||
            key.BootId.Length > 128 || key.RentalId.Length > 128)
            throw new InvalidDataException("The Stream Guard grant history contains an invalid current lease key.");
    }

    private sealed record GrantHistoryDocument(int Version, StreamLeaseGrant[]? Grants, StreamLeaseKey? CurrentLease);
}
