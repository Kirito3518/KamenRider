using KamenRaider.Contracts;
namespace Controller.Tests;
public class LeaseReviewTests {
    [Fact] public async Task StaleMachineKeepsExistingAllocationAndRejectsAnotherLease() {
        using var f=new LeaseFixture();
        var lease=await f.Create();
        f.Time.Now=f.Time.Now.AddSeconds(21);
        Assert.Equal("unreachable",Assert.Single(f.Db.List()).Readiness);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Create("next"));
        Assert.Equal("preparing",(await f.Leases.GetLeaseAsync(lease.Id,default))!.State);
        Assert.Equal(lease.Id,Assert.Single(await f.Leases.GetUserLeasesAsync("alice",default)).Id);
    }
}
