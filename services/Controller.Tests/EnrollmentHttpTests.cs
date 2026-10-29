using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KamenRaider.Contracts;
using KamenRaider.Controller;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace Controller.Tests;
public class EnrollmentHttpTests
{
    private static WebApplication Build(string path)=>ControllerHost.Build([],b=>{
        b.Configuration.AddInMemoryCollection(new Dictionary<string,string?> { ["InternalUrl"]="http://127.0.0.1:5101",["AgentUrl"]="https://127.0.0.1:5102",["DatabasePath"]=path,["ServiceCredential"]="secret" });
        b.WebHost.UseTestServer();b.Services.AddTransient<IStartupFilter,LoopbackTestFilter>();
    });
    [Fact] public async Task SignedEnrollmentRequiredAndApprovalSurvivesHostRestart() {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
        using var key=ECDsa.Create();
        var enrollment=new EnrollmentRequest("pc",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2);
        await using(var app=Build(path)) {
            await app.StartAsync();using var client=app.GetTestClient();client.BaseAddress=new Uri("https://localhost");client.DefaultRequestHeaders.Add("Test-Local-Port","5102");
            var body=JsonSerializer.Serialize(enrollment,new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsync("/agent/enroll",new StringContent(body,Encoding.UTF8,"application/json"))).StatusCode);
            var timestamp=DateTimeOffset.UtcNow.ToUnixTimeSeconds();var nonce=Guid.NewGuid().ToString();
            var data=Encoding.UTF8.GetBytes($"pc\nPOST\n/agent/enroll\n{timestamp}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}");
            client.DefaultRequestHeaders.Add("X-Agent-Timestamp",timestamp.ToString());client.DefaultRequestHeaders.Add("X-Agent-Nonce",nonce);client.DefaultRequestHeaders.Add("X-Agent-Signature",Convert.ToBase64String(key.SignData(data,HashAlgorithmName.SHA256)));
            Assert.Equal(HttpStatusCode.OK,(await client.PostAsync("/agent/enroll",new StringContent(body,Encoding.UTF8,"application/json"))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsync("/agent/enroll",new StringContent(body,Encoding.UTF8,"application/json"))).StatusCode);
            client.DefaultRequestHeaders.Remove("Test-Local-Port");client.DefaultRequestHeaders.Add("X-Service-Credential","secret");
            Assert.Equal(HttpStatusCode.OK,(await client.PatchAsJsonAsync("/internal/machines/pc?expectedRevision=1",new MachineUpdate("PC",500,3,4))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync("/internal/machines/pc/approve",null)).StatusCode);
            client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id","admin-user");
            Assert.Equal(HttpStatusCode.OK,(await client.PostAsync("/internal/machines/pc/approve",null)).StatusCode);
            await app.StopAsync();
        }
        await using(var app=Build(path)) {
            await app.StartAsync();using var client=app.GetTestClient();client.DefaultRequestHeaders.Add("X-Service-Credential","secret");
            var machine=Assert.Single((await client.GetFromJsonAsync<MachineDto[]>("/internal/machines"))!);
            Assert.Equal("approved",machine.ApprovalStatus);Assert.Equal("admin-user",machine.ApprovedBy);Assert.Equal("PC",machine.DisplayName);Assert.Equal(500,machine.HourlyPriceMinorUnits);
            await app.StopAsync();
        }
        File.Delete(path);
    }
    [Theory]
    [InlineData(null,"001122334455")]
    [InlineData("","001122334455")]
    [InlineData("not-an-ip","001122334455")]
    [InlineData("192.0.2.1",null)]
    [InlineData("192.0.2.1","")]
    [InlineData("192.0.2.1","NOTAMAC")]
    [InlineData("192.0.2.1","001122334455667788")]
    [InlineData("123456789012345678901234567890123456789012345678901234567890","001122334455")]
    public async Task SignedInvalidNetworkMetadataReturnsBadRequestWithoutInserting(string? ip,string? mac) {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
        await using(var app=Build(path)) {
            await app.StartAsync();using var client=app.GetTestClient();client.BaseAddress=new Uri("https://localhost");client.DefaultRequestHeaders.Add("Test-Local-Port","5102");
            using var key=ECDsa.Create();
            var body=JsonSerializer.Serialize(new EnrollmentRequest("bad",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),ip!,mac!,1,2),new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var timestamp=DateTimeOffset.UtcNow.ToUnixTimeSeconds();var nonce=Guid.NewGuid().ToString();
            var data=Encoding.UTF8.GetBytes($"bad\nPOST\n/agent/enroll\n{timestamp}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}");
            client.DefaultRequestHeaders.Add("X-Agent-Timestamp",timestamp.ToString());client.DefaultRequestHeaders.Add("X-Agent-Nonce",nonce);client.DefaultRequestHeaders.Add("X-Agent-Signature",Convert.ToBase64String(key.SignData(data,HashAlgorithmName.SHA256)));
            Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync("/agent/enroll",new StringContent(body,Encoding.UTF8,"application/json"))).StatusCode);
            client.DefaultRequestHeaders.Remove("Test-Local-Port");client.DefaultRequestHeaders.Add("X-Service-Credential","secret");
            Assert.Empty((await client.GetFromJsonAsync<MachineDto[]>("/internal/machines"))!);
            await app.StopAsync();
        }
        File.Delete(path);
    }
    [Fact] public async Task NewEnrollmentBurstReturns429WhileKnownIdentityStillEnrolls() {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
        await using(var app=Build(path)) {
            await app.StartAsync();using var client=app.GetTestClient();client.BaseAddress=new Uri("https://localhost");client.DefaultRequestHeaders.Add("Test-Local-Port","5102");
            using var key=ECDsa.Create();
            for(var index=0;index<12;index++) {
                var id=index==11?"pc0":"pc"+index;
                var body=JsonSerializer.Serialize(new EnrollmentRequest(id,Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2),new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var timestamp=DateTimeOffset.UtcNow.ToUnixTimeSeconds();var nonce=Guid.NewGuid().ToString();
                var data=Encoding.UTF8.GetBytes($"{id}\nPOST\n/agent/enroll\n{timestamp}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}");
                using var request=new HttpRequestMessage(HttpMethod.Post,"/agent/enroll") {Content=new StringContent(body,Encoding.UTF8,"application/json")};
                request.Headers.Add("X-Agent-Timestamp",timestamp.ToString());request.Headers.Add("X-Agent-Nonce",nonce);request.Headers.Add("X-Agent-Signature",Convert.ToBase64String(key.SignData(data,HashAlgorithmName.SHA256)));
                using var response=await client.SendAsync(request);
                Assert.Equal(index==10?HttpStatusCode.TooManyRequests:HttpStatusCode.OK,response.StatusCode);
            }
            client.DefaultRequestHeaders.Remove("Test-Local-Port");client.DefaultRequestHeaders.Add("X-Service-Credential","secret");
            Assert.Equal(10,(await client.GetFromJsonAsync<MachineDto[]>("/internal/machines"))!.Length);
            await app.StopAsync();
        }
        File.Delete(path);
    }
}
