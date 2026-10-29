using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KamenRaider.Agent.Identity;
using KamenRaider.Contracts;
namespace KamenRaider.Agent.Startup;
public sealed class ReconciliationClient(HttpClient http,DeviceIdentity identity,Uri controller):IBootReconciliationClient {
 public async Task<ReconcileResponse> ReconcileAsync(ReconcileRequest value,CancellationToken ct) {
  const string path="/agent/reconcile";var body=JsonSerializer.Serialize(value,new JsonSerializerOptions(JsonSerializerDefaults.Web));var timestamp=DateTimeOffset.UtcNow;var nonce=Guid.NewGuid().ToString("N");
  using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(controller,path)){Content=new StringContent(body,Encoding.UTF8,"application/json")};
  request.Headers.Add("X-Agent-Id",identity.MachineId);request.Headers.Add("X-Agent-Timestamp",timestamp.ToUnixTimeSeconds().ToString());request.Headers.Add("X-Agent-Nonce",nonce);request.Headers.Add("X-Agent-Signature",identity.Sign("POST",path,timestamp,nonce,body));
  using var response=await http.SendAsync(request,ct);response.EnsureSuccessStatusCode();return await response.Content.ReadFromJsonAsync<ReconcileResponse>(ct)??throw new InvalidDataException("Missing reconciliation response");
 }
}
