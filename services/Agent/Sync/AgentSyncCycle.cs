using KamenRaider.Agent.Configuration;
using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Startup;
using KamenRaider.Contracts;

namespace KamenRaider.Agent.Sync;

public sealed class AgentSyncCycle(SyncClient client,Uri controller,IBootClock clock,AppliedConfiguration configuration,LeaseCommandDispatcher commands,HardwareInfo? hardware=null,Func<long>? appliedSunshineCredentialRevision=null,Action<long>? observeSunshineCredentialRevision=null) {
    private string? agentUpdateChallenge;
    public async Task RunAsync(string bootId,string localIp,string macAddress,Readiness readiness,CancellationToken ct,ReadinessDiagnostic[]? readinessDiagnostics=null) {
        var pairingOffer=readiness==Readiness.Ready?await commands.GetPairingOfferAsync(ct):null;
        var request=AgentSyncRequestFactory.Create(bootId,localIp,macAddress,configuration.Current?.Revision??0,readiness,commands.PendingResults,pairingOffer,readinessDiagnostics) with {
            Hardware=hardware,
            AppliedSunshineCredentialRevision=appliedSunshineCredentialRevision?.Invoke()
        };
        var verifiedConfigurationRevision=readiness==Readiness.Ready?configuration.Current?.Revision:null;
        SyncResponse response;long requestStartedUptimeMilliseconds;
        var challenges=0;
        while(true){
            requestStartedUptimeMilliseconds=clock.UptimeMilliseconds;
            var exchange=await client.SyncAsync(controller,request with { AgentUpdateChallenge=agentUpdateChallenge },ct);
            if(exchange is AgentSyncExchange.ChallengeRequired challenge){agentUpdateChallenge=challenge.Token;if(++challenges==2)throw new HttpRequestException("Sync challenge repeated; retry later");continue;}
            response=((AgentSyncExchange.Accepted)exchange).Response;break;
        }
        agentUpdateChallenge=response.AgentUpdateChallenge;
        // Authenticated result of this sync: hand the fleet Sunshine credential revision to the hosted
        // credential worker without blocking lease, configuration, or readiness processing.
        observeSunshineCredentialRevision?.Invoke(response.SunshineCredentialRevision);
        // Authenticated time remains useful when local configuration cannot be saved.
        if(readiness==Readiness.Ready)await commands.ObserveResponseTimeAsync(response,requestStartedUptimeMilliseconds,ct);
        else await commands.AcceptBlockedAsync(response,requestStartedUptimeMilliseconds,ct);
        try{await configuration.ApplyAsync(response.Configuration,ct);}
        catch{if(readiness==Readiness.Ready)await commands.BlockWithoutAcknowledgingAsync(ct);throw;}
        if(readiness==Readiness.Ready){
            var configurationStillVerified=verifiedConfigurationRevision is {} revision&&response.Configuration.Revision==revision&&configuration.Current?.Revision==revision;
            if(configurationStillVerified)await commands.AcceptObservedResponseAsync(response,ct);
            else await commands.AcceptAcknowledgedBlockedResponseAsync(ct);
        }
    }
}
