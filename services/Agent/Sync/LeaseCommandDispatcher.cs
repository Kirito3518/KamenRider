using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Sunshine;
using KamenRaider.Contracts;
namespace KamenRaider.Agent.Sync;
public sealed class LeaseCommandDispatcher(LeaseEnforcer enforcer,KamenRaider.Agent.Sunshine.ISunshinePairingSession? pairing=null,KamenRaider.Agent.Preparation.IMachinePreparation? machinePreparation=null,ISunshineAdmin? sunshineAdmin=null,TimeProvider? time=null,KamenRaider.Agent.Parsec.ParsecManager? parsec=null) {
 public Task<PairingOffer?> GetPairingOfferAsync(CancellationToken ct)=>enforcer.CanStream&&pairing is not null?pairing.CreateOfferAsync(ct):Task.FromResult<PairingOffer?>(null);
 private CommandResult[] pending=[];
 public CommandResult[] PendingResults=>pending.ToArray();
 public async Task AcceptAsync(SyncResponse response,long requestStartedUptimeMilliseconds,CancellationToken ct){await ObserveResponseTimeAsync(response,requestStartedUptimeMilliseconds,ct);await AcceptObservedResponseAsync(response,ct);}
 public async Task AcceptAsync(SyncResponse response,CancellationToken ct){await enforcer.ObserveControllerTimeAsync(response.ServerTimeUtc,ct);await AcceptObservedResponseAsync(response,ct);}
 internal Task ObserveResponseTimeAsync(SyncResponse response,long requestStartedUptimeMilliseconds,CancellationToken ct)=>enforcer.ObserveControllerTimeAsync(response.ServerTimeUtc,requestStartedUptimeMilliseconds,ct);
 public async Task AcceptBlockedAsync(SyncResponse response,long requestStartedUptimeMilliseconds,CancellationToken ct){
  // Presence-only responses do not process lease/configuration command results; retain that batch
  // until a ready sync acknowledges it.
  await ObserveResponseTimeAsync(response,requestStartedUptimeMilliseconds,ct);
  await BlockWithoutAcknowledgingAsync(ct);
  // Machine-scoped Sunshine repair must still work while the machine is not ready (a wedged
  // Sunshine is the reason an admin sends it). Results ride on the next sync's PendingResults.
  foreach(var command in response.Commands.Where(c=>c.Type is "RestartSunshine" or "UnpairSunshine"))
   pending=[..pending,await ExecuteSunshineMaintenanceAsync(command,ct)];
 }
 internal async Task AcceptAcknowledgedBlockedResponseAsync(CancellationToken ct){pending=[];await enforcer.ConfirmAsync(null,ct);}
 internal Task BlockWithoutAcknowledgingAsync(CancellationToken ct)=>enforcer.ConfirmAsync(null,ct);
 internal async Task AcceptObservedResponseAsync(SyncResponse response,CancellationToken ct){
  // A successful sync response acknowledges the results sent in that request.
  pending=[];
  foreach(var command in response.Commands.Where(c=>c.Type=="PrepareMachine"))pending=[..pending,await ExecutePreparationAsync(command,ct)];
  foreach(var command in response.Commands.Where(c=>c.Type=="ApplyLease"))pending=[..pending,await enforcer.ApplyAsync(command,ct)];
  await enforcer.ConfirmAsync(response.ActiveLease,ct);
  foreach(var command in response.Commands.Where(c=>c.Type=="PairPin"))pending=[..pending,await ExecutePairAsync(command,ct)];
  foreach(var command in response.Commands.Where(c=>c.Type=="ParsecConnect"))pending=[..pending,await ExecuteParsecAsync(command,ct)];
  foreach(var command in response.Commands.Where(c=>c.Type=="EndLease"))pending=[..pending,await enforcer.EndLeaseAsync(command,ct)];
  foreach(var command in response.Commands.Where(c=>c.Type=="ShutdownMachine"))pending=[..pending,await enforcer.ShutdownMachineAsync(command,ct)];
  // Machine-scoped Sunshine maintenance is accepted even on a not-ready sync: repairing a
  // wedged Sunshine is exactly why an admin presses it while the machine is off the shelf.
  foreach(var command in response.Commands.Where(c=>c.Type is "RestartSunshine" or "UnpairSunshine"))pending=[..pending,await ExecuteSunshineMaintenanceAsync(command,ct)];
 }
 private async Task<CommandResult> ExecutePreparationAsync(CommandEnvelope command,CancellationToken ct){
  var failed=new CommandResult(command.CommandId,"failed",command.LeaseRevision,"PreparationUnavailable");
  if(machinePreparation is null||!await enforcer.CanPrepareMachineAsync(command,ct))return failed;
  try{return await machinePreparation.PrepareAsync(command,ct)?new(command.CommandId,"applied",command.LeaseRevision):failed;}
  catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
  catch{return failed;}
 }
 private async Task<CommandResult> ExecutePairAsync(CommandEnvelope command,CancellationToken ct){
  var rejected=new CommandResult(command.CommandId,"failed",command.LeaseRevision,"PairingUnavailable");var remaining=enforcer.PairingCommandTimeRemaining(command);if(pairing is null||remaining<=TimeSpan.Zero)return rejected;
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(remaining);
  try{var result=await pairing.ExecuteAsync(command,timeout.Token);return !timeout.IsCancellationRequested&&enforcer.PairingCommandTimeRemaining(command)>TimeSpan.Zero?result:rejected;}
  catch(OperationCanceledException) when(!ct.IsCancellationRequested){return rejected;}
 }
 private async Task<CommandResult> ExecuteParsecAsync(CommandEnvelope command,CancellationToken ct){
  var rejected=new CommandResult(command.CommandId,"failed",command.LeaseRevision,"ParsecUnavailable");
  if(parsec is null||command.Type!="ParsecConnect"||!enforcer.IsCurrentLeaseCommand(command))return rejected;
  string attemptId,deadlineRaw;KamenRaider.Contracts.ParsecSealedSecret secret;
  try{
   attemptId=command.Payload.GetProperty("attemptId").GetString()??"";
   deadlineRaw=command.Payload.GetProperty("deadlineAtUtc").GetString()??"";
   var sealedNode=command.Payload.GetProperty("secret");
   secret=new(
    sealedNode.GetProperty("wrappedKey").GetString()??"",
    sealedNode.GetProperty("iv").GetString()??"",
    sealedNode.GetProperty("ciphertext").GetString()??"");
   if(attemptId.Length is <1 or >128||!DateTimeOffset.TryParse(deadlineRaw,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out var deadline))
    return rejected;
   var result=await parsec.StartAsync(attemptId,deadline,secret,ct);
   return result.Accepted
    ?new(command.CommandId,"applied",command.LeaseRevision)
    :new(command.CommandId,"failed",command.LeaseRevision,result.ErrorCode??"ParsecRefused");
  }
  catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
  catch(Exception ex) when(ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException){return rejected;}
  catch{return new(command.CommandId,"failed",command.LeaseRevision,"ParsecUnavailable");}
 }
 private async Task<CommandResult> ExecuteSunshineMaintenanceAsync(CommandEnvelope command,CancellationToken ct){
  var failed=new CommandResult(command.CommandId,"failed",0,"SunshineMaintenanceUnavailable");
  if(sunshineAdmin is null||!string.Equals(command.BootId,enforcer.BootId,StringComparison.Ordinal))return failed;
  var now=(time??TimeProvider.System).GetUtcNow();
  if(command.ValidUntilUtc<=now)return new(command.CommandId,"failed",0,"CommandExpired");
  var force=false;
  if(command.Payload.ValueKind==System.Text.Json.JsonValueKind.Object&&
     command.Payload.TryGetProperty("force",out var value)&&
     value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
   force=value.GetBoolean();
  try{
   var result=command.Type=="RestartSunshine"
    ?await sunshineAdmin.RestartAsync(force,ct)
    :await sunshineAdmin.UnpairAsync(force,ct);
   return result.Ok
    ?new(command.CommandId,"applied",0,result.ErrorCode)
    :new(command.CommandId,"failed",0,result.ErrorCode??"SunshineMaintenanceFailed");
  }
  catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
  catch{return failed;}
 }
}
