using KamenRaider.Agent.Sunshine;
using KamenRaider.Contracts;

namespace KamenRaider.Agent.Startup;

// Boot cleanup drives Sunshine's own local REST contract: unpair every Moonlight client so
// no previous renter (or anyone who paired on a previous boot) is trusted. Restoration and
// desktop-health adapters are not qualified yet and are reported as diagnostics only.
public sealed class SunshineBootEnvironment(Func<ISunshinePairingSession> pairingSession) : IBootEnvironment
{
    public Task<BootEvidenceStatus> VerifyRestorationAsync(ReconciliationChallenge challenge, CancellationToken ct)
        => Task.FromResult(BootEvidenceStatus.Unavailable);

    public async Task<BootEvidenceStatus> ClearStreamAccessAsync(ReconciliationChallenge challenge, CancellationToken ct)
    {
        try
        {
            return await pairingSession().ClearAsync(ct)
                ? BootEvidenceStatus.Passed
                : BootEvidenceStatus.Blocked;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return BootEvidenceStatus.Unavailable;
        }
    }

    public Task<BootEvidenceStatus> VerifyHealthAsync(ReconciliationChallenge challenge, CancellationToken ct)
        => Task.FromResult(BootEvidenceStatus.Unavailable);
}
