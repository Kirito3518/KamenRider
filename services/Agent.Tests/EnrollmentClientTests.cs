using System.Net;
using System.Text.Json;
using KamenRaider.Agent.Identity;
using KamenRaider.Agent.Enrollment;
using KamenRaider.Controller.Security;
namespace Agent.Tests;
public class EnrollmentClientTests
{
    [Fact] public async Task EnrollmentSignsMetadataAndRejectsHttp() {
        var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
        using(var identity=DeviceIdentity.LoadOrCreate(dir)) {
            using var handler=new VerifyHandler(identity);using var client=new HttpClient(handler);
            var enrollment=new EnrollmentClient(client,identity);
            await enrollment.EnrollAsync(new Uri("https://controller:5102"),"192.0.2.1","001122334455",47989,47990,CancellationToken.None);
            Assert.True(handler.Verified);
            await Assert.ThrowsAsync<ArgumentException>(()=>enrollment.EnrollAsync(new Uri("http://controller"),"192.0.2.1","001122334455",1,2,CancellationToken.None));
        }
        Directory.Delete(dir,true);
    }
    private sealed class VerifyHandler(DeviceIdentity identity):HttpMessageHandler {
        public bool Verified {get;private set;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
            var body=await request.Content!.ReadAsStringAsync(token);
            var timestamp=DateTimeOffset.FromUnixTimeSeconds(long.Parse(request.Headers.GetValues("X-Agent-Timestamp").Single()));
            Verified=new AgentAuthentication().Verify(identity.MachineId,identity.PublicKey,"POST","/agent/enroll",timestamp,request.Headers.GetValues("X-Agent-Nonce").Single(),body,request.Headers.GetValues("X-Agent-Signature").Single(),DateTimeOffset.UtcNow);
            Assert.Equal("192.0.2.1",JsonDocument.Parse(body).RootElement.GetProperty("localIp").GetString());
            return new HttpResponseMessage(Verified?HttpStatusCode.OK:HttpStatusCode.Unauthorized);
        }
    }
}
