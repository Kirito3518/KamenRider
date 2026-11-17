using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using KamenRaider.Agent.Hardware;
using KamenRaider.Agent.Identity;
using Microsoft.Extensions.Options;
namespace KamenRaider.Agent.Enrollment;
public sealed class EnrollmentWorker(IOptions<AgentOptions> options,ILogger<EnrollmentWorker> logger,HostHardwareCollector hardwareCollector):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var settings=options.Value;
        using var identity=DeviceIdentity.LoadOrCreate(settings.DataDirectory);
        // Platform certificate validation is mandatory. Installation provisions the trusted controller certificate.
        using var http=new HttpClient {Timeout=TimeSpan.FromSeconds(15)};
        var client=new EnrollmentClient(http,identity);
        var enrollmentTicket=new EnrollmentTicketStore(settings.DataDirectory);
        // Display-only hardware identity for the machine's initial name/specs; never gates enrollment.
        var hardware=hardwareCollector.Collect();
        while(!stoppingToken.IsCancellationRequested) {
            try {
                var adapter=NetworkInterface.GetAllNetworkInterfaces().Single(x=>x.Id==settings.NetworkAdapterId);
                var address=adapter.GetIPProperties().UnicastAddresses.First(x=>x.Address.AddressFamily==AddressFamily.InterNetwork).Address.ToString();
                var result=await client.EnrollAsync(new Uri(settings.ControllerUrl),address,adapter.GetPhysicalAddress().ToString(),settings.MoonlightPort,settings.SunshinePort,enrollmentTicket.ReadIfPresent(),hardware,stoppingToken);
                var fingerprint=Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(identity.PublicKey)));
                await new EnrollmentReceiptStore(settings.DataDirectory).SaveAsync(result,fingerprint,stoppingToken);
                enrollmentTicket.Clear();
                return;
            } catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {return;}
            catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException or InvalidOperationException or NetworkInformationException or IOException or System.Text.Json.JsonException) {
                // Do not log requests, credentials or private identity material.
                logger.LogWarning("Enrollment unavailable; retrying in 5 seconds ({ErrorType})",ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken);
        }
    }
}
