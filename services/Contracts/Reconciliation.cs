namespace KamenRaider.Contracts;
public record ReconciliationChallenge(string Token,string BootId,string? AllocationId,long? LeaseRevision,DateTimeOffset ExpiresAtUtc);
public record ReconcileRequest(string BootId,long AppliedConfigRevision,bool BaselineValidated,bool RestorationVerified=false,bool FencedCleanupVerified=false,bool HealthVerified=false,ReconciliationChallenge? Challenge=null,bool StreamReady=false);
public record ReconcileResponse(DateTimeOffset ServerTimeUtc,ConfigurationDto Configuration,ActiveLeaseConfirmation? Lease,string Action,ReconciliationChallenge? Challenge=null);
