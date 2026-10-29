using KamenRaider.Agent.Leases;
using KamenRaider.Contracts;
namespace KamenRaider.Agent.Sync;
public sealed class LeaseCommandDispatcher(LeaseEnforcer enforcer) {
 private CommandResult[] pending=[];
 public CommandResult[] PendingResults=>pending.ToArray();
 public async Task AcceptAsync(SyncResponse response,long requestStartedUptimeMilliseconds,CancellationToken ct){await enforcer.ObserveControllerTimeAsync(response.ServerTimeUtc,requestStartedUptimeMilliseconds,ct);await ProcessAsync(response,ct);}
 public async Task AcceptAsync(SyncResponse response,CancellationToken ct){await enforcer.ObserveControllerTimeAsync(response.ServerTimeUtc,ct);await ProcessAsync(response,ct);}
 private async Task ProcessAsync(SyncResponse response,CancellationToken ct){
  // A successful sync response acknowledges the results sent in that request.
  pending=[];
  foreach(var command in response.Commands.Where(c=>c.Type=="ApplyLease"))pending=[..pending,await enforcer.ApplyAsync(command,ct)];
  await enforcer.ConfirmAsync(response.ActiveLease,ct);
 }
}
