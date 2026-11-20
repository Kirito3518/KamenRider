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
using Microsoft.Data.Sqlite;
namespace Controller.Tests;
public class SyncHttpTests {
 [Fact] public async Task InvalidProtocolTwoProofCannotRetireActiveLeaseOrAcknowledgePendingCommand() {
 var file=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
 await using(var app=ControllerHost.Build([],b=>{b.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{["InternalUrl"]="http://127.0.0.1:5101",["AgentUrl"]="https://127.0.0.1:5102",["DatabasePath"]=file,["ServiceCredential"]="sync-proof-fixture"});b.WebHost.UseTestServer();b.Services.AddTransient<IStartupFilter,LoopbackTestFilter>();})) {
 await app.StartAsync();var db=app.Services.GetRequiredService<ControllerDatabase>();using var key=ECDsa.Create();
 db.EnrollWithTicket(new("pc",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2));
 db.Update("pc",new("PC",500,1,2),1);db.Approve("pc","admin");
 using var client=app.GetTestClient();client.BaseAddress=new("https://localhost");client.DefaultRequestHeaders.Add("Test-Local-Port","5102");
 var revision=db.List().Single().ConfigRevision;
 var request=new SyncRequest("boot","192.0.2.1","001122334455",revision,"ready",[]) { SyncProofProtocol=2 };
 using var initial=await Send(request);Assert.Equal(HttpStatusCode.PreconditionRequired,initial.StatusCode);
 var challenge=(await initial.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;
 using var proven=await Send(request with { AgentUpdateChallenge=challenge });Assert.Equal(HttpStatusCode.OK,proven.StatusCode);
 var nextChallenge=(await proven.Content.ReadFromJsonAsync<SyncResponse>())!.AgentUpdateChallenge;
 var lease=db.CreateLease("alice","pc",600,"verified-lease");

 using var poll=await Send(request with { AgentUpdateChallenge=nextChallenge });Assert.Equal(HttpStatusCode.OK,poll.StatusCode);
 var pollResult=(await poll.Content.ReadFromJsonAsync<SyncResponse>())!;var prepare=Assert.Single(pollResult.Commands);
 using var prepared=await Send(request with { AgentUpdateChallenge=pollResult.AgentUpdateChallenge,CommandResults=[new(prepare.CommandId,"applied",prepare.LeaseRevision)] });
 Assert.Equal(HttpStatusCode.OK,prepared.StatusCode);
 var preparedResult=(await prepared.Content.ReadFromJsonAsync<SyncResponse>())!;var apply=Assert.Single(preparedResult.Commands);
 using var active=await Send(request with { AgentUpdateChallenge=preparedResult.AgentUpdateChallenge,CommandResults=[new(apply.CommandId,"applied",apply.LeaseRevision)] });
 Assert.Equal(HttpStatusCode.OK,active.StatusCode);
 nextChallenge=(await active.Content.ReadFromJsonAsync<SyncResponse>())!.AgentUpdateChallenge;
 Assert.Equal("active",db.GetLease(lease.Id)!.State);

 var pendingExtension=db.ExtendLease(lease.Id,60,1,"pending-extension");Assert.Equal(2,pendingExtension.PendingRevision);
 using var extensionPoll=await Send(request with { AgentUpdateChallenge=nextChallenge });Assert.Equal(HttpStatusCode.OK,extensionPoll.StatusCode);
 var extensionResponse=(await extensionPoll.Content.ReadFromJsonAsync<SyncResponse>())!;var command=Assert.Single(extensionResponse.Commands);
 var currentProof=extensionResponse.AgentUpdateChallenge;
 var before=db.GetLease(lease.Id)!;var beforeStatus=ReadCommandStatus(command.CommandId);
 Assert.Equal("pending",beforeStatus);

 var rejectedBody=request with { LocalIp="192.0.2.99",AppliedConfigRevision=0,
  CommandResults=[new(command.CommandId,"applied",command.LeaseRevision)],
  PendingPairing=new(lease.Id,1,"boot",DateTimeOffset.UtcNow.AddSeconds(30),[PairingIds.FoundationSunshineSinglePendingSlotId]),AgentUpdateChallenge="wrong-proof" };
 using var rejected=await Send(rejectedBody);Assert.Equal(HttpStatusCode.PreconditionRequired,rejected.StatusCode);
 Assert.Equal(currentProof,(await rejected.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge);
 var after=db.GetLease(lease.Id)!;var machine=Assert.Single(db.List());
 Assert.Equal(before.State,after.State);Assert.Equal(before.Revision,after.Revision);Assert.Equal(before.PendingRevision,after.PendingRevision);
 Assert.Equal(before.ExpiresAtUtc,after.ExpiresAtUtc);Assert.Equal(beforeStatus,ReadCommandStatus(command.CommandId));
 Assert.Equal("boot",machine.BootId);Assert.Equal("192.0.2.1",machine.LocalIp);Assert.Equal(revision,machine.AppliedConfigRevision);
 Assert.Equal(0L,Scalar("SELECT COUNT(*) FROM pairing_offers WHERE machine_id='pc'"));
 await app.StopAsync();

 long Scalar(string sql) {
  using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file,Pooling=false}.ToString());connection.Open();
  using var query=connection.CreateCommand();query.CommandText=sql;return Convert.ToInt64(query.ExecuteScalar());
 }
 string ReadCommandStatus(string commandId) {
  using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file,Pooling=false}.ToString());connection.Open();
  using var query=connection.CreateCommand();query.CommandText="SELECT status FROM command_outbox WHERE command_id=$id";query.Parameters.AddWithValue("$id",commandId);
  return (string)query.ExecuteScalar()!;
 }
 async Task<HttpResponseMessage> Send(SyncRequest value) {
  var body=JsonSerializer.Serialize(value,new JsonSerializerOptions(JsonSerializerDefaults.Web));var at=DateTimeOffset.UtcNow.ToUnixTimeSeconds();var nonce=Guid.NewGuid().ToString("N");
  using var message=new HttpRequestMessage(HttpMethod.Post,"/agent/sync"){Content=new StringContent(body,Encoding.UTF8,"application/json")};
  message.Headers.Add("X-Agent-Id","pc");message.Headers.Add("X-Agent-Timestamp",at.ToString());message.Headers.Add("X-Agent-Nonce",nonce);
  message.Headers.Add("X-Agent-Signature",Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes($"pc\nPOST\n/agent/sync\n{at}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}"),HashAlgorithmName.SHA256)));
  return await client.SendAsync(message);
 }
 }File.Delete(file);
 }

 [Fact] public async Task LegacySyncWithHeldLeaseIsRejectedWithoutAcknowledgingOrExtendingIt() {
 var file=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
 await using(var app=ControllerHost.Build([],b=>{b.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{["InternalUrl"]="http://127.0.0.1:5101",["AgentUrl"]="https://127.0.0.1:5102",["DatabasePath"]=file,["ServiceCredential"]="sync-proof-fixture"});b.WebHost.UseTestServer();b.Services.AddTransient<IStartupFilter,LoopbackTestFilter>();})) {
 await app.StartAsync();var db=app.Services.GetRequiredService<ControllerDatabase>();using var key=ECDsa.Create();
 db.EnrollWithTicket(new("pc",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2));
 db.Update("pc",new("PC",500,1,2),1);db.Approve("pc","admin");
 using var client=app.GetTestClient();client.BaseAddress=new("https://localhost");client.DefaultRequestHeaders.Add("Test-Local-Port","5102");
 var revision=db.List().Single().ConfigRevision;
 var request=new SyncRequest("boot","192.0.2.1","001122334455",revision,"ready",[]) { SyncProofProtocol=2 };
 using var initial=await Send(request);Assert.Equal(HttpStatusCode.PreconditionRequired,initial.StatusCode);
 var challenge=(await initial.Content.ReadFromJsonAsync<SyncChallengeRequired>())!.AgentUpdateChallenge;
 using var proven=await Send(request with { AgentUpdateChallenge=challenge });Assert.Equal(HttpStatusCode.OK,proven.StatusCode);
 var nextChallenge=(await proven.Content.ReadFromJsonAsync<SyncResponse>())!.AgentUpdateChallenge;
 Assert.True(Assert.Single(db.List()).CanRent);

 var lease=db.CreateLease("alice","pc",60,"old-agent-held-lease");
 using var poll=await Send(request with { AgentUpdateChallenge=nextChallenge });Assert.Equal(HttpStatusCode.OK,poll.StatusCode);
 var pollResult=(await poll.Content.ReadFromJsonAsync<SyncResponse>())!;var prepare=Assert.Single(pollResult.Commands);
 nextChallenge=pollResult.AgentUpdateChallenge;
 using var prepared=await Send(request with { AgentUpdateChallenge=nextChallenge,CommandResults=[new(prepare.CommandId,"applied",prepare.LeaseRevision)] });
 Assert.Equal(HttpStatusCode.OK,prepared.StatusCode);
 var preparedResult=(await prepared.Content.ReadFromJsonAsync<SyncResponse>())!;var apply=Assert.Single(preparedResult.Commands);
 nextChallenge=preparedResult.AgentUpdateChallenge;
 using var active=await Send(request with { AgentUpdateChallenge=nextChallenge,CommandResults=[new(apply.CommandId,"applied",apply.LeaseRevision)] });
 Assert.Equal(HttpStatusCode.OK,active.StatusCode);
 var activeResult=(await active.Content.ReadFromJsonAsync<SyncResponse>())!;var confirmed=activeResult.ActiveLease;
 nextChallenge=activeResult.AgentUpdateChallenge;
 Assert.NotNull(confirmed);
 var before=db.GetLease(lease.Id)!;

 var oldRequest=request with { SyncProofProtocol=null,AgentUpdateChallenge="legacy-echo",CommandResults=[new("captured-command","applied",99)] };
 using var oldResponse=await Send(oldRequest);
 Assert.Equal(HttpStatusCode.Conflict,oldResponse.StatusCode);
 var after=db.GetLease(lease.Id)!;
 Assert.Equal("active",after.State);
 Assert.Equal(before.ExpiresAtUtc,after.ExpiresAtUtc);
 Assert.Equal(confirmed!.ExpiresAtUtc,before.ExpiresAtUtc);

 using var recheck=await Send(request with { AgentUpdateChallenge=nextChallenge });
 Assert.Equal(HttpStatusCode.OK,recheck.StatusCode);
 Assert.Equal(lease.Id,(await recheck.Content.ReadFromJsonAsync<SyncResponse>())!.ActiveLease?.RentalId);
 await app.StopAsync();

 async Task<HttpResponseMessage> Send(SyncRequest value) {
 var body=JsonSerializer.Serialize(value,new JsonSerializerOptions(JsonSerializerDefaults.Web));var at=DateTimeOffset.UtcNow.ToUnixTimeSeconds();var nonce=Guid.NewGuid().ToString("N");
 using var message=new HttpRequestMessage(HttpMethod.Post,"/agent/sync"){Content=new StringContent(body,Encoding.UTF8,"application/json")};
 message.Headers.Add("X-Agent-Id","pc");message.Headers.Add("X-Agent-Timestamp",at.ToString());message.Headers.Add("X-Agent-Nonce",nonce);
 message.Headers.Add("X-Agent-Signature",Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes($"pc\nPOST\n/agent/sync\n{at}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}"),HashAlgorithmName.SHA256)));
 return await client.SendAsync(message);
 }
 }File.Delete(file);
 }

 [Fact] public async Task SyncRequiresStoredKeyAndWakeDoesNotMakeMachineReady() {
 var file=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
 await using(var app=ControllerHost.Build([],b=>{b.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{["InternalUrl"]="http://127.0.0.1:5101",["AgentUrl"]="https://127.0.0.1:5102",["DatabasePath"]=file,["ServiceCredential"]="secret",["WakeBroadcastAddress"]="192.0.2.255"});b.WebHost.UseTestServer();b.Services.AddTransient<IStartupFilter,LoopbackTestFilter>();b.Services.AddSingleton<KamenRaider.Controller.Network.IWakeSender>(new NoNetwork());})) {
 await app.StartAsync();using var key=ECDsa.Create();using var wrong=ECDsa.Create();app.Services.GetRequiredService<ControllerDatabase>().EnrollWithTicket(new("pc",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2));
 using var client=app.GetTestClient();client.BaseAddress=new("https://localhost");client.DefaultRequestHeaders.Add("Test-Local-Port","5102");
 foreach(var signer in new[]{wrong,key}) {var body=JsonSerializer.Serialize(new SyncRequest("boot","192.0.2.1","001122334455",1,"notReady",[]),new JsonSerializerOptions(JsonSerializerDefaults.Web));var at=DateTimeOffset.UtcNow.ToUnixTimeSeconds();var nonce=Guid.NewGuid().ToString();using var req=new HttpRequestMessage(HttpMethod.Post,"/agent/sync"){Content=new StringContent(body,Encoding.UTF8,"application/json")};req.Headers.Add("X-Agent-Id","pc");req.Headers.Add("X-Agent-Timestamp",at.ToString());req.Headers.Add("X-Agent-Nonce",nonce);req.Headers.Add("X-Agent-Signature",Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes($"pc\nPOST\n/agent/sync\n{at}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}"),HashAlgorithmName.SHA256)));Assert.Equal(signer==key?HttpStatusCode.OK:HttpStatusCode.Unauthorized,(await client.SendAsync(req)).StatusCode);}
 client.DefaultRequestHeaders.Remove("Test-Local-Port");client.DefaultRequestHeaders.Add("X-Service-Credential","secret");Assert.Equal(HttpStatusCode.OK,(await client.PostAsync("/internal/machines/pc/wake",null)).StatusCode);Assert.Equal("notReady",Assert.Single((await client.GetFromJsonAsync<MachineDto[]>("/internal/machines"))!).Readiness);await app.StopAsync();
 }File.Delete(file);
 }
 sealed class NoNetwork:KamenRaider.Controller.Network.IWakeSender {public Task SendAsync(string mac,string broadcast,CancellationToken ct) {Assert.Equal("001122334455",mac);Assert.Equal("192.0.2.255",broadcast);return Task.CompletedTask;}}

 [Fact] public async Task ReadinessDiagnosticsArePersistedOnlyWhenTheirSignedBodyIsUnchanged() {
 var file=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
 await using(var app=ControllerHost.Build([],b=>{b.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{["InternalUrl"]="http://127.0.0.1:5101",["AgentUrl"]="https://127.0.0.1:5102",["DatabasePath"]=file,["ServiceCredential"]="secret"});b.WebHost.UseTestServer();b.Services.AddTransient<IStartupFilter,LoopbackTestFilter>();})) {
 await app.StartAsync();using var key=ECDsa.Create();app.Services.GetRequiredService<ControllerDatabase>().EnrollWithTicket(new("pc",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2));
 using var client=app.GetTestClient();client.BaseAddress=new("https://localhost");client.DefaultRequestHeaders.Add("Test-Local-Port","5102");
 var request=new SyncRequest("boot","192.0.2.1","001122334455",1,"notReady",[]) { ReadinessDiagnostics=[new(ReadinessDiagnosticIds.Baseline,ReadinessDiagnosticStatuses.Unavailable)] };
 var signedBody=JsonSerializer.Serialize(request,new JsonSerializerOptions(JsonSerializerDefaults.Web));
 var valid=await SendSigned(signedBody,signedBody);Assert.Equal(HttpStatusCode.OK,valid.StatusCode);
 client.DefaultRequestHeaders.Remove("Test-Local-Port");client.DefaultRequestHeaders.Add("X-Service-Credential","secret");
 var diagnostics=Assert.Single((await client.GetFromJsonAsync<MachineDto[]>("/internal/machines"))!).ReadinessDiagnostics;
 Assert.Equal(new ReadinessDiagnostic(ReadinessDiagnosticIds.Baseline,ReadinessDiagnosticStatuses.Unavailable),Assert.Single(diagnostics!));
 client.DefaultRequestHeaders.Remove("X-Service-Credential");client.DefaultRequestHeaders.Add("Test-Local-Port","5102");
 var tamperedBody=signedBody.Replace("unavailable","passed",StringComparison.Ordinal);
 Assert.NotEqual(signedBody,tamperedBody);
 var tampered=await SendSigned(tamperedBody,signedBody);Assert.Equal(HttpStatusCode.Unauthorized,tampered.StatusCode);
 await app.StopAsync();
 async Task<HttpResponseMessage> SendSigned(string body,string bodyUsedForSignature) {
 var at=DateTimeOffset.UtcNow.ToUnixTimeSeconds();var nonce=Guid.NewGuid().ToString();
 using var req=new HttpRequestMessage(HttpMethod.Post,"/agent/sync"){Content=new StringContent(body,Encoding.UTF8,"application/json")};
 req.Headers.Add("X-Agent-Id","pc");req.Headers.Add("X-Agent-Timestamp",at.ToString());req.Headers.Add("X-Agent-Nonce",nonce);
 req.Headers.Add("X-Agent-Signature",Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes($"pc\nPOST\n/agent/sync\n{at}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bodyUsedForSignature)))}"),HashAlgorithmName.SHA256)));
 return await client.SendAsync(req);
 }
 }File.Delete(file);
 }
}

