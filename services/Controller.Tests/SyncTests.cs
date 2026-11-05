using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using System.Security.Cryptography;
namespace Controller.Tests;
public class SyncTests {
 sealed class Clock:TimeProvider {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
 [Fact] public void HeartbeatExpiresAndCannotOverwriteWebConfiguration() {
 var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");var clock=new Clock();
 using(var db=new ControllerDatabase(path,clock:clock)) {using var key=ECDsa.Create();db.EnrollWithTicket(new("pc",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2));
 db.Update("pc",new("PC",500,5000,6000),1);
 var response=db.Sync("pc",new("boot","192.0.2.2","001122334455",1,"ready",[]));
 Assert.Equal(5000,response.Configuration.MoonlightPort);Assert.Empty(response.Commands);
 var machine=Assert.Single(db.List());Assert.Equal("ready",machine.Readiness);Assert.False(machine.CanRent);Assert.Equal("boot",machine.BootId);
 clock.Now=clock.Now.AddSeconds(21);Assert.Equal("unreachable",Assert.Single(db.List()).Readiness);
 }
 using(var db=new ControllerDatabase(path,clock:clock)) Assert.Equal("unreachable",Assert.Single(db.List()).Readiness);
 File.Delete(path);
 }
}
