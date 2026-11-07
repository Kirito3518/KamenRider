using KamenRaider.Agent.Startup;
using KamenRaider.Contracts;
namespace KamenRaider.Agent.Sync;
public static class AgentSyncRequestFactory {
 public static SyncRequest Create(string bootId,string localIp,string macAddress,long appliedConfigRevision,Readiness readiness,CommandResult[] commandResults,PairingOffer? pendingPairing,ReadinessDiagnostic[]? readinessDiagnostics=null){var ready=readiness==Readiness.Ready;return new(bootId,localIp,macAddress,appliedConfigRevision,ready?"ready":"notReady",commandResults,ready?pendingPairing:null){AgentVersion=AgentBuildVersion.Current,ReadinessDiagnostics=readinessDiagnostics?.ToArray()};}
}
