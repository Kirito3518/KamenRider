using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using KamenRaider.Contracts;
using Microsoft.Win32.SafeHandles;

namespace KamenRaider.AgentMaintenance;

public sealed record TrustedIdentityDocument(string MachineId, string PrivateKey);

public interface ITrustedIdentitySource
{
    TrustedIdentityDocument ReadExisting();
}

public sealed class TrustedIdentity : IDisposable
{
    private static readonly Regex MachineIdPattern = new("\\A[0-9a-fA-F]{32}\\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly ECDsa signingKey;

    public string MachineId { get; }
    public string KeyFingerprint { get; }
    public string PublicKey { get; }

    private TrustedIdentity(string machineId, ECDsa signingKey)
    {
        MachineId = machineId;
        this.signingKey = signingKey;
        var publicKeyBytes = signingKey.ExportSubjectPublicKeyInfo();
        PublicKey = Convert.ToBase64String(publicKeyBytes);
        KeyFingerprint = Convert.ToHexString(SHA256.HashData(publicKeyBytes));
    }

    public static TrustedIdentity ReadExisting(ITrustedIdentitySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var document = source.ReadExisting()
            ?? throw new InvalidDataException("The existing enrollment identity is unavailable.");
        if (!MachineIdPattern.IsMatch(document.MachineId) || string.IsNullOrWhiteSpace(document.PrivateKey) || document.PrivateKey.Length > 8192)
            throw new InvalidDataException("The existing enrollment identity is malformed.");

        byte[] privateKey;
        try { privateKey = Convert.FromBase64String(document.PrivateKey); }
        catch (FormatException ex) { throw new InvalidDataException("The existing enrollment private key is malformed.", ex); }
        if (privateKey.Length is <= 0 or > 4096)
            throw new InvalidDataException("The existing enrollment private key has an invalid size.");

        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(privateKey, out var bytesRead);
            if (bytesRead != privateKey.Length)
                throw new InvalidDataException("The existing enrollment private key has trailing data.");
            return new(document.MachineId, key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    public string Sign(string method, string path, long signedSeconds, string nonce, string body)
    {
        if (!string.Equals(method, "POST", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(path) ||
            path[0] != '/' || path.Contains('?') || path.Contains('#') || path.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(nonce) || nonce.Length > 128 ||
            nonce.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) ||
            body is null || System.Text.Encoding.UTF8.GetByteCount(body) > 1024 * 1024)
            throw new ArgumentException("The signed Agent request is invalid.");

        var bodyDigest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)));
        var canonical = System.Text.Encoding.UTF8.GetBytes(
            $"{MachineId}\n{method}\n{path}\n{signedSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n{nonce}\n{bodyDigest}");
        return Convert.ToBase64String(signingKey.SignData(canonical, HashAlgorithmName.SHA256));
    }

    public string SignSourceEvidence(AgentUpdateSourceEvidence evidence)
        => Convert.ToBase64String(signingKey.SignData(
            AgentUpdateSourceEvidenceCanonicalizer.CanonicalBytes(evidence), HashAlgorithmName.SHA256));

    public void Dispose() => signingKey.Dispose();
}

[SupportedOSPlatform("windows")]
public sealed class WindowsTrustedIdentitySource : ITrustedIdentitySource
{
    private const int MaximumIdentityBytes = 16 * 1024;
    private readonly string dataDirectory;

    public WindowsTrustedIdentitySource()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The trusted Agent identity reader requires Windows ACLs.");
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonData) || !Path.IsPathFullyQualified(commonData))
            throw new InvalidOperationException("The native CommonApplicationData path is unavailable.");
        dataDirectory = Path.Combine(Path.GetFullPath(commonData), "KamenRaider", "Agent");
    }

    public TrustedIdentityDocument ReadExisting()
    {
        EnsureNoReparseAncestors(dataDirectory);
        var identityDirectory = Path.Combine(dataDirectory, "identity");
        var identityPath = Path.Combine(identityDirectory, "identity.json");
        if (!Directory.Exists(identityDirectory) || !File.Exists(identityPath))
            throw new FileNotFoundException("The existing Agent enrollment identity is missing.", identityPath);

        var serviceSid = WindowsMaintenanceFenceStore.ResolveAgentServiceSid();
        EnsurePrivateDirectory(identityDirectory, serviceSid);
        EnsurePrivateFile(identityPath, serviceSid);
        var bytes = ReadSingleLinkFile(identityPath);
        if (bytes.Length is <= 0 or > MaximumIdentityBytes)
            throw new InvalidDataException("The existing Agent identity file has an invalid size.");
        return ParseDocument(bytes);
    }

    private static TrustedIdentityDocument ParseDocument(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The existing Agent identity document is not an object.");
        string? machineId = null;
        string? privateKey = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "MachineId", StringComparison.OrdinalIgnoreCase))
            {
                if (machineId is not null || property.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("The existing Agent identity has duplicate or invalid machine IDs.");
                machineId = property.Value.GetString();
            }
            else if (string.Equals(property.Name, "PrivateKey", StringComparison.OrdinalIgnoreCase))
            {
                if (privateKey is not null || property.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("The existing Agent identity has duplicate or invalid private keys.");
                privateKey = property.Value.GetString();
            }
            else
            {
                throw new InvalidDataException("The existing Agent identity has an unexpected field.");
            }
        }
        if (machineId is null || privateKey is null)
            throw new InvalidDataException("The existing Agent identity is incomplete.");
        return new(machineId, privateKey);
    }

    private static void EnsureNoReparseAncestors(string path)
    {
        foreach (var ancestor in Ancestors(path))
        {
            if (Directory.Exists(ancestor) || File.Exists(ancestor))
                if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("The existing Agent identity path contains a reparse point.");
        }
    }

    private static void EnsurePrivateDirectory(string path, SecurityIdentifier serviceSid)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new UnauthorizedAccessException("The Agent identity directory is not a regular directory.");
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!HasExpectedAcl(security, serviceSid, isDirectory: true))
            throw new UnauthorizedAccessException("The Agent identity directory owner or ACL is unsafe.");
    }

    private static void EnsurePrivateFile(string path, SecurityIdentifier serviceSid)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new UnauthorizedAccessException("The Agent identity is not a regular file.");
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!HasExpectedAcl(security, serviceSid, isDirectory: false))
            throw new UnauthorizedAccessException("The Agent identity file owner or ACL is unsafe.");
        using var handle = OpenReadHandle(path);
        if (!GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks != 1)
            throw new UnauthorizedAccessException("The Agent identity file must have exactly one hard link.");
    }

    private static bool HasExpectedAcl(FileSystemSecurity security, SecurityIdentifier serviceSid, bool isDirectory)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (!security.AreAccessRulesProtected || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != serviceSid)
            return false;
        var inheritance = isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (rules.Length != 3) return false;
        var identities = new HashSet<SecurityIdentifier>();
        foreach (var rule in rules)
            if (rule.AccessControlType != AccessControlType.Allow || rule.IsInherited || rule.InheritanceFlags != inheritance ||
                rule.PropagationFlags != PropagationFlags.None || rule.FileSystemRights != FileSystemRights.FullControl ||
                rule.IdentityReference is not SecurityIdentifier sid || !identities.Add(sid))
                return false;
        return identities.SetEquals([serviceSid, system, admins]);
    }

    private static byte[] ReadSingleLinkFile(string path)
    {
        using var handle = OpenReadHandle(path);
        if (!GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks != 1 ||
            (info.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) != 0)
            throw new UnauthorizedAccessException("The Agent identity must be a regular single-link file.");
        using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        if (stream.Length is <= 0 or > MaximumIdentityBytes) return [];
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static SafeFileHandle OpenReadHandle(string path)
    {
        const uint genericRead = 0x80000000;
        const uint shareRead = 0x00000001;
        const uint openExisting = 3;
        const uint openReparsePoint = 0x00200000;
        var handle = CreateFile(path, genericRead, shareRead, IntPtr.Zero, openExisting, openReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("The existing Agent identity could not be opened safely.", new System.ComponentModel.Win32Exception(error));
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInformation
    {
        public uint Attributes;
        public ulong CreationTime;
        public ulong LastAccessTime;
        public ulong LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
