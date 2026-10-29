using System.Net;
using System.Net.Http.Json;
using KamenRaider.Controller;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using KamenRaider.Contracts;
namespace Controller.Tests;
public class PairingTests {
 const string Id="0123456789abcdef0123456789abcdef";
 [Fact] public async Task OnlyOwnerOfActiveLeaseCanSelectAnOfferedId(){using var f=new LeaseFixture();var lease=await f.Active();Offer(f,lease);Assert.Throws<UnauthorizedAccessException>(()=>f.Db.QueuePairing(lease.Id,"bob",new("alice",Id,"1234","key")));Assert.Throws<InvalidOperationException>(()=>f.Db.QueuePairing(lease.Id,"alice",new("alice","ffffffffffffffffffffffffffffffff","1234","key")));var queued=f.Db.QueuePairing(lease.Id,"alice",new("alice",Id,"1234","key"));Assert.Equal("queued",queued.Status);var command=Assert.Single(f.Sync().Commands);Assert.Equal("PairPin",command.Type);Assert.Equal(Id,command.Payload.GetProperty("pairingId").GetString());Assert.Equal(queued,f.Db.QueuePairing(lease.Id,"alice",new("alice",Id,"1234","key")));}
 [Theory][InlineData("123")][InlineData("１２３４")][InlineData("abc4")]
 public async Task InvalidPinRejected(string pin){using var f=new LeaseFixture();var lease=await f.Active();Offer(f,lease);Assert.Throws<ArgumentException>(()=>f.Db.QueuePairing(lease.Id,"alice",new("alice",Id,pin,"key")));Assert.Empty(f.Sync().Commands);}
 [Fact] public async Task ExpiredOfferOrLeaseCannotQueue(){using var f=new LeaseFixture();var lease=await f.Active();Offer(f,lease);f.Time.Now=f.Time.Now.AddSeconds(30);Assert.Throws<InvalidOperationException>(()=>f.Db.QueuePairing(lease.Id,"alice",new("alice",Id,"1234","key")));f.Time.Now=f.Time.Now.AddSeconds(30);Assert.Throws<InvalidOperationException>(()=>f.Db.QueuePairing(lease.Id,"alice",new("alice",Id,"1234","key")));}
 [Fact] public async Task CompletionIsOwnerScopedAndPinIsRemovedFromCommand(){using var f=new LeaseFixture();var lease=await f.Active();Offer(f,lease);var queued=f.Db.QueuePairing(lease.Id,"alice",new("alice",Id,"1234","key"));f.Sync("boot",new CommandResult(queued.CommandId,"applied",1));Assert.Equal("succeeded",f.Db.PairingStatus(lease.Id,queued.CommandId,"alice").Status);Assert.Throws<UnauthorizedAccessException>(()=>f.Db.PairingStatus(lease.Id,queued.CommandId,"bob"));using var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+f.Path+";Pooling=False");connection.Open();using var cmd=connection.CreateCommand();cmd.CommandText="SELECT payload FROM command_outbox WHERE command_id=$id";cmd.Parameters.AddWithValue("$id",queued.CommandId);Assert.Equal("{}",cmd.ExecuteScalar());}
 [Fact] public async Task PairingRoutesRequireServiceCredentialAndExactOwner(){using var f=new LeaseFixture();f.Time.Now=DateTimeOffset.UtcNow;var lease=await f.Active();Offer(f,lease);
  await using var app=ControllerHost.Build([],b=>{b.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{["InternalUrl"]="http://127.0.0.1:5101",["AgentUrl"]="https://127.0.0.1:5102",["DatabasePath"]=f.Path,["ServiceCredential"]="secret"});b.WebHost.UseTestServer();b.Services.AddTransient<IStartupFilter,LoopbackTestFilter>();b.Services.AddSingleton(f.Db);});
  await app.StartAsync();using var client=app.GetTestClient();var url=$"/internal/leases/{lease.Id}/pair";var request=new PairingRequest("alice",Id,"1234","key");
  Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync(url,request)).StatusCode);client.DefaultRequestHeaders.Add("X-Service-Credential","secret");client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id","bob");client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role","admin");Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(url,request)).StatusCode);
  client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Id");client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id","alice");Assert.Equal(new[]{Id},(await client.GetFromJsonAsync<PairingOffer>(url+"/pending"))!.PairingIds);var response=await client.PostAsJsonAsync(url,request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);var command=(await response.Content.ReadFromJsonAsync<PairingCommandStatus>())!;Assert.Equal("queued",(await client.GetFromJsonAsync<PairingCommandStatus>(url+"/"+command.CommandId))!.Status);await app.StopAsync();
 }
 [Fact] public async Task RestartAndTtlNeverRecoverOrDeliverPin(){using var f=new LeaseFixture();var lease=await f.Active();Offer(f,lease);var queued=f.Db.QueuePairing(lease.Id,"alice",new("alice",Id,"1234","key"));
  using(var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+f.Path+";Pooling=False")){connection.Open();using var cmd=connection.CreateCommand();cmd.CommandText="SELECT payload FROM command_outbox WHERE command_id=$id";cmd.Parameters.AddWithValue("$id",queued.CommandId);using var payload=System.Text.Json.JsonDocument.Parse((string)cmd.ExecuteScalar()!);Assert.False(payload.RootElement.TryGetProperty("pin",out _));}
  f.Reopen();Assert.Empty(f.Sync().Commands);Assert.Equal("expired",f.Db.PairingStatus(lease.Id,queued.CommandId,"alice").Status);
 }
 [Fact] public async Task OwnerCanReadOfferedIdsAndQueueAgainAfterCompletion(){using var f=new LeaseFixture();var lease=await f.Active();Offer(f,lease);Assert.Equal(new[]{Id},f.Db.GetPairingOffer(lease.Id,"alice")!.PairingIds);Assert.Throws<UnauthorizedAccessException>(()=>f.Db.GetPairingOffer(lease.Id,"bob"));var first=f.Db.QueuePairing(lease.Id,"alice",new("alice",Id,"1234","one"));f.Sync("boot",new CommandResult(first.CommandId,"failed",1));Offer(f,lease);Assert.NotEqual(first.CommandId,f.Db.QueuePairing(lease.Id,"alice",new("alice",Id,"1234","two")).CommandId);}
 static void Offer(LeaseFixture f,LeaseDto lease)=>f.Db.Sync("pc",new("boot","192.0.2.1","001122334455",1,"ready",[],new(lease.Id,1,"boot",f.Time.Now.AddSeconds(30),[Id])));
}





