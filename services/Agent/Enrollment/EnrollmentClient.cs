using System.Text;
using System.Text.Json;
using KamenRaider.Agent.Identity;
using KamenRaider.Contracts;
namespace KamenRaider.Agent.Enrollment;
public sealed class EnrollmentClient(HttpClient client,DeviceIdentity identity)
{
    public async Task EnrollAsync(Uri controller,string localIp,string macAddress,int moonlightPort,int sunshinePort,CancellationToken token) {
        if(controller.Scheme!=Uri.UriSchemeHttps)throw new ArgumentException("HTTPS required");
        var body=JsonSerializer.Serialize(new EnrollmentRequest(identity.MachineId,identity.PublicKey,localIp,macAddress,moonlightPort,sunshinePort),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var timestamp=DateTimeOffset.UtcNow;var nonce=Guid.NewGuid().ToString("N");
        using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(controller,"/agent/enroll")) {Content=new StringContent(body,Encoding.UTF8,"application/json")};
        request.Headers.Add("X-Agent-Timestamp",timestamp.ToUnixTimeSeconds().ToString());request.Headers.Add("X-Agent-Nonce",nonce);request.Headers.Add("X-Agent-Signature",identity.Sign("POST","/agent/enroll",timestamp,nonce,body));
        using var response=await client.SendAsync(request,token);response.EnsureSuccessStatusCode();
    }
}
