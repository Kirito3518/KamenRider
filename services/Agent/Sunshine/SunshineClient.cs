using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
namespace KamenRaider.Agent.Sunshine;
// Low-level adapter for the researched current schema; deliberately not registered until target qualification.
public sealed class SunshineClient:ISunshinePairingApi,IDisposable {
 private readonly HttpClient http;
 public SunshineClient(Uri endpoint,NetworkCredential credential,HttpMessageHandler? transport=null){
  if(!endpoint.IsAbsoluteUri||endpoint.Scheme!="https"||!(endpoint.Host=="localhost"||IPAddress.TryParse(endpoint.Host.Trim('[',']'),out var ip)&&IPAddress.IsLoopback(ip))||endpoint.AbsolutePath!="/"||endpoint.Query!=""||endpoint.Fragment!=""||endpoint.UserInfo!="")throw new ArgumentException("Sunshine requires a localhost HTTPS origin");
  if(string.IsNullOrWhiteSpace(credential.UserName)||string.IsNullOrEmpty(credential.Password)||credential.UserName.Contains(':'))throw new ArgumentException("Sunshine credentials required");
  // Default chain/name/expiry validation is mandatory. Provision a trusted localhost certificate; never bypass TLS.
  http=new HttpClient(transport??new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false}){BaseAddress=endpoint,Timeout=TimeSpan.FromSeconds(10),MaxResponseContentBufferSize=65536};
  http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.UserName+":"+credential.Password)));
 }
 // An authenticated API response cannot prove capture/desktop readiness or a closed public stream surface.
 public Task<bool> CheckReadyAsync(CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(false);}
 private static bool ValidId(string id)=>id.Length==32&&id.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
 public async Task<string[]> PendingIdsAsync(CancellationToken ct){
  using var response=await http.GetAsync("/api/pin",ct);response.EnsureSuccessStatusCode();
  try{using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));var rows=json.RootElement.GetProperty("pairings");if(rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>100)throw new InvalidDataException("Unsupported Sunshine pending schema");
   var ids=rows.EnumerateArray().Select(row=>row.GetProperty("id").GetString()??"").ToArray();
   if(ids.Any(id=>!ValidId(id))||ids.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=ids.Length)throw new InvalidDataException("Invalid or ambiguous Sunshine pending IDs");return ids;
  }catch(Exception ex) when(ex is JsonException or KeyNotFoundException or InvalidOperationException){throw new InvalidDataException("Unsupported Sunshine pending schema");}
 }
 public async Task<bool> SubmitAsync(string id,string pin,CancellationToken ct){
  if(!ValidId(id)||pin.Length!=4||pin.Any(c=>c<'0'||c>'9'))return false;
  return await MutationAsync(HttpMethod.Post,"/api/pin",new{pairing_id=id,pin,name="Rental client"},ct);
 }
 private async Task<bool> MutationAsync(HttpMethod method,string path,object payload,CancellationToken ct){
  using var request=new HttpRequestMessage(method,path){Content=JsonContent.Create(payload)};
  using var response=await http.SendAsync(request,ct);response.EnsureSuccessStatusCode();
  try{using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));return json.RootElement.TryGetProperty("status",out var status)&&status.ValueKind==JsonValueKind.True;}
  catch(JsonException){return false;}
 }
 // Caller must already fence new connections and in-flight handshakes before this sequence.
 public async Task ClearAsync(CancellationToken ct){
  foreach(var id in await PendingIdsAsync(ct))if(!await MutationAsync(HttpMethod.Delete,"/api/pin",new{pairing_id=id},ct))throw new IOException("Sunshine cancellation failed");
  if(!await MutationAsync(HttpMethod.Post,"/api/clients/unpair-all",new{},ct)||!await MutationAsync(HttpMethod.Post,"/api/apps/close",new{},ct))throw new IOException("Sunshine cleanup failed");
  if((await PendingIdsAsync(ct)).Length!=0)throw new IOException("Sunshine pending requests remain");
 }
 public void Dispose()=>http.Dispose();
}
