using System.Net;
using System.Text;
using System.Security.Cryptography;
using KamenRaider.Agent.Identity;
using KamenRaider.Agent.Sync;
using KamenRaider.Agent.Startup;
using KamenRaider.Contracts;
namespace Agent.Tests;
public class SyncClientTests {
 sealed class Handler(DeviceIdentity identity):HttpMessageHandler {protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {Assert.Equal("/agent/sync",request.RequestUri!.AbsolutePath);Assert.Equal(identity.MachineId,request.Headers.GetValues("X-Agent-Id").Single());var body=await request.Content!.ReadAsStringAsync(ct);var at=request.Headers.GetValues("X-Agent-Timestamp").Single();var nonce=request.Headers.GetValues("X-Agent-Nonce").Single();using var key=ECDsa.Create();key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(identity.PublicKey),out _);Assert.True(key.VerifyData(Encoding.UTF8.GetBytes($"{identity.MachineId}\nPOST\n/agent/sync\n{at}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}"),Convert.FromBase64String(request.Headers.GetValues("X-Agent-Signature").Single()),HashAlgorithmName.SHA256));return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"serverTimeUtc\":\"2026-10-29T00:00:00Z\",\"configuration\":{\"revision\":4,\"moonlightPort\":5000,\"sunshinePort\":6000},\"commands\":[]}",Encoding.UTF8,"application/json")};}}
 [Fact] public void ReadyHeartbeatKeepsPairingOffer(){var offer=new PairingOffer("rental",2,"boot",DateTimeOffset.UtcNow.AddSeconds(30),new[] { "pending-client" });var request=AgentSyncRequestFactory.Create("boot","192.0.2.1","001122334455",4,Readiness.Ready,Array.Empty<CommandResult>(),offer);Assert.Equal("ready",request.Readiness);Assert.Equal(offer,request.PendingPairing);}
 [Theory][InlineData(Readiness.NotReady)][InlineData(Readiness.Fault)] public void BlockedHeartbeatNeverPublishesPairingOffer(Readiness readiness){var offer=new PairingOffer("rental",2,"boot",DateTimeOffset.UtcNow.AddSeconds(30),new[] { "pending-client" });var request=AgentSyncRequestFactory.Create("boot","192.0.2.1","001122334455",4,readiness,Array.Empty<CommandResult>(),offer);Assert.Equal("notReady",request.Readiness);Assert.Null(request.PendingPairing);}
 [Fact] public async Task PostsSignedHeartbeatAndReadsAuthoritativeConfiguration() {var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());try{using var identity=DeviceIdentity.LoadOrCreate(dir);using var http=new HttpClient(new Handler(identity));var client=new SyncClient(http,identity);var result=await client.SyncAsync(new("https://controller.test"),new("boot","192.0.2.1","001122334455",1,"unknown",[]),default);Assert.Equal(4,result.Configuration.Revision);}finally{Directory.Delete(dir,true);}}
}

