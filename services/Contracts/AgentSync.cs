namespace KamenRaider.Contracts;
public record SyncRequest(string BootId,string LocalIp,string MacAddress,long AppliedConfigRevision,string Readiness,CommandResult[] CommandResults,PairingOffer? PendingPairing=null) {
    public string? AgentVersion { get; init; }
}
public record ConfigurationDto(long Revision,int MoonlightPort,int SunshinePort);
public record SyncResponse(DateTimeOffset ServerTimeUtc,ConfigurationDto Configuration,CommandEnvelope[] Commands,ActiveLeaseConfirmation? ActiveLease=null);
