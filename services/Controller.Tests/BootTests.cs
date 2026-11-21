using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
namespace Controller.Tests;
public class BootTests {
 [Theory][InlineData("notReady")][InlineData("unknown")]
 public async Task BlockedHeartbeatsPreserveActiveLeaseUntilVerifiedBootTransition(string readiness) {
  using var f=new LeaseFixture();var lease=await f.Active();await f.Leases.ExtendLeaseAsync(lease.Id,30,1,"extension",default);
  var challenge=BootChallengeTests.Issue(f);
  for(var i=0;i<2;i++) {
   var heartbeat=f.Db.Sync("pc",new("new","192.0.2.2","001122334455",3,readiness,[]));
   Assert.Empty(heartbeat.Commands);Assert.Null(heartbeat.ActiveLease);
   var retained=f.Db.GetLease(lease.Id)!;Assert.Equal("active",retained.State);Assert.Equal(2,retained.PendingRevision);Assert.Equal(lease.ExpiresAtUtc,retained.ExpiresAtUtc);
   var machine=Assert.Single(f.Db.List());Assert.Equal("new",machine.BootId);Assert.Equal("192.0.2.2",machine.LocalIp);Assert.False(machine.CanRent);
  }
  var resumed=BootChallengeTests.Prove(f,challenge);
  Assert.Equal("ResumeLease",resumed.Action);Assert.Equal(lease.ExpiresAtUtc,resumed.Lease!.ExpiresAtUtc);Assert.Equal(1,resumed.Lease.LeaseRevision);Assert.Equal("new",resumed.Lease.BootId);
  f.Reopen();Assert.Throws<ArgumentException>(()=>f.Db.Sync("pc",new("boot","192.0.2.1","001122334455",3,readiness,[])));
 }
 [Theory][InlineData("notReady")][InlineData("unknown")]
 public async Task BlockedHeartbeatCannotApplyFailureResultsOrReturnLeaseCommands(string readiness) {
  using var f=new LeaseFixture();var lease=await f.Active();await f.Leases.ExtendLeaseAsync(lease.Id,30,1,"extension",default);var extension=Assert.Single(f.Sync().Commands);
  var heartbeat=f.Db.Sync("pc",new("boot","192.0.2.1","001122334455",3,readiness,[new(extension.CommandId,"failed",2)]));
  Assert.Empty(heartbeat.Commands);Assert.Null(heartbeat.ActiveLease);Assert.Equal("active",f.Db.GetLease(lease.Id)!.State);Assert.Equal(extension.CommandId,Assert.Single(f.Sync().Commands).CommandId);
 }
 [Fact] public async Task VerifiedNewBootResumesOnlyCommittedExpiryAndInvalidatesOldCommands() {
  using var f=new LeaseFixture();var lease=await f.Active();await f.Leases.ExtendLeaseAsync(lease.Id,30,1,"extension",default);
  var result=BootChallengeTests.Prove(f,BootChallengeTests.Issue(f));
  Assert.Equal("ResumeLease",result.Action);Assert.Equal(lease.ExpiresAtUtc,result.Lease!.ExpiresAtUtc);Assert.Equal(1,result.Lease.LeaseRevision);Assert.Equal("new",result.Lease.BootId);
  Assert.Empty(f.Sync("new").Commands);Assert.Null(f.Db.GetLease(lease.Id)!.PendingRevision);
  Assert.Throws<ArgumentException>(()=>f.Sync("boot"));f.Reopen();Assert.Equal(lease.ExpiresAtUtc,BootChallengeTests.Prove(f,BootChallengeTests.Issue(f)).Lease!.ExpiresAtUtc);
 }
 [Theory][InlineData(false,3)][InlineData(true,2)]
 public async Task MissingFencedCleanupOrStaleConfigurationHoldsAllocation(bool cleanup,long revision) {
  using var f=new LeaseFixture();var lease=await f.Active();
  var challenge=BootChallengeTests.Issue(f);
  Assert.Equal("Hold",f.Db.Reconcile("pc",new(challenge.BootId,revision,true,true,cleanup,true,challenge,StreamReady:true)).Action);
  await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Create("another"));Assert.Equal("active",f.Db.GetLease(lease.Id)!.State);
 }
 [Fact] public async Task StreamReadyAndFencedCleanupAcceptBootWithoutAdvisoryEvidence() {
  using var f=new LeaseFixture();var lease=await f.Active();
  var challenge=BootChallengeTests.Issue(f);
  // Baseline, restoration and desktop-health evidence remain advisory; 0jay-style readiness
  // is pairing cleanup plus a live Sunshine stream.
  var result=f.Db.Reconcile("pc",new(challenge.BootId,3,false,false,true,false,challenge,StreamReady:true));
  Assert.Equal("ResumeLease",result.Action);
  Assert.Equal(lease.Id,result.Lease!.RentalId);
 }
 [Fact] public async Task MissingStreamReadyHoldsAllocationEvenWithFencedCleanup() {
  using var f=new LeaseFixture();var lease=await f.Active();
  var challenge=BootChallengeTests.Issue(f);
  Assert.Equal("Hold",BootChallengeTests.Prove(f,challenge,streamReady:false).Action);
  Assert.Equal("active",f.Db.GetLease(lease.Id)!.State);
 }
 [Fact] public async Task ExpiredLeaseIsNeverResumedAndCleanupReleasesAllocationWithoutShutdownCommand() {
  using var f=new LeaseFixture();var lease=await f.Active();f.Time.Now=f.Time.Now.AddSeconds(60);
  var result=BootChallengeTests.Prove(f,BootChallengeTests.Issue(f));Assert.Equal("PrepareAvailable",result.Action);Assert.Null(result.Lease);
  Assert.Empty(f.Sync("new").Commands);Assert.Equal("completed",f.Db.GetLease(lease.Id)!.State);f.Time.Now=f.Time.Now.AddSeconds(31);await f.Create("next");
 }
 [Fact] public async Task PreparingLeaseCannotBeResumedAfterReboot() {
  using var f=new LeaseFixture();var lease=await f.Create();var result=BootChallengeTests.Prove(f,BootChallengeTests.Issue(f));
  Assert.Equal("PrepareAvailable",result.Action);Assert.Null(result.Lease);Assert.Equal("failed",f.Db.GetLease(lease.Id)!.State);Assert.Empty(f.Sync("new").Commands);
 }
}
