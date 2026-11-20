using KamenRaider.Contracts;
namespace KamenRaider.Agent.Startup;
// Hardware evidence adapters are not yet qualified; never assert proof in production.
public sealed class UnavailableBootEnvironment:IBootEnvironment {
 public Task<BootEvidenceStatus> VerifyRestorationAsync(ReconciliationChallenge challenge,CancellationToken ct)=>Task.FromResult(BootEvidenceStatus.Unavailable);
 public Task<BootEvidenceStatus> ClearStreamAccessAsync(ReconciliationChallenge challenge,CancellationToken ct)=>Task.FromResult(BootEvidenceStatus.Unavailable);
 public Task<BootEvidenceStatus> VerifyHealthAsync(ReconciliationChallenge challenge,CancellationToken ct)=>Task.FromResult(BootEvidenceStatus.Unavailable);
}
