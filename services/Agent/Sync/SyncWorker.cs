using System.Net.NetworkInformation;
using System.Net.Sockets;
using KamenRaider.Agent.Identity;
using KamenRaider.Agent.Configuration;
using KamenRaider.Agent.Startup;
using Microsoft.Extensions.Options;
namespace KamenRaider.Agent.Sync;
public sealed class SyncWorker(IOptions<AgentOptions> options,ILogger<SyncWorker> logger,KamenRaider.Agent.Leases.IBootClock clock,KamenRaider.Agent.Leases.IStreamGate gate,LeaseCommandDispatcher commands):BackgroundService {
 protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
 var settings=options.Value;using var identity=DeviceIdentity.LoadOrCreate(settings.DataDirectory);var configuration=new AppliedConfiguration(settings.DataDirectory);
 using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(15)};var client=new SyncClient(http,identity);var bootId=clock.BootId;var failures=0;var reconciled=false;
 var reconciler=new BootReconciler(clock,gate,configuration,new ReconciliationClient(http,identity,new Uri(settings.ControllerUrl)),new UnavailableBootEnvironment(),false);
 while(!stoppingToken.IsCancellationRequested) {
 try {
 await BootSyncBarrier.RunAsync(async ()=>{if(reconciled&&clock.BootId==bootId)return Readiness.Ready;var readiness=await reconciler.ReconcileAsync(stoppingToken);reconciled=readiness==Readiness.Ready;return readiness;},async readiness=>{var adapter=NetworkInterface.GetAllNetworkInterfaces().Single(x=>x.Id==settings.NetworkAdapterId);var ip=adapter.GetIPProperties().UnicastAddresses.First(x=>x.Address.AddressFamily==AddressFamily.InterNetwork).Address.ToString();
 // A heartbeat reports presence, not proof of restoration, desktop health, or stream readiness.
 var requestStartedUptimeMilliseconds=clock.UptimeMilliseconds;
 var pairingOffer=readiness==Readiness.Ready?await commands.GetPairingOfferAsync(stoppingToken):null;var request=AgentSyncRequestFactory.Create(bootId,ip,adapter.GetPhysicalAddress().ToString(),configuration.Current?.Revision??0,readiness,commands.PendingResults,pairingOffer);
 var response=await client.SyncAsync(new(settings.ControllerUrl),request,stoppingToken);
 await configuration.ApplyAsync(response.Configuration,stoppingToken);failures=0;
 if(readiness==Readiness.Ready)await commands.AcceptAsync(response,requestStartedUptimeMilliseconds,stoppingToken);
 else await commands.AcceptBlockedAsync(response with{Commands=[],ActiveLease=null},requestStartedUptimeMilliseconds,stoppingToken);
 });
 }catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){return;}
 catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException or InvalidOperationException or NetworkInformationException or IOException or System.Text.Json.JsonException) {failures=Math.Min(failures+1,4);logger.LogWarning("Sync unavailable ({ErrorType}); retrying",ex.GetType().Name);}
 await Task.Delay(TimeSpan.FromSeconds(failures==0?5:Math.Min(60,5*(1<<failures))),stoppingToken);
 }
 }
}
