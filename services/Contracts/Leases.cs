namespace KamenRaider.Contracts;
public record LeaseDto(string Id,string UserId,string MachineId,string State,long Revision,DateTimeOffset? StartsAtUtc,DateTimeOffset? ExpiresAtUtc,long? PendingRevision,DateTimeOffset? EndedAtUtc) {
    /// <summary>Set when the renter presses Power; null means the lease is waiting for that press.</summary>
    public DateTimeOffset? WakeRequestedAtUtc { get; init; }
    /// <summary>True while the renter may still request the Wake-on-LAN power-on.</summary>
    public bool CanRequestPower { get; init; }
    /// <summary>Seconds remaining before an unpowered lease is abandoned (null otherwise).</summary>
    public int? PowerDeadlineRemainingSeconds { get; init; }
}
public record ActiveLeaseConfirmation(string RentalId,long LeaseRevision,string BootId,DateTimeOffset ExpiresAtUtc);
public record CreateLeaseRequest(string UserId,string MachineId,int DurationSeconds,string IdempotencyKey);
public record ExtendLeaseRequest(int AdditionalSeconds,long ExpectedRevision,string IdempotencyKey);
public record LeasePowerRequest(string RequestId);
public record LeaseCancelRequest(long ExpectedRevision,string IdempotencyKey);
