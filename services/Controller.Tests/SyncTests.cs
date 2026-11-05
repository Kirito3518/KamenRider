using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using System.Security.Cryptography;
namespace Controller.Tests;
public class SyncTests {
 sealed class Clock:TimeProvider {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
 [Theory][InlineData("")][InlineData("1.2.3\nready")][InlineData("1/2/3")]
 public void HeartbeatRejectsInvalidAgentVersion(string version) {
 var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
 try {using var db=new ControllerDatabase(path);using var key=ECDsa.Create();db.EnrollWithTicket(new("pc",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2));Assert.Throws<ArgumentException>(()=>db.Sync("pc",new SyncRequest("boot","192.0.2.2","001122334455",1,"notReady",[]){AgentVersion=version}));}
 finally {File.Delete(path);}
 }
 [Fact] public void HeartbeatExpiresAndCannotOverwriteWebConfiguration() {
 var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");var clock=new Clock();
 using(var db=new ControllerDatabase(path,clock:clock)) {using var key=ECDsa.Create();db.EnrollWithTicket(new("pc",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2));
 db.Update("pc",new("PC",500,5000,6000),1);
 var response=db.Sync("pc",new SyncRequest("boot","192.0.2.2","001122334455",1,"ready",[]) { AgentVersion="1.2.3" });
 Assert.Equal(5000,response.Configuration.MoonlightPort);Assert.Empty(response.Commands);
 var machine=Assert.Single(db.List());Assert.Equal("ready",machine.Readiness);Assert.False(machine.CanRent);Assert.Equal("boot",machine.BootId);Assert.Equal("1.2.3",machine.AgentVersion);
 db.Sync("pc",new("boot","192.0.2.2","001122334455",1,"ready",[]));
 Assert.Equal("1.2.3",Assert.Single(db.List()).AgentVersion);
 clock.Now=clock.Now.AddSeconds(21);Assert.Equal("unreachable",Assert.Single(db.List()).Readiness);
 }
 using(var db=new ControllerDatabase(path,clock:clock)){var machine=Assert.Single(db.List());Assert.Equal("unreachable",machine.Readiness);Assert.Equal("1.2.3",machine.AgentVersion);}
 File.Delete(path);
 }
}
