using System.Net;
using System.Text;
using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using KamenRaider.Agent.Identity;
using KamenRaider.Agent.Sync;
using KamenRaider.Agent.Startup;
using KamenRaider.Contracts;
namespace Agent.Tests;
public class SyncClientTests {
 sealed class Handler(DeviceIdentity identity):HttpMessageHandler {protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {Assert.Equal("/agent/sync",request.RequestUri!.AbsolutePath);Assert.Equal(identity.MachineId,request.Headers.GetValues("X-Agent-Id").Single());var body=await request.Content!.ReadAsStringAsync(ct);using var json=JsonDocument.Parse(body);Assert.Equal(2,json.RootElement.GetProperty("syncProofProtocol").GetInt32());var at=request.Headers.GetValues("X-Agent-Timestamp").Single();var nonce=request.Headers.GetValues("X-Agent-Nonce").Single();using var key=ECDsa.Create();key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(identity.PublicKey),out _);Assert.True(key.VerifyData(Encoding.UTF8.GetBytes($"{identity.MachineId}\nPOST\n/agent/sync\n{at}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}"),Convert.FromBase64String(request.Headers.GetValues("X-Agent-Signature").Single()),HashAlgorithmName.SHA256));return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"serverTimeUtc\":\"2026-10-29T00:00:00Z\",\"configuration\":{\"revision\":4,\"moonlightPort\":5000,\"sunshinePort\":6000},\"commands\":[]}",Encoding.UTF8,"application/json")};}}
 [Fact] public void ReadyHeartbeatKeepsPairingOfferAndReportsInformationalAgentVersion(){var offer=new PairingOffer("rental",2,"boot",DateTimeOffset.UtcNow.AddSeconds(30),new[] { "pending-client" });var request=AgentSyncRequestFactory.Create("boot","192.0.2.1","001122334455",4,Readiness.Ready,Array.Empty<CommandResult>(),offer);Assert.Equal("ready",request.Readiness);Assert.Equal(offer,request.PendingPairing);Assert.Equal(AgentBuildVersion.Current,request.AgentVersion);Assert.NotEqual(typeof(AgentSyncRequestFactory).Assembly.GetName().Version?.ToString(),request.AgentVersion);}
 [Theory][InlineData(Readiness.NotReady)][InlineData(Readiness.Fault)] public void BlockedHeartbeatNeverPublishesPairingOffer(Readiness readiness){var offer=new PairingOffer("rental",2,"boot",DateTimeOffset.UtcNow.AddSeconds(30),new[] { "pending-client" });var request=AgentSyncRequestFactory.Create("boot","192.0.2.1","001122334455",4,readiness,Array.Empty<CommandResult>(),offer);Assert.Equal("notReady",request.Readiness);Assert.Null(request.PendingPairing);}
 [Fact] public void SyncFactoryIncludesAnIndependentCopyOfReadinessDiagnostics() {
  var method=typeof(AgentSyncRequestFactory).GetMethod("Create",BindingFlags.Public|BindingFlags.Static);Assert.NotNull(method);
  var diagnosticParameter=method!.GetParameters().SingleOrDefault(parameter=>parameter.Name=="readinessDiagnostics");Assert.NotNull(diagnosticParameter);
  var diagnosticType=diagnosticParameter!.ParameterType.GetElementType();Assert.NotNull(diagnosticType);
  var constructor=diagnosticType!.GetConstructor([typeof(string),typeof(string)]);Assert.NotNull(constructor);
  var diagnostics=Array.CreateInstance(diagnosticType,1);
  diagnostics.SetValue(constructor!.Invoke(["baseline","blocked"]),0);
  var args=new object?[]{"boot","192.0.2.1","001122334455",4L,Readiness.NotReady,Array.Empty<CommandResult>(),null,diagnostics};
  var request=Assert.IsType<SyncRequest>(method.Invoke(null,args));
  diagnostics.SetValue(constructor.Invoke(["baseline","passed"]),0);
  using var json=JsonDocument.Parse(JsonSerializer.Serialize(request,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
  var check=Assert.Single(json.RootElement.GetProperty("readinessDiagnostics").EnumerateArray());
  Assert.Equal("baseline",check.GetProperty("checkId").GetString());
  Assert.Equal("blocked",check.GetProperty("status").GetString());
 }
 [Fact] public async Task PostsSignedHeartbeatAndReadsAuthoritativeConfiguration() {var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());try{using var identity=DeviceIdentity.LoadOrCreate(dir);using var http=new HttpClient(new Handler(identity));var client=new SyncClient(http,identity);var result=await client.SyncAsync(new("https://controller.test"),new("boot","192.0.2.1","001122334455",1,"unknown",[]),default);Assert.Equal(4,Assert.IsType<AgentSyncExchange.Accepted>(result).Response.Configuration.Revision);}finally{Directory.Delete(dir,true);}}
 [Fact] public async Task ChallengeBodyCannotOutliveHttpTimeout() {
  var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
  try {
   using var identity=DeviceIdentity.LoadOrCreate(dir);using var http=new HttpClient(new StalledHandler()){Timeout=TimeSpan.FromMilliseconds(100)};
   using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(3));
   var client=new SyncClient(http,identity);
   await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>client.SyncAsync(new("https://controller.test"),new("boot","192.0.2.1","001122334455",1,"unknown",[]),cleanup.Token).WaitAsync(TimeSpan.FromSeconds(1)));
  } finally {Directory.Delete(dir,true);}
 }
 private sealed class StalledHandler:HttpMessageHandler {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage((HttpStatusCode)428){Content=new StalledContent()});
 }
 private sealed class StalledContent:HttpContent {
  protected override bool TryComputeLength(out long length){length=0;return false;}
  protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>throw new NotSupportedException();
  protected override Task<Stream> CreateContentReadStreamAsync()=>Task.FromResult<Stream>(new StalledStream());
 }
 private sealed class StalledStream:Stream {
  public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
  public override long Length=>throw new NotSupportedException();public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
  public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)=>new(WaitForCancellationAsync(ct));
  private static async Task<int> WaitForCancellationAsync(CancellationToken ct){await Task.Delay(Timeout.InfiniteTimeSpan,ct);return 0;}
  public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();public override void Flush()=>throw new NotSupportedException();
  public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
 }
}

