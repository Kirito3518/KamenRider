using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KamenRaider.Contracts;
using KamenRaider.Controller;
using KamenRaider.Controller.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace Controller.Tests;
public class BootHttpTests {
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
}
