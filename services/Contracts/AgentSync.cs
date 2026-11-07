using System.Collections.ObjectModel;

namespace KamenRaider.Contracts;
public record SyncRequest(string BootId,string LocalIp,string MacAddress,long AppliedConfigRevision,string Readiness,CommandResult[] CommandResults,PairingOffer? PendingPairing=null) {
    public string? AgentVersion { get; init; }
    public ReadinessDiagnostic[]? ReadinessDiagnostics { get; init; }
}
public record ReadinessDiagnostic(string CheckId,string Status);
public static class ReadinessDiagnosticIds {
    public const string Baseline="baseline";
    public const string BootChallenge="bootChallenge";
    public const string Restoration="restoration";
    public const string StreamFence="streamFence";
    public const string DesktopHealth="desktopHealth";
    public const string ConfigurationRevision="configurationRevision";
    public const string LeaseReconciliation="leaseReconciliation";
    public const string RuntimeHealth="runtimeHealth";
    public static ReadOnlyCollection<string> Canonical { get; } = Array.AsReadOnly(new[] { Baseline,BootChallenge,Restoration,StreamFence,DesktopHealth,ConfigurationRevision,LeaseReconciliation,RuntimeHealth });
}
public static class ReadinessDiagnosticStatuses {
    public const string Passed="passed";
    public const string Blocked="blocked";
    public const string Unavailable="unavailable";
    public const string Unknown="unknown";
}
public record ConfigurationDto(long Revision,int MoonlightPort,int SunshinePort);
public record SyncResponse(DateTimeOffset ServerTimeUtc,ConfigurationDto Configuration,CommandEnvelope[] Commands,ActiveLeaseConfirmation? ActiveLease=null);
