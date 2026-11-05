using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using KamenRaider.Agent.Identity;
using KamenRaider.Contracts;
namespace KamenRaider.Agent.Enrollment;
public sealed class EnrollmentClient(HttpClient client,DeviceIdentity identity)
{
    public async Task<EnrollmentResult> EnrollAsync(Uri controller,string localIp,string macAddress,int moonlightPort,int sunshinePort,string? enrollmentTicket,CancellationToken token) {
        if(controller.Scheme!=Uri.UriSchemeHttps)throw new ArgumentException("HTTPS required");
        if(enrollmentTicket is not null&&!System.Text.RegularExpressions.Regex.IsMatch(enrollmentTicket,@"\A[A-Za-z0-9_-]{43}\z"))throw new ArgumentException("Invalid enrollment ticket",nameof(enrollmentTicket));
        var body=JsonSerializer.Serialize(new EnrollmentRequest(identity.MachineId,identity.PublicKey,localIp,macAddress,moonlightPort,sunshinePort),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var timestamp=DateTimeOffset.UtcNow;var nonce=Guid.NewGuid().ToString("N");
        using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(controller,"/agent/enroll")) {Content=new StringContent(body,Encoding.UTF8,"application/json")};
        request.Headers.Add("X-Agent-Timestamp",timestamp.ToUnixTimeSeconds().ToString());request.Headers.Add("X-Agent-Nonce",nonce);request.Headers.Add("X-Agent-Signature",identity.Sign("POST","/agent/enroll",timestamp,nonce,body));
        if(enrollmentTicket is not null)request.Headers.Add("X-Enrollment-Ticket",enrollmentTicket);
        using var response=await client.SendAsync(request,token);response.EnsureSuccessStatusCode();
        var result=await response.Content.ReadFromJsonAsync<EnrollmentResult>(cancellationToken:token);
        if(result is null||result.MachineId!=identity.MachineId||result.ApprovalStatus is not ("pending" or "approved"))throw new InvalidDataException("Invalid enrollment acknowledgement");
        return result;
    }
}
