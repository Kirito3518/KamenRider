namespace KamenRaider.Contracts;
public record ReconcileRequest(string BootId,long AppliedConfigRevision,bool BaselineValidated,bool RestorationVerified=false,bool FencedCleanupVerified=false,bool HealthVerified=false);
public record ReconcileResponse(DateTimeOffset ServerTimeUtc,ConfigurationDto Configuration,ActiveLeaseConfirmation? Lease,string Action);
