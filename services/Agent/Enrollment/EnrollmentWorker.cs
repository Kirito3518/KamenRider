using System.Net.NetworkInformation;
using System.Net.Sockets;
using KamenRaider.Agent.Identity;
using Microsoft.Extensions.Options;
namespace KamenRaider.Agent.Enrollment;
public sealed class EnrollmentWorker(IOptions<AgentOptions> options,ILogger<EnrollmentWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var settings=options.Value;
        using var identity=DeviceIdentity.LoadOrCreate(settings.DataDirectory);
        // Platform certificate validation is mandatory. Installation provisions the trusted controller certificate.
        using var http=new HttpClient {Timeout=TimeSpan.FromSeconds(15)};
        var client=new EnrollmentClient(http,identity);
        while(!stoppingToken.IsCancellationRequested) {
            try {
                var adapter=NetworkInterface.GetAllNetworkInterfaces().Single(x=>x.Id==settings.NetworkAdapterId);
                var address=adapter.GetIPProperties().UnicastAddresses.First(x=>x.Address.AddressFamily==AddressFamily.InterNetwork).Address.ToString();
                await client.EnrollAsync(new Uri(settings.ControllerUrl),address,adapter.GetPhysicalAddress().ToString(),settings.MoonlightPort,settings.SunshinePort,stoppingToken);
                return;
            } catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {return;}
            catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException or InvalidOperationException or NetworkInformationException) {
                // Do not log requests, credentials or private identity material.
                logger.LogWarning("Enrollment unavailable; retrying in 5 seconds ({ErrorType})",ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken);
        }
    }
}
