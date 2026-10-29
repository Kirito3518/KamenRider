using System.Net;
using System.Security.Cryptography;
using System.Text;
using KamenRaider.Agent.Identity;
using KamenRaider.Agent.Startup;
namespace Agent.Tests;
public class ReconciliationClientTests {
 sealed class Handler(DeviceIdentity identity):HttpMessageHandler {
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
   Assert.Equal("/agent/reconcile",request.RequestUri!.AbsolutePath);var body=await request.Content!.ReadAsStringAsync(ct);var at=request.Headers.GetValues("X-Agent-Timestamp").Single();var nonce=request.Headers.GetValues("X-Agent-Nonce").Single();
   Assert.Equal(identity.MachineId,request.Headers.GetValues("X-Agent-Id").Single());using var key=ECDsa.Create();key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(identity.PublicKey),out _);
   Assert.True(key.VerifyData(Encoding.UTF8.GetBytes($"{identity.MachineId}\nPOST\n/agent/reconcile\n{at}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}"),Convert.FromBase64String(request.Headers.GetValues("X-Agent-Signature").Single()),HashAlgorithmName.SHA256));
   return new(HttpStatusCode.OK){Content=new StringContent("{\"serverTimeUtc\":\"2026-10-29T00:00:00Z\",\"configuration\":{\"revision\":4,\"moonlightPort\":5000,\"sunshinePort\":6000},\"action\":\"Hold\"}",Encoding.UTF8,"application/json")};
  }
 }
 [Fact] public async Task SignsReconciliationRouteAndReadsResponse() {var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());try {using var identity=DeviceIdentity.LoadOrCreate(dir);using var http=new HttpClient(new Handler(identity));var client=new ReconciliationClient(http,identity,new Uri("https://controller.test"));var response=await client.ReconcileAsync(new("boot",0,false),default);Assert.Equal("Hold",response.Action);Assert.Equal(4,response.Configuration.Revision);}finally{Directory.Delete(dir,true);}}
}
