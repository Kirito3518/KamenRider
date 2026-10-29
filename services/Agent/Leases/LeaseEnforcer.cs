using System.Text.Json;
using KamenRaider.Contracts;
using KamenRaider.Agent.Power;
namespace KamenRaider.Agent.Leases;
public interface IBootClock {string BootId {get;} long UptimeMilliseconds {get;}}
// Task 6 supplies the live adapter. Permission is consumed through CanStream, never an ApplyLease ACK.
public interface IStreamGate {Task BlockAsync(CancellationToken ct);}
public sealed class LeaseEnforcer(LeaseStore store,TimeProvider time,IBootClock boot,IStreamGate gate,IShutdown shutdown) {
 private readonly SemaphoreSlim mutex=new(1,1);private bool initialized,confirmed,shutdownRequested;private DateTimeOffset? serverTime;private long serverUptime;
 public LeaseSnapshot? Current {get;private set;} public string? Fault {get;private set;}
 public bool CanStream=>confirmed&&Fault is null&&Current is {} s&&s.BootId==boot.BootId&&boot.UptimeMilliseconds<s.DeadlineUptimeMilliseconds;
 public async Task InitializeAsync(CancellationToken ct){await mutex.WaitAsync(ct);try{await InitializeCoreAsync(ct);}finally{mutex.Release();}}
 private async Task InitializeCoreAsync(CancellationToken ct){if(initialized)return;await gate.BlockAsync(ct);try{Current=await store.LoadAsync(ct);if(Current is {} s&&(s.BootId!=boot.BootId||s.Revision<=0||s.DeadlineUptimeMilliseconds<=0||string.IsNullOrWhiteSpace(s.LastCommandId)))Fault="ReconciliationRequired";}catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException){Fault="LeaseStorageFailed";}initialized=true;}
 public async Task ObserveControllerTimeAsync(DateTimeOffset observed,CancellationToken ct){await mutex.WaitAsync(ct);try{var current=EffectiveNow();serverTime=observed>current?observed:current;serverUptime=boot.UptimeMilliseconds;}finally{mutex.Release();}}
 private DateTimeOffset EffectiveNow(){var now=time.GetUtcNow();if(serverTime is {} origin){var projected=origin.AddMilliseconds(boot.UptimeMilliseconds-serverUptime);if(projected>now)now=projected;}if(Current is {} s&&s.BootId==boot.BootId){var projected=s.ExpiresAtUtc.AddMilliseconds(boot.UptimeMilliseconds-s.DeadlineUptimeMilliseconds);if(projected>now)now=projected;}return now;}
 public async Task<CommandResult> ApplyAsync(CommandEnvelope command,CancellationToken ct){await mutex.WaitAsync(ct);try{
  await InitializeCoreAsync(ct);var failed=new CommandResult(command.CommandId,"failed",command.LeaseRevision,"LeaseRejected");
  if(Fault is not null||command.Type!="ApplyLease"||command.BootId!=boot.BootId||command.LeaseRevision<=0||string.IsNullOrWhiteSpace(command.RentalId)||command.ValidUntilUtc<=EffectiveNow())return failed;
  DateTimeOffset expires,starts;try{expires=command.Payload.GetProperty("expiresAtUtc").GetDateTimeOffset();starts=command.Payload.GetProperty("startsAtUtc").GetDateTimeOffset();}catch(Exception ex) when(ex is InvalidOperationException or KeyNotFoundException or FormatException){return failed;}
  if(expires<=EffectiveNow()||starts>expires)return failed;
  if(Current is {} previous){
   if(previous.BootId!=boot.BootId||boot.UptimeMilliseconds>=previous.DeadlineUptimeMilliseconds)return failed;
   if(command.CommandId==previous.LastCommandId)return command.RentalId==previous.RentalId&&command.LeaseRevision==previous.Revision&&expires==previous.ExpiresAtUtc?new(command.CommandId,"applied",previous.Revision):failed;
   if(command.RentalId!=previous.RentalId||command.LeaseRevision!=previous.Revision+1||expires<=previous.ExpiresAtUtc)return failed;
  }
  confirmed=false;await gate.BlockAsync(ct);
  var deadline=checked(boot.UptimeMilliseconds+(long)(expires-EffectiveNow()).TotalMilliseconds);
  var next=new LeaseSnapshot(command.RentalId,command.LeaseRevision,expires,boot.BootId,command.CommandId,deadline);
  try{await store.SaveAsync(next,ct);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){Fault="LeaseStorageFailed";return failed;}
  Current=next;return new(command.CommandId,"applied",command.LeaseRevision);
 }finally{mutex.Release();}}
 public async Task ConfirmAsync(ActiveLeaseConfirmation? confirmation,CancellationToken ct){await mutex.WaitAsync(ct);try{await InitializeCoreAsync(ct);confirmed=Fault is null&&Current is {} s&&s.BootId==boot.BootId&&boot.UptimeMilliseconds<s.DeadlineUptimeMilliseconds&&confirmation is {} c&&c.RentalId==s.RentalId&&c.BootId==s.BootId&&c.LeaseRevision==s.Revision&&c.ExpiresAtUtc==s.ExpiresAtUtc;if(!confirmed)await gate.BlockAsync(ct);}finally{mutex.Release();}}
 public async Task EnforceAsync(CancellationToken ct){await mutex.WaitAsync(ct);try{await InitializeCoreAsync(ct);if(Current is not {} s||s.BootId!=boot.BootId||boot.UptimeMilliseconds<s.DeadlineUptimeMilliseconds)return;confirmed=false;
  // Invoke power first: a slow or failed stream/network cleanup must never postpone shutdown.
  if(!shutdownRequested){try{await shutdown.RequestImmediateShutdownAsync(ct);shutdownRequested=true;Fault="ShutdownRequested";}catch(Exception ex) when(ex is not OperationCanceledException){Fault="ShutdownFailed";}}
  await gate.BlockAsync(ct);
 }finally{mutex.Release();}}
}

