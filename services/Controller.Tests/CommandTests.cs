using KamenRaider.Contracts;
namespace Controller.Tests;
public class CommandTests {
    [Fact] public async Task ExtensionLostAckRedeliversIdenticalCommandAndReplayCannotAddTime() {
        using var f=new LeaseFixture(); var lease=await f.Active();
        var pending=await f.Leases.ExtendLeaseAsync(lease.Id,30,1,"extension",default);
        var command=Assert.Single(f.Sync().Commands);
        f.Reopen();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(command),System.Text.Json.JsonSerializer.Serialize(Assert.Single(f.Sync().Commands)));
        var first=f.Sync("boot",new CommandResult(command.CommandId,"applied",2)).ActiveLease;
        f.Reopen();
        var replay=f.Sync("boot",new CommandResult(command.CommandId,"applied",2)).ActiveLease;
        Assert.Equal(first,replay);
        Assert.Empty(f.Sync().Commands);
        Assert.Equal(pending.ExpiresAtUtc!.Value.AddSeconds(30),replay!.ExpiresAtUtc);
    }

    [Fact] public async Task OldBootCannotReappearAfterRestartAndConfirmPreviousLease() {
        using var f=new LeaseFixture(); await f.Active();
        Assert.Null(f.Sync("new-boot").ActiveLease); f.Reopen();
        Assert.Throws<ArgumentException>(()=>f.Sync("boot"));
        Assert.Null(f.Sync("new-boot").ActiveLease);
    }

    [Fact] public async Task LateExtensionAckCannotResurrectExpiredLease() {
        using var f=new LeaseFixture(); var lease=await f.Active();
        await f.Leases.ExtendLeaseAsync(lease.Id,30,1,"extension",default);
        var command=Assert.Single(f.Sync().Commands); f.Time.Now=f.Time.Now.AddSeconds(61);
        Assert.Null(f.Sync("boot",new CommandResult(command.CommandId,"applied",2)).ActiveLease);
        Assert.Equal("ending",(await f.Leases.GetLeaseAsync(lease.Id,default))!.State);
    }

    [Fact] public async Task UnpoweredLeaseBecomesTerminalAtThePreparationDeadline() {
        using var f=new LeaseFixture(); var lease=await f.Create();
        f.Time.Now=f.Time.Now.AddMinutes(5).AddSeconds(1);
        Assert.Equal("failed",(await f.Leases.GetLeaseAsync(lease.Id,default))!.State);
        Assert.Empty(f.Sync().Commands);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Create("other"));
    }
}

