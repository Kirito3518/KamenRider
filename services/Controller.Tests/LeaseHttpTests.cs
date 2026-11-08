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
    [Fact] public async Task TrustedAdminCanCreateAndExtendForCustomerWhileBrowserAssertionsCannot() {
        using var f=new LeaseFixture();
        f.Time.Now=DateTimeOffset.UtcNow;f.Sync();
        await using var app=ControllerHost.Build([], b=> {
            b.Configuration.AddInMemoryCollection(new Dictionary<string,string?> { ["InternalUrl"]="http://127.0.0.1:5101",["AgentUrl"]="https://127.0.0.1:5102",["DatabasePath"]=f.Path,["ServiceCredential"]="secret" });
            b.WebHost.UseTestServer(); b.Services.AddTransient<IStartupFilter,LoopbackTestFilter>(); b.Services.AddSingleton(f.Db);
        });
        await app.StartAsync(); using var client=app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id","admin-user");
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role","admin");
        var create=new TrialLeaseRequest("alice","pc",60,"admin-create");
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/internal/trial-leases",create)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Service-Credential","secret");
        client.DefaultRequestHeaders.Add("Test-Local-Port","5102");
        Assert.Equal(HttpStatusCode.NotFound,(await client.PostAsJsonAsync("/internal/trial-leases",create)).StatusCode);
        client.DefaultRequestHeaders.Remove("Test-Local-Port");
        client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Role");
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role","user");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/internal/trial-leases",create)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Role"); client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role","admin");
        var response=await client.PostAsJsonAsync("/internal/trial-leases",create);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var lease=(await response.Content.ReadFromJsonAsync<LeaseDto>())!; Assert.Equal("alice",lease.UserId);
        var db=app.Services.GetRequiredService<ControllerDatabase>();
        SyncResponse Sync(params CommandResult[] results) => db.Sync("pc",new("boot","192.0.2.1","001122334455",1,"ready",results));
        var prepare=Assert.Single(Sync().Commands);
        var apply=Assert.Single(Sync(new CommandResult(prepare.CommandId,"applied",1)).Commands);
        Assert.NotNull(Sync(new CommandResult(apply.CommandId,"applied",1)).ActiveLease);
        client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Role"); client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role","user");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync($"/internal/leases/{lease.Id}/extend",new ExtendLeaseRequest(30,1,"admin-extend"))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Role"); client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role","admin");
        var extension=await client.PostAsJsonAsync($"/internal/leases/{lease.Id}/extend",new ExtendLeaseRequest(30,1,"admin-extend"));
        Assert.Equal(HttpStatusCode.OK,extension.StatusCode);
        Assert.Equal(2,(await extension.Content.ReadFromJsonAsync<LeaseDto>())!.PendingRevision);
        await app.StopAsync();
    }
}
