namespace KamenRaider.Controller.Data;

/// <summary>
/// Supplies authenticated, durable allocation, payment, and revocation history that is independent
/// of the Controller database snapshot. A restore epoch alone is not history evidence.
/// </summary>
public interface IExternalRecoveryHistory
{
    CompleteMachineHistory? TryGetCompleteMachineHistory(string machineId, long restoreEpoch);
}

public sealed record CompleteMachineHistory(
    string MachineId,
    long RestoreEpoch,
    string HistoryDigest,
    bool CoversMachineAndAllocations,
    bool CoversPayments,
    bool CoversRevocations);

/// <summary>
/// The production history source is intentionally unavailable until an independently authenticated
/// complete provider is designed, deployed, and reviewed. No restored machine can be released by
/// treating the restored SQLite rows or local backup artifacts as independent history.
/// </summary>
public sealed class UnavailableExternalRecoveryHistory : IExternalRecoveryHistory
{
    public CompleteMachineHistory? TryGetCompleteMachineHistory(string machineId, long restoreEpoch) => null;
}
