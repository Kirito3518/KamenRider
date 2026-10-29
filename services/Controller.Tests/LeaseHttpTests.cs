using System.Net;
using System.Net.Http.Json;
using KamenRaider.Contracts;
using KamenRaider.Controller;
using KamenRaider.Controller.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace Controller.Tests;
public class LeaseHttpTests {
    [Fact] public async Task LeaseRoutesRequireServiceCredentialAndAuthenticatedOwner() {
        using var f=new LeaseFixture(); var lease=await f.Create();
        await using var app=ControllerHost.Build([], b=> {
            b.Configuration.AddInMemoryCollection(new Dictionary<string,string?> { ["InternalUrl"]="http://127.0.0.1:5101",["AgentUrl"]="https://127.0.0.1:5102",["DatabasePath"]=f.Path,["ServiceCredential"]="secret" });
            b.WebHost.UseTestServer(); b.Services.AddTransient<IStartupFilter,LoopbackTestFilter>();
            b.Services.AddSingleton(f.Db);
        });
        await app.StartAsync(); using var client=app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/internal/users/alice/leases")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Service-Credential","secret");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/internal/users/alice/leases")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id","bob");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/internal/users/alice/leases")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<LeaseDto[]>("/internal/users/bob/leases"))!);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/internal/trial-leases",new TrialLeaseRequest("alice","pc",60,"spoof"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync($"/internal/leases/{lease.Id}/extend",new ExtendLeaseRequest(30,1,"spoof"))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Id"); client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id","alice");
        Assert.Equal(lease.Id,Assert.Single((await client.GetFromJsonAsync<LeaseDto[]>("/internal/users/alice/leases"))!).Id);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/internal/trial-leases",new TrialLeaseRequest("alice","pc",60,"create"))).StatusCode);
        client.DefaultRequestHeaders.Add("Test-Local-Port","5102");
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/internal/users/alice/leases")).StatusCode);
        await app.StopAsync();
    }
}
