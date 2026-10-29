using KamenRaider.Contracts;
using Microsoft.Data.Sqlite;

namespace Controller.Tests;

public class RentalLifecycleTests {
    [Fact] public async Task ControllerLifecycleHoldsOneAllocationUntilSimulatedBootProof() {
        // LeaseFixture enrolls, configures and approves the device; sync and proof below simulate an Agent.
        using var f = new LeaseFixture();
        var (lease, apply) = await f.Prepare();
        Assert.Equal("preparing", f.Db.GetLease(lease.Id)!.State);
        Assert.Equal(1L, Held(f));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Create("collision", "bob"));
        var active = f.Sync("boot", new CommandResult(apply.CommandId, "applied", 1)).ActiveLease!;
        Assert.Equal(lease.Id, active.RentalId);

        const string pairingId = "0123456789abcdef0123456789abcdef";
        f.Db.Sync("pc", new("boot", "192.0.2.1", "001122334455", 1, "ready", [],
            new(lease.Id, 1, "boot", f.Time.Now.AddSeconds(30), [pairingId])));
        var pair = f.Db.QueuePairing(lease.Id, "alice", new("alice", pairingId, "1234", "pair"));
        Assert.Equal("PairPin", Assert.Single(f.Sync().Commands).Type);
        f.Sync("boot", new CommandResult(pair.CommandId, "applied", 1));
        Assert.Equal("succeeded", f.Db.PairingStatus(lease.Id, pair.CommandId, "alice").Status);

        var pending = await f.Leases.ExtendLeaseAsync(lease.Id, 30, 1, "extend", default);
        var extension = Assert.Single(f.Sync().Commands);
        Assert.Equal(extension.CommandId, Assert.Single(f.Sync().Commands).CommandId); // Lost ACK: same command redelivered.
        Assert.Equal(pending, await f.Leases.ExtendLeaseAsync(lease.Id, 30, 1, "extend", default));
        var extended = f.Sync("boot", new CommandResult(extension.CommandId, "applied", 2)).ActiveLease!;
        Assert.Equal(active.ExpiresAtUtc.AddSeconds(30), extended.ExpiresAtUtc);
        Assert.Equal(1L, Held(f));

        f.Time.Now = extended.ExpiresAtUtc;
        Assert.Null(f.Sync().ActiveLease);
        Assert.Equal(1L, Held(f));
        Assert.Throws<InvalidOperationException>(() => f.Db.QueuePairing(lease.Id, "alice", new("alice", pairingId, "1234", "old")));
        Assert.Equal("Hold", f.Db.Reconcile("pc", new("new", 3, true, true, true, true)).Action);
        var result = BootChallengeTests.Prove(f, BootChallengeTests.Issue(f));
        Assert.Equal("PrepareAvailable", result.Action);
        Assert.Null(result.Lease);
        Assert.Equal(0L, Held(f));
        var beforeSync = Assert.Single(f.Db.List());
        Assert.Equal("notReady", beforeSync.Readiness);
        Assert.False(beforeSync.CanRent);
        Assert.Empty(f.Sync("new").Commands);
        var afterSync = Assert.Single(f.Db.List());
        Assert.Equal("ready", afterSync.Readiness);
        Assert.True(afterSync.CanRent);
        Assert.Throws<ArgumentException>(() => f.Sync("boot"));
        var next = await f.Create("next", "bob");
        Assert.NotEqual(lease.Id, next.Id);
        Assert.Equal(1L, Held(f));
        Assert.Throws<UnauthorizedAccessException>(() => f.Db.GetPairingOffer(next.Id, "alice"));
    }

    static long Held(LeaseFixture f) {
        using var connection = new SqliteConnection("Data Source=" + f.Path + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM leases WHERE machine_id='pc' AND allocation_held=1";
        return (long)command.ExecuteScalar()!;
    }
}
