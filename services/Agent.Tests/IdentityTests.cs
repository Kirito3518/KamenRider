using KamenRaider.Agent.Identity;
using KamenRaider.Controller.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
namespace Agent.Tests;
public class IdentityTests
{
    [Fact] public void IdentityPersistsAndVerifierAuthenticatesWithoutOwningReplayAcceptance() {
        var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
        string id, key;
        using(var identity=DeviceIdentity.LoadOrCreate(dir)) { id=identity.MachineId; key=identity.PublicKey; }
        using(var identity=DeviceIdentity.LoadOrCreate(dir)) {
            Assert.Equal(id,identity.MachineId); Assert.Equal(key,identity.PublicKey);
            var auth=new AgentAuthentication(); var now=DateTimeOffset.UtcNow;
            var signature=identity.Sign("POST","/agent/heartbeat",now,"nonce","body");
            var seconds=now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Null(auth.VerifySignature(id,key,"POST","/agent/heartbeat",seconds,"nonce",Encoding.UTF8.GetBytes("tampered"),signature));
            var verified=auth.VerifySignature(id,key,"POST","/agent/heartbeat",seconds,"nonce",Encoding.UTF8.GetBytes("body"),signature);
            Assert.NotNull(verified);
            Assert.Equal(id,verified.MachineId);Assert.Equal("POST",verified.Method);Assert.Equal("/agent/heartbeat",verified.Path);
            Assert.NotNull(auth.VerifySignature(id,key,"POST","/agent/heartbeat",seconds,"nonce",Encoding.UTF8.GetBytes("body"),signature));
            var future=now.AddMinutes(5);
            var futureSignature=identity.Sign("POST","/agent/heartbeat",future,"future-nonce","body");
            Assert.NotNull(auth.VerifySignature(id,key,"POST","/agent/heartbeat",future.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),"future-nonce",Encoding.UTF8.GetBytes("body"),futureSignature));
        }
        Directory.Delete(dir,true);
    }

    [Fact] public async Task ConcurrentIdentityLoadsPublishOnePrivateIdentityWithoutChangingDataDirectoryAcl() {
        if(!OperatingSystem.IsWindows()) return;
        var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        const AccessControlSections comparedSections=AccessControlSections.Access|AccessControlSections.Owner;
        var originalAcl=new DirectoryInfo(dir).GetAccessControl(comparedSections).GetSecurityDescriptorSddlForm(comparedSections);
        DeviceIdentity[] identities=[];
        try {
            identities=await Task.WhenAll(Enumerable.Range(0,16).Select(_=>Task.Run(()=>DeviceIdentity.LoadOrCreate(dir))));
            Assert.Single(identities.Select(x=>x.MachineId).Distinct());
            Assert.Single(identities.Select(x=>x.PublicKey).Distinct());
            Assert.Equal(originalAcl,new DirectoryInfo(dir).GetAccessControl(comparedSections).GetSecurityDescriptorSddlForm(comparedSections));
            var identityDirectory=Path.Combine(dir,"identity");
            var identityPath=Path.Combine(identityDirectory,"identity.json");
            Assert.True(File.Exists(identityPath));
            Assert.False(File.Exists(Path.Combine(dir,"identity.json")));
            Assert.DoesNotContain(Directory.EnumerateFiles(identityDirectory),x=>Path.GetFileName(x).EndsWith(".tmp",StringComparison.OrdinalIgnoreCase));
            var serviceSid=WindowsIdentity.GetCurrent().User!;
            var directoryAcl=new DirectoryInfo(identityDirectory).GetAccessControl(AccessControlSections.Access|AccessControlSections.Owner);
            var fileAcl=new FileInfo(identityPath).GetAccessControl(AccessControlSections.Access|AccessControlSections.Owner);
            Assert.True(directoryAcl.AreAccessRulesProtected);
            Assert.True(fileAcl.AreAccessRulesProtected);
            Assert.Equal(serviceSid,directoryAcl.GetOwner(typeof(SecurityIdentifier)));
            Assert.Equal(serviceSid,fileAcl.GetOwner(typeof(SecurityIdentifier)));
        } finally {
            foreach(var identity in identities) identity.Dispose();
            Directory.Delete(dir,true);
        }
    }
}
