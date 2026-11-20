using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/internal/leases",new CreateLeaseRequest("alice","pc",60,"spoof"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync($"/internal/leases/{lease.Id}/extend",new ExtendLeaseRequest(30,1,"spoof"))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Id"); client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id","alice");
        Assert.Equal(lease.Id,Assert.Single((await client.GetFromJsonAsync<LeaseDto[]>("/internal/users/alice/leases"))!).Id);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/internal/leases",new CreateLeaseRequest("alice","pc",60,"create"))).StatusCode);
        client.DefaultRequestHeaders.Add("Test-Local-Port","5102");
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/internal/users/alice/leases")).StatusCode);
        await app.StopAsync();
    }
    [Fact] public async Task TrustedAdminCanExtendForCustomerButCannotCreateOnTheirBehalf() {
        using var f=new LeaseFixture();
        f.Time.Now=DateTimeOffset.UtcNow;
        await using var app=ControllerHost.Build([], b=> {
            b.Configuration.AddInMemoryCollection(new Dictionary<string,string?> { ["InternalUrl"]="http://127.0.0.1:5101",["AgentUrl"]="https://127.0.0.1:5102",["DatabasePath"]=f.Path,["ServiceCredential"]="secret" });
            b.WebHost.UseTestServer(); b.Services.AddTransient<IStartupFilter,LoopbackTestFilter>(); b.Services.AddSingleton<TimeProvider>(f.Time);
        });
        await app.StartAsync(); using var client=app.GetTestClient();
        var db=app.Services.GetRequiredService<ControllerDatabase>();
        db.Sync("pc",new("boot","192.0.2.1","001122334455",db.List().Single().ConfigRevision,"ready",[]));
        Assert.True(db.List().Single().CanRent);
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id","admin-user");
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role","admin");
        var create=new CreateLeaseRequest("alice","pc",60,"admin-create");
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/internal/leases",create)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Service-Credential","secret");
        client.DefaultRequestHeaders.Add("Test-Local-Port","5102");
        Assert.Equal(HttpStatusCode.NotFound,(await client.PostAsJsonAsync("/internal/leases",create)).StatusCode);
        client.DefaultRequestHeaders.Remove("Test-Local-Port");
        client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Role");
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role","user");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/internal/leases",create)).StatusCode);
        // An administrator may never rent on behalf of a customer, regardless of the asserted role.
        client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Role"); client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role","admin");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/internal/leases",create)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Id"); client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id","alice");
        client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Role"); client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role","user");
        var response=await client.PostAsJsonAsync("/internal/leases",create);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var lease=(await response.Content.ReadFromJsonAsync<LeaseDto>())!; Assert.Equal("alice",lease.UserId); Assert.Null(lease.EndedAtUtc);
        SyncResponse Sync(params CommandResult[] results) => db.Sync("pc",new("boot","192.0.2.1","001122334455",1,"ready",results));
        var prepare=Assert.Single(Sync().Commands);
        var apply=Assert.Single(Sync(new CommandResult(prepare.CommandId,"applied",1)).Commands);
        Assert.NotNull(Sync(new CommandResult(apply.CommandId,"applied",1)).ActiveLease);
        client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Id"); client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id","admin-user");
        client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Role"); client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role","admin");
        var extension=await client.PostAsJsonAsync($"/internal/leases/{lease.Id}/extend",new ExtendLeaseRequest(30,1,"admin-extend"));
        Assert.Equal(HttpStatusCode.OK,extension.StatusCode);
        Assert.Equal(2,(await extension.Content.ReadFromJsonAsync<LeaseDto>())!.PendingRevision);
        await app.StopAsync();
    }
    [Fact] public async Task AdminLeaseHistoryRequiresAdminRoleAndValidTake() {
        using var f=new LeaseFixture();
        var lease=await f.Create();
        await using var app=ControllerHost.Build([], b=> {
            b.Configuration.AddInMemoryCollection(new Dictionary<string,string?> { ["InternalUrl"]="http://127.0.0.1:5101",["AgentUrl"]="https://127.0.0.1:5102",["DatabasePath"]=f.Path,["ServiceCredential"]="secret" });
            b.WebHost.UseTestServer(); b.Services.AddTransient<IStartupFilter,LoopbackTestFilter>(); b.Services.AddSingleton<TimeProvider>(f.Time);
        });
        await app.StartAsync(); using var client=app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Service-Credential","secret");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/internal/leases")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id","alice");
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role","user");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/internal/leases")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Role"); client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role","admin");
        using var history=await client.GetAsync("/internal/leases");
        Assert.Equal(HttpStatusCode.OK,history.StatusCode);
        Assert.Equal("no-store",history.Headers.CacheControl?.ToString());
        Assert.Equal(lease.Id,Assert.Single((await history.Content.ReadFromJsonAsync<LeaseDto[]>())!).Id);
        foreach(var take in new[]{"0","501","-1","abc",""})
            Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync($"/internal/leases?take={take}")).StatusCode);
        Assert.Equal(lease.Id,Assert.Single((await client.GetFromJsonAsync<LeaseDto[]>("/internal/leases?take=1"))!).Id);
        Assert.Equal(lease.Id,Assert.Single((await client.GetFromJsonAsync<LeaseDto[]>("/internal/leases?take=500"))!).Id);
        await app.StopAsync();
    }
    [Fact] public async Task AdminLeaseHistoryReturnsNewestLeasesFirst() {
        using var f=new LeaseFixture();
        f.AddRentableMachine("pc2");
        var first=await f.Leases.CreateLeaseAsync("alice","pc",60,"first",default);
        var second=await f.Leases.CreateLeaseAsync("bob","pc2",60,"second",default);
        await using var app=ControllerHost.Build([], b=> {
            b.Configuration.AddInMemoryCollection(new Dictionary<string,string?> { ["InternalUrl"]="http://127.0.0.1:5101",["AgentUrl"]="https://127.0.0.1:5102",["DatabasePath"]=f.Path,["ServiceCredential"]="secret" });
            b.WebHost.UseTestServer(); b.Services.AddTransient<IStartupFilter,LoopbackTestFilter>(); b.Services.AddSingleton<TimeProvider>(f.Time);
        });
        await app.StartAsync(); using var client=app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Service-Credential","secret");
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id","admin-user");
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role","admin");
        var all=(await client.GetFromJsonAsync<LeaseDto[]>("/internal/leases"))!;
        Assert.Equal(new[]{second.Id,first.Id},all.Select(item=>item.Id));
        Assert.Equal(second.Id,Assert.Single((await client.GetFromJsonAsync<LeaseDto[]>("/internal/leases?take=1"))!).Id);
        await app.StopAsync();
    }
}
