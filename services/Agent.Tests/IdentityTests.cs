using KamenRaider.Agent.Identity;
using KamenRaider.Controller.Security;
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
}
