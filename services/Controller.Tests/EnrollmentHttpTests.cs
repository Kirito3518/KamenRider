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
}
