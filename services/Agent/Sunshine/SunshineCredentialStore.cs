using System.ComponentModel;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace KamenRaider.Agent.Sunshine;

public sealed record SunshineManagementCredential(string UserName, string Password);

internal interface ISunshineCredentialProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsDpapiMachineSecretProtector : ISunshineCredentialProtector
{
    private const uint UiForbidden = 0x00000001;
    private const uint LocalMachine = 0x00000004;
    private const int MaximumPayloadBytes = 16 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;

        public DataBlob(int length, IntPtr data)
        {
            Length = length;
            Data = data;
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        out IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public byte[] Protect(ReadOnlySpan<byte> plaintext) => Transform(plaintext, protect: true);
    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => Transform(ciphertext, protect: false);

    private static byte[] Transform(ReadOnlySpan<byte> value, bool protect)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Sunshine credentials require Windows DPAPI.");
        if (value.IsEmpty || value.Length > MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(value), "The credential payload size is invalid.");

        var inputBytes = value.ToArray();
        var inputHandle = GCHandle.Alloc(inputBytes, GCHandleType.Pinned);
        var input = new DataBlob(inputBytes.Length, inputHandle.AddrOfPinnedObject());
        var output = default(DataBlob);
        var description = IntPtr.Zero;
        try
        {
            var succeeded = protect
                ? CryptProtectData(ref input, "KamenRaider Sunshine credentials", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden | LocalMachine, out output)
                : CryptUnprotectData(ref input, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);

            if (!succeeded)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not protect or unprotect Sunshine credentials.");
            if (output.Data == IntPtr.Zero || output.Length is <= 0 or > MaximumPayloadBytes)
                throw new InvalidDataException("Windows returned an invalid protected credential payload.");

            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                if (output.Length is > 0 and <= MaximumPayloadBytes)
                    Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length);
                _ = LocalFree(output.Data);
            }
            if (description != IntPtr.Zero)
                _ = LocalFree(description);
            inputHandle.Free();
            CryptographicOperations.ZeroMemory(inputBytes);
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsSunshineCredentialStore : ISunshineManagementCredentialSink
{
    internal const string AgentServiceName = "KamenRaider Agent";
    internal const string CredentialDirectoryName = "sunshine";
    internal const string CredentialFileName = "management-credentials.dpapi";
    private const int MaximumCiphertextBytes = 32 * 1024;

    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private readonly SecurityIdentifier serviceSid;
    private readonly ISunshineCredentialProtector protector;
    private readonly string dataDirectory;

    private sealed record CredentialEnvelope(int Version, string UserName, string Password);

    public string DirectoryPath { get; }
    public string CredentialPath { get; }

    internal WindowsSunshineCredentialStore(string dataDirectory, SecurityIdentifier serviceSid, ISunshineCredentialProtector protector)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Sunshine credential storage requires Windows ACLs.");
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(serviceSid);
        ArgumentNullException.ThrowIfNull(protector);

        this.dataDirectory = Path.GetFullPath(dataDirectory);
        if (!Path.IsPathFullyQualified(this.dataDirectory))
            throw new ArgumentException("Agent data directory must be absolute.", nameof(dataDirectory));

        this.serviceSid = serviceSid;
        this.protector = protector;
        DirectoryPath = Path.Combine(this.dataDirectory, CredentialDirectoryName);
        CredentialPath = Path.Combine(DirectoryPath, CredentialFileName);
    }

    internal SunshineManagementCredential? Load()
    {
        if (!Directory.Exists(dataDirectory)) return null;
        EnsureRealDirectory(dataDirectory, "Agent data directory");
        if (!Directory.Exists(DirectoryPath)) return null;

        EnsureRealDirectory(DirectoryPath, "Sunshine credential directory");
        ValidatePrivateAcl(DirectoryPath, directory: true);
        if (!File.Exists(CredentialPath)) return null;

        EnsureRealFile(CredentialPath);
        ValidatePrivateAcl(CredentialPath, directory: false);
        var info = new FileInfo(CredentialPath);
        if (info.Length is <= 0 or > MaximumCiphertextBytes)
            throw new InvalidDataException("Sunshine credential file has an invalid size.");

        var encrypted = File.ReadAllBytes(CredentialPath);
        if (encrypted.Length is <= 0 or > MaximumCiphertextBytes)
            throw new InvalidDataException("Sunshine credential file has an invalid size.");

        byte[]? plaintext = null;
        try
        {
            plaintext = protector.Unprotect(encrypted);
            if (plaintext.Length is <= 0 or > MaximumCiphertextBytes)
                throw new InvalidDataException("Sunshine credential payload has an invalid size.");

            var envelope = JsonSerializer.Deserialize<CredentialEnvelope>(plaintext)
                ?? throw new InvalidDataException("Sunshine credential payload is empty.");
            if (envelope.Version != 1)
                throw new InvalidDataException("Sunshine credential payload version is unsupported.");

            var credential = new SunshineManagementCredential(envelope.UserName, envelope.Password);
            ValidateCredential(credential);
            return credential;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Sunshine credential payload is invalid.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal void Save(SunshineManagementCredential credential, bool replace = false)
    {
        ValidateCredential(credential);
        EnsureTrustedCaller();
        EnsureRealDirectory(dataDirectory, "Agent data directory");
        EnsureCredentialDirectory();

        var exists = File.Exists(CredentialPath);
        if (exists)
        {
            EnsureRealFile(CredentialPath);
            ValidatePrivateAcl(CredentialPath, directory: false);
            if (!replace) throw new IOException("Sunshine credentials already exist; explicit replacement is required.");
        }
        else if (File.GetAttributes(DirectoryPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Sunshine credential directory must not be a reparse point.");
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new CredentialEnvelope(1, credential.UserName, credential.Password));
        byte[]? encrypted = null;
        var temporaryPath = Path.Combine(DirectoryPath, ".credentials-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (plaintext.Length > MaximumCiphertextBytes)
                throw new InvalidDataException("Sunshine credential payload is too large.");
            encrypted = protector.Protect(plaintext);
            if (encrypted.Length is <= 0 or > MaximumCiphertextBytes)
                throw new InvalidDataException("Windows returned an invalid protected credential payload.");

            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                SetPrivateAcl(temporaryPath, directory: false);
                output.Write(encrypted);
                output.Flush(flushToDisk: true);
            }

            if (File.Exists(CredentialPath))
            {
                if (!replace) throw new IOException("Sunshine credentials already exist; explicit replacement is required.");
                File.Replace(temporaryPath, CredentialPath, null, ignoreMetadataErrors: false);
                temporaryPath = string.Empty;
            }
            else
            {
                File.Move(temporaryPath, CredentialPath);
                temporaryPath = string.Empty;
            }

            ValidatePrivateAcl(CredentialPath, directory: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            if (!string.IsNullOrEmpty(temporaryPath) && File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    internal static SecurityIdentifier ResolveAgentServiceSid()
    {
        var serviceName = Encoding.Unicode.GetBytes(AgentServiceName.ToUpperInvariant());
        var hash = SHA1.HashData(serviceName);
        var sid = "S-1-5-80-" + string.Join('-', Enumerable.Range(0, 5)
            .Select(index => BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(index * sizeof(uint), sizeof(uint)))));
        return new SecurityIdentifier(sid);
    }

    private void EnsureCredentialDirectory()
    {
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
        throw new UnauthorizedAccessException("Only the Agent service or an administrator may provision Sunshine credentials.");
    }

    private void SetPrivateAcl(string path, bool directory)
    {
        var currentSid = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Current Windows identity has no user SID.");
        SecurityIdentifier owner;
        if (currentSid == serviceSid || currentSid == SystemSid) owner = currentSid;
        else if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) owner = AdministratorsSid;
        else throw new UnauthorizedAccessException("Only the Agent service or an administrator may protect Sunshine credentials.");

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
            throw new UnauthorizedAccessException("Sunshine credential ACL must not inherit access rules.");

        var trusted = new HashSet<SecurityIdentifier> { serviceSid, SystemSid, AdministratorsSid };
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner))
            throw new UnauthorizedAccessException("Sunshine credential owner is not trusted.");

        var fullControlSids = new HashSet<SecurityIdentifier>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference is not SecurityIdentifier sid || !trusted.Contains(sid))
                throw new UnauthorizedAccessException("Sunshine credential ACL contains an untrusted access rule.");
            if ((rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
                fullControlSids.Add(sid);
        }

        if (trusted.Any(sid => !fullControlSids.Contains(sid)))
            throw new UnauthorizedAccessException("Sunshine credential ACL is missing a required trusted principal.");
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
            throw new InvalidDataException("Sunshine credential file must not be a reparse point.");
    }

    private static void ValidateCredential(SunshineManagementCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(credential.UserName) || credential.UserName.Length > 128 || credential.UserName.Contains(':') || credential.UserName.Any(char.IsControl))
            throw new ArgumentException("Sunshine username is invalid.", nameof(credential));
        if (string.IsNullOrEmpty(credential.Password) || credential.Password.Length > 2048 || credential.Password.Any(c => c == '\0'))
            throw new ArgumentException("Sunshine password is invalid.", nameof(credential));
    }

    void ISunshineManagementCredentialSink.Save(SunshineManagementCredential credential, bool replace) => Save(credential, replace);
}
