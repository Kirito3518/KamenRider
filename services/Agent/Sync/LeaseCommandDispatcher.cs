using KamenRaider.Agent.Leases;
using KamenRaider.Contracts;
namespace KamenRaider.Agent.Sync;
public sealed class LeaseCommandDispatcher(LeaseEnforcer enforcer,KamenRaider.Agent.Sunshine.PairingSession? pairing=null,KamenRaider.Agent.Preparation.IMachinePreparation? machinePreparation=null) {
 public Task<PairingOffer?> GetPairingOfferAsync(CancellationToken ct)=>enforcer.CanStream&&pairing is not null?pairing.CreateOfferAsync(ct):Task.FromResult<PairingOffer?>(null);
 private CommandResult[] pending=[];
 public CommandResult[] PendingResults=>pending.ToArray();
 public async Task AcceptAsync(SyncResponse response,long requestStartedUptimeMilliseconds,CancellationToken ct){await ObserveResponseTimeAsync(response,requestStartedUptimeMilliseconds,ct);await AcceptObservedResponseAsync(response,ct);}
 public async Task AcceptAsync(SyncResponse response,CancellationToken ct){await enforcer.ObserveControllerTimeAsync(response.ServerTimeUtc,ct);await AcceptObservedResponseAsync(response,ct);}
 internal Task ObserveResponseTimeAsync(SyncResponse response,long requestStartedUptimeMilliseconds,CancellationToken ct)=>enforcer.ObserveControllerTimeAsync(response.ServerTimeUtc,requestStartedUptimeMilliseconds,ct);
 public async Task AcceptBlockedAsync(SyncResponse response,long requestStartedUptimeMilliseconds,CancellationToken ct){
  // Presence-only responses do not process command results; retain the batch until a ready sync acknowledges it.
  await ObserveResponseTimeAsync(response,requestStartedUptimeMilliseconds,ct);
  await BlockWithoutAcknowledgingAsync(ct);
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
}
