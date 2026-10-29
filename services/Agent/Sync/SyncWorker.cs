using System.Net.NetworkInformation;
using System.Net.Sockets;
using KamenRaider.Agent.Identity;
using KamenRaider.Agent.Configuration;
using Microsoft.Extensions.Options;
namespace KamenRaider.Agent.Sync;
public sealed class SyncWorker(IOptions<AgentOptions> options,ILogger<SyncWorker> logger,KamenRaider.Agent.Leases.IBootClock clock,LeaseCommandDispatcher commands):BackgroundService {
 protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
 var settings=options.Value;using var identity=DeviceIdentity.LoadOrCreate(settings.DataDirectory);var configuration=new AppliedConfiguration(settings.DataDirectory);
 using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(15)};var client=new SyncClient(http,identity);var bootId=clock.BootId;var failures=0;
 while(!stoppingToken.IsCancellationRequested) {
 try {var adapter=NetworkInterface.GetAllNetworkInterfaces().Single(x=>x.Id==settings.NetworkAdapterId);var ip=adapter.GetIPProperties().UnicastAddresses.First(x=>x.Address.AddressFamily==AddressFamily.InterNetwork).Address.ToString();
 // Readiness needs a future Sunshine health probe; a successful sync alone never proves ready.
 var requestStartedUptimeMilliseconds=clock.UptimeMilliseconds;
 var response=await client.SyncAsync(new(settings.ControllerUrl),new(bootId,ip,adapter.GetPhysicalAddress().ToString(),configuration.Current?.Revision??0,"unknown",commands.PendingResults),stoppingToken);
 await configuration.ApplyAsync(response.Configuration,stoppingToken);failures=0;
 await commands.AcceptAsync(response,requestStartedUptimeMilliseconds,stoppingToken);
 }catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){return;}
 catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException or InvalidOperationException or NetworkInformationException or IOException or System.Text.Json.JsonException) {failures=Math.Min(failures+1,4);logger.LogWarning("Sync unavailable ({ErrorType}); retrying",ex.GetType().Name);}
 await Task.Delay(TimeSpan.FromSeconds(failures==0?5:Math.Min(60,5*(1<<failures))),stoppingToken);
 }
 }
}
