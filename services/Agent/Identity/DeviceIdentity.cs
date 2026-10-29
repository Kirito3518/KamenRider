using System.Security.Cryptography;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
namespace KamenRaider.Agent.Identity;
public sealed class DeviceIdentity : IDisposable
{
    private readonly ECDsa key;
    public string MachineId {get;}
    public string PublicKey=>Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private DeviceIdentity(string id,byte[] bytes) {MachineId=id; key=ECDsa.Create();key.ImportPkcs8PrivateKey(bytes,out _);}
    private record Saved(string MachineId,string PrivateKey);
    public static DeviceIdentity LoadOrCreate(string directory) {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Identity requires Windows ACLs");
        Directory.CreateDirectory(directory);
        var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);
        var service=WindowsIdentity.GetCurrent().User!;
        foreach(var sid in new[]{service,new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)}.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
        var path=Path.Combine(directory,"identity.json");
        if(!File.Exists(path)) {
            using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var saved=new Saved(Guid.NewGuid().ToString("N"),Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
            try {using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);JsonSerializer.Serialize(file,saved);file.Flush(true);} catch(IOException) when(File.Exists(path)) { }
        }
        var acl=new FileSecurity();acl.SetAccessRuleProtection(true,false);
        foreach(var sid in new[]{service,new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)}.Distinct()) acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
        var identity=JsonSerializer.Deserialize<Saved>(File.ReadAllText(path))??throw new InvalidDataException("Invalid identity");
        return new(identity.MachineId,Convert.FromBase64String(identity.PrivateKey));
    }
    public string Sign(string method,string path,DateTimeOffset timestamp,string nonce,string body)=>Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes($"{MachineId}\n{method}\n{path}\n{timestamp.ToUnixTimeSeconds()}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}"),HashAlgorithmName.SHA256));
    public void Dispose()=>key.Dispose();
}
