using KamenRaider.Contracts;
namespace Controller.Tests;
public class CommandTests {
    [Fact] public async Task LostAckRedeliversIdenticalCommandAndReplayAfterRestartCannotAddTime() {
        using var f=new LeaseFixture(); var (lease,apply)=await f.Prepare();
        f.Time.Now=f.Time.Now.AddSeconds(10); f.Reopen();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(apply),System.Text.Json.JsonSerializer.Serialize(Assert.Single(f.Sync().Commands)));
        var first=f.Sync("boot",new CommandResult(apply.CommandId,"applied",1)).ActiveLease;
        f.Reopen(); f.Time.Now=f.Time.Now.AddSeconds(10);
        var replay=f.Sync("boot",new CommandResult(apply.CommandId,"applied",1)).ActiveLease;
        Assert.Equal(first,replay); Assert.Empty(f.Sync().Commands);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T00:01:00Z"),replay!.ExpiresAtUtc);
    }
    [Theory] [InlineData("revision")] [InlineData("boot")] [InlineData("expiry")] [InlineData("preparationTimeout")]
    public async Task InvalidApplyAckCannotActivate(string invalid) {
        using var f=new LeaseFixture();
        var lease=await f.Create();
        if(invalid=="preparationTimeout") f.Time.Now=f.Time.Now.AddSeconds(290);
        var prepare=Assert.Single(f.Sync().Commands);
        var apply=Assert.Single(f.Sync("boot",new CommandResult(prepare.CommandId,"applied",1)).Commands);
        if(invalid=="expiry") f.Time.Now=f.Time.Now.AddSeconds(60);
        if(invalid=="preparationTimeout") f.Time.Now=f.Time.Now.AddSeconds(10);
        Assert.Null(f.Sync(invalid=="boot"?"new-boot":"boot",new CommandResult(apply.CommandId,"applied",invalid=="revision"?2:1)).ActiveLease);
        Assert.NotEqual("active",(await f.Leases.GetLeaseAsync(lease.Id,default))!.State);
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
        var command=Assert.Single(f.Sync().Commands); f.Time.Now=f.Time.Now.AddSeconds(60);
        Assert.Null(f.Sync("boot",new CommandResult(command.CommandId,"applied",2)).ActiveLease);
        Assert.Equal("ending",(await f.Leases.GetLeaseAsync(lease.Id,default))!.State);
    }
    [Fact] public async Task FailedPreparationIsTerminalEvenWhenSuccessIsReplayed() {
        using var f=new LeaseFixture(); var lease=await f.Create(); var command=Assert.Single(f.Sync().Commands);
        f.Sync("boot",new CommandResult(command.CommandId,"failed",1,"secret-error")); f.Reopen();
        Assert.Null(f.Sync("boot",new CommandResult(command.CommandId,"applied",1)).ActiveLease);
        Assert.Empty(f.Sync().Commands); Assert.Equal("failed",(await f.Leases.GetLeaseAsync(lease.Id,default))!.State);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Create("other"));
    }
}
