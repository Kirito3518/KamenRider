using System.Net;
using System.Net.Http.Json;
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
public class SyncHttpTests {
 [Fact] public async Task SyncRequiresStoredKeyAndWakeDoesNotMakeMachineReady() {
 var file=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
 await using(var app=ControllerHost.Build([],b=>{b.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{["InternalUrl"]="http://127.0.0.1:5101",["AgentUrl"]="https://127.0.0.1:5102",["DatabasePath"]=file,["ServiceCredential"]="secret",["WakeBroadcastAddress"]="192.0.2.255"});b.WebHost.UseTestServer();b.Services.AddTransient<IStartupFilter,LoopbackTestFilter>();b.Services.AddSingleton<KamenRaider.Controller.Network.IWakeSender>(new NoNetwork());})) {
 await app.StartAsync();using var key=ECDsa.Create();using var wrong=ECDsa.Create();app.Services.GetRequiredService<ControllerDatabase>().Enroll(new("pc",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2));
 using var client=app.GetTestClient();client.BaseAddress=new("https://localhost");client.DefaultRequestHeaders.Add("Test-Local-Port","5102");
 foreach(var signer in new[]{wrong,key}) {var body=JsonSerializer.Serialize(new SyncRequest("boot","192.0.2.1","001122334455",1,"notReady",[]),new JsonSerializerOptions(JsonSerializerDefaults.Web));var at=DateTimeOffset.UtcNow.ToUnixTimeSeconds();var nonce=Guid.NewGuid().ToString();using var req=new HttpRequestMessage(HttpMethod.Post,"/agent/sync"){Content=new StringContent(body,Encoding.UTF8,"application/json")};req.Headers.Add("X-Agent-Id","pc");req.Headers.Add("X-Agent-Timestamp",at.ToString());req.Headers.Add("X-Agent-Nonce",nonce);req.Headers.Add("X-Agent-Signature",Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes($"pc\nPOST\n/agent/sync\n{at}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}"),HashAlgorithmName.SHA256)));Assert.Equal(signer==key?HttpStatusCode.OK:HttpStatusCode.Unauthorized,(await client.SendAsync(req)).StatusCode);}
 client.DefaultRequestHeaders.Remove("Test-Local-Port");client.DefaultRequestHeaders.Add("X-Service-Credential","secret");Assert.Equal(HttpStatusCode.OK,(await client.PostAsync("/internal/machines/pc/wake",null)).StatusCode);Assert.Equal("notReady",Assert.Single((await client.GetFromJsonAsync<MachineDto[]>("/internal/machines"))!).Readiness);await app.StopAsync();
 }File.Delete(file);
 }
 sealed class NoNetwork:KamenRaider.Controller.Network.IWakeSender {public Task SendAsync(string mac,string broadcast,CancellationToken ct) {Assert.Equal("001122334455",mac);Assert.Equal("192.0.2.255",broadcast);return Task.CompletedTask;}}
}

