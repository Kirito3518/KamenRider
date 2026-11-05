using KamenRaider.Agent.Identity;
using KamenRaider.Controller.Security;
using System.Security.AccessControl;
namespace Agent.Tests;
public class IdentityTests
{
    [Fact] public void IdentityPersistsAndSignsBodyWithReplayProtection() {
        var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
        string id, key;
        using(var identity=DeviceIdentity.LoadOrCreate(dir)) { id=identity.MachineId; key=identity.PublicKey; }
        using(var identity=DeviceIdentity.LoadOrCreate(dir)) {
            Assert.Equal(id,identity.MachineId); Assert.Equal(key,identity.PublicKey);
            var auth=new AgentAuthentication(); var now=DateTimeOffset.UtcNow;
            var signature=identity.Sign("POST","/agent/heartbeat",now,"nonce","body");
            Assert.False(auth.Verify(id,key,"POST","/agent/heartbeat",now,"nonce","tampered",signature,now));
            Assert.True(auth.Verify(id,key,"POST","/agent/heartbeat",now,"nonce","body",signature,now));
            Assert.False(auth.Verify(id,key,"POST","/agent/heartbeat",now,"nonce","body",signature,now));
            Assert.False(new AgentAuthentication().Verify(id,key,"POST","/agent/heartbeat",now,"nonce","body",signature,now.AddSeconds(61)));
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
            Assert.True(File.Exists(Path.Combine(identityDirectory,"identity.json")));
            Assert.False(File.Exists(Path.Combine(dir,"identity.json")));
            Assert.DoesNotContain(Directory.EnumerateFiles(identityDirectory),x=>Path.GetFileName(x).EndsWith(".tmp",StringComparison.OrdinalIgnoreCase));
        } finally {
            foreach(var identity in identities) identity.Dispose();
            Directory.Delete(dir,true);
        }
    }
}
