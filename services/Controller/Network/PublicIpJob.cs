using System.Net;
using System.Net.Sockets;
namespace KamenRaider.Controller.Network;
public sealed class PublicIpJob(HttpClient http,Uri endpoint):BackgroundService {
 public string? Address {get;private set;}
 public bool Stale {get;private set;}=true;
 public async Task RefreshAsync(CancellationToken ct) {
 try {using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));
 var text=(await http.GetStringAsync(endpoint,timeout.Token)).Trim();
 if(!IPAddress.TryParse(text,out var ip)||ip.AddressFamily!=AddressFamily.InterNetwork||!Routable(ip)) {Stale=true;return;}
 Address=ip.ToString();Stale=false;
 }catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException){if(ct.IsCancellationRequested)throw;Stale=true;}
 }
 private static bool Routable(IPAddress ip) {var b=ip.GetAddressBytes();return !(b[0] is 0 or 10 or 127 || b[0]>=224 || b[0]==169&&b[1]==254 || b[0]==172&&b[1]>=16&&b[1]<=31 || b[0]==192&&(b[1]==168||b[1]==0||b[1]==2) || b[0]==100&&b[1]>=64&&b[1]<=127 || b[0]==198&&(b[1] is 18 or 19 or 51) || b[0]==203&&b[1]==0&&b[2]==113);}
 protected override async Task ExecuteAsync(CancellationToken stoppingToken) {while(!stoppingToken.IsCancellationRequested){await RefreshAsync(stoppingToken);await Task.Delay(TimeSpan.FromMinutes(5),stoppingToken);}}
}
