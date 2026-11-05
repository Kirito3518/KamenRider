using KamenRaider.Contracts;
namespace KamenRaider.Agent.Startup;
// Hardware evidence adapters are not yet qualified; never assert proof in production.
public sealed class UnavailableBootEnvironment:IBootEnvironment {
 public Task<bool> VerifyRestorationAsync(ReconciliationChallenge challenge,CancellationToken ct)=>Task.FromResult(false);
 public Task<bool> FenceAndClearAsync(ReconciliationChallenge challenge,CancellationToken ct)=>Task.FromResult(false);
 public Task<bool> VerifyHealthAsync(ReconciliationChallenge challenge,CancellationToken ct)=>Task.FromResult(false);
 public Task<bool> VerifyRuntimeHealthAsync(string bootId,long configurationRevision,CancellationToken ct)=>Task.FromResult(false);
 public Task<bool> RestoreLeaseAsync(ActiveLeaseConfirmation lease,DateTimeOffset controllerTime,long requestStartedUptime,CancellationToken ct)=>Task.FromResult(false);
}
