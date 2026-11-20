using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Leases;
using System.Security.Cryptography;
namespace Controller.Tests;

public sealed class LeaseFixture : IDisposable {
    public sealed class Clock : TimeProvider {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-29T00:00:00Z");
        private long timestamp;
        public DateTimeOffset Now {
            get => now;
            set {
                if (value > now) timestamp = checked(timestamp + (value - now).Ticks);
                now = value;
            }
        }
        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
    }
    public string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kr-lease-fixture-" + Guid.NewGuid().ToString("N"), "controller.db");
    public Clock Time = new();
    public ControllerDatabase Db;
    public ECDsa AgentKey { get; } = ECDsa.Create();
    private readonly bool agentReleaseRemediationEnabled;
    private readonly string pinnedHelperSha256;
    private readonly bool exclusionProcedureQualified;
    private readonly string qualifiedAutoResumeStatus;
    public LeaseService Leases => new(Db);
    public LeaseFixture(bool agentReleaseRemediationEnabled = false, string pinnedHelperSha256 = "",
        bool exclusionProcedureQualified = false, string qualifiedAutoResumeStatus = "") {
        this.agentReleaseRemediationEnabled = agentReleaseRemediationEnabled;
        this.pinnedHelperSha256 = pinnedHelperSha256;
        this.exclusionProcedureQualified = exclusionProcedureQualified;
        this.qualifiedAutoResumeStatus = qualifiedAutoResumeStatus;
        Db = new(Path, clock: Time, agentReleaseRemediationEnabled: agentReleaseRemediationEnabled,
            agentMaintenancePinnedHelperSha256: pinnedHelperSha256,
            agentMaintenanceExclusionProcedureQualified: exclusionProcedureQualified,
            agentMaintenanceQualifiedAutoResumeStatus: qualifiedAutoResumeStatus);
        Db.EnrollWithTicket(new("pc",Convert.ToBase64String(AgentKey.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2));
        Db.Update("pc",new("PC",500,1,2),1); Db.Approve("pc","admin");
        Sync();
    }
    public SyncResponse Sync(string boot="boot", params CommandResult[] results) => Db.Sync("pc",new(boot,"192.0.2.1","001122334455",Db.List().Single().ConfigRevision,"ready",results));
    /// <summary>Enrolls, prices, approves and heartbeats an additional rentable machine.</summary>
    public void AddRentableMachine(string machineId, string boot="boot-2") {
        using var key=ECDsa.Create();
        Db.EnrollWithTicket(new(machineId,Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2));
        Db.Update(machineId,new("PC-"+machineId,500,1,2),1);
        Db.Approve(machineId,"admin");
        Db.Sync(machineId,new(boot,"192.0.2.1","001122334455",Db.List().Single(machine=>machine.MachineId==machineId).ConfigRevision,"ready",[]));
    }
    public Task<LeaseDto> Create(string key="create",string user="alice") => Leases.CreateLeaseAsync(user,"pc",60,key,default);
    public async Task<(LeaseDto,CommandEnvelope)> Prepare() {
        var lease = await Create();
        var prepare = Assert.Single(Sync().Commands);
        Assert.Equal("PrepareMachine",prepare.Type);
        var apply = Assert.Single(Sync("boot",new CommandResult(prepare.CommandId,"applied",prepare.LeaseRevision)).Commands);
        Assert.Equal("ApplyLease",apply.Type);
        return (lease,apply);
    }
    public async Task<LeaseDto> Active() {
        var (lease,apply) = await Prepare();
        Sync("boot",new CommandResult(apply.CommandId,"applied",apply.LeaseRevision));
        return (await Leases.GetLeaseAsync(lease.Id,default))!;
    }
    public void Reopen() { Db.Dispose(); Db=new(Path,clock:Time,agentReleaseRemediationEnabled:agentReleaseRemediationEnabled,
        agentMaintenancePinnedHelperSha256:pinnedHelperSha256,agentMaintenanceExclusionProcedureQualified:exclusionProcedureQualified,
        agentMaintenanceQualifiedAutoResumeStatus:qualifiedAutoResumeStatus); }
    public void Dispose() { Db.Dispose(); AgentKey.Dispose(); var directory=System.IO.Path.GetDirectoryName(Path)!; if(Directory.Exists(directory))Directory.Delete(directory,true); }
}
public class LeaseTests {
    [Theory]
    [InlineData("notReady")]
    [InlineData("unknown")]
    public async Task CreateLeaseRejectsUnreadyHeartbeatWithoutCreatingAllocation(string readiness) {
        using var f=new LeaseFixture();
        f.Db.Sync("pc",new("boot","192.0.2.1","001122334455",3,readiness,[]));

        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Create());

        Assert.Empty(await f.Leases.GetUserLeasesAsync("alice",default));
    }

    [Fact] public async Task CreateLeaseRejectsHeartbeatOlderThanTwentySeconds() {
        using var f=new LeaseFixture();
        f.Time.Now=f.Time.Now.AddSeconds(21);

        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Create());

        Assert.Empty(await f.Leases.GetUserLeasesAsync("alice",default));
    }

    [Fact] public async Task CreateLeaseRequiresCurrentConfigurationAcknowledgment() {
        using var f=new LeaseFixture();
        var current=Assert.Single(f.Db.List());
        f.Db.Update("pc",new("PC",500,5000,6000),current.ConfigRevision);

        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Create("same-key"));
        Assert.Empty(await f.Leases.GetUserLeasesAsync("alice",default));

        f.Sync();
        var lease=await f.Create("same-key");

        Assert.Equal(lease.Id,Assert.Single(await f.Leases.GetUserLeasesAsync("alice",default)).Id);
    }

    [Fact] public void AvailabilityWaitsForCurrentConfigurationAcknowledgmentAfterAdminEdit() {
        using var f=new LeaseFixture();var before=Assert.Single(f.Db.List());Assert.True(before.CanRent);Assert.Equal(3,before.AppliedConfigRevision);
        var updated=f.Db.Update("pc",new("PC",500,5000,6000),before.ConfigRevision);
        Assert.Equal(4,updated.ConfigRevision);Assert.False(updated.CanRent);
        f.Db.Sync("pc",new("boot","192.0.2.1","001122334455",3,"ready",[]));
        var stale=Assert.Single(f.Db.List());Assert.Equal(3,stale.AppliedConfigRevision);Assert.False(stale.CanRent);
        f.Db.Sync("pc",new("boot","192.0.2.1","001122334455",4,"ready",[]));
        var current=Assert.Single(f.Db.List());Assert.Equal(4,current.AppliedConfigRevision);Assert.True(current.CanRent);
    }
    [Fact] public async Task AvailabilityRequiresFreshReadyAndNoHeldAllocation() {
        using var f=new LeaseFixture();
        Assert.True(f.Db.List().Single().CanRent);
        var lease=await f.Create();
        Assert.False(f.Db.List().Single().CanRent);
        var prepare=Assert.Single(f.Sync().Commands);
        var apply=Assert.Single(f.Sync("boot",new CommandResult(prepare.CommandId,"applied",lease.Revision)).Commands);
        f.Sync("boot",new CommandResult(apply.CommandId,"applied",lease.Revision));
        Assert.False(f.Db.List().Single().CanRent);
        using var g=new LeaseFixture();
        g.Db.Sync("pc",new("boot","192.0.2.1","001122334455",1,"notReady",[]));
        Assert.False(g.Db.List().Single().CanRent);
        g.Sync(); g.Time.Now=g.Time.Now.AddSeconds(21);
        Assert.False(g.Db.List().Single().CanRent);
    }

    [Fact] public async Task AllocationIsUniqueAcrossConnectionsAndRetriesAreDurable() {
        using var f=new LeaseFixture();
        using var other=new ControllerDatabase(f.Path,clock:f.Time);
        other.Sync("pc",new("boot","192.0.2.1","001122334455",3,"ready",[]));
        var start=new TaskCompletionSource();
        async Task<LeaseDto?> Attempt(LeaseService service,string key) {
            await start.Task;
            try { return await service.CreateLeaseAsync("alice","pc",60,key,default); }
            catch(InvalidOperationException) { return null; }
        }
        var attempts=new[]{Task.Run(()=>Attempt(f.Leases,"one")),Task.Run(()=>Attempt(new(other),"two"))};
        start.SetResult(); var results=await Task.WhenAll(attempts);
        var lease=Assert.Single(results,x=>x is not null)!;
        f.Reopen();
        var key=results[0] is not null?"one":"two";
        Assert.Equal(lease.Id,(await f.Create(key)).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Leases.CreateLeaseAsync("alice","pc",61,key,default));
    }
    [Fact] public async Task ReadyStartsFixedDeadlineButOnlyApplyAckActivates() {
        using var f=new LeaseFixture(); var lease=await f.Create();
        Assert.Null(lease.StartsAtUtc); Assert.Null(lease.ExpiresAtUtc); Assert.Equal("preparing",lease.State);
        f.Time.Now=f.Time.Now.AddSeconds(40);
        var prepare=Assert.Single(f.Sync().Commands);
        var apply=Assert.Single(f.Sync("boot",new CommandResult(prepare.CommandId,"applied",1)).Commands);
        var waiting=(await f.Leases.GetLeaseAsync(lease.Id,default))!;
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T00:00:40Z"),waiting.StartsAtUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T00:01:40Z"),waiting.ExpiresAtUtc);
        Assert.Null(f.Sync().ActiveLease);
        f.Time.Now=f.Time.Now.AddSeconds(10);
        var confirmed=f.Sync("boot",new CommandResult(apply.CommandId,"applied",1)).ActiveLease;
        Assert.NotNull(confirmed); Assert.Equal(lease.Id,confirmed.RentalId); Assert.Equal("boot",confirmed.BootId);
        Assert.Equal(waiting.ExpiresAtUtc,confirmed.ExpiresAtUtc);
        Assert.Equal("active",(await f.Leases.GetLeaseAsync(lease.Id,default))!.State);
    }
    [Fact] public async Task PreparationTimesOutAtFiveMinutesWithoutReleasingAllocation() {
        using var f=new LeaseFixture(); var lease=await f.Create(); var prepare=Assert.Single(f.Sync().Commands);
        f.Time.Now=f.Time.Now.AddMinutes(5);
        Assert.Null(f.Sync("boot",new CommandResult(prepare.CommandId,"applied",1)).ActiveLease);
        Assert.Empty(f.Sync().Commands);
        Assert.Equal("failed",(await f.Leases.GetLeaseAsync(lease.Id,default))!.State);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Create("next","bob"));
    }
    [Fact] public async Task ExtensionWaitsForAckAndRetriesNeverAddMoreTime() {
        using var f=new LeaseFixture(); var lease=await f.Active();
        var pending=await f.Leases.ExtendLeaseAsync(lease.Id,30,1,"extend",default);
        Assert.Equal(1,pending.Revision); Assert.Equal(2,pending.PendingRevision); Assert.Equal(lease.ExpiresAtUtc,pending.ExpiresAtUtc);
        Assert.Equal(pending,await f.Leases.ExtendLeaseAsync(lease.Id,30,1,"extend",default));
        var command=Assert.Single(f.Sync().Commands); Assert.Equal(2,command.LeaseRevision);
        var active=f.Sync("boot",new CommandResult(command.CommandId,"applied",2)).ActiveLease!;
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T00:01:30Z"),active.ExpiresAtUtc);
        f.Reopen();
        var retry=await f.Leases.ExtendLeaseAsync(lease.Id,30,1,"extend",default);
        Assert.Equal(2,retry.Revision); Assert.Null(retry.PendingRevision); Assert.Equal(active.ExpiresAtUtc,retry.ExpiresAtUtc);
    }
    [Fact] public async Task ExpiredActiveLeaseKeepsAllocationAndRejectsExtension() {
        using var f=new LeaseFixture(); var lease=await f.Active(); f.Time.Now=f.Time.Now.AddSeconds(60);
        Assert.Null(f.Sync().ActiveLease); Assert.Equal("ending",(await f.Leases.GetLeaseAsync(lease.Id,default))!.State);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Leases.ExtendLeaseAsync(lease.Id,30,1,"extend",default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Create("next","bob"));
    }
}
