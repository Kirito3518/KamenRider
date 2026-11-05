using KamenRaider.Agent.Leases;
using KamenRaider.Contracts;

namespace KamenRaider.Agent.Startup;

public sealed class LeaseEnforcerReconciledLeaseImporter(LeaseEnforcer enforcer) : IReconciledLeaseImporter
{
    public Task<bool> ClearAsync(string bootId, CancellationToken ct) =>
        enforcer.ClearReconciledLeaseAsync(bootId, ct);

    public Task<bool> ImportAsync(
        ActiveLeaseConfirmation lease,
        DateTimeOffset controllerTime,
        long requestStartedUptime,
        CancellationToken ct) =>
        enforcer.ReconcileConfirmedLeaseAsync(lease, controllerTime, requestStartedUptime, ct);
}
