using KamenRaider.Contracts;

namespace KamenRaider.Agent.Startup;

// TEST ONLY. Registered when KAMENRAIDER_AGENT_LAB_READY=1 so the rental/pairing flow can be
// exercised without the hardware-qualified boot baseline, restoration, desktop and runtime health
// evidence providers. Never enable this on a machine that is actually rented out.
public sealed class LabBootBaselineEvidence : IBootBaselineEvidence
{
    public BootEvidenceStatus Status => BootEvidenceStatus.Passed;
}

public sealed class LabBootEnvironment : IBootEnvironment
{
    public Task<BootEvidenceStatus> VerifyRestorationAsync(ReconciliationChallenge challenge, CancellationToken ct)
        => Task.FromResult(BootEvidenceStatus.Passed);

    public Task<BootEvidenceStatus> FenceAndClearAsync(ReconciliationChallenge challenge, CancellationToken ct)
        => Task.FromResult(BootEvidenceStatus.Passed);

    public Task<BootEvidenceStatus> VerifyHealthAsync(ReconciliationChallenge challenge, CancellationToken ct)
        => Task.FromResult(BootEvidenceStatus.Passed);

    public Task<BootEvidenceStatus> VerifyRuntimeHealthAsync(string bootId, long configurationRevision, CancellationToken ct)
        => Task.FromResult(BootEvidenceStatus.Passed);
}
