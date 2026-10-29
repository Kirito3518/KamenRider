using KamenRaider.Agent.Leases;
using KamenRaider.Contracts;
namespace KamenRaider.Agent.Sync;
public sealed class LeaseCommandDispatcher(LeaseEnforcer enforcer) {
 private CommandResult[] pending=[];
 public CommandResult[] PendingResults=>pending.ToArray();
 public async Task AcceptAsync(SyncResponse response,CancellationToken ct){
  // A successful sync response acknowledges the results sent in that request.
  pending=[];
  await enforcer.ObserveControllerTimeAsync(response.ServerTimeUtc,ct);
  foreach(var command in response.Commands.Where(c=>c.Type=="ApplyLease"))pending=[..pending,await enforcer.ApplyAsync(command,ct)];
  await enforcer.ConfirmAsync(response.ActiveLease,ct);
 }
}

