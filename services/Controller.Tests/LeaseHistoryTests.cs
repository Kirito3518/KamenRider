using KamenRaider.Contracts;
using Microsoft.Data.Sqlite;

namespace Controller.Tests;

/// <summary>
/// The lease end instant is written exactly once, at the transition that takes the lease out of
/// its active window, and later transitions must never move it.
/// </summary>
public class LeaseHistoryTests
{
    [Fact]
    public void TakeBoundsAreValidated()
    {
        using var f = new LeaseFixture();
        Assert.Throws<ArgumentException>(() => f.Db.GetAllLeases(0));
        Assert.Throws<ArgumentException>(() => f.Db.GetAllLeases(501));
        Assert.Empty(f.Db.GetAllLeases(1));
        Assert.Empty(f.Db.GetAllLeases(500));
    }

    [Fact]
    public async Task LeasesAreReturnedNewestFirst()
    {
        using var f = new LeaseFixture();
        f.AddRentableMachine("pc2");
        var first = await f.Leases.CreateLeaseAsync("alice", "pc", 60, "first", default);
        var second = await f.Leases.CreateLeaseAsync("bob", "pc2", 60, "second", default);

        var all = f.Db.GetAllLeases(200);

        Assert.Equal(new[]{second.Id, first.Id}, all.Select(lease => lease.Id));
        Assert.Equal(second.Id, Assert.Single(f.Db.GetAllLeases(1)).Id);
    }

    [Fact]
    public async Task NaturalExpiryRecordsTheExpiryInstantAsTheEndedAt()
    {
        using var f = new LeaseFixture();
        var lease = await f.Active();
        Assert.Null(lease.EndedAtUtc);

        f.Time.Now = f.Time.Now.AddSeconds(60);
        f.Sync();

        var ended = f.Db.GetLease(lease.Id)!;
        Assert.Equal("ending", ended.State);
        Assert.Equal(ended.ExpiresAtUtc, ended.EndedAtUtc);
        // A natural end leaves zero unused time: the recorded instant is exactly the expiry.
        Assert.Equal(lease.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds(), EndedAt(f.Path, lease.Id));
    }

    [Fact]
    public async Task PreparationTimeoutRecordsTheCheckInstantAsTheEndedAt()
    {
        using var f = new LeaseFixture();
        var lease = await f.Create();
        Assert.Null(lease.EndedAtUtc);

        f.Time.Now = f.Time.Now.AddMinutes(5);
        f.Sync();

        var ended = f.Db.GetLease(lease.Id)!;
        Assert.Equal("failed", ended.State);
        Assert.Equal(f.Time.Now, ended.EndedAtUtc);
        Assert.Equal(f.Time.Now.ToUnixTimeMilliseconds(), EndedAt(f.Path, lease.Id));
    }

    [Fact]
    public async Task AdminPowerOffRecordsTheEndedAtInstant()
    {
        using var f = new LeaseFixture();
        var lease = await f.Active();

        f.Db.RequestMachinePowerOff("pc", "admin-1", "power-off-1");

        var ended = f.Db.GetLease(lease.Id)!;
        Assert.Equal("ending", ended.State);
        Assert.Equal(f.Time.Now, ended.EndedAtUtc);
        Assert.Equal(f.Time.Now.ToUnixTimeMilliseconds(), EndedAt(f.Path, lease.Id));
    }

    [Fact]
    public async Task AdminCancelRecordsTheCancellationInstantAndTheAgentAckDoesNotOverwriteIt()
    {
        using var f = new LeaseFixture();
        var lease = await f.Active();

        var cancelled = f.Db.CancelMachineLease("pc", lease.Revision, "cancel-1", "cancel-request-1", "admin-1");
        var cancellationInstant = f.Time.Now;
        Assert.Equal(cancellationInstant, cancelled.EndedAtUtc);

        // Later transitions must preserve the first recorded end instant.
        f.Time.Now = f.Time.Now.AddSeconds(30);
        var command = Assert.Single(f.Sync().Commands);
        Assert.Equal("EndLease", command.Type);
        f.Sync("boot", new CommandResult(command.CommandId, "applied", lease.Revision));

        var completed = f.Db.GetLease(lease.Id)!;
        Assert.Equal("completed", completed.State);
        Assert.Equal(cancellationInstant, completed.EndedAtUtc);
        Assert.Equal(cancellationInstant.ToUnixTimeMilliseconds(), EndedAt(f.Path, lease.Id));
    }

    [Fact]
    public async Task AgentFailureRecordsTheEndedAtInstant()
    {
        using var f = new LeaseFixture();
        var lease = await f.Active();
        await f.Leases.ExtendLeaseAsync(lease.Id, 30, 1, "extend", default);
        var apply = Assert.Single(f.Sync().Commands);
        Assert.Equal("ApplyLease", apply.Type);

        f.Sync("boot", new CommandResult(apply.CommandId, "failed", 2));

        var ended = f.Db.GetLease(lease.Id)!;
        Assert.Equal("ending", ended.State);
        Assert.Equal(f.Time.Now, ended.EndedAtUtc);
        Assert.Equal(f.Time.Now.ToUnixTimeMilliseconds(), EndedAt(f.Path, lease.Id));
    }

    [Fact]
    public async Task RebootFailsPreparingLeaseWithTheCurrentInstant()
    {
        using var f = new LeaseFixture();
        var lease = await f.Create();

        BootChallengeTests.Prove(f, BootChallengeTests.Issue(f));

        var ended = f.Db.GetLease(lease.Id)!;
        Assert.Equal("failed", ended.State);
        Assert.Equal(f.Time.Now, ended.EndedAtUtc);
    }

    [Fact]
    public async Task RebootCompletesAnEndedLeaseWithoutOverwritingItsNaturalEnd()
    {
        using var f = new LeaseFixture();
        var lease = await f.Active();
        f.Time.Now = f.Time.Now.AddSeconds(60);
        f.Sync();
        var naturalEnd = f.Db.GetLease(lease.Id)!.EndedAtUtc;
        Assert.Equal(lease.ExpiresAtUtc, naturalEnd);

        f.Time.Now = f.Time.Now.AddMinutes(1);
        Assert.Equal("PrepareAvailable", BootChallengeTests.Prove(f, BootChallengeTests.Issue(f)).Action);

        var completed = f.Db.GetLease(lease.Id)!;
        Assert.Equal("completed", completed.State);
        Assert.Equal(naturalEnd, completed.EndedAtUtc);
    }

    [Fact]
    public async Task ANewBootRetiresTheActiveLeaseWithTheCurrentInstant()
    {
        using var f = new LeaseFixture();
        var lease = await f.Active();

        f.Time.Now = f.Time.Now.AddSeconds(5);
        f.Sync("new-boot");

        var ended = f.Db.GetLease(lease.Id)!;
        Assert.Equal("ending", ended.State);
        Assert.Equal(f.Time.Now, ended.EndedAtUtc);
        Assert.Equal(f.Time.Now.ToUnixTimeMilliseconds(), EndedAt(f.Path, lease.Id));
    }

    private static long? EndedAt(string path, string leaseId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ended_at FROM leases WHERE id=$id";
        command.Parameters.AddWithValue("$id", leaseId);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
