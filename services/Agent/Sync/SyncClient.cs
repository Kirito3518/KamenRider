using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KamenRaider.Agent.Identity;
using KamenRaider.Contracts;
namespace KamenRaider.Agent.Sync;
public abstract record AgentSyncExchange {
 public sealed record Accepted(SyncResponse Response):AgentSyncExchange;
 public sealed record ChallengeRequired(string Token):AgentSyncExchange;
}
public sealed class SyncClient(HttpClient http,DeviceIdentity identity) {
 public async Task<AgentSyncExchange> SyncAsync(Uri controller,SyncRequest heartbeat,CancellationToken ct) {
 var body=JsonSerializer.Serialize(heartbeat with{SyncProofProtocol=2},new JsonSerializerOptions(JsonSerializerDefaults.Web));var timestamp=DateTimeOffset.UtcNow;var nonce=Guid.NewGuid().ToString("N");
 using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(controller,"/agent/sync")){Content=new StringContent(body,Encoding.UTF8,"application/json")};
 request.Headers.Add("X-Agent-Id",identity.MachineId);request.Headers.Add("X-Agent-Timestamp",timestamp.ToUnixTimeSeconds().ToString());request.Headers.Add("X-Agent-Nonce",nonce);request.Headers.Add("X-Agent-Signature",identity.Sign("POST","/agent/sync",timestamp,nonce,body));
 using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);if(http.Timeout!=Timeout.InfiniteTimeSpan)timeout.CancelAfter(http.Timeout);var requestCt=timeout.Token;
 using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,requestCt);
 if(response.StatusCode==HttpStatusCode.PreconditionRequired){
  const int maxBytes=4096;var buffer=new byte[maxBytes+1];var length=0;
  using var stream=await response.Content.ReadAsStreamAsync(requestCt);
  while(length<buffer.Length){var read=await stream.ReadAsync(buffer.AsMemory(length),requestCt);if(read==0)break;length+=read;}
  if(length>maxBytes)throw new HttpRequestException("Sync challenge response is too large");
  var challenge=JsonSerializer.Deserialize<SyncChallengeRequired>(buffer.AsSpan(0,length),new JsonSerializerOptions(JsonSerializerDefaults.Web))?.AgentUpdateChallenge;
  if(string.IsNullOrWhiteSpace(challenge)||challenge.Length>128)throw new HttpRequestException("Invalid sync challenge");
  return new AgentSyncExchange.ChallengeRequired(challenge);
 }
 response.EnsureSuccessStatusCode();
 if(response.StatusCode!=HttpStatusCode.OK)throw new HttpRequestException("Unexpected sync response status",null,response.StatusCode);
 return new AgentSyncExchange.Accepted(await response.Content.ReadFromJsonAsync<SyncResponse>(requestCt)??throw new InvalidDataException("Missing sync response"));
 }
}
