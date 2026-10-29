using KamenRaider.Contracts;
namespace Controller.Tests;
public class LeaseReviewTests {
    [Fact] public async Task OfflineAllocationWaitsForNewBootAndRejectsRetiredBootReplay() {
        using var f=new LeaseFixture();
        f.Time.Now=f.Time.Now.AddSeconds(21);
        var lease=await f.Create();
        f.Reopen();
        var prepare=Assert.Single(f.Sync("new-boot").Commands);
        Assert.Equal("PrepareMachine",prepare.Type); Assert.Equal("new-boot",prepare.BootId);
        Assert.Equal("preparing",(await f.Leases.GetLeaseAsync(lease.Id,default))!.State);
        Assert.Throws<ArgumentException>(()=>f.Sync("boot"));
        var apply=Assert.Single(f.Sync("new-boot",new CommandResult(prepare.CommandId,"applied",1)).Commands);
        var active=f.Sync("new-boot",new CommandResult(apply.CommandId,"applied",1)).ActiveLease;
        Assert.NotNull(active); Assert.Equal("new-boot",active.BootId); Assert.Equal(lease.Id,active.RentalId);
    }
}
