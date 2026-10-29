using System.Text.Json;
using KamenRaider.Contracts;
using KamenRaider.Agent.Power;
namespace KamenRaider.Agent.Leases;
public interface IBootClock {string BootId {get;} long UptimeMilliseconds {get;}}
public interface IStreamGate {Task BlockAsync(CancellationToken ct);}
public sealed class LeaseEnforcer(LeaseStore store,TimeProvider time,IBootClock boot,IStreamGate gate,IShutdown shutdown) {
 private readonly SemaphoreSlim commands=new(1,1);
 private readonly object state=new();
 private bool initialized;private bool requiresControllerTime;private bool controllerTimeObserved;private bool importSaved;private volatile bool confirmed;private int shutdownState;private int expiryGateInFlight;private long? recoveryDeadline;
 private LeaseSnapshot? current;private LeaseSnapshot? pendingInitial;private string? fault;private DateTimeOffset? serverTime;private long serverUptime;
 public LeaseSnapshot? Current=>Volatile.Read(ref current);
 public string? Fault=>Volatile.Read(ref fault);
 public bool CanStream=>confirmed&&Fault is null&&Current is {} s&&s.BootId==boot.BootId&&boot.UptimeMilliseconds<s.DeadlineUptimeMilliseconds;
 public TimeSpan PairingCommandTimeRemaining(CommandEnvelope command){lock(state){if(!CanStream||Current is not {} s||command.Type!="PairPin"||command.RentalId!=s.RentalId||command.LeaseRevision!=s.Revision||command.BootId!=s.BootId)return TimeSpan.Zero;var until=command.ValidUntilUtc<s.ExpiresAtUtc?command.ValidUntilUtc:s.ExpiresAtUtc;var remaining=until-EffectiveNow();return remaining>TimeSpan.Zero?remaining:TimeSpan.Zero;}}
 public async Task InitializeAsync(CancellationToken ct){await commands.WaitAsync(ct);try{await InitializeCoreAsync(ct);}finally{commands.Release();}}
 private async Task InitializeCoreAsync(CancellationToken ct,bool blockGate=true){if(initialized)return;
  try{var loaded=await store.LoadAsync(ct);
   lock(state){current=loaded;requiresControllerTime=loaded is not null;recoveryDeadline=loaded?.RecoveryDeadlineUptimeMilliseconds;if(loaded is {} s&&(s.BootId!=boot.BootId||s.Revision<=0||s.DeadlineUptimeMilliseconds<=0||string.IsNullOrWhiteSpace(s.LastCommandId)))fault="ReconciliationRequired";initialized=true;}
  }catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException){lock(state){fault="LeaseStorageFailed";initialized=true;}}
  if(blockGate)await gate.BlockAsync(ct);
 }
 public Task ObserveControllerTimeAsync(DateTimeOffset observed,CancellationToken ct)=>ObserveControllerTimeAsync(observed,boot.UptimeMilliseconds,ct);
  public async Task ObserveControllerTimeAsync(DateTimeOffset observed,long requestStartedUptimeMilliseconds,CancellationToken ct){ct.ThrowIfCancellationRequested();LeaseSnapshot? shortened=null;
  lock(state){var nowUptime=boot.UptimeMilliseconds;if(requestStartedUptimeMilliseconds>nowUptime||requestStartedUptimeMilliseconds<0){fault="InvalidClockSample";confirmed=false;return;}var conservative=observed.AddMilliseconds(nowUptime-requestStartedUptimeMilliseconds);var now=EffectiveNow();serverTime=conservative>now?conservative:now;serverUptime=nowUptime;controllerTimeObserved=true;
   if(current is {} snapshot&&snapshot.BootId==boot.BootId){var remaining=snapshot.ExpiresAtUtc-serverTime.Value;var deadline=remaining<=TimeSpan.Zero?nowUptime:checked(nowUptime+(long)remaining.TotalMilliseconds);if(deadline<snapshot.DeadlineUptimeMilliseconds){shortened=snapshot with{DeadlineUptimeMilliseconds=deadline};current=shortened;confirmed=false;if(fault is null)fault="ClockPersistenceRequired";}}
  }
  if(shortened is null)return;
  await commands.WaitAsync(ct);try{
   lock(state){if(!ReferenceEquals(current,shortened))return;}
   await gate.BlockAsync(ct);
   try{await store.SaveAsync(shortened,ct);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){lock(state){fault="LeaseStorageFailed";}return;}
   lock(state){if(ReferenceEquals(current,shortened)&&(fault=="ClockPersistenceRequired"||(fault=="ImportPending"&&importSaved)))fault=null;}
  }finally{commands.Release();}
 }
private DateTimeOffset EffectiveNow(){var now=time.GetUtcNow();if(serverTime is {} origin){var projected=origin.AddMilliseconds(boot.UptimeMilliseconds-serverUptime);if(projected>now)now=projected;}if(Current is {} s&&s.BootId==boot.BootId){var projected=s.ExpiresAtUtc.AddMilliseconds(boot.UptimeMilliseconds-s.DeadlineUptimeMilliseconds);if(projected>now)now=projected;}return now;}
 public async Task<bool> ImportReconciledLeaseAsync(ActiveLeaseConfirmation lease,DateTimeOffset controllerTime,long requestStartedUptime,CancellationToken ct){await commands.WaitAsync(ct);try{
  await InitializeCoreAsync(ct,false);LeaseSnapshot next;
  lock(state){var now=boot.UptimeMilliseconds;var old=current;if(fault!="ReconciliationRequired"||old is null||old.BootId==boot.BootId||old.RecoveryDeadlineUptimeMilliseconds is not null||lease.BootId!=boot.BootId||lease.RentalId!=old.RentalId||lease.LeaseRevision!=old.Revision||lease.ExpiresAtUtc!=old.ExpiresAtUtc||requestStartedUptime<0||requestStartedUptime>now)return false;
   var effective=controllerTime.AddMilliseconds(now-requestStartedUptime);var floor=EffectiveNow();if(floor>effective)effective=floor;if(lease.ExpiresAtUtc<=effective)return false;
   next=new(lease.RentalId,lease.LeaseRevision,lease.ExpiresAtUtc,lease.BootId,old.LastCommandId,checked(now+(long)(lease.ExpiresAtUtc-effective).TotalMilliseconds));current=next;recoveryDeadline=null;fault="ImportPending";confirmed=false;importSaved=false;controllerTimeObserved=true;
  }
  await gate.BlockAsync(ct);
  try{await store.SaveAsync(next,ct);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){lock(state){fault="LeaseStorageFailed";}return false;}
  lock(state){importSaved=true;if(!ReferenceEquals(current,next)||fault!="ImportPending"||boot.BootId!=next.BootId||boot.UptimeMilliseconds>=next.DeadlineUptimeMilliseconds)return false;fault=null;return true;}
 }finally{commands.Release();}}
 public async Task<CommandResult> ApplyAsync(CommandEnvelope command,CancellationToken ct){await commands.WaitAsync(ct);try{
  await InitializeCoreAsync(ct,false);var failed=new CommandResult(command.CommandId,"failed",command.LeaseRevision,"LeaseRejected");LeaseSnapshot? previous;LeaseSnapshot next;
  lock(state){
   if(fault is not null||command.Type!="ApplyLease"||command.BootId!=boot.BootId||command.LeaseRevision<=0||string.IsNullOrWhiteSpace(command.RentalId)||command.ValidUntilUtc<=EffectiveNow())return failed;
   DateTimeOffset expires,starts;try{expires=command.Payload.GetProperty("expiresAtUtc").GetDateTimeOffset();starts=command.Payload.GetProperty("startsAtUtc").GetDateTimeOffset();}catch(Exception ex) when(ex is InvalidOperationException or KeyNotFoundException or FormatException){return failed;}
   if(expires<=EffectiveNow()||starts>expires)return failed;
   previous=current;
   if(previous is not null){if(previous.BootId!=boot.BootId||boot.UptimeMilliseconds>=EnforcementDeadline(previous))return failed;
    if(command.CommandId==previous.LastCommandId)return command.RentalId==previous.RentalId&&command.LeaseRevision==previous.Revision&&expires==previous.ExpiresAtUtc?new(command.CommandId,"applied",previous.Revision):failed;
    if(command.RentalId!=previous.RentalId||command.LeaseRevision!=previous.Revision+1||expires<=previous.ExpiresAtUtc)return failed;}
   confirmed=false;var deadline=checked(boot.UptimeMilliseconds+(long)(expires-EffectiveNow()).TotalMilliseconds);
   next=new(command.RentalId,command.LeaseRevision,expires,boot.BootId,command.CommandId,deadline,previous?.RecoveryDeadlineUptimeMilliseconds??previous?.DeadlineUptimeMilliseconds);
   if(previous is null)pendingInitial=next;
  }
  await gate.BlockAsync(ct);
  lock(state){if(!CanCommit(previous,next,command))return failed;}
  try{await store.SaveAsync(next,ct);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){lock(state){fault="LeaseStorageFailed";}return failed;}
  // Neither disk nor gate owns the expiry path. Recheck after every await before publishing.
  lock(state){if(!CanCommit(previous,next,command))return failed;current=next;return new(command.CommandId,"applied",command.LeaseRevision);}
 }finally{commands.Release();}}
 private bool CanCommit(LeaseSnapshot? previous,LeaseSnapshot next,CommandEnvelope command)=>fault is null&&ReferenceEquals(previous,current)&&boot.BootId==next.BootId&&boot.UptimeMilliseconds<next.DeadlineUptimeMilliseconds&&(previous is null||boot.UptimeMilliseconds<EnforcementDeadline(previous))&&command.ValidUntilUtc>EffectiveNow();
 public async Task ConfirmAsync(ActiveLeaseConfirmation? confirmation,CancellationToken ct){await commands.WaitAsync(ct);try{await InitializeCoreAsync(ct);LeaseSnapshot? candidate;
  lock(state){confirmed=false;candidate=Matches(confirmation)?current:null;if(candidate is not null)recoveryDeadline=null;}
  if(candidate?.RecoveryDeadlineUptimeMilliseconds is not null){var finalized=candidate with{RecoveryDeadlineUptimeMilliseconds=null};try{await store.SaveAsync(finalized,ct);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){lock(state){fault="LeaseStorageFailed";}return;}lock(state){if(ReferenceEquals(current,candidate)&&Matches(confirmation))current=finalized;}}
  lock(state){confirmed=Matches(confirmation);}if(!confirmed)await gate.BlockAsync(ct);
 }finally{commands.Release();}}
 private bool Matches(ActiveLeaseConfirmation? confirmation)=>fault is null&&(!requiresControllerTime||controllerTimeObserved)&&current is {} s&&s.BootId==boot.BootId&&boot.UptimeMilliseconds<EnforcementDeadline(s)&&confirmation is {} c&&c.RentalId==s.RentalId&&c.BootId==s.BootId&&c.LeaseRevision==s.Revision&&c.ExpiresAtUtc==s.ExpiresAtUtc;
 public async Task EnforceAsync(CancellationToken ct){
  // No command semaphore, disk operation, or gate operation may delay known expiry.
  if(Current is null&&!initialized){if(!commands.Wait(0))return;try{await InitializeCoreAsync(ct,false);}finally{commands.Release();}}
  lock(state){if((current??pendingInitial) is not {} s||s.BootId!=boot.BootId||boot.UptimeMilliseconds<EnforcementDeadline(s))return;confirmed=false;fault="LeaseExpired";}
  if(Interlocked.CompareExchange(ref shutdownState,1,0)==0){try{await shutdown.RequestImmediateShutdownAsync(ct);Volatile.Write(ref shutdownState,2);lock(state){fault="ShutdownRequested";}}catch(Exception ex) when(ex is not OperationCanceledException){Volatile.Write(ref shutdownState,0);lock(state){fault="ShutdownFailed";}}}
  // Observe eventual failure without awaiting an adapter that could be stalled.
  if(Interlocked.CompareExchange(ref expiryGateInFlight,1,0)==0)_=BlockAfterExpiryAsync();
 }
 private long EnforcementDeadline(LeaseSnapshot snapshot)=>recoveryDeadline is {} guard?Math.Min(guard,snapshot.DeadlineUptimeMilliseconds):snapshot.DeadlineUptimeMilliseconds;
 private async Task BlockAfterExpiryAsync(){try{await gate.BlockAsync(CancellationToken.None);}catch(Exception){lock(state){confirmed=false;}}finally{Volatile.Write(ref expiryGateInFlight,0);}}
}
