using KamenRaider.Controller.Network;
using System.Net;
namespace Controller.Tests;
public class NetworkTests {
 [Fact] public void MagicPacketRepeatsSixByteAddressSixteenTimes() {var packet=WakeService.MagicPacket("00:11:22:33:44:55");Assert.Equal(102,packet.Length);Assert.All(packet[..6],b=>Assert.Equal(255,b));for(int i=6;i<102;i+=6)Assert.Equal(new byte[]{0,17,34,51,68,85},packet[i..(i+6)]);}
 sealed class Handler:HttpMessageHandler {public string Value="8.8.8.8";public bool Timeout;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken c)=>Timeout?Task.FromException<HttpResponseMessage>(new TaskCanceledException()):Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Value)});}
 [Fact] public async Task InvalidOrTimedOutPublicIpKeepsLastValueAndMarksStale() {var handler=new Handler();using var http=new HttpClient(handler);var job=new PublicIpJob(http,new Uri("https://example.test/ip"));await job.RefreshAsync(default);Assert.Equal("8.8.8.8",job.Address);Assert.False(job.Stale);handler.Value="192.168.1.1";await job.RefreshAsync(default);Assert.Equal("8.8.8.8",job.Address);Assert.True(job.Stale);handler.Timeout=true;await job.RefreshAsync(default);Assert.Equal("8.8.8.8",job.Address);Assert.True(job.Stale);}
}
