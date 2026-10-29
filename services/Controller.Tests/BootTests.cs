using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
namespace Controller.Tests;
public class BootTests {
 [Fact] public async Task VerifiedNewBootResumesOnlyCommittedExpiryAndInvalidatesOldCommands() {
  using var f=new LeaseFixture();var lease=await f.Active();await f.Leases.ExtendLeaseAsync(lease.Id,30,1,"extension",default);
  var result=BootChallengeTests.Prove(f,BootChallengeTests.Issue(f));
  Assert.Equal("ResumeLease",result.Action);Assert.Equal(lease.ExpiresAtUtc,result.Lease!.ExpiresAtUtc);Assert.Equal(1,result.Lease.LeaseRevision);Assert.Equal("new",result.Lease.BootId);
  Assert.Empty(f.Sync("new").Commands);Assert.Null(f.Db.GetLease(lease.Id)!.PendingRevision);
  Assert.Throws<ArgumentException>(()=>f.Sync("boot"));f.Reopen();Assert.Equal(lease.ExpiresAtUtc,BootChallengeTests.Prove(f,BootChallengeTests.Issue(f)).Lease!.ExpiresAtUtc);
 }
 [Theory][InlineData(false,true,true,true,3)][InlineData(true,false,true,true,3)][InlineData(true,true,false,true,3)][InlineData(true,true,true,false,3)][InlineData(true,true,true,true,2)]
 public async Task MissingProofOrStaleConfigurationHoldsAllocation(bool baseline,bool restoration,bool cleanup,bool health,long revision) {
  using var f=new LeaseFixture();var lease=await f.Active();f.Time.Now=f.Time.Now.AddMinutes(2);
  Assert.Equal("Hold",f.Db.Reconcile("pc",new("new",revision,baseline,restoration,cleanup,health)).Action);
  await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Create("another"));Assert.Equal("ending",f.Db.GetLease(lease.Id)!.State);
 }
 [Fact] public async Task ExpiredLeaseIsNeverResumedAndCleanupReleasesAllocationWithoutShutdownCommand() {
  using var f=new LeaseFixture();var lease=await f.Active();f.Time.Now=f.Time.Now.AddSeconds(60);
  var result=BootChallengeTests.Prove(f,BootChallengeTests.Issue(f));Assert.Equal("PrepareAvailable",result.Action);Assert.Null(result.Lease);
  Assert.Empty(f.Sync("new").Commands);Assert.Equal("ending",f.Db.GetLease(lease.Id)!.State);await f.Create("next");
 }
 [Fact] public async Task PreparingLeaseCannotBeResumedAfterReboot() {
  using var f=new LeaseFixture();var lease=await f.Create();var result=BootChallengeTests.Prove(f,BootChallengeTests.Issue(f));
  Assert.Equal("PrepareAvailable",result.Action);Assert.Null(result.Lease);Assert.Equal("failed",f.Db.GetLease(lease.Id)!.State);Assert.Empty(f.Sync("new").Commands);
 }
}
