using KamenRaider.Contracts;
using KamenRaider.Agent.Configuration;
using KamenRaider.Agent.Leases;
namespace KamenRaider.Agent.Startup;
public enum Readiness { NotReady,Ready,Fault }
public interface IBootReconciliationClient {Task<ReconcileResponse> ReconcileAsync(ReconcileRequest request,CancellationToken ct);}
// Implementations must independently verify restoration for this boot, a real stream fence,
// empty pairing/client state, and desktop/capture/input/audio health. Never infer these from uptime.
public interface IBootEnvironment {
 Task<bool> VerifyRestorationAsync(string bootId,CancellationToken ct);
 Task<bool> FenceAndClearAsync(CancellationToken ct);
 Task<bool> VerifyHealthAsync(CancellationToken ct);
 Task<bool> RestoreLeaseAsync(ActiveLeaseConfirmation lease,DateTimeOffset controllerTime,long requestStartedUptime,CancellationToken ct);
}
public sealed class BootReconciler(IBootClock clock,IStreamGate gate,AppliedConfiguration configuration,IBootReconciliationClient client,IBootEnvironment environment,bool baselineValidated) {
  private readonly SemaphoreSlim serial=new(1,1);
 public async Task<Readiness> ReconcileAsync(CancellationToken ct) {
  await serial.WaitAsync(ct);
  try {
   await gate.BlockAsync(ct);
   var boot=clock.BootId;
   var initial=await client.ReconcileAsync(new(boot,configuration.Current?.Revision??0,baselineValidated),ct);
   await configuration.ApplyAsync(initial.Configuration,ct);
   if(!baselineValidated||!await environment.VerifyRestorationAsync(boot,ct)||!await environment.FenceAndClearAsync(ct)||!await environment.VerifyHealthAsync(ct))return Readiness.NotReady;
   var started=clock.UptimeMilliseconds;
   var response=await client.ReconcileAsync(new(boot,configuration.Current!.Revision,true,true,true,true),ct);
   await configuration.ApplyAsync(response.Configuration,ct);
   if(clock.BootId!=boot||response.Configuration.Revision!=initial.Configuration.Revision)return Readiness.NotReady;
   if(response.Action=="PrepareAvailable"&&response.Lease is null)return Readiness.Ready;
   if(response.Action!="ResumeLease"||response.Lease is not {} lease||lease.BootId!=boot||lease.ExpiresAtUtc<=response.ServerTimeUtc.AddMilliseconds(clock.UptimeMilliseconds-started))return Readiness.NotReady;
   var restored=await environment.RestoreLeaseAsync(lease,response.ServerTimeUtc,started,ct);
   return restored&&clock.BootId==boot&&lease.ExpiresAtUtc>response.ServerTimeUtc.AddMilliseconds(clock.UptimeMilliseconds-started)?Readiness.Ready:Readiness.NotReady;
  }catch(HttpRequestException){return Readiness.NotReady;}
  catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
  catch(Exception){return Readiness.Fault;}
  finally{serial.Release();}
 }
}
