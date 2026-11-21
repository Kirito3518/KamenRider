using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace KamenRaider.Agent.Identity;

public sealed class DeviceIdentity : IAgentRequestSigner, IDisposable
{
    private static readonly ConcurrentDictionary<string, object> LoadLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ECDsa key;

    public string MachineId { get; }
    public string PublicKey => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    private DeviceIdentity(string id, byte[] bytes)
    {
        MachineId = id;
        key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(bytes, out _);
    }

    private sealed record Saved(string MachineId, string PrivateKey);

    public static DeviceIdentity LoadOrCreate(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Identity requires Windows ACLs");

        var dataDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(dataDirectory);
        var gate = LoadLocks.GetOrAdd(dataDirectory, static _ => new object());
        Saved saved;

        lock (gate)
        {
            var identityDirectory = Path.Combine(dataDirectory, "identity");
            Directory.CreateDirectory(identityDirectory);
            var directoryInfo = new DirectoryInfo(identityDirectory);
            if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Identity directory must not be a reparse point");

            using var windowsIdentity = WindowsIdentity.GetCurrent();
            var service = windowsIdentity.User!;
            // LocalSystem's token user is SYSTEM, but its token owner (used for newly created
            // objects) is BUILTIN\Administrators. The Agent now runs as LocalSystem, so the
            // identity owner must match the token owner, not the token user.
            var owner = windowsIdentity.Owner ?? service;
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            if (directoryInfo.GetAccessControl(AccessControlSections.Owner)
                    .GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier existingDirectoryOwner ||
                (!owner.Equals(existingDirectoryOwner) && !IsAdoptableLegacyOwner(owner, existingDirectoryOwner)))
                throw new UnauthorizedAccessException("Identity directory owner does not match the current service account.");

            var directorySecurity = new DirectorySecurity();
            directorySecurity.SetAccessRuleProtection(true, false);
            directorySecurity.SetOwner(owner);
            foreach (var sid in new[] { service, owner, administrators, system }.Distinct())
            {
                directorySecurity.AddAccessRule(new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }
            directoryInfo.SetAccessControl(directorySecurity);
            var appliedDirectorySecurity = directoryInfo.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (!appliedDirectorySecurity.AreAccessRulesProtected ||
                !owner.Equals(appliedDirectorySecurity.GetOwner(typeof(SecurityIdentifier))))
                throw new UnauthorizedAccessException("Identity directory ACL could not be secured for the current service account.");

            var path = Path.Combine(identityDirectory, "identity.json");
            if (!File.Exists(path))
            {
                using var generatedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var generated = new Saved(
                    Guid.NewGuid().ToString("N"),
                    Convert.ToBase64String(generatedKey.ExportPkcs8PrivateKey()));
                var temporaryPath = Path.Combine(identityDirectory, ".identity-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        JsonSerializer.Serialize(file, generated);
                        file.Flush(true);
                    }

                    try { File.Move(temporaryPath, path); }
                    catch (IOException) when (File.Exists(path)) { }
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }

            var identityInfo = new FileInfo(path);
            if ((identityInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Identity file must not be a reparse point");

            if (identityInfo.GetAccessControl(AccessControlSections.Owner)
                    .GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier existingFileOwner ||
                (!owner.Equals(existingFileOwner) && !IsAdoptableLegacyOwner(owner, existingFileOwner)))
                throw new UnauthorizedAccessException("Identity file owner does not match the current service account.");

            var fileSecurity = new FileSecurity();
            fileSecurity.SetAccessRuleProtection(true, false);
            fileSecurity.SetOwner(owner);
            foreach (var sid in new[] { service, owner, administrators, system }.Distinct())
                fileSecurity.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            identityInfo.SetAccessControl(fileSecurity);
            var appliedFileSecurity = identityInfo.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (!appliedFileSecurity.AreAccessRulesProtected ||
                !owner.Equals(appliedFileSecurity.GetOwner(typeof(SecurityIdentifier))))
                throw new UnauthorizedAccessException("Identity file ACL could not be secured for the current service account.");

            saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Invalid identity");
        }

        return new(saved.MachineId, Convert.FromBase64String(saved.PrivateKey));
    }

    // A legacy install ran the Agent as the virtual service account S-1-5-80-...; only SYSTEM
    // (whose token owner is BUILTIN\Administrators) may adopt that identity, and it re-owns it
    // immediately below. An identity directory owned by BUILTIN\Administrators is also adopted:
    // the LocalSystem token uses that owner for newly created objects.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsAdoptableLegacyOwner(SecurityIdentifier currentOwner, SecurityIdentifier existingOwner)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (!currentOwner.Equals(system) && !currentOwner.Equals(administrators))
            return false;
        return existingOwner.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal)
            || existingOwner.Equals(administrators);
    }

    public string Sign(string method, string path, DateTimeOffset timestamp, string nonce, string body) =>        Convert.ToBase64String(key.SignData(
            Encoding.UTF8.GetBytes($"{MachineId}\n{method}\n{path}\n{timestamp.ToUnixTimeSeconds()}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}"),
            HashAlgorithmName.SHA256));

    public void Dispose() => key.Dispose();
}
