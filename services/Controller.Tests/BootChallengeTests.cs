using KamenRaider.Contracts;
namespace Controller.Tests;
public class BootChallengeTests {
 [Fact] public async Task ProofFlagsWithoutControllerChallengeCannotResumeLease() {
  using var f=new LeaseFixture();await f.Active();Assert.Equal("Hold",f.Db.Reconcile("pc",new("new",3,true,true,true,true)).Action);
 }
 internal static ReconciliationChallenge Issue(LeaseFixture f,string boot="new")=>Assert.IsType<ReconciliationChallenge>(f.Db.Reconcile("pc",new(boot,3,true)).Challenge);
 internal static ReconcileResponse Prove(LeaseFixture f,ReconciliationChallenge challenge,string? boot=null)=>f.Db.Reconcile("pc",new(boot??challenge.BootId,3,true,true,true,true,challenge));
 [Fact] public async Task DelayedProofForUnallocatedMachineCannotFailNewAllocation() {
  using var f=new LeaseFixture();var challenge=Issue(f);var lease=await f.Create();
  Assert.Equal("Hold",Prove(f,challenge).Action);Assert.Equal("preparing",f.Db.GetLease(lease.Id)!.State);
  await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Create("another"));
 }
 [Fact] public async Task ReplayedProofCannotReleaseReplacementAllocation() {
  using var f=new LeaseFixture();await f.Active();f.Time.Now=f.Time.Now.AddMinutes(2);f.Sync();var challenge=Issue(f);
  Assert.Equal("PrepareAvailable",Prove(f,challenge).Action);f.Sync("new");var next=await f.Create("next");
  Assert.Equal("Hold",Prove(f,challenge).Action);Assert.Equal("preparing",f.Db.GetLease(next.Id)!.State);
  await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Create("third"));
 }
 [Fact] public async Task ChangedLeaseRevisionRejectsOldProof() {
  using var f=new LeaseFixture();var lease=await f.Active();var challenge=Issue(f);
  await f.Leases.ExtendLeaseAsync(lease.Id,30,1,"extension",default);var apply=Assert.Single(f.Sync().Commands);f.Sync("boot",new CommandResult(apply.CommandId,"applied",2));
  Assert.Equal("Hold",Prove(f,challenge).Action);Assert.Equal(2,f.Db.GetLease(lease.Id)!.Revision);Assert.Equal("boot",Assert.Single(f.Db.List()).BootId);
 }
 [Theory][InlineData(true)][InlineData(false)] public async Task ExpiredOrWrongBootChallengeCannotChangeLease(bool expired) {
  using var f=new LeaseFixture();var lease=await f.Active();var challenge=Issue(f);
  if(expired)f.Time.Now=f.Time.Now.AddSeconds(30);
  Assert.Equal("Hold",Prove(f,challenge,expired?null:"wrong-boot").Action);Assert.Equal("active",f.Db.GetLease(lease.Id)!.State);Assert.Equal("boot",Assert.Single(f.Db.List()).BootId);
 }
 [Fact] public async Task ChallengeSurvivesControllerRestartButOnlyOneSubmissionCanSucceed() {
  using var f=new LeaseFixture();var lease=await f.Active();var challenge=Issue(f);f.Reopen();
  Assert.Equal("ResumeLease",Prove(f,challenge).Action);Assert.Equal("Hold",Prove(f,challenge).Action);Assert.Equal(lease.ExpiresAtUtc,f.Db.GetLease(lease.Id)!.ExpiresAtUtc);
 }
}
