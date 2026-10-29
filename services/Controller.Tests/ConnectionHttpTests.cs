using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KamenRaider.Controller;
using KamenRaider.Controller.Network;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace Controller.Tests;
public class ConnectionHttpTests {
 sealed class Handler:HttpMessageHandler {public bool Fail;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Fail?"192.168.0.1":"8.8.8.8")});}
 [Fact] public async Task OnlyOwnerSeesPublicConnectionAndStaleness(){
 using var f=new LeaseFixture();f.Time.Now=DateTimeOffset.UtcNow;f.Sync();var lease=await f.Active();var handler=new Handler();using var http=new HttpClient(handler);var ip=new PublicIpJob(http,new Uri("https://example.test/ip"));await ip.RefreshAsync(default);
 await using var app=ControllerHost.Build([],b=>{b.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{["InternalUrl"]="http://127.0.0.1:5101",["AgentUrl"]="https://127.0.0.1:5102",["DatabasePath"]=f.Path,["ServiceCredential"]="secret"});b.WebHost.UseTestServer();b.Services.AddTransient<IStartupFilter,LoopbackTestFilter>();b.WebHost.ConfigureTestServices(s=>s.AddSingleton(f.Db));b.Services.AddSingleton(ip);});
 await app.StartAsync();using var client=app.GetTestClient();var path=$"/internal/leases/{lease.Id}/connection";
 Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(path)).StatusCode);client.DefaultRequestHeaders.Add("X-Service-Credential","secret");client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id","bob");client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role","admin");Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(path)).StatusCode);
 client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Id");client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id","alice");var value=(await client.GetFromJsonAsync<JsonElement>(path));Assert.Equal("8.8.8.8:1",value.GetProperty("address").GetString());Assert.False(value.GetProperty("stale").GetBoolean());Assert.Equal(2,value.EnumerateObject().Count());
 handler.Fail=true;await ip.RefreshAsync(default);value=await client.GetFromJsonAsync<JsonElement>(path);Assert.True(value.GetProperty("stale").GetBoolean());f.Time.Now=f.Time.Now.AddSeconds(60);await f.Leases.GetLeaseAsync(lease.Id,default);value=await client.GetFromJsonAsync<JsonElement>(path);Assert.Equal(JsonValueKind.Null,value.GetProperty("address").ValueKind);await app.StopAsync();
 }
}
