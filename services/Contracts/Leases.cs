namespace KamenRaider.Contracts;
public record LeaseDto(string Id,string UserId,string MachineId,string State,long Revision,DateTimeOffset? StartsAtUtc,DateTimeOffset? ExpiresAtUtc,long? PendingRevision);
public record ActiveLeaseConfirmation(string RentalId,long LeaseRevision,string BootId,DateTimeOffset ExpiresAtUtc);
public record TrialLeaseRequest(string UserId,string MachineId,int DurationSeconds,string IdempotencyKey);
public record ExtendLeaseRequest(int AdditionalSeconds,long ExpectedRevision,string IdempotencyKey);
