using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace KamenRaider.Agent.Identity;

public sealed class DeviceIdentity : IDisposable
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

            var service = WindowsIdentity.GetCurrent().User!;
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var directorySecurity = new DirectorySecurity();
            directorySecurity.SetAccessRuleProtection(true, false);
            directorySecurity.SetOwner(service);
            foreach (var sid in new[] { service, administrators, system }.Distinct())
            {
                directorySecurity.AddAccessRule(new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }
            directoryInfo.SetAccessControl(directorySecurity);

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

            var fileSecurity = new FileSecurity();
            fileSecurity.SetAccessRuleProtection(true, false);
            fileSecurity.SetOwner(service);
            foreach (var sid in new[] { service, administrators, system }.Distinct())
                fileSecurity.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            identityInfo.SetAccessControl(fileSecurity);

            saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Invalid identity");
        }

        return new(saved.MachineId, Convert.FromBase64String(saved.PrivateKey));
    }

    public string Sign(string method, string path, DateTimeOffset timestamp, string nonce, string body) =>
        Convert.ToBase64String(key.SignData(
            Encoding.UTF8.GetBytes($"{MachineId}\n{method}\n{path}\n{timestamp.ToUnixTimeSeconds()}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}"),
            HashAlgorithmName.SHA256));

    public void Dispose() => key.Dispose();
}
