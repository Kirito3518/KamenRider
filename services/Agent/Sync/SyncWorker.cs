using System.Net.NetworkInformation;
using System.Net.Sockets;
using KamenRaider.Agent.Hardware;
using KamenRaider.Agent.Identity;
using KamenRaider.Agent.Configuration;
using KamenRaider.Agent.Startup;
using KamenRaider.Agent.Sunshine;
using Microsoft.Extensions.Options;
namespace KamenRaider.Agent.Sync;
public sealed class SyncWorker(IOptions<AgentOptions> options,ILogger<SyncWorker> logger,KamenRaider.Agent.Leases.IBootClock clock,AppliedConfiguration configuration,KamenRaider.Agent.Sunshine.ISunshineLiveness liveness,LeaseCommandDispatcher commands,IBootEnvironment bootEnvironment,IBootBaselineEvidence baselineEvidence,IReconciledLeaseImporter leaseImporter,HostHardwareCollector hardwareCollector,SunshineAppliedCredentialStore appliedSunshineCredentialStore,SunshineCredentialSyncSignal sunshineCredentialSignal,KamenRaider.Agent.Parsec.ParsecCapabilityProvider parsecCapability):BackgroundService {
 protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
 var settings=options.Value;using var identity=DeviceIdentity.LoadOrCreate(settings.DataDirectory);
 using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(15)};var client=new SyncClient(http,identity);var bootId=clock.BootId;var failures=0;var reconciled=false;
 // Display-only hardware identity; collected once per process start and attached to every heartbeat.
 var hardware=hardwareCollector.Collect();
 var cycle=new AgentSyncCycle(client,new(settings.ControllerUrl),clock,configuration,commands,hardware,ReadAppliedSunshineCredentialRevision,sunshineCredentialSignal.Publish,() => { var current=parsecCapability.Current; return new KamenRaider.Contracts.ParsecCapability(current.BootKeyId ?? "", current.PublicKey ?? "", current.Available); });
 var reconciler=new BootReconciler(clock,configuration,new ReconciliationClient(http,identity,new Uri(settings.ControllerUrl)),bootEnvironment,leaseImporter,baselineEvidence,liveness);
 long ReadAppliedSunshineCredentialRevision() {
  try {return appliedSunshineCredentialStore.Read();}
  catch(Exception ex) {
   // A missing file means revision 0; a broken file must not block heartbeat.
   logger.LogWarning("Applied Sunshine credential revision is unavailable ({ErrorType}); reporting 0",ex.GetType().Name);
   return 0;
  }
 }
 while(!stoppingToken.IsCancellationRequested) {
 try {
 await BootSyncBarrier.RunAsync(async ()=>{if(reconciled&&clock.BootId==bootId){var runtime=await reconciler.RevalidateRuntimeAsync(stoppingToken);if(runtime!=Readiness.Ready)reconciled=reconciler.HasRuntimeEvidence;return runtime;}var readiness=await reconciler.ReconcileAsync(stoppingToken);reconciled=readiness==Readiness.Ready;if(reconciled)bootId=clock.BootId;return readiness;},async readiness=>{var adapter=NetworkInterface.GetAllNetworkInterfaces().Single(x=>x.Id==settings.NetworkAdapterId);var ip=adapter.GetIPProperties().UnicastAddresses.First(x=>x.Address.AddressFamily==AddressFamily.InterNetwork).Address.ToString();
  // A heartbeat reports presence, not proof of restoration, desktop health, or stream readiness.
  await cycle.RunAsync(bootId,ip,adapter.GetPhysicalAddress().ToString(),readiness,stoppingToken,reconciler.ReadinessDiagnostics);failures=0;
 });
 }catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){return;}
 catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException or InvalidOperationException or NetworkInformationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {failures=Math.Min(failures+1,4);logger.LogWarning("Sync unavailable ({ErrorType}); retrying",ex.GetType().Name);}
 await Task.Delay(TimeSpan.FromSeconds(failures==0?5:Math.Min(60,5*(1<<failures))),stoppingToken);
 }
 }
}
