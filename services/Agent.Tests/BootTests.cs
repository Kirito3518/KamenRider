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
 [Fact] public async Task ResumeCannotBecomeReadyUntilLeaseIsDurablyRestored() {
  using var f=new Fixture();f.Client.Resume=true;Assert.Equal(Readiness.NotReady,await f.Run());f.Environment.LeaseRestored=true;Assert.Equal(Readiness.Ready,await f.Run());
 }
 [Fact] public async Task LeaseExpiryDuringImportCannotReportReady() {
  using var f=new Fixture();f.Client.Resume=true;f.Environment.LeaseRestored=true;f.Environment.OnRestore=()=>f.Clock.Uptime=120010;
  Assert.Equal(Readiness.NotReady,await f.Run());
 }
 sealed class Fixture:IDisposable {
  readonly string path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());public Clock Clock=new();public Gate Gate=new();public Client Client=new();public Environment Environment=new();public AppliedConfiguration Configuration;
  public Fixture(){Directory.CreateDirectory(path);Configuration=new(path);}
  public Task<Readiness> Run()=>new BootReconciler(Clock,Gate,Configuration,Client,Environment,true).ReconcileAsync(default);
  public void Dispose()=>Directory.Delete(path,true);
 }
 sealed class Clock:IBootClock {public string BootId=>"os-boot";public long Uptime=10;public long UptimeMilliseconds=>Uptime;}
 sealed class Gate:IStreamGate {public bool Blocked;public Task BlockAsync(CancellationToken ct){Blocked=true;return Task.CompletedTask;}}
 sealed class Client:IBootReconciliationClient {
  public bool Offline,Resume;public ReconcileRequest? Last;
  public Task<ReconcileResponse> ReconcileAsync(ReconcileRequest request,CancellationToken ct){Last=request;if(Offline)throw new HttpRequestException();return Task.FromResult(new ReconcileResponse(DateTimeOffset.UtcNow,new(7,5000,6000),Resume?new("rental",1,"os-boot",DateTimeOffset.UtcNow.AddMinutes(1)):null,request.HealthVerified?(Resume?"ResumeLease":"PrepareAvailable"):"Hold"));}
 }
 sealed class Environment:IBootEnvironment {
  public bool Restored=true,Cleaned=true,Healthy=true,LeaseRestored;public Action? OnRestore;
  public Task<bool> VerifyRestorationAsync(string bootId,CancellationToken ct)=>Task.FromResult(Restored);
  public Task<bool> FenceAndClearAsync(CancellationToken ct)=>Task.FromResult(Cleaned);
  public Task<bool> VerifyHealthAsync(CancellationToken ct)=>Task.FromResult(Healthy);
  public Task<bool> RestoreLeaseAsync(ActiveLeaseConfirmation lease,DateTimeOffset controllerTime,long requestStartedUptime,CancellationToken ct){OnRestore?.Invoke();return Task.FromResult(LeaseRestored);}
 }
}
