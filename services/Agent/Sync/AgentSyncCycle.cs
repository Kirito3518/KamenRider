using KamenRaider.Agent.Configuration;
using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Startup;

namespace KamenRaider.Agent.Sync;

public sealed class AgentSyncCycle(SyncClient client,Uri controller,IBootClock clock,AppliedConfiguration configuration,LeaseCommandDispatcher commands) {
    public async Task RunAsync(string bootId,string localIp,string macAddress,Readiness readiness,CancellationToken ct) {
        var pairingOffer=readiness==Readiness.Ready?await commands.GetPairingOfferAsync(ct):null;
        var request=AgentSyncRequestFactory.Create(bootId,localIp,macAddress,configuration.Current?.Revision??0,readiness,commands.PendingResults,pairingOffer);
        var requestStartedUptimeMilliseconds=clock.UptimeMilliseconds;
        var response=await client.SyncAsync(controller,request,ct);
        // Authenticated time remains useful when local configuration cannot be saved.
        if(readiness==Readiness.Ready)await commands.ObserveResponseTimeAsync(response,requestStartedUptimeMilliseconds,ct);
        else await commands.AcceptBlockedAsync(response,requestStartedUptimeMilliseconds,ct);
        await configuration.ApplyAsync(response.Configuration,ct);
        if(readiness==Readiness.Ready)await commands.AcceptObservedResponseAsync(response,ct);
    }
}
