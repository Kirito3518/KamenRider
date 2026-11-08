using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using System.Security.Cryptography;
namespace Controller.Tests;
public class SyncTests {
 sealed class Clock:TimeProvider {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
 [Fact] public void CanonicalReadinessDiagnosticIdsAreImmutableAndStable() {
 var expected=new[] { "baseline","bootChallenge","restoration","streamFence","desktopHealth","configurationRevision","leaseReconciliation","runtimeHealth" };
 Assert.Equal(expected,ReadinessDiagnosticIds.Canonical);
 Assert.IsNotType<string[]>(ReadinessDiagnosticIds.Canonical);
 var ids=Assert.IsAssignableFrom<IList<string>>(ReadinessDiagnosticIds.Canonical);
 Assert.True(ids.IsReadOnly);
 Assert.Throws<NotSupportedException>(()=>ids[0]="changed");
 }
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

 [Fact] public void HeartbeatCannotAcknowledgeAConfigurationAheadOfTheController() {
 using var f=new LeaseFixture();var before=Assert.Single(f.Db.List());Assert.True(before.CanRent);
 Assert.Throws<ArgumentException>(()=>f.Db.Sync("pc",new("boot","192.0.2.1","001122334455",before.ConfigRevision+1,"ready",[])));
 var updated=f.Db.Update("pc",new("PC",500,5000,6000),before.ConfigRevision);
 Assert.False(updated.CanRent);
 f.Db.Sync("pc",new("boot","192.0.2.1","001122334455",updated.ConfigRevision,"ready",[]));
 Assert.True(f.Db.List().Single().CanRent);
 }

 [Fact] public void HeartbeatValidatesDiagnosticsAndPersistsThemInCanonicalOrderThenClearsThemWhenOmitted() {
 using var f=new LeaseFixture();
 var input=ReadinessDiagnosticIds.Canonical.Reverse().Select(id=>new ReadinessDiagnostic(id,ReadinessDiagnosticStatuses.Passed)).ToArray();
 f.Db.Sync("pc",new SyncRequest("boot","192.0.2.1","001122334455",f.Db.List().Single().ConfigRevision,"ready",[]) { ReadinessDiagnostics=input });
 var stored=Assert.Single(f.Db.List());
 Assert.Equal(ReadinessDiagnosticIds.Canonical,stored.ReadinessDiagnostics!.Select(item=>item.CheckId).ToArray());
 Assert.True(stored.CanRent);

 f.Db.Sync("pc",new SyncRequest("boot","192.0.2.1","001122334455",f.Db.List().Single().ConfigRevision,"ready",[]) { ReadinessDiagnostics=[new("runtimeHealth",ReadinessDiagnosticStatuses.Blocked)] });
 var replacement=Assert.Single(f.Db.List());
 Assert.Equal([new ReadinessDiagnostic("runtimeHealth",ReadinessDiagnosticStatuses.Blocked)],replacement.ReadinessDiagnostics!);
 Assert.True(replacement.CanRent);

 f.Db.Sync("pc",new SyncRequest("boot","192.0.2.1","001122334455",f.Db.List().Single().ConfigRevision,"notReady",[]) { ReadinessDiagnostics=input });
 Assert.False(Assert.Single(f.Db.List()).CanRent);
 f.Db.Sync("pc",new("boot","192.0.2.1","001122334455",f.Db.List().Single().ConfigRevision,"notReady",[]));
 var oldAgent=Assert.Single(f.Db.List());
 Assert.Null(oldAgent.ReadinessDiagnostics);
 Assert.False(oldAgent.CanRent);
 }

 [Theory]
 [MemberData(nameof(InvalidDiagnostics))]
 public void HeartbeatRejectsMalformedDiagnostics(ReadinessDiagnostic[] diagnostics) {
 using var f=new LeaseFixture();
 Assert.Throws<ArgumentException>(()=>f.Db.Sync("pc",new SyncRequest("boot","192.0.2.1","001122334455",f.Db.List().Single().ConfigRevision,"notReady",[]) { ReadinessDiagnostics=diagnostics }));
 Assert.Null(Assert.Single(f.Db.List()).ReadinessDiagnostics);
 }

 public static IEnumerable<object[]> InvalidDiagnostics() =>
 [
  [new ReadinessDiagnostic[] { null! }],
  [new[] { new ReadinessDiagnostic("futureCheck",ReadinessDiagnosticStatuses.Passed) }],
  [new[] { new ReadinessDiagnostic(ReadinessDiagnosticIds.Baseline,"futureStatus") }],
  [new[] { new ReadinessDiagnostic(ReadinessDiagnosticIds.Baseline,ReadinessDiagnosticStatuses.Passed),new ReadinessDiagnostic(ReadinessDiagnosticIds.Baseline,ReadinessDiagnosticStatuses.Blocked) }],
  [Enumerable.Range(0,9).Select(_=>new ReadinessDiagnostic(ReadinessDiagnosticIds.Baseline,ReadinessDiagnosticStatuses.Passed)).ToArray()],
 ];
}
