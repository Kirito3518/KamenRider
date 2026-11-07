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
  using var f=new Fixture();f.Client.Resume=true;Assert.Equal(Readiness.NotReady,await f.Run());f.Importer.Result=true;Assert.Equal(Readiness.Ready,await f.Run());
  Assert.Equal(2,f.Importer.Calls);Assert.Equal(f.Client.LastResponse!.Lease,f.Importer.Lease);Assert.Equal(f.Client.LastResponse.ServerTimeUtc,f.Importer.ControllerTime);Assert.Equal(f.Clock.UptimeMilliseconds,f.Importer.RequestStartedUptime);
 }
 [Fact] public async Task LeaseExpiryDuringImportCannotReportReady() {
  using var f=new Fixture();f.Client.Resume=true;f.Importer.Result=true;f.Importer.OnImport=()=>f.Clock.Uptime=120010;
  Assert.Equal(Readiness.NotReady,await f.Run());
 }
 [Fact] public async Task AvailableMachineDoesNotImportAnActiveLease() {
  using var f=new Fixture();Assert.Equal(Readiness.Ready,await f.Run());Assert.Equal(0,f.Importer.Calls);Assert.Equal(1,f.Importer.ClearCalls);Assert.Equal(f.Clock.BootId,f.Importer.ClearedBootId);
 }
 [Fact] public async Task AvailableMachineCannotBecomeReadyUntilOldLeaseSnapshotIsCleared() {
  using var f=new Fixture();f.Importer.ClearResult=false;Assert.Equal(Readiness.NotReady,await f.Run());Assert.Equal(1,f.Importer.ClearCalls);Assert.Equal(0,f.Importer.Calls);
 }
 [Theory][InlineData(true,false)][InlineData(false,true)] public async Task ResumeLeaseMustMatchAllocationAndRevisionBoundToChallenge(bool mismatchAllocation,bool mismatchRevision) {
  using var f=new Fixture();f.Client.Resume=true;f.Client.MismatchAllocation=mismatchAllocation;f.Client.MismatchRevision=mismatchRevision;f.Importer.Result=true;
  Assert.Equal(Readiness.NotReady,await f.Run());Assert.Equal(0,f.Importer.Calls);
 }
 [Theory][InlineData(false,true,true)][InlineData(true,false,true)][InlineData(true,true,false)] public async Task ActiveLeaseIsNotImportedBeforeAllBootEvidencePasses(bool restored,bool cleaned,bool healthy) {
  using var f=new Fixture();f.Client.Resume=true;f.Environment.Restored=restored;f.Environment.Cleaned=cleaned;f.Environment.Healthy=healthy;f.Importer.Result=true;
  Assert.Equal(Readiness.NotReady,await f.Run());Assert.Equal(0,f.Importer.Calls);
 }
  [Fact] public async Task FencedPairingCleanupCompletesBeforeAResumedLeaseIsImported() {
   using var f=new Fixture();f.Client.Resume=true;f.Importer.Result=true;
   Assert.Equal(Readiness.Ready,await f.Run());
   Assert.True(f.Events.IndexOf("pairing-and-client-cleanup")<f.Events.IndexOf("lease-import"));
  }
 [Fact] public async Task MissingControllerChallengeCannotSubmitProofOrBecomeReady() {
  using var f=new Fixture();f.Client.NoChallenge=true;Assert.Equal(Readiness.NotReady,await f.Run());Assert.False(f.Client.Last!.HealthVerified);
  Assert.Equal("blocked", Diagnostic(f.Reconciler, "bootChallenge"));
  Assert.Equal("unknown", Diagnostic(f.Reconciler, "restoration"));
 }
 [Theory][InlineData(true,false)][InlineData(false,true)] public async Task InvalidControllerChallengeIsReportedAsBlocked(bool expired,bool wrongBoot) {
  using var f=new Fixture();f.Client.ExpiredChallenge=expired;f.Client.MismatchedBoot=wrongBoot;
  Assert.Equal(Readiness.NotReady,await f.Run());
  Assert.Equal("blocked", Diagnostic(f.Reconciler, "bootChallenge"));
  Assert.Equal("unknown", Diagnostic(f.Reconciler, "restoration"));
 }
 [Fact] public async Task UnavailableBaselineIsReportedWithoutOpeningTheGate() {
  using var f=new Fixture(baselineValidated:false);
  Assert.Equal(Readiness.NotReady,await f.Run());
  Assert.True(f.Gate.Blocked);
  Assert.Equal("unavailable", Diagnostic(f.Reconciler, "baseline"));
  Assert.Equal("passed", Diagnostic(f.Reconciler, "bootChallenge"));
  Assert.Equal("unknown", Diagnostic(f.Reconciler, "restoration"));
 }
 [Theory][InlineData(false,true,true,"restoration")][InlineData(true,false,true,"streamFence")][InlineData(true,true,false,"desktopHealth")]
 public async Task FailedEvidenceReportsOnlyTheFirstBlockingCheck(bool restored,bool cleaned,bool healthy,string checkId) {
  using var f=new Fixture();f.Environment.Restored=restored;f.Environment.Cleaned=cleaned;f.Environment.Healthy=healthy;
  Assert.Equal(Readiness.NotReady,await f.Run());
  Assert.Equal("blocked", Diagnostic(f.Reconciler, checkId));
  var order = DiagnosticIds;
  var index = Array.IndexOf(order, checkId);
  Assert.All(order.Skip(index + 1).Where(id=>id!="configurationRevision"), id => Assert.Equal("unknown", Diagnostic(f.Reconciler, id)));
 }
 [Fact] public async Task PositiveBootProofReportsChecksAsPassedOnlyAfterEachCheckRuns() {
  using var f=new Fixture();
  Assert.Equal(Readiness.Ready,await f.Run());
  Assert.Equal("unknown", Diagnostic(f.Reconciler, "runtimeHealth"));
  Assert.Equal(Readiness.Ready,await f.Reconciler.RevalidateRuntimeAsync(default));
  Assert.Equal(DiagnosticIds.Select(id => (id, "passed")), DiagnosticReport(f.Reconciler));
 }
 [Fact] public async Task RuntimeHealthFailureUpdatesRuntimeDiagnosticAndKeepsGateClosed() {
  using var f=new Fixture();Assert.Equal(Readiness.Ready,await f.Run());
  f.Environment.RuntimeHealthy=false;
  Assert.Equal(Readiness.NotReady,await f.Reconciler.RevalidateRuntimeAsync(default));
  Assert.Equal("blocked", Diagnostic(f.Reconciler, "runtimeHealth"));
  Assert.True(f.Gate.Blocked);
  f.Environment.RuntimeHealthy=true;
  Assert.Equal(Readiness.Ready,await f.Reconciler.RevalidateRuntimeAsync(default));
  Assert.Equal("passed", Diagnostic(f.Reconciler, "runtimeHealth"));
 }
 [Fact] public async Task RuntimeHealthExceptionDoesNotLeaveAPreviousPassedDiagnostic() {
  using var f=new Fixture();Assert.Equal(Readiness.Ready,await f.Run());
  Assert.Equal(Readiness.Ready,await f.Reconciler.RevalidateRuntimeAsync(default));
  f.Environment.RuntimeThrows=true;
  Assert.Equal(Readiness.Fault,await f.Reconciler.RevalidateRuntimeAsync(default));
  Assert.Equal("unknown",Diagnostic(f.Reconciler,"runtimeHealth"));
 }
 [Fact] public async Task ProofIsBoundToExactControllerChallenge() {
  using var f=new Fixture();Assert.Equal(Readiness.Ready,await f.Run());Assert.Equal("generation-1",f.Client.Last!.Challenge?.Token);Assert.Equal("allocation-1",f.Client.Last.Challenge?.AllocationId);Assert.Equal(4,f.Client.Last.Challenge?.LeaseRevision);
 }
 sealed class Fixture:IDisposable {
  readonly string path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());public Clock Clock=new();public Gate Gate=new();public Client Client=new();public Environment Environment=new();public Importer Importer=new();public List<string> Events=[];public AppliedConfiguration Configuration;public BootReconciler Reconciler;
  public Fixture(bool baselineValidated=true){Directory.CreateDirectory(path);Configuration=new(path);Environment.Events=Events;Importer.Events=Events;Reconciler=new BootReconciler(Clock,Gate,Configuration,Client,Environment,Importer,new BaselineEvidence(baselineValidated?BootEvidenceStatus.Passed:BootEvidenceStatus.Unavailable));}
  public Task<Readiness> Run()=>Reconciler.ReconcileAsync(default);
  public void Dispose()=>Directory.Delete(path,true);
 }
 sealed class Clock:IBootClock {public string BootId{get;set;}="os-boot";public long Uptime=10;public long UptimeMilliseconds=>Uptime;}
 sealed class Gate:IStreamGate {public bool Blocked;public bool Fail;public int Calls;public Task BlockAsync(CancellationToken ct){Calls++;if(Fail)return Task.FromException(new IOException("Stream gate unavailable"));Blocked=true;return Task.CompletedTask;}public Task AllowAsync(StreamLeaseGrant grant,CancellationToken ct){if(Fail)return Task.FromException(new IOException("Stream gate unavailable"));Blocked=false;return Task.CompletedTask;}}
 sealed class Client:IBootReconciliationClient {
  public bool Offline,Resume,NoChallenge,ExpiredChallenge,MismatchedBoot,MismatchAllocation,MismatchRevision;public ReconcileRequest? Last;public ReconcileResponse? LastResponse;
  public Task<ReconcileResponse> ReconcileAsync(ReconcileRequest request,CancellationToken ct){Last=request;if(Offline)throw new HttpRequestException();var lease=Resume?new ActiveLeaseConfirmation("rental",1,"os-boot",DateTimeOffset.UtcNow.AddMinutes(1)):null;var allocation=Resume?(MismatchAllocation?"another-rental":"rental"):"allocation-1";var revision=Resume?(MismatchRevision?2:1):4;var challengeBoot=MismatchedBoot?"other-boot":"os-boot";var challengeExpiry=ExpiredChallenge?DateTimeOffset.UtcNow.AddSeconds(-1):DateTimeOffset.UtcNow.AddSeconds(30);LastResponse=new ReconcileResponse(DateTimeOffset.UtcNow,new(7,5000,6000),lease,request.HealthVerified?(Resume?"ResumeLease":"PrepareAvailable"):"Hold",NoChallenge?null:new("generation-1",challengeBoot,allocation,revision,challengeExpiry));return Task.FromResult(LastResponse);}
 }
 sealed class Environment:IBootEnvironment {
  public bool Restored=true,Cleaned=true,Healthy=true,RuntimeHealthy=true,RuntimeThrows;public List<string>? Events;
  public Task<BootEvidenceStatus> VerifyRestorationAsync(ReconciliationChallenge challenge,CancellationToken ct){Events?.Add("restoration-proof");return Task.FromResult(Restored?BootEvidenceStatus.Passed:BootEvidenceStatus.Blocked);}
  public Task<BootEvidenceStatus> FenceAndClearAsync(ReconciliationChallenge challenge,CancellationToken ct){Events?.Add("pairing-and-client-cleanup");return Task.FromResult(Cleaned?BootEvidenceStatus.Passed:BootEvidenceStatus.Blocked);}
  public Task<BootEvidenceStatus> VerifyHealthAsync(ReconciliationChallenge challenge,CancellationToken ct){Events?.Add("desktop-health");return Task.FromResult(Healthy?BootEvidenceStatus.Passed:BootEvidenceStatus.Blocked);}
  public Task<BootEvidenceStatus> VerifyRuntimeHealthAsync(string bootId,long configurationRevision,CancellationToken ct){if(RuntimeThrows)throw new IOException("runtime evidence failed");return Task.FromResult(RuntimeHealthy?BootEvidenceStatus.Passed:BootEvidenceStatus.Blocked);}
 }
 sealed class Importer:IReconciledLeaseImporter {
   public bool Result,ClearResult=true;public int Calls,ClearCalls;public string? ClearedBootId;public ActiveLeaseConfirmation? Lease;public DateTimeOffset ControllerTime;public long RequestStartedUptime;public Action? OnImport;public List<string>? Events;
   public Task<bool> ClearAsync(string bootId,CancellationToken ct){Events?.Add("old-lease-clear");ClearCalls++;ClearedBootId=bootId;return Task.FromResult(ClearResult);}
   public Task<bool> ImportAsync(ActiveLeaseConfirmation lease,DateTimeOffset controllerTime,long requestStartedUptime,CancellationToken ct){Events?.Add("lease-import");Calls++;Lease=lease;ControllerTime=controllerTime;RequestStartedUptime=requestStartedUptime;OnImport?.Invoke();return Task.FromResult(Result);}
 }

 sealed class BaselineEvidence(BootEvidenceStatus status):IBootBaselineEvidence { public BootEvidenceStatus Status=>status; }

 private static readonly string[] DiagnosticIds=["baseline","bootChallenge","restoration","streamFence","desktopHealth","configurationRevision","leaseReconciliation","runtimeHealth"];
 private static string Diagnostic(BootReconciler reconciler,string checkId)=>DiagnosticReport(reconciler).Single(x=>x.CheckId==checkId).Status;
 private static (string CheckId,string Status)[] DiagnosticReport(BootReconciler reconciler) {
  var property=typeof(BootReconciler).GetProperty("ReadinessDiagnostics");Assert.NotNull(property);
  var values=Assert.IsAssignableFrom<System.Collections.IEnumerable>(property!.GetValue(reconciler));
  return values.Cast<object>().Select(value=>{
   var type=value.GetType();
   var checkId=type.GetProperty("CheckId")?.GetValue(value)?.ToString();
   var status=type.GetProperty("Status")?.GetValue(value)?.ToString();
   return (Assert.IsType<string>(checkId),Assert.IsType<string>(status));
  }).ToArray();
 }
}
