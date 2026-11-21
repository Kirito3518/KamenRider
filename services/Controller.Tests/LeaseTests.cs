using KamenRaider.Contracts;
using KamenRaider.Controller;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Leases;
using KamenRaider.Controller.Network;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
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
    public sealed class RecordingWakeSender : IWakeSender {
        public List<string> Macs { get; } = [];
        public int Calls { get; private set; }
        public Task SendAsync(string macAddress,string broadcastAddress,CancellationToken ct) {
            Calls++; Macs.Add(macAddress); return Task.CompletedTask;
        }
    }
    public string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kr-lease-fixture-" + Guid.NewGuid().ToString("N"), "controller.db");
    public Clock Time = new();
    public ControllerDatabase Db;
    public RecordingWakeSender Wake { get; } = new();
    public ECDsa AgentKey { get; } = ECDsa.Create();
    private readonly bool agentReleaseRemediationEnabled;
    private readonly string pinnedHelperSha256;
    private readonly bool exclusionProcedureQualified;
    private readonly string qualifiedAutoResumeStatus;
    private static readonly IOptions<ControllerOptions> WakeOptions = Options.Create(new ControllerOptions {
        WakeInterfaceAddress = "192.0.2.10",
        WakeBroadcastAddress = "192.0.2.255"
    });
    public LeaseService Leases => new(Db,Wake,WakeOptions);
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
    public string Boot = "boot";
    public SyncResponse Sync(string? boot=null, params CommandResult[] results) => Db.Sync("pc",new(boot ?? Boot,"192.0.2.1","001122334455",Db.List().Single().ConfigRevision,"ready",results));
    /// <summary>Enrolls, prices, approves and powers off an additional rentable machine.</summary>
    public void AddRentableMachine(string machineId, string boot="boot-2") {
        using var key=ECDsa.Create();
        Db.EnrollWithTicket(new(machineId,Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2));
        Db.Update(machineId,new("PC-"+machineId,500,1,2),1);
        Db.Approve(machineId,"admin");
        Db.Sync(machineId,new(boot,"192.0.2.1","001122334455",Db.List().Single(machine=>machine.MachineId==machineId).ConfigRevision,"ready",[]));
        Time.Now=Time.Now.AddSeconds(21);
    }
    public Task<LeaseDto> Create(string key="create",string user="alice") { ForceMachineOffline(); return Leases.CreateLeaseAsync(user,"pc",60,key,default); }
    public async Task<LeaseDto> Power(LeaseDto lease,string user="alice") {
        await Leases.RequestPowerAsync(lease.Id,user,Guid.NewGuid().ToString(),default);
        return (await Leases.GetLeaseAsync(lease.Id,default))!;
    }
    /// <summary>Simulates the customer Power press and the Agent's first post-boot proof.</summary>
    public async Task<LeaseDto> Activate(LeaseDto lease,string boot="boot",string user="alice") {
        await Power(lease,user);
        var revision=Db.List().Single().ConfigRevision;
        var hold=Db.Reconcile("pc",new(boot,revision,true));
        var challenge=hold.Challenge ?? throw new InvalidOperationException("The reconcile hold did not issue a challenge: "+hold.Action);
        var proof=Db.Reconcile("pc",new(boot,revision,true,true,true,true,challenge,StreamReady:true));
        if(proof.Lease is null) throw new InvalidOperationException("The boot proof did not activate the prepared lease: "+proof.Action);
        Boot=boot;
        Sync(boot);
        return (await Leases.GetLeaseAsync(lease.Id,default))!;
    }
    public async Task<LeaseDto> Active() { ForceMachineOffline(); Time.Now=Time.Now.AddSeconds(21); return await Activate(await Create()); }
    public void ForceMachineOffline() {
        using var connection=new SqliteConnection("Data Source="+Path+";Pooling=False");connection.Open();
        using var command=connection.CreateCommand();
        command.CommandText="UPDATE machine_heartbeats SET last_seen='2000-01-01T00:00:00.0000000Z' WHERE machine_id='pc'";
        command.ExecuteNonQuery();
    }
    public void Reopen() { Db.Dispose(); Db=new(Path,clock:Time,agentReleaseRemediationEnabled:agentReleaseRemediationEnabled,
        agentMaintenancePinnedHelperSha256:pinnedHelperSha256,agentMaintenanceExclusionProcedureQualified:exclusionProcedureQualified,
        agentMaintenanceQualifiedAutoResumeStatus:qualifiedAutoResumeStatus); }
    public void Dispose() { Db.Dispose(); AgentKey.Dispose(); var directory=System.IO.Path.GetDirectoryName(Path)!; if(Directory.Exists(directory))Directory.Delete(directory,true); }
}
public class LeaseTests {
    [Fact] public async Task OfflineMachineCanBeReservedWithoutSendingWakeUntilTheRenterPressesPower() {
        using var f=new LeaseFixture();
        Assert.True(f.Db.List().Single().CanRent);
        var lease=await f.Create();
        Assert.Equal("preparing",lease.State);
        Assert.Null(lease.WakeRequestedAtUtc);
        Assert.True(lease.CanRequestPower);
        Assert.Equal(0,f.Wake.Calls);

        var powered=await f.Leases.RequestPowerAsync(lease.Id,"alice",Guid.NewGuid().ToString(),default);
        Assert.NotNull(powered.WakeRequestedAtUtc);
        Assert.Equal(3,f.Wake.Calls);
        Assert.Equal(3,f.Wake.Macs.Count);
        Assert.All(f.Wake.Macs,mac=>Assert.Equal("001122334455",mac));
        Assert.Equal(powered.WakeRequestedAtUtc,(await f.Leases.RequestPowerAsync(lease.Id,"alice",Guid.NewGuid().ToString(),default)).WakeRequestedAtUtc);
        Assert.Equal(3,f.Wake.Calls);
    }

    [Fact] public async Task OnlineIdleMachineCannotBeRentedUntilItIsPoweredOff() {
        using var f=new LeaseFixture();
        f.Sync();
        Assert.Equal("onlineIdle",f.Db.List().Single().OperationalState);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Leases.CreateLeaseAsync("alice","pc",60,"direct-online",default));
        Assert.False(f.Db.List().Single().AllocationHeld);
    }

    [Theory]
    [InlineData("maintenance")]
    [InlineData("disabled")]
    public async Task AdminModeBlocksNewRentals(string availability) {
        using var f=new LeaseFixture();
        var current=Assert.Single(f.Db.List());
        var updated=f.Db.Update("pc",new("PC",500,1,2){Availability=availability},current.ConfigRevision);
        Assert.Equal(availability,updated.Availability);
        Assert.Equal(availability,updated.OperationalState);
        Assert.False(updated.CanRent);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Create());
    }

    [Fact] public async Task PowerActivationStartsThePaidClockOnlyWhenTheAgentBoots() {
        using var f=new LeaseFixture();var lease=await f.Create();
        Assert.Null(lease.StartsAtUtc);Assert.Null(lease.ExpiresAtUtc);
        var active=await f.Activate(lease);
        Assert.Equal("active",active.State);
        Assert.NotNull(active.StartsAtUtc);Assert.NotNull(active.ExpiresAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(60),active.ExpiresAtUtc!.Value-active.StartsAtUtc!.Value);
        Assert.False(f.Db.List().Single().CanRent);
        Assert.Equal("busy",f.Db.List().Single().OperationalState);
    }

    [Fact] public async Task CancelBeforePowerReleasesAllocationAndShowsClearingForThirtySeconds() {
        using var f=new LeaseFixture();var lease=await f.Create();
        var cancelled=await f.Leases.CancelUserLeaseAsync(lease.Id,"alice",lease.Revision,"cancel",default);
        Assert.Equal("failed",cancelled.State);
        Assert.Null(cancelled.StartsAtUtc);
        Assert.Equal("clearing",f.Db.List().Single().OperationalState);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Create("next","bob"));
        f.Time.Now=f.Time.Now.AddSeconds(31);
        Assert.True(f.Db.List().Single().CanRent);
    }

    [Fact] public async Task ActiveCancelIsIdempotentAndClearsAfterGrace() {
        using var f=new LeaseFixture();var active=await f.Active();
        var cancelled=await f.Leases.CancelUserLeaseAsync(active.Id,"alice",active.Revision,"cancel-active",default);
        Assert.Equal("ending",cancelled.State);
        var replay=await f.Leases.CancelUserLeaseAsync(active.Id,"alice",active.Revision,"cancel-active",default);
        Assert.Equal(cancelled,replay);
        f.Time.Now=f.Time.Now.AddSeconds(31);
        Assert.True(f.Db.List().Single().CanRent);
    }

    [Fact] public async Task UnpoweredLeaseTimesOutAtFiveMinutesAndRefundsAsFailed() {
        using var f=new LeaseFixture();var lease=await f.Create();
        f.Time.Now=f.Time.Now.AddMinutes(5).AddSeconds(1);
        var expired=await f.Leases.GetLeaseAsync(lease.Id,default);
        Assert.Equal("failed",expired!.State);
        Assert.Null(expired.StartsAtUtc);
        Assert.Equal("clearing",f.Db.List().Single().OperationalState);
        f.Time.Now=f.Time.Now.AddSeconds(31);
        Assert.True(f.Db.List().Single().CanRent);
    }

    [Fact] public async Task ExpiredActiveLeaseReleasesAllocationAndEntersClearing() {
        using var f=new LeaseFixture();var lease=await f.Active();
        f.Time.Now=lease.ExpiresAtUtc!.Value;
        var expired=await f.Leases.GetLeaseAsync(lease.Id,default);
        Assert.Equal("ending",expired!.State);
        Assert.Equal("clearing",f.Db.List().Single().OperationalState);
        Assert.False(f.Db.List().Single().CanRent);
        f.Time.Now=f.Time.Now.AddSeconds(31);
        Assert.True(f.Db.List().Single().CanRent);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Leases.ExtendLeaseAsync(lease.Id,30,1,"late",default));
    }

    [Fact] public async Task ReplayedCreateReturnsTheSameAllocationAndAnotherKeyConflicts() {
        using var f=new LeaseFixture();var lease=await f.Create();
        Assert.Equal(lease.Id,(await f.Create()).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Create("different"));
    }

    [Fact] public async Task ExtensionWaitsForAckAndRetriesNeverAddMoreTime() {
        using var f=new LeaseFixture();var lease=await f.Active();
        var pending=await f.Leases.ExtendLeaseAsync(lease.Id,30,1,"extend",default);
        Assert.Equal(1,pending.Revision);Assert.Equal(2,pending.PendingRevision);Assert.Equal(lease.ExpiresAtUtc,pending.ExpiresAtUtc);
        Assert.Equal(pending,await f.Leases.ExtendLeaseAsync(lease.Id,30,1,"extend",default));
        var command=Assert.Single(f.Sync().Commands);Assert.Equal(2,command.LeaseRevision);
        var confirmed=f.Sync("boot",new CommandResult(command.CommandId,"applied",2)).ActiveLease!;
        Assert.Equal(lease.ExpiresAtUtc!.Value.AddSeconds(30),confirmed.ExpiresAtUtc);
        f.Reopen();
        var retry=await f.Leases.ExtendLeaseAsync(lease.Id,30,1,"extend",default);
        Assert.Equal(2,retry.Revision);Assert.Null(retry.PendingRevision);Assert.Equal(confirmed.ExpiresAtUtc,retry.ExpiresAtUtc);
    }
}

