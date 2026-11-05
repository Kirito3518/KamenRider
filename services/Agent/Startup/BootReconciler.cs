using KamenRaider.Contracts;
using KamenRaider.Agent.Configuration;
using KamenRaider.Agent.Leases;
namespace KamenRaider.Agent.Startup;
public enum Readiness { NotReady,Ready,Fault }
public interface IBootReconciliationClient {Task<ReconcileResponse> ReconcileAsync(ReconcileRequest request,CancellationToken ct);}
public interface IBootBaselineEvidence {bool HasValidatedBaseline {get;}}
public sealed class UnvalidatedBootBaselineEvidence:IBootBaselineEvidence {public bool HasValidatedBaseline=>false;}
public interface IReconciledLeaseImporter {Task<bool> ImportAsync(ActiveLeaseConfirmation lease,DateTimeOffset controllerTime,long requestStartedUptime,CancellationToken ct);Task<bool> ClearAsync(string bootId,CancellationToken ct);}
// Implementations must independently verify restoration for this boot, a real stream fence,
// empty pairing/client state, and desktop/capture/input/audio health. Never infer these from uptime.
public interface IBootEnvironment {
 Task<bool> VerifyRestorationAsync(ReconciliationChallenge challenge,CancellationToken ct);
 Task<bool> FenceAndClearAsync(ReconciliationChallenge challenge,CancellationToken ct);
 Task<bool> VerifyHealthAsync(ReconciliationChallenge challenge,CancellationToken ct);
 // Recheck volatile runtime health each sync cycle without reusing an expired one-time challenge.
 Task<bool> VerifyRuntimeHealthAsync(string bootId,long configurationRevision,CancellationToken ct);
}
public sealed class BootReconciler(IBootClock clock,IStreamGate gate,AppliedConfiguration configuration,IBootReconciliationClient client,IBootEnvironment environment,IReconciledLeaseImporter leaseImporter,bool baselineValidated) {
  private readonly SemaphoreSlim serial=new(1,1);private string? readyBootId;private long readyConfigurationRevision=-1;
 public bool HasRuntimeEvidence=>readyBootId==clock.BootId&&readyConfigurationRevision==(configuration.Current?.Revision??0);
 public async Task<Readiness> RevalidateRuntimeAsync(CancellationToken ct){await serial.WaitAsync(ct);try{
  var boot=readyBootId;
  if(boot is null||!baselineValidated||boot!=clock.BootId||readyConfigurationRevision!=(configuration.Current?.Revision??0))return await InvalidateReadinessAsync(ct);
  if(!await environment.VerifyRuntimeHealthAsync(boot,readyConfigurationRevision,ct)){await gate.BlockAsync(ct);return Readiness.NotReady;}
  return Readiness.Ready;
 }catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}catch(Exception){try{await gate.BlockAsync(ct);}catch(Exception){readyBootId=null;readyConfigurationRevision=-1;}return Readiness.Fault;}finally{serial.Release();}}
 private async Task<Readiness> InvalidateReadinessAsync(CancellationToken ct){readyBootId=null;readyConfigurationRevision=-1;await gate.BlockAsync(ct);return Readiness.NotReady;}
 public async Task<Readiness> ReconcileAsync(CancellationToken ct) {
  await serial.WaitAsync(ct);
  try {
   readyBootId=null;readyConfigurationRevision=-1;
   await gate.BlockAsync(ct);
   var boot=clock.BootId;
   var initialStarted=clock.UptimeMilliseconds;
   var initial=await client.ReconcileAsync(new(boot,configuration.Current?.Revision??0,baselineValidated),ct);
   await configuration.ApplyAsync(initial.Configuration,ct);
   var challenge=initial.Challenge;
   if(challenge is null||challenge.BootId!=boot||challenge.ExpiresAtUtc<=initial.ServerTimeUtc.AddMilliseconds(clock.UptimeMilliseconds-initialStarted))return Readiness.NotReady;
   if(!baselineValidated||!await environment.VerifyRestorationAsync(challenge,ct)||!await environment.FenceAndClearAsync(challenge,ct)||!await environment.VerifyHealthAsync(challenge,ct))return Readiness.NotReady;
   if(challenge.ExpiresAtUtc<=initial.ServerTimeUtc.AddMilliseconds(clock.UptimeMilliseconds-initialStarted))return Readiness.NotReady;
   var started=clock.UptimeMilliseconds;
   var response=await client.ReconcileAsync(new(boot,configuration.Current!.Revision,true,true,true,true,challenge),ct);
   await configuration.ApplyAsync(response.Configuration,ct);
   if(clock.BootId!=boot||response.Configuration.Revision!=initial.Configuration.Revision)return Readiness.NotReady;
   if(response.Action=="PrepareAvailable"&&response.Lease is null){var cleared=await leaseImporter.ClearAsync(boot,ct);if(!cleared||clock.BootId!=boot||response.Configuration.Revision!=initial.Configuration.Revision)return Readiness.NotReady;readyBootId=boot;readyConfigurationRevision=response.Configuration.Revision;return Readiness.Ready;}
   if(response.Action!="ResumeLease"||response.Lease is not {} lease||lease.BootId!=boot||challenge.AllocationId is null||challenge.LeaseRevision is null||challenge.AllocationId!=lease.RentalId||challenge.LeaseRevision.Value!=lease.LeaseRevision||lease.ExpiresAtUtc<=response.ServerTimeUtc.AddMilliseconds(clock.UptimeMilliseconds-started))return Readiness.NotReady;
   var restored=await leaseImporter.ImportAsync(lease,response.ServerTimeUtc,started,ct);
   if(!restored||clock.BootId!=boot||lease.ExpiresAtUtc<=response.ServerTimeUtc.AddMilliseconds(clock.UptimeMilliseconds-started))return Readiness.NotReady;
   readyBootId=boot;readyConfigurationRevision=response.Configuration.Revision;return Readiness.Ready;
  }catch(HttpRequestException){return Readiness.NotReady;}
  catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
  catch(Exception){return Readiness.Fault;}
  finally{serial.Release();}
 }
}
