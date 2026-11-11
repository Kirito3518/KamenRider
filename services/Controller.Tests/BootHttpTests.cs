using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KamenRaider.Contracts;
using KamenRaider.Controller;
using KamenRaider.Controller.Data;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace Controller.Tests;
public class BootHttpTests {
 [Fact] public async Task ReconciliationNonceReplayIsRejectedAfterControllerRestart() {
  var file=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");using var key=ECDsa.Create();var clock=new FixedClock();string? originalChallenge=null;
  var requestBody=JsonSerializer.Serialize(new ReconcileRequest("boot",1,false),new JsonSerializerOptions(JsonSerializerDefaults.Web));
  var seconds=clock.Now.ToUnixTimeSeconds();var nonce=Guid.NewGuid().ToString("N");
  var signature=Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes($"pc\nPOST\n/agent/reconcile\n{seconds}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestBody)))}"),HashAlgorithmName.SHA256));
  await using(var first=Build(file,clock)) {
   await first.StartAsync();first.Services.GetRequiredService<ControllerDatabase>().EnrollWithTicket(new("pc",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2));
   using var client=first.GetTestClient();client.BaseAddress=new("https://localhost");client.DefaultRequestHeaders.Add("Test-Local-Port","5102");
   using var response=await Send(client);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
   var challenge=(await response.Content.ReadFromJsonAsync<ReconcileResponse>())!.Challenge;
   Assert.NotNull(challenge);
   originalChallenge=challenge.Token;
   Assert.Equal(challenge.Token,ReadChallenge());
   await first.StopAsync();
  }
  await using(var second=Build(file,clock)) {
   await second.StartAsync();using var client=second.GetTestClient();client.BaseAddress=new("https://localhost");client.DefaultRequestHeaders.Add("Test-Local-Port","5102");
   using var replay=await Send(client);Assert.Equal(HttpStatusCode.Unauthorized,replay.StatusCode);
   Assert.Equal(originalChallenge,ReadChallenge());
   await second.StopAsync();
  }
  File.Delete(file);

  HttpRequestMessage CreateRequest() {
   var message=new HttpRequestMessage(HttpMethod.Post,"/agent/reconcile"){Content=new StringContent(requestBody,Encoding.UTF8,"application/json")};
   message.Headers.Add("X-Agent-Id","pc");message.Headers.Add("X-Agent-Timestamp",seconds.ToString());message.Headers.Add("X-Agent-Nonce",nonce);message.Headers.Add("X-Agent-Signature",signature);return message;
  }
  async Task<HttpResponseMessage> Send(HttpClient client) { using var message=CreateRequest();return await client.SendAsync(message); }
  string ReadChallenge() {
   using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file,Pooling=false}.ToString());connection.Open();
   using var command=connection.CreateCommand();command.CommandText="SELECT token FROM boot_reconciliation_challenges WHERE machine_id='pc'";return (string)command.ExecuteScalar()!;
  }
 }

 [Fact] public async Task ReconciliationRequiresRegisteredSignatureAndCannotTreatBaselineAsRestoration() {
  var file=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
  await using(var app=ControllerHost.Build([],b=>{b.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{["InternalUrl"]="http://127.0.0.1:5101",["AgentUrl"]="https://127.0.0.1:5102",["DatabasePath"]=file,["ServiceCredential"]="secret"});b.WebHost.UseTestServer();b.Services.AddTransient<IStartupFilter,LoopbackTestFilter>();})) {
   await app.StartAsync();using var key=ECDsa.Create();using var wrong=ECDsa.Create();var db=app.Services.GetRequiredService<ControllerDatabase>();db.EnrollWithTicket(new("pc",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2));db.Update("pc",new("PC",1,1,2),1);db.Approve("pc","admin");
   using var client=app.GetTestClient();client.BaseAddress=new("https://localhost");client.DefaultRequestHeaders.Add("Test-Local-Port","5102");
   foreach(var signer in new[]{wrong,key}) {
    var body=JsonSerializer.Serialize(new ReconcileRequest("boot",3,true),new JsonSerializerOptions(JsonSerializerDefaults.Web));var at=DateTimeOffset.UtcNow.ToUnixTimeSeconds();var nonce=Guid.NewGuid().ToString();
    using var request=new HttpRequestMessage(HttpMethod.Post,"/agent/reconcile"){Content=new StringContent(body,Encoding.UTF8,"application/json")};request.Headers.Add("X-Agent-Id","pc");request.Headers.Add("X-Agent-Timestamp",at.ToString());request.Headers.Add("X-Agent-Nonce",nonce);request.Headers.Add("X-Agent-Signature",Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes($"pc\nPOST\n/agent/reconcile\n{at}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}"),HashAlgorithmName.SHA256)));
    using var response=await client.SendAsync(request);Assert.Equal(signer==key?HttpStatusCode.OK:HttpStatusCode.Unauthorized,response.StatusCode);if(signer==key){var result=JsonSerializer.Deserialize<ReconcileResponse>(await response.Content.ReadAsStringAsync(),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;Assert.Equal("Hold",result.Action);Assert.Null(result.Lease);}
   }await app.StopAsync();
  }File.Delete(file);
 }

 private static WebApplication Build(string path,TimeProvider clock)=>ControllerHost.Build([],b=>{
  b.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{["InternalUrl"]="http://127.0.0.1:5101",["AgentUrl"]="https://127.0.0.1:5102",["DatabasePath"]=path,["ServiceCredential"]="secret"});b.WebHost.UseTestServer();b.Services.AddTransient<IStartupFilter,LoopbackTestFilter>();b.Services.AddSingleton<TimeProvider>(clock);
 });

 private sealed class FixedClock:TimeProvider {
  public DateTimeOffset Now {get;}=DateTimeOffset.UtcNow;
  public override DateTimeOffset GetUtcNow()=>Now;
 }
}
