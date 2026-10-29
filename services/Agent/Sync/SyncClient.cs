using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KamenRaider.Agent.Identity;
using KamenRaider.Contracts;
namespace KamenRaider.Agent.Sync;
public sealed class SyncClient(HttpClient http,DeviceIdentity identity) {
 public async Task<SyncResponse> SyncAsync(Uri controller,SyncRequest heartbeat,CancellationToken ct) {
 var body=JsonSerializer.Serialize(heartbeat,new JsonSerializerOptions(JsonSerializerDefaults.Web));var timestamp=DateTimeOffset.UtcNow;var nonce=Guid.NewGuid().ToString("N");
 using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(controller,"/agent/sync")){Content=new StringContent(body,Encoding.UTF8,"application/json")};
 request.Headers.Add("X-Agent-Id",identity.MachineId);request.Headers.Add("X-Agent-Timestamp",timestamp.ToUnixTimeSeconds().ToString());request.Headers.Add("X-Agent-Nonce",nonce);request.Headers.Add("X-Agent-Signature",identity.Sign("POST","/agent/sync",timestamp,nonce,body));
 using var response=await http.SendAsync(request,ct);response.EnsureSuccessStatusCode();return await response.Content.ReadFromJsonAsync<SyncResponse>(ct)??throw new InvalidDataException("Missing sync response");
 }
}
