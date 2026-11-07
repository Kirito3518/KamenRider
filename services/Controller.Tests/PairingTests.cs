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
 [Fact] public async Task FoundationSunshineSlotOfferReachesOwnerAndQueuesPairPin(){using var f=new LeaseFixture();var lease=await f.Active();var slotId=PairingIds.FoundationSunshineSinglePendingSlotId;f.Db.Sync("pc",new("boot","192.0.2.1","001122334455",1,"ready",[],new(lease.Id,1,"boot",f.Time.Now.AddSeconds(30),[slotId])));var offer=f.Db.GetPairingOffer(lease.Id,"alice");Assert.NotNull(offer);Assert.Equal(new[]{slotId},offer.PairingIds);var queued=f.Db.QueuePairing(lease.Id,"alice",new("alice",slotId,"1234","foundation-slot"));Assert.Equal("queued",queued.Status);var command=Assert.Single(f.Sync().Commands);Assert.Equal("PairPin",command.Type);Assert.Equal(slotId,command.Payload.GetProperty("pairingId").GetString());}
 [Theory][InlineData("foundation-sunshine-single-pending-slot-extra")][InlineData("0123456789abcdef0123456789ABCDEF")]
 public async Task UnsupportedPairingIdsAreRejectedInOffersAndRequests(string id){using var f=new LeaseFixture();var lease=await f.Active();f.Db.Sync("pc",new("boot","192.0.2.1","001122334455",1,"ready",[],new(lease.Id,1,"boot",f.Time.Now.AddSeconds(30),[id])));Assert.Null(f.Db.GetPairingOffer(lease.Id,"alice"));Assert.Throws<ArgumentException>(()=>f.Db.QueuePairing(lease.Id,"alice",new("alice",id,"1234","invalid-id")));}
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
 [Fact] public async Task IdlePinExpiresWithoutAnotherControllerRequest(){using var f=new LeaseFixture();var clock=new TimerClock{Now=f.Time.Now};f.Db.Dispose();f.Db=new(f.Path,clock:clock);var lease=await f.Active();Offer(f,lease);var queued=f.Db.QueuePairing(lease.Id,"alice",new("alice",Id,"1234","idle"));Assert.Equal(1,RetainedPinCount(f.Db));
  clock.Advance(TimeSpan.FromSeconds(29));Assert.Equal(1,RetainedPinCount(f.Db));clock.Advance(TimeSpan.FromSeconds(1));Assert.Equal(0,RetainedPinCount(f.Db));
  using var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+f.Path+";Pooling=False");connection.Open();using var cmd=connection.CreateCommand();cmd.CommandText="SELECT status FROM command_outbox WHERE command_id=$id";cmd.Parameters.AddWithValue("$id",queued.CommandId);Assert.Equal("expired",cmd.ExecuteScalar());
 }
 [Fact] public async Task DisposeClearsRetainedPinsAndCancelsExpiration(){using var f=new LeaseFixture();var clock=new TimerClock{Now=f.Time.Now};f.Db.Dispose();f.Db=new(f.Path,clock:clock);var lease=await f.Active();Offer(f,lease);f.Db.QueuePairing(lease.Id,"alice",new("alice",Id,"1234","dispose"));Assert.Equal(1,RetainedPinCount(f.Db));f.Db.Dispose();Assert.Equal(0,RetainedPinCount(f.Db));Assert.All(clock.Timers,t=>Assert.True(t.Disposed));clock.Advance(TimeSpan.FromMinutes(1));}
 // Inspect retention directly: calling any production query would itself purge and hide the idle-memory defect.
 static int RetainedPinCount(KamenRaider.Controller.Data.ControllerDatabase db)=>((System.Collections.IDictionary)typeof(KamenRaider.Controller.Data.ControllerDatabase).GetField("pairingPins",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(db)!).Count;
 sealed class TimerClock:TimeProvider {
  public DateTimeOffset Now;public List<ManualTimer> Timers=[];public override DateTimeOffset GetUtcNow()=>Now;
  public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period){var timer=new ManualTimer(callback,state,Now+dueTime);Timers.Add(timer);return timer;}
  public void Advance(TimeSpan elapsed){Now+=elapsed;foreach(var timer in Timers.ToArray())if(!timer.Disposed&&timer.Due<=Now){timer.Dispose();timer.Callback(timer.State);}}
 }
 sealed class ManualTimer(TimerCallback callback,object? state,DateTimeOffset due):ITimer {
  public TimerCallback Callback=callback;public object? State=state;public DateTimeOffset Due=due;public bool Disposed;
  public bool Change(TimeSpan dueTime,TimeSpan period)=>throw new NotSupportedException();public void Dispose()=>Disposed=true;public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
 }
 static void Offer(LeaseFixture f,LeaseDto lease)=>f.Db.Sync("pc",new("boot","192.0.2.1","001122334455",1,"ready",[],new(lease.Id,1,"boot",f.Time.Now.AddSeconds(30),[Id])));
}





