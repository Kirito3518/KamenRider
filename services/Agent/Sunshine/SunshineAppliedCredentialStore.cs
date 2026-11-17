using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

// This file implements the Agent's Windows-only private-file contract: the Agent service SID,
// protected ACLs, and DPAPI-adjacent storage. The composition root rejects non-Windows first.
#pragma warning disable CA1416

namespace KamenRaider.Agent.Sunshine;

/// <summary>
/// Persists only the fleet Sunshine management credential revision this Agent has applied. It never
/// stores the username or password; the credential itself lives in the DPAPI-protected store written
/// by <see cref="WindowsSunshineCredentialStore"/>. A missing file means revision 0, so a manually
/// provisioned credential (no applied-revision file) is replaced by the configured fleet credential.
/// The file and its directory use the same protected ACL model as the other private Agent files.
/// </summary>
public sealed class SunshineAppliedCredentialStore : ISunshineCredentialRevisionStore
{
    internal const string CredentialDirectoryName = "sunshine";
    internal const string AppliedRevisionFileName = "applied-credential.json";
    private const int MaximumFileBytes = 256;

    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SecurityIdentifier serviceSid;
    private readonly string dataDirectory;

    internal sealed record AppliedRevision(long Revision);

    public string DirectoryPath { get; }
    public string FilePath { get; }

    internal SunshineAppliedCredentialStore(string dataDirectory, SecurityIdentifier serviceSid)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Sunshine applied credential storage requires Windows ACLs.");
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(serviceSid);
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        if (!Path.IsPathFullyQualified(this.dataDirectory))
            throw new ArgumentException("Agent data directory must be absolute.", nameof(dataDirectory));
        this.serviceSid = serviceSid;
        DirectoryPath = Path.Combine(this.dataDirectory, CredentialDirectoryName);
        FilePath = Path.Combine(DirectoryPath, AppliedRevisionFileName);
    }

    /// <summary>
    /// Returns the last applied fleet credential revision, or 0 when no revision has been applied yet.
    /// A malformed, unreadable, or insecurely protected file is an error rather than silently ignored.
    /// </summary>
    public long Read()
    {
        if (!Directory.Exists(dataDirectory)) return 0;
        EnsureRealDirectory(dataDirectory, "Agent data directory");
        if (!Directory.Exists(DirectoryPath)) return 0;
        EnsureRealDirectory(DirectoryPath, "Sunshine credential directory");
        ValidatePrivateAcl(DirectoryPath, directory: true);
        if (!File.Exists(FilePath)) return 0;

        EnsureRealFile(FilePath);
        ValidatePrivateAcl(FilePath, directory: false);
        var info = new FileInfo(FilePath);
        if (info.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("Sunshine applied credential revision file has an invalid size.");

        var payload = File.ReadAllBytes(FilePath);
        try
        {
            var envelope = JsonSerializer.Deserialize<AppliedRevision>(payload, JsonOptions)
                ?? throw new InvalidDataException("Sunshine applied credential revision file is empty.");
            if (envelope.Revision <= 0)
                throw new InvalidDataException("Sunshine applied credential revision is invalid.");
            return envelope.Revision;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Sunshine applied credential revision file is invalid.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>Atomically records the fleet credential revision whose credential is already stored.</summary>
    public void Save(long revision)
    {
        if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision), "Applied credential revision must be positive.");
        EnsureTrustedCaller();
        EnsureRevisionDirectory();

        if (File.Exists(FilePath))
        {
            EnsureRealFile(FilePath);
            ValidatePrivateAcl(FilePath, directory: false);
        }
        else if (File.GetAttributes(DirectoryPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Sunshine credential directory must not be a reparse point.");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new AppliedRevision(revision), JsonOptions);
        if (payload.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("Sunshine applied credential revision payload is too large.");

        var temporaryPath = Path.Combine(DirectoryPath, ".applied-credential-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                SetPrivateAcl(temporaryPath, directory: false);
                output.Write(payload);
                output.Flush(flushToDisk: true);
            }

            if (File.Exists(FilePath))
            {
                File.Replace(temporaryPath, FilePath, null, ignoreMetadataErrors: false);
                temporaryPath = string.Empty;
            }
            else
            {
                File.Move(temporaryPath, FilePath);
                temporaryPath = string.Empty;
            }

            ValidatePrivateAcl(FilePath, directory: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (!string.IsNullOrEmpty(temporaryPath) && File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private void EnsureRevisionDirectory()
    {
        EnsureRealDirectory(dataDirectory, "Agent data directory");
        if (Directory.Exists(DirectoryPath))
        {
            EnsureRealDirectory(DirectoryPath, "Sunshine credential directory");
            ValidatePrivateAcl(DirectoryPath, directory: true);
            return;
        }

        Directory.CreateDirectory(DirectoryPath);
        EnsureRealDirectory(DirectoryPath, "Sunshine credential directory");
        SetPrivateAcl(DirectoryPath, directory: true);
        ValidatePrivateAcl(DirectoryPath, directory: true);
    }

    private void EnsureTrustedCaller()
    {
        var identity = WindowsIdentity.GetCurrent();
        var currentSid = identity.User ?? throw new UnauthorizedAccessException("Current Windows identity has no user SID.");
        if (currentSid == serviceSid || currentSid == SystemSid || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return;
        throw new UnauthorizedAccessException("Only the Agent service or an administrator may record an applied Sunshine credential revision.");
    }

    private void SetPrivateAcl(string path, bool directory)
    {
        var identity = WindowsIdentity.GetCurrent();
        var currentSid = identity.User ?? throw new UnauthorizedAccessException("Current Windows identity has no user SID.");
        SecurityIdentifier owner;
        if (currentSid == serviceSid || currentSid == SystemSid) owner = currentSid;
        else if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) owner = AdministratorsSid;
        else throw new UnauthorizedAccessException("Only the Agent service or an administrator may protect Agent private files.");

        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(owner);
        foreach (var sid in new[] { serviceSid, SystemSid, AdministratorsSid }.Distinct())
        {
            var inheritance = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        }

        if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else new FileInfo(path).SetAccessControl((FileSecurity)security);
    }

    private void ValidatePrivateAcl(string path, bool directory)
    {
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("Agent private file ACL must not inherit access rules.");

        var trusted = new HashSet<SecurityIdentifier> { serviceSid, SystemSid, AdministratorsSid };
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner))
            throw new UnauthorizedAccessException("Agent private file owner is not trusted.");

        var fullControlSids = new HashSet<SecurityIdentifier>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference is not SecurityIdentifier sid || !trusted.Contains(sid))
                throw new UnauthorizedAccessException("Agent private file ACL contains an untrusted access rule.");
            if ((rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
                fullControlSids.Add(sid);
        }

        if (trusted.Any(sid => !fullControlSids.Contains(sid)))
            throw new UnauthorizedAccessException("Agent private file ACL is missing a required trusted principal.");
    }

    private static void EnsureRealDirectory(string path, string description)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"{description} does not exist.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{description} must not be a reparse point.");
    }

    private static void EnsureRealFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Agent private file must not be a reparse point.");
    }
}

#pragma warning restore CA1416
