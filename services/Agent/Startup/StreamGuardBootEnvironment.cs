using KamenRaider.Agent.Sunshine;
using KamenRaider.Contracts;

namespace KamenRaider.Agent.Startup;

// Boot pairing cleanup can use the existing Guard-backed pairing session. This provider does not
// establish Reboot Restore Rx, desktop, capture, audio, or input evidence.
public sealed class StreamGuardBootEnvironment(Func<ISunshinePairingSession> pairingSession) : IBootEnvironment
{
    public Task<BootEvidenceStatus> VerifyRestorationAsync(ReconciliationChallenge challenge, CancellationToken ct)
        => Task.FromResult(BootEvidenceStatus.Unavailable);

    public async Task<BootEvidenceStatus> FenceAndClearAsync(ReconciliationChallenge challenge, CancellationToken ct)
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

    public Task<BootEvidenceStatus> VerifyRuntimeHealthAsync(string bootId, long configurationRevision, CancellationToken ct)
        => Task.FromResult(BootEvidenceStatus.Unavailable);
}
