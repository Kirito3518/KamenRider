using KamenRaider.Contracts;
using Microsoft.Data.Sqlite;

namespace Controller.Tests;

public class RentalLifecycleTests {
    [Fact] public async Task ActiveLeasePairsExtendsAndEntersClearingAtExpiry() {
        using var f = new LeaseFixture();
        var lease = await f.Active();
        Assert.Equal("active", f.Db.GetLease(lease.Id)!.State);
        Assert.Equal(1L, Held(f));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Create("collision", "bob"));

        const string pairingId = "0123456789abcdef0123456789abcdef";
        f.Db.Sync("pc", new("boot", "192.0.2.1", "001122334455", 1, "ready", [],
            new(lease.Id, 1, "boot", f.Time.Now.AddSeconds(30), [pairingId])));
        var pair = f.Db.QueuePairing(lease.Id, "alice", new("alice", pairingId, "1234", "pair"));
        Assert.Equal("PairPin", Assert.Single(f.Sync().Commands).Type);
        f.Sync("boot", new CommandResult(pair.CommandId, "applied", 1));
        Assert.Equal("succeeded", f.Db.PairingStatus(lease.Id, pair.CommandId, "alice").Status);

        var pending = await f.Leases.ExtendLeaseAsync(lease.Id, 30, 1, "extend", default);
        var extension = Assert.Single(f.Sync().Commands);
        f.Sync("boot", new CommandResult(extension.CommandId, "applied", 2));
        Assert.Equal(1L, Held(f));

        f.Time.Now = pending.ExpiresAtUtc!.Value.AddSeconds(30);
        Assert.Null(f.Sync().ActiveLease);
        Assert.Equal("ending", f.Db.GetLease(lease.Id)!.State);
        Assert.Equal("clearing", Assert.Single(f.Db.List()).OperationalState);
        Assert.Equal(0L, Held(f));
        Assert.False(f.Db.List().Single().CanRent);
        f.Time.Now = f.Time.Now.AddSeconds(31);
        Assert.True(f.Db.List().Single().CanRent);
        var next = await f.Create("next", "bob");
        Assert.NotEqual(lease.Id, next.Id);
        Assert.Equal(1L, Held(f));
    }

    static long Held(LeaseFixture f) {
        using var connection = new SqliteConnection("Data Source=" + f.Path + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM leases WHERE machine_id='pc' AND allocation_held=1";
        return (long)command.ExecuteScalar()!;
    }
}

