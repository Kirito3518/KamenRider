using KamenRaider.Agent.Startup;
using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Configuration;
using KamenRaider.Contracts;
namespace Agent.Tests;
public class BootTests {
 [Theory][InlineData(false,true,true)][InlineData(true,false,true)][InlineData(true,true,false)]
 public async Task MissingRestorationFenceOrHealthCannotReportReady(bool restored,bool cleaned,bool healthy) {
  using var f=new Fixture();f.Environment.Restored=restored;f.Environment.Cleaned=cleaned;f.Environment.Healthy=healthy;
  Assert.Equal(Readiness.NotReady,await f.Run());Assert.False(f.Client.Last!.RestorationVerified);
 }
 [Fact] public async Task OfflineBootClosesGateAndCannotReportReady() {
  using var f=new Fixture();f.Client.Offline=true;Assert.Equal(Readiness.NotReady,await f.Run());Assert.True(f.Gate.Blocked);
 }
 [Fact] public async Task CurrentConfigurationMustBeAppliedBeforeProofSubmission() {
  using var f=new Fixture();Assert.Equal(Readiness.Ready,await f.Run());Assert.Equal(7,f.Client.Last!.AppliedConfigRevision);Assert.Equal("os-boot",f.Client.Last.BootId);Assert.True(f.Client.Last.FencedCleanupVerified);
 }
 [Fact] public async Task SameBootServiceRestartStillRequiresIndependentRestorationProof() {
  using var f=new Fixture();Assert.Equal(Readiness.Ready,await f.Run());f.Environment.Restored=false;Assert.Equal(Readiness.NotReady,await f.Run());
 }
 [Fact] public async Task ReadyIsRevokedWhenRuntimeHealthFalls() {
  using var f=new Fixture();Assert.Equal(Readiness.Ready,await f.Run());var before=f.Gate.Calls;f.Environment.RuntimeHealthy=false;Assert.Equal(Readiness.NotReady,await f.Reconciler.RevalidateRuntimeAsync(default));Assert.True(f.Gate.Calls>before);f.Environment.RuntimeHealthy=true;Assert.Equal(Readiness.Ready,await f.Reconciler.RevalidateRuntimeAsync(default));
 }
 [Fact] public async Task GateFailureInvalidatesRuntimeEvidenceUntilReconciliationSucceeds() {
  using var f=new Fixture();Assert.Equal(Readiness.Ready,await f.Run());f.Environment.RuntimeHealthy=false;f.Gate.Fail=true;Assert.Equal(Readiness.Fault,await f.Reconciler.RevalidateRuntimeAsync(default));Assert.False(f.Reconciler.HasRuntimeEvidence);f.Environment.RuntimeHealthy=true;f.Gate.Fail=false;Assert.Equal(Readiness.NotReady,await f.Reconciler.RevalidateRuntimeAsync(default));Assert.False(f.Reconciler.HasRuntimeEvidence);
 }
 [Fact] public async Task BootChangeInvalidatesRuntimeEvidenceAndClosesGate() {
  using var f=new Fixture();Assert.Equal(Readiness.Ready,await f.Run());var before=f.Gate.Calls;f.Clock.BootId="next-boot";Assert.Equal(Readiness.NotReady,await f.Reconciler.RevalidateRuntimeAsync(default));Assert.False(f.Reconciler.HasRuntimeEvidence);Assert.True(f.Gate.Calls>before);
 }
 [Fact] public async Task ConfigurationChangeRequiresFreshBootProof() {
  using var f=new Fixture();Assert.Equal(Readiness.Ready,await f.Run());var before=f.Gate.Calls;await f.Configuration.ApplyAsync(new(8,6001,6002),default);Assert.Equal(Readiness.NotReady,await f.Reconciler.RevalidateRuntimeAsync(default));Assert.False(f.Reconciler.HasRuntimeEvidence);Assert.True(f.Gate.Calls>before);
 }
 [Fact] public async Task ResumeCannotBecomeReadyUntilLeaseIsDurablyRestored() {
  using var f=new Fixture();f.Client.Resume=true;Assert.Equal(Readiness.NotReady,await f.Run());f.Environment.LeaseRestored=true;Assert.Equal(Readiness.Ready,await f.Run());
 }
 [Fact] public async Task LeaseExpiryDuringImportCannotReportReady() {
  using var f=new Fixture();f.Client.Resume=true;f.Environment.LeaseRestored=true;f.Environment.OnRestore=()=>f.Clock.Uptime=120010;
  Assert.Equal(Readiness.NotReady,await f.Run());
 }
 [Fact] public async Task MissingControllerChallengeCannotSubmitProofOrBecomeReady() {
  using var f=new Fixture();f.Client.NoChallenge=true;Assert.Equal(Readiness.NotReady,await f.Run());Assert.False(f.Client.Last!.HealthVerified);
 }
 [Fact] public async Task ProofIsBoundToExactControllerChallenge() {
  using var f=new Fixture();Assert.Equal(Readiness.Ready,await f.Run());Assert.Equal("generation-1",f.Client.Last!.Challenge?.Token);Assert.Equal("allocation-1",f.Client.Last.Challenge?.AllocationId);Assert.Equal(4,f.Client.Last.Challenge?.LeaseRevision);
 }
 sealed class Fixture:IDisposable {
  readonly string path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());public Clock Clock=new();public Gate Gate=new();public Client Client=new();public Environment Environment=new();public AppliedConfiguration Configuration;public BootReconciler Reconciler;
  public Fixture(){Directory.CreateDirectory(path);Configuration=new(path);Reconciler=new BootReconciler(Clock,Gate,Configuration,Client,Environment,true);}
  public Task<Readiness> Run()=>Reconciler.ReconcileAsync(default);
  public void Dispose()=>Directory.Delete(path,true);
 }
 sealed class Clock:IBootClock {public string BootId{get;set;}="os-boot";public long Uptime=10;public long UptimeMilliseconds=>Uptime;}
 sealed class Gate:IStreamGate {public bool Blocked;public bool Fail;public int Calls;public Task BlockAsync(CancellationToken ct){Calls++;if(Fail)return Task.FromException(new IOException("Stream gate unavailable"));Blocked=true;return Task.CompletedTask;}}
 sealed class Client:IBootReconciliationClient {
  public bool Offline,Resume,NoChallenge;public ReconcileRequest? Last;
  public Task<ReconcileResponse> ReconcileAsync(ReconcileRequest request,CancellationToken ct){Last=request;if(Offline)throw new HttpRequestException();return Task.FromResult(new ReconcileResponse(DateTimeOffset.UtcNow,new(7,5000,6000),Resume?new("rental",1,"os-boot",DateTimeOffset.UtcNow.AddMinutes(1)):null,request.HealthVerified?(Resume?"ResumeLease":"PrepareAvailable"):"Hold",NoChallenge?null:new("generation-1","os-boot","allocation-1",4,DateTimeOffset.UtcNow.AddSeconds(30))));}
 }
 sealed class Environment:IBootEnvironment {
  public bool Restored=true,Cleaned=true,Healthy=true,RuntimeHealthy=true,LeaseRestored;public Action? OnRestore;
  public Task<bool> VerifyRestorationAsync(ReconciliationChallenge challenge,CancellationToken ct)=>Task.FromResult(Restored);
  public Task<bool> FenceAndClearAsync(ReconciliationChallenge challenge,CancellationToken ct)=>Task.FromResult(Cleaned);
  public Task<bool> VerifyHealthAsync(ReconciliationChallenge challenge,CancellationToken ct)=>Task.FromResult(Healthy);
  public Task<bool> VerifyRuntimeHealthAsync(string bootId,long configurationRevision,CancellationToken ct)=>Task.FromResult(RuntimeHealthy);
  public Task<bool> RestoreLeaseAsync(ActiveLeaseConfirmation lease,DateTimeOffset controllerTime,long requestStartedUptime,CancellationToken ct){OnRestore?.Invoke();return Task.FromResult(LeaseRestored);}
 }
}
