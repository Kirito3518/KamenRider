using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KamenRaider.Agent.Configuration;
using KamenRaider.Agent.Identity;
using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Power;
using KamenRaider.Agent.Preparation;
using KamenRaider.Agent.Startup;
using KamenRaider.Agent.Sunshine;
using KamenRaider.Agent.Sync;
using KamenRaider.Contracts;

namespace Agent.Tests;

public class AgentSyncCycleTests {
    [Fact] public async Task ChallengeRetriesImmediatelyWithoutAcknowledgingOrChangingLeaseState() {
        using var f=new Fixture();await f.Configuration.ApplyAsync(new(1,5000,6000),default);
        var dispatcher=new LeaseCommandDispatcher(f.Enforcer);
        await dispatcher.AcceptAsync(new(f.Clock.Now,new(1,5000,6000),[f.Command()]),f.Clock.UptimeMilliseconds,default);
        var confirmation=new ActiveLeaseConfirmation("rental",1,"boot",f.Clock.Now.AddSeconds(60));
        await f.Enforcer.ConfirmAsync(confirmation,default);
        var snapshot=f.Enforcer.Current;var blocks=f.Gate.BlockCalls;var allows=f.Gate.AllowCalls;
        var extension=f.Command(2,120,"extension");
        using var identity=DeviceIdentity.LoadOrCreate(Path.Combine(f.Directory,"identity"));
        using var transport=new ScriptedTransport((attempt,request)=> {
            if(attempt==1){f.Clock.Advance(20);return new((HttpStatusCode)428){Content=JsonContent.Create(new {
                agentUpdateChallenge=" opaque+/=token ",serverTimeUtc=f.Clock.Now.AddSeconds(30),
                configuration=new {revision=2,moonlightPort=5001,sunshinePort=6001},commands=new[]{extension},activeLease=(object?)null
            })};}
            Assert.Equal(2,attempt);Assert.Equal(" opaque+/=token ",request.AgentUpdateChallenge);
            Assert.Equal(snapshot,f.Enforcer.Current);Assert.True(f.Enforcer.CanStream);
            Assert.Equal(blocks,f.Gate.BlockCalls);Assert.Equal(allows,f.Gate.AllowCalls);
            Assert.Equal(1,f.Configuration.Current!.Revision);
            Assert.Equal(new CommandResult("cmd","applied",1),Assert.Single(dispatcher.PendingResults));
            return new(HttpStatusCode.OK){Content=JsonContent.Create(new SyncResponse(f.Clock.Now,new(1,5000,6000),[extension],new("rental",2,"boot",f.Clock.Now.AddSeconds(100))))};
        },identity);
        using var http=new HttpClient(transport);
        var cycle=new AgentSyncCycle(new SyncClient(http,identity),new("https://controller.test"),f.Clock,f.Configuration,dispatcher);

        await cycle.RunAsync("boot","192.0.2.1","001122334455",Readiness.Ready,default);

        Assert.Equal(2,transport.Requests.Count);
        Assert.Null(transport.Requests[0].AgentUpdateChallenge);
        Assert.All(transport.Requests,r=>Assert.Equal(new CommandResult("cmd","applied",1),Assert.Single(r.CommandResults)));
        Assert.Equal(new CommandResult("extension","applied",2),Assert.Single(dispatcher.PendingResults));
        Assert.Equal(2,f.Enforcer.Current!.Revision);Assert.Equal(121000,f.Enforcer.Current.DeadlineUptimeMilliseconds);Assert.True(f.Enforcer.CanStream);
        Assert.Equal(blocks+1,f.Gate.BlockCalls);Assert.Equal(allows+1,f.Gate.AllowCalls);
    }

    public static IEnumerable<object[]> InvalidChallenges() {
        yield return ["{}",typeof(HttpRequestException)];
        yield return ["{\"challenge\":\"wrong-field\"}",typeof(HttpRequestException)];
        yield return ["{\"agentUpdateChallenge\":null}",typeof(HttpRequestException)];
        yield return ["{\"agentUpdateChallenge\":\"\"}",typeof(HttpRequestException)];
        yield return ["{\"agentUpdateChallenge\":\"  \"}",typeof(HttpRequestException)];
        yield return ["{\"agentUpdateChallenge\":42}",typeof(JsonException)];
        yield return ["{",typeof(JsonException)];
        yield return ["{\"agentUpdateChallenge\":\""+new string('x',129)+"\"}",typeof(HttpRequestException)];
        yield return ["{\"agentUpdateChallenge\":\"token\",\"padding\":\""+new string('x',4096)+"\"}",typeof(HttpRequestException)];
    }

    [Theory][MemberData(nameof(InvalidChallenges))]
    public async Task InvalidChallengeFailsWithoutAcknowledgingAndRetainsLastValidToken(string body,Type errorType) {
        using var f=new Fixture();await f.Configuration.ApplyAsync(new(1,5000,6000),default);
        var dispatcher=new LeaseCommandDispatcher(f.Enforcer);
        await dispatcher.AcceptAsync(new(f.Clock.Now,new(1,5000,6000),[f.Command()]),f.Clock.UptimeMilliseconds,default);
        var confirmation=new ActiveLeaseConfirmation("rental",1,"boot",f.Clock.Now.AddSeconds(60));await f.Enforcer.ConfirmAsync(confirmation,default);
        var snapshot=f.Enforcer.Current;var blocks=f.Gate.BlockCalls;var allows=f.Gate.AllowCalls;
        using var transport=new ScriptedTransport((attempt,request)=>attempt switch {
            1=>new((HttpStatusCode)428){Content=JsonContent.Create(new {agentUpdateChallenge="retained-token"})},
            2=>new((HttpStatusCode)428){Content=new StringContent(body,System.Text.Encoding.UTF8,"application/json")},
            _=>new(HttpStatusCode.OK){Content=JsonContent.Create(new SyncResponse(f.Clock.Now,new(1,5000,6000),[],confirmation))}
        });
        using var identity=DeviceIdentity.LoadOrCreate(Path.Combine(f.Directory,"identity"));using var http=new HttpClient(transport);
        var cycle=new AgentSyncCycle(new SyncClient(http,identity),new("https://controller.test"),f.Clock,f.Configuration,dispatcher);

        var error=await Record.ExceptionAsync(()=>cycle.RunAsync("boot","192.0.2.1","001122334455",Readiness.Ready,default));

        Assert.IsType(errorType,error);Assert.Equal(2,transport.Requests.Count);
        Assert.Equal(snapshot,f.Enforcer.Current);Assert.True(f.Enforcer.CanStream);
        Assert.Equal(blocks,f.Gate.BlockCalls);Assert.Equal(allows,f.Gate.AllowCalls);Assert.Equal(1,f.Configuration.Current!.Revision);
        Assert.Equal(new CommandResult("cmd","applied",1),Assert.Single(dispatcher.PendingResults));
        await cycle.RunAsync("boot","192.0.2.1","001122334455",Readiness.Ready,default);
        Assert.Equal("retained-token",transport.Requests[2].AgentUpdateChallenge);
        Assert.Empty(dispatcher.PendingResults);Assert.True(f.Enforcer.CanStream);
    }

    [Theory][InlineData(201)][InlineData(500)]
    public async Task OtherStatusesDoNotAcknowledge(int status) {
        using var f=new Fixture();await f.Configuration.ApplyAsync(new(1,5000,6000),default);
        var dispatcher=new LeaseCommandDispatcher(f.Enforcer);
        await dispatcher.AcceptAsync(new(f.Clock.Now,new(1,5000,6000),[f.Command()]),f.Clock.UptimeMilliseconds,default);
        var confirmation=new ActiveLeaseConfirmation("rental",1,"boot",f.Clock.Now.AddSeconds(60));await f.Enforcer.ConfirmAsync(confirmation,default);
        var snapshot=f.Enforcer.Current;var blocks=f.Gate.BlockCalls;
        using var transport=new ScriptedTransport((_,_)=>new((HttpStatusCode)status){Content=JsonContent.Create(new SyncResponse(f.Clock.Now,new(2,5001,6001),[],null))});
        using var identity=DeviceIdentity.LoadOrCreate(Path.Combine(f.Directory,"identity"));using var http=new HttpClient(transport);
        var cycle=new AgentSyncCycle(new SyncClient(http,identity),new("https://controller.test"),f.Clock,f.Configuration,dispatcher);

        await Assert.ThrowsAsync<HttpRequestException>(()=>cycle.RunAsync("boot","192.0.2.1","001122334455",Readiness.Ready,default));

        Assert.Equal(snapshot,f.Enforcer.Current);Assert.True(f.Enforcer.CanStream);Assert.Equal(blocks,f.Gate.BlockCalls);
        Assert.Equal(1,f.Configuration.Current!.Revision);Assert.Equal(new CommandResult("cmd","applied",1),Assert.Single(dispatcher.PendingResults));
    }

    [Fact] public async Task CancellationStopsRepeatedChallengesWithoutAcknowledging() {
        using var f=new Fixture();await f.Configuration.ApplyAsync(new(1,5000,6000),default);
        var dispatcher=new LeaseCommandDispatcher(f.Enforcer);
        await dispatcher.AcceptAsync(new(f.Clock.Now,new(1,5000,6000),[f.Command()]),f.Clock.UptimeMilliseconds,default);
        await f.Enforcer.ConfirmAsync(new("rental",1,"boot",f.Clock.Now.AddSeconds(60)),default);
        var snapshot=f.Enforcer.Current;var blocks=f.Gate.BlockCalls;
        using var cancellation=new CancellationTokenSource();
        using var transport=new ScriptedTransport((attempt,_)=> {
            if(attempt==2)cancellation.Cancel();
            return new((HttpStatusCode)428){Content=JsonContent.Create(new {agentUpdateChallenge="token-"+attempt})};
        });
        using var identity=DeviceIdentity.LoadOrCreate(Path.Combine(f.Directory,"identity"));using var http=new HttpClient(transport);
        var cycle=new AgentSyncCycle(new SyncClient(http,identity),new("https://controller.test"),f.Clock,f.Configuration,dispatcher);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cycle.RunAsync("boot","192.0.2.1","001122334455",Readiness.Ready,cancellation.Token));

        Assert.Equal(2,transport.Requests.Count);Assert.Equal(snapshot,f.Enforcer.Current);Assert.True(f.Enforcer.CanStream);
        Assert.Equal(blocks,f.Gate.BlockCalls);Assert.Equal(new CommandResult("cmd","applied",1),Assert.Single(dispatcher.PendingResults));
    }

    [Theory][InlineData(Readiness.Ready)][InlineData(Readiness.NotReady)]
    public async Task RepeatedChallengesBackOffAndRetainNewestToken(Readiness readiness) {
        using var f=new Fixture();await f.Configuration.ApplyAsync(new(1,5000,6000),default);
        var dispatcher=new LeaseCommandDispatcher(f.Enforcer);
        await dispatcher.AcceptAsync(new(f.Clock.Now,new(1,5000,6000),[f.Command()]),f.Clock.UptimeMilliseconds,default);
        var confirmation=new ActiveLeaseConfirmation("rental",1,"boot",f.Clock.Now.AddSeconds(60));await f.Enforcer.ConfirmAsync(confirmation,default);
        var snapshot=f.Enforcer.Current;var blocks=f.Gate.BlockCalls;
        using var transport=new ScriptedTransport((attempt,_)=>attempt<=2
            ?new((HttpStatusCode)428){Content=JsonContent.Create(new {agentUpdateChallenge="token-"+attempt})}
            :new(HttpStatusCode.OK){Content=JsonContent.Create(new SyncResponse(f.Clock.Now,new(1,5000,6000),[],confirmation))});
        using var identity=DeviceIdentity.LoadOrCreate(Path.Combine(f.Directory,"identity"));using var http=new HttpClient(transport);
        var cycle=new AgentSyncCycle(new SyncClient(http,identity),new("https://controller.test"),f.Clock,f.Configuration,dispatcher);

        await Assert.ThrowsAsync<HttpRequestException>(()=>cycle.RunAsync("boot","192.0.2.1","001122334455",readiness,default));

        Assert.Equal(2,transport.Requests.Count);Assert.Equal("token-1",transport.Requests[1].AgentUpdateChallenge);
        Assert.Equal(snapshot,f.Enforcer.Current);Assert.True(f.Enforcer.CanStream);Assert.Equal(blocks,f.Gate.BlockCalls);
        Assert.Equal(new CommandResult("cmd","applied",1),Assert.Single(dispatcher.PendingResults));
        await cycle.RunAsync("boot","192.0.2.1","001122334455",readiness,default);
        Assert.Equal("token-2",transport.Requests[2].AgentUpdateChallenge);
        if(readiness==Readiness.Ready){Assert.Empty(dispatcher.PendingResults);Assert.True(f.Enforcer.CanStream);}
        else {Assert.Single(dispatcher.PendingResults);Assert.False(f.Enforcer.CanStream);}
    }

    [Fact] public async Task SyntheticChallengeOnlySuccessShowsTheOldClientAcknowledgmentHazard() {
        using var f=new Fixture();await f.Configuration.ApplyAsync(new(1,5000,6000),default);
        var dispatcher=new LeaseCommandDispatcher(f.Enforcer);
        await dispatcher.AcceptAsync(new(f.Clock.Now,new(1,5000,6000),[f.Command()]),f.Clock.UptimeMilliseconds,default);
        await f.Enforcer.ConfirmAsync(new("rental",1,"boot",f.Clock.Now.AddSeconds(60)),default);
        using var transport=new Transport((_,_)=>new(f.Clock.Now,new(1,5000,6000),[],null){AgentUpdateChallenge="synthetic"});
        using var identity=DeviceIdentity.LoadOrCreate(Path.Combine(f.Directory,"identity"));using var http=new HttpClient(transport);
        var cycle=new AgentSyncCycle(new SyncClient(http,identity),new("https://controller.test"),f.Clock,f.Configuration,dispatcher);

        await cycle.RunAsync("boot","192.0.2.1","001122334455",Readiness.Ready,default);

        Assert.Empty(dispatcher.PendingResults);Assert.False(f.Enforcer.CanStream);
    }

    [Fact] public async Task EchoesControllerUpdateChallengeOnlyInTheNextSignedHeartbeat() {
        using var f=new Fixture();
        using var transport=new ChallengeTransport();
        using var identity=DeviceIdentity.LoadOrCreate(Path.Combine(f.Directory,"identity"));
        using var http=new HttpClient(transport);
        var cycle=new AgentSyncCycle(new SyncClient(http,identity),new("https://controller.test"),f.Clock,f.Configuration,new LeaseCommandDispatcher(f.Enforcer));

        await cycle.RunAsync("boot","192.0.2.1","001122334455",Readiness.NotReady,default);
        await cycle.RunAsync("boot","192.0.2.1","001122334455",Readiness.NotReady,default);

        Assert.False(transport.Requests[0].TryGetProperty("agentUpdateChallenge",out var first) && first.ValueKind != JsonValueKind.Null);
        Assert.Equal("post-outcome-challenge",transport.Requests[1].GetProperty("agentUpdateChallenge").GetString());
    }

    private sealed class ChallengeTransport:HttpMessageHandler {
        public List<JsonElement> Requests=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); Requests.Add(body.RootElement.Clone());
            return new(HttpStatusCode.OK){Content=JsonContent.Create(new {serverTimeUtc=DateTimeOffset.UtcNow,configuration=new {revision=1,moonlightPort=5000,sunshinePort=6000},commands=Array.Empty<object>(),agentUpdateChallenge="post-outcome-challenge"})};
        }
    }
    [Fact] public async Task PrepareMachineWithoutProviderFailsInsteadOfBeingIgnored() {
        using var f=new Fixture();var dispatcher=new LeaseCommandDispatcher(f.Enforcer);
        var command=new CommandEnvelope("prepare","PrepareMachine","rental",1,"boot",f.Clock.Now.AddMinutes(3),JsonSerializer.SerializeToElement(new{}));

        await dispatcher.AcceptAsync(new(f.Clock.Now,new(1,5000,6000),[command]),f.Clock.UptimeMilliseconds,default);

        Assert.Equal(new CommandResult("prepare","failed",1,"PreparationUnavailable"),Assert.Single(dispatcher.PendingResults));
    }

    [Fact] public async Task PrepareMachineUsesProviderOnlyForCurrentUnleasedBoot() {
        using var f=new Fixture();var preparation=new TestMachinePreparation(true);var dispatcher=new LeaseCommandDispatcher(f.Enforcer,machinePreparation:preparation);
        var command=new CommandEnvelope("prepare","PrepareMachine","rental",1,"boot",f.Clock.Now.AddMinutes(3),JsonSerializer.SerializeToElement(new{}));

        await dispatcher.AcceptAsync(new(f.Clock.Now,new(1,5000,6000),[command]),f.Clock.UptimeMilliseconds,default);

        Assert.Equal(1,preparation.Calls);Assert.Equal(new CommandResult("prepare","applied",1),Assert.Single(dispatcher.PendingResults));
    }

    [Fact] public async Task PrepareMachineRejectsWrongBootWithoutCallingProvider() {
        using var f=new Fixture();var preparation=new TestMachinePreparation(true);var dispatcher=new LeaseCommandDispatcher(f.Enforcer,machinePreparation:preparation);
        var command=new CommandEnvelope("prepare","PrepareMachine","rental",1,"stale-boot",f.Clock.Now.AddMinutes(3),JsonSerializer.SerializeToElement(new{}));

        await dispatcher.AcceptAsync(new(f.Clock.Now,new(1,5000,6000),[command]),f.Clock.UptimeMilliseconds,default);

        Assert.Equal(0,preparation.Calls);Assert.Equal(new CommandResult("prepare","failed",1,"PreparationUnavailable"),Assert.Single(dispatcher.PendingResults));
    }

    [Fact] public async Task PrepareMachineRejectsWhenLeaseSnapshotAlreadyExists() {
        using var f=new Fixture();await f.Enforcer.ApplyAsync(f.Command(),default);await f.Enforcer.ConfirmAsync(new("rental",1,"boot",f.Clock.Now.AddSeconds(60)),default);
        var preparation=new TestMachinePreparation(true);var dispatcher=new LeaseCommandDispatcher(f.Enforcer,machinePreparation:preparation);
        var command=new CommandEnvelope("prepare-again","PrepareMachine","another-rental",1,"boot",f.Clock.Now.AddMinutes(3),JsonSerializer.SerializeToElement(new{}));

        await dispatcher.AcceptAsync(new(f.Clock.Now,new(1,5000,6000),[command]),f.Clock.UptimeMilliseconds,default);

        Assert.Equal(0,preparation.Calls);Assert.Equal(new CommandResult("prepare-again","failed",1,"PreparationUnavailable"),Assert.Single(dispatcher.PendingResults));
    }

    [Fact] public async Task ReadySyncWithNewerConfigurationDoesNotUseOldReadinessForPreparation() {
        using var f=new Fixture();await f.Configuration.ApplyAsync(new(1,5000,6000),default);
        var preparation=new TestMachinePreparation(true);var dispatcher=new LeaseCommandDispatcher(f.Enforcer,machinePreparation:preparation);
        var command=new CommandEnvelope("prepare","PrepareMachine","rental",1,"boot",f.Clock.Now.AddMinutes(3),JsonSerializer.SerializeToElement(new{}));
        using var transport=new Transport((_,_)=>new(f.Clock.Now,new(2,5001,6001),[command]));
        using var identity=DeviceIdentity.LoadOrCreate(Path.Combine(f.Directory,"identity"));using var http=new HttpClient(transport);
        var cycle=new AgentSyncCycle(new SyncClient(http,identity),new("https://controller.test"),f.Clock,f.Configuration,dispatcher);

        await cycle.RunAsync("boot","192.0.2.1","001122334455",Readiness.Ready,default);

        Assert.Equal(2,f.Configuration.Current!.Revision);Assert.Equal(0,preparation.Calls);Assert.Empty(dispatcher.PendingResults);Assert.False(f.Enforcer.CanStream);
    }

    [Fact] public async Task ReadySyncWithNewerConfigurationDoesNotApplyLeaseOrConfirmActiveLease() {
        using var f=new Fixture();await f.Configuration.ApplyAsync(new(1,5000,6000),default);await f.Enforcer.ApplyAsync(f.Command(),default);
        var active=new ActiveLeaseConfirmation("rental",1,"boot",f.Clock.Now.AddSeconds(60));await f.Enforcer.ConfirmAsync(active,default);
        var extension=f.Command(2,120,"extension");using var transport=new Transport((_,_)=>new(f.Clock.Now,new(2,5001,6001),[extension],active));
        using var identity=DeviceIdentity.LoadOrCreate(Path.Combine(f.Directory,"identity"));using var http=new HttpClient(transport);
        var cycle=new AgentSyncCycle(new SyncClient(http,identity),new("https://controller.test"),f.Clock,f.Configuration,new LeaseCommandDispatcher(f.Enforcer));

        await cycle.RunAsync("boot","192.0.2.1","001122334455",Readiness.Ready,default);

        Assert.Equal(2,f.Configuration.Current!.Revision);Assert.Equal(1,f.Enforcer.Current!.Revision);Assert.False(f.Enforcer.CanStream);
    }

    [Fact] public async Task ReadyResponseObservesTimeBeforeFailedConfigurationWriteWithoutProcessingCommandsOrAcknowledgments() {
        using var f=new Fixture();var dispatcher=new LeaseCommandDispatcher(f.Enforcer);
        await dispatcher.AcceptAsync(new(f.Clock.Now,new(1,5000,6000),[f.Command()]),f.Clock.UptimeMilliseconds,default);
        await f.Configuration.ApplyAsync(new(1,5000,6000),default);
        var configPath=Path.Combine(f.ConfigDirectory,"applied-configuration.json");File.Delete(configPath);Directory.CreateDirectory(configPath);
        using var transport=new Transport((_,_)=>new(f.Clock.Now.AddSeconds(10),new(2,5001,6001),[f.Command(2,120,"extension")],new("rental",1,"boot",f.Clock.Now.AddSeconds(60))));
        using var identity=DeviceIdentity.LoadOrCreate(Path.Combine(f.Directory,"identity"));using var http=new HttpClient(transport);
        var cycle=new AgentSyncCycle(new SyncClient(http,identity),new("https://controller.test"),f.Clock,f.Configuration,dispatcher);

        var writeFailure=await Record.ExceptionAsync(()=>cycle.RunAsync("boot","192.0.2.1","001122334455",Readiness.Ready,default));

        Assert.True(writeFailure is IOException or UnauthorizedAccessException);
        Assert.Equal(51000,f.Enforcer.Current!.DeadlineUptimeMilliseconds);Assert.Equal(1,f.Enforcer.Current.Revision);Assert.False(f.Enforcer.CanStream);
        Assert.Equal(new CommandResult("cmd","applied",1),Assert.Single(dispatcher.PendingResults));
        Assert.Equal(1,f.Configuration.Current!.Revision);Assert.Equal(1,Assert.Single(transport.Requests).AppliedConfigRevision);
    }

    [Fact] public async Task BlockedResponsesObserveTimeButRetainUnacknowledgedResultsDespiteConfigurationWriteFailure() {
        using var f=new Fixture();
        var dispatcher=new LeaseCommandDispatcher(f.Enforcer);
        await dispatcher.AcceptAsync(new(f.Clock.Now,new(1,5000,6000),[f.Command()]),f.Clock.UptimeMilliseconds,default);
        await f.Configuration.ApplyAsync(new(1,5000,6000),default);
        // A directory occupying the target prevents atomic publication without faking the config store.
        var configPath=Path.Combine(f.ConfigDirectory,"applied-configuration.json");
        File.Delete(configPath);Directory.CreateDirectory(configPath);
        var origin=f.Clock.Now;
        using var transport=new Transport((attempt,request)=>attempt==1?null:new(origin.AddSeconds((attempt-1)*10),new(2,5001,6001),request.Readiness=="ready"?[]:[f.Command(2,120,"extension")],new("rental",1,"boot",origin.AddSeconds(60))));
        using var identity=DeviceIdentity.LoadOrCreate(Path.Combine(f.Directory,"identity"));
        using var http=new HttpClient(transport);
        var cycle=new AgentSyncCycle(new SyncClient(http,identity),new("https://controller.test"),f.Clock,f.Configuration,dispatcher);

        await Assert.ThrowsAsync<HttpRequestException>(()=>cycle.RunAsync("boot","192.0.2.1","001122334455",Readiness.NotReady,default));
        Assert.Single(dispatcher.PendingResults);Assert.Equal(61000,f.Enforcer.Current!.DeadlineUptimeMilliseconds);
        var firstWriteFailure=await Record.ExceptionAsync(()=>cycle.RunAsync("boot","192.0.2.1","001122334455",Readiness.NotReady,default));
        Assert.True(firstWriteFailure is IOException or UnauthorizedAccessException);
        Assert.Single(dispatcher.PendingResults);Assert.Equal(51000,f.Enforcer.Current!.DeadlineUptimeMilliseconds);
        var secondWriteFailure=await Record.ExceptionAsync(()=>cycle.RunAsync("boot","192.0.2.1","001122334455",Readiness.Fault,default));
        Assert.True(secondWriteFailure is IOException or UnauthorizedAccessException);
        Assert.Single(dispatcher.PendingResults);Assert.Equal(41000,f.Enforcer.Current!.DeadlineUptimeMilliseconds);
        Assert.Equal(1,f.Enforcer.Current.Revision);Assert.False(f.Enforcer.CanStream);Assert.Equal(1,f.Configuration.Current!.Revision);
        Directory.Delete(configPath);
        await cycle.RunAsync("boot","192.0.2.1","001122334455",Readiness.Ready,default);
        Assert.Empty(dispatcher.PendingResults);Assert.Equal(2,f.Configuration.Current!.Revision);
        await cycle.RunAsync("boot","192.0.2.1","001122334455",Readiness.Ready,default);
        Assert.Equal(new[]{1L,1L,1L,1L,2L},transport.Requests.Select(r=>r.AppliedConfigRevision));
        Assert.Equal(new[]{1,1,1,1,0},transport.Requests.Select(r=>r.CommandResults.Length));
        Assert.All(transport.Requests.Take(3),r=>Assert.Equal("notReady",r.Readiness));
    }

    [Fact] public async Task PairingDiscoveryDelayIsExcludedFromHeartbeatRoundTripClockSample() {
        using var f=new Fixture();await f.Configuration.ApplyAsync(new(1,5000,6000),default);await f.Enforcer.ApplyAsync(f.Command(),default);
        var confirmation=new ActiveLeaseConfirmation("rental",1,"boot",f.Clock.Now.AddSeconds(60));
        await f.Enforcer.ConfirmAsync(confirmation,default);
        var pairing=new PairingSession(new DelayedPairingApi(f.Clock),new PairingFence(),()=>f.Enforcer.CurrentPairingScope,f.Clock);
        var dispatcher=new LeaseCommandDispatcher(f.Enforcer,pairing);
        using var transport=new Transport((_,_)=>new(f.Clock.Now,new(1,5000,6000),[],confirmation));
        using var identity=DeviceIdentity.LoadOrCreate(Path.Combine(f.Directory,"identity"));
        using var http=new HttpClient(transport);
        var cycle=new AgentSyncCycle(new SyncClient(http,identity),new("https://controller.test"),f.Clock,f.Configuration,dispatcher);

        await cycle.RunAsync("boot","192.0.2.1","001122334455",Readiness.Ready,default);

        Assert.NotNull(Assert.Single(transport.Requests).PendingPairing);
        Assert.Equal(21000,f.Clock.UptimeMilliseconds);Assert.Equal(61000,f.Enforcer.Current!.DeadlineUptimeMilliseconds);Assert.True(f.Enforcer.CanStream);
    }

    private sealed class Fixture:IDisposable {
        public string Directory=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        public string ConfigDirectory=>Path.Combine(Directory,"config");
        public Clock Clock=new();
        public AppliedConfiguration Configuration;
        public Gate Gate=new();public LeaseEnforcer Enforcer;
        public Fixture(){System.IO.Directory.CreateDirectory(ConfigDirectory);Configuration=new(ConfigDirectory);Enforcer=new(new LeaseStore(Path.Combine(Directory,"leases")),Clock,Clock,Gate,new Power());}
        public CommandEnvelope Command(long revision=1,int expiry=60,string id="cmd")=>new(id,"ApplyLease","rental",revision,"boot",Clock.Now.AddMinutes(3),JsonSerializer.SerializeToElement(new{startsAtUtc=DateTimeOffset.Parse("2026-10-29T00:00:00Z"),expiresAtUtc=DateTimeOffset.Parse("2026-10-29T00:00:00Z").AddSeconds(expiry)}));
        public void Dispose(){if(System.IO.Directory.Exists(Directory))System.IO.Directory.Delete(Directory,true);}
    }
    private sealed class Clock:TimeProvider,IBootClock {
        public string BootId=>"boot";
        public long UptimeMilliseconds {get;private set;}=1000;
        public DateTimeOffset Now=DateTimeOffset.Parse("2026-10-29T00:00:00Z");
        public override DateTimeOffset GetUtcNow()=>Now;
        public void Advance(int seconds){Now=Now.AddSeconds(seconds);UptimeMilliseconds+=seconds*1000;}
    }
    private sealed class Gate:IStreamGate {public int BlockCalls,AllowCalls;public Task BlockAsync(CancellationToken ct){BlockCalls++;return Task.CompletedTask;}public Task AllowAsync(StreamLeaseGrant grant,CancellationToken ct){AllowCalls++;return Task.CompletedTask;}}
    private sealed class Power:IShutdown {public Task RequestImmediateShutdownAsync(CancellationToken ct)=>Task.CompletedTask;}
    private sealed class TestMachinePreparation(bool result):IMachinePreparation {public int Calls;public Task<bool> PrepareAsync(CommandEnvelope command,CancellationToken cancellationToken){Calls++;return Task.FromResult(result);}}
    private sealed class PairingFence:IPairingFence {public Task<bool> VerifyOpenForLeaseAsync(PairingScope scope,CancellationToken ct)=>Task.FromResult(true);public async Task<bool> RunFencedCleanupAsync(Func<CancellationToken,Task> clear,CancellationToken ct){await clear(ct);return true;}}
    private sealed class DelayedPairingApi(Clock clock):ISunshinePairingApi {
        public Task<string[]> PendingIdsAsync(CancellationToken ct){clock.Advance(20);return Task.FromResult(new[]{"pending-client"});}
        public Task<bool> SubmitAsync(string id,string pin,CancellationToken ct)=>Task.FromResult(false);
        public Task ClearAsync(CancellationToken ct)=>Task.CompletedTask;
    }
    private sealed class Transport(Func<int,SyncRequest,SyncResponse?> reply):HttpMessageHandler {
        public List<SyncRequest> Requests=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
            var heartbeat=(await request.Content!.ReadFromJsonAsync<SyncRequest>(ct))!;Requests.Add(heartbeat);
            var response=reply(Requests.Count,heartbeat);
            return response is null?new(HttpStatusCode.ServiceUnavailable):new(HttpStatusCode.OK){Content=JsonContent.Create(response)};
        }
    }
    private sealed class ScriptedTransport(Func<int,SyncRequest,HttpResponseMessage> reply,DeviceIdentity? identity=null):HttpMessageHandler {
        public List<SyncRequest> Requests=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
            var body=await request.Content!.ReadAsStringAsync(ct);
            if(identity is not null){
                using var json=JsonDocument.Parse(body);Assert.Equal(2,json.RootElement.GetProperty("syncProofProtocol").GetInt32());
                var timestamp=request.Headers.GetValues("X-Agent-Timestamp").Single();var nonce=request.Headers.GetValues("X-Agent-Nonce").Single();
                using var key=ECDsa.Create();key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(identity.PublicKey),out _);
                var signed=$"{identity.MachineId}\nPOST\n/agent/sync\n{timestamp}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}";
                Assert.True(key.VerifyData(Encoding.UTF8.GetBytes(signed),Convert.FromBase64String(request.Headers.GetValues("X-Agent-Signature").Single()),HashAlgorithmName.SHA256));
            }
            var heartbeat=JsonSerializer.Deserialize<SyncRequest>(body,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;Requests.Add(heartbeat);
            return reply(Requests.Count,heartbeat);
        }
    }
}
