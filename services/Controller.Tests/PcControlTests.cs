using KamenRaider.Contracts;
using KamenRaider.Controller.Machines;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Controller.Tests;

public class PcControlTests
{
    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long Scalar(string path, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Text(string path, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static void Execute(string path, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task InternalPcControlEndpointsRequireTrustedAdminHeaders()
    {
        using var fixture = new LeaseFixture();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Db);
        using var app = builder.Build();
        app.MapPcControl();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var unauthenticated = await client.GetAsync("/internal/pc-control");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, unauthenticated.StatusCode);
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "admin-1");
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role", "admin");
        using var authenticated = await client.GetAsync("/internal/pc-control");
        Assert.Equal(System.Net.HttpStatusCode.OK, authenticated.StatusCode);
    }

    [Fact]
    public async Task SnapshotReturnsEveryMachineAndTheAllocationHoldingLease()
    {
        using var fixture = new LeaseFixture();
        var empty = fixture.Db.GetPcControlSnapshot();
        Assert.Single(empty.Machines);
        Assert.Null(empty.Machines[0].Lease);

        var lease = await fixture.Active();

        var snapshot = fixture.Db.GetPcControlSnapshot();
        var machine = Assert.Single(snapshot.Machines);
        Assert.NotNull(machine.Lease);
        Assert.Equal(lease.Id, machine.Lease!.RentalId);
        Assert.Equal("alice", machine.Lease.UserId);
        Assert.Equal("active", machine.Lease.State);
        Assert.True(machine.Lease.RemainingSeconds > 0);
    }

    [Fact]
    public async Task PowerOffEndsTheActiveRentalAndEnqueuesASingleShutdownCommand()
    {
        using var fixture = new LeaseFixture();
        var lease = await fixture.Active();

        var response = fixture.Db.RequestMachinePowerOff("pc", "admin-1", "power-request-1");
        Assert.Equal("requested", response.Status);

        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM command_outbox WHERE machine_id='pc' AND type='ShutdownMachine' AND status='pending'"));
        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM leases WHERE id=$id AND state='ending' AND allocation_held=0", ("$id", lease.Id)));

        var retried = fixture.Db.RequestMachinePowerOff("pc", "admin-1", "power-request-1");
        Assert.Equal(response.CommandId, retried.CommandId);
        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM machine_admin_actions WHERE actor_id='admin-1' AND request_id='power-request-1'"));
    }

    [Fact]
    public async Task PowerOffRejectsOfflineMachines()
    {
        using var fixture = new LeaseFixture();
        fixture.Time.Now = fixture.Time.Now.AddSeconds(30);
        Assert.Throws<InvalidOperationException>(() => fixture.Db.RequestMachinePowerOff("pc", "admin-1", "power-offline"));
    }

    [Fact]
    public async Task CancelLeaseQueuesEndLeaseAndReleasesTheHoldOnlyAfterAgentAck()
    {
        using var fixture = new LeaseFixture();
        var lease = await fixture.Active();

        var pending = fixture.Db.CancelMachineLease("pc", lease.Revision, "cancel-1", "cancel-request-1", "admin-1");
        Assert.Equal(lease.Id, pending.Id);
        Assert.Equal(lease.Revision, pending.Revision);
        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM command_outbox WHERE rental_id=$id AND type='EndLease' AND status='pending'", ("$id", lease.Id)));
        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM leases WHERE id=$id AND state='ending' AND allocation_held=0", ("$id", lease.Id)));

        var retried = fixture.Db.CancelMachineLease("pc", lease.Revision, "cancel-1", "cancel-request-1", "admin-1");
        Assert.Equal(lease.Id, retried.Id);
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM command_outbox WHERE rental_id=$id AND type='EndLease'", ("$id", lease.Id)));

        var commandId = Text(fixture.Path,
            "SELECT command_id FROM command_outbox WHERE rental_id=$id AND type='EndLease'", ("$id", lease.Id));
        fixture.Sync("boot", new CommandResult(commandId, "applied", lease.Revision));

        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM leases WHERE id=$id AND state='completed' AND allocation_held=0", ("$id", lease.Id)));
        Assert.Equal("applied", Text(fixture.Path,
            "SELECT status FROM command_outbox WHERE command_id=$command", ("$command", commandId)));
    }

    [Fact]
    public async Task AdjustTimeSupportsIncreaseAndDecreaseAndKeepsIdempotency()
    {
        using var fixture = new LeaseFixture();
        var lease = await fixture.Active();
        var originalExpiry = lease.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds();

        Assert.Throws<ArgumentException>(() => fixture.Db.AdjustMachineLeaseTime("pc", -60_000, lease.Revision, "adjust-low", "admin-1"));
        Assert.Throws<ArgumentException>(() => fixture.Db.AdjustMachineLeaseTime("pc", 7 * 24 * 60 * 60, lease.Revision, "adjust-high", "admin-1"));

        var extended = fixture.Db.AdjustMachineLeaseTime("pc", 3600, lease.Revision, "adjust-1", "admin-1");
        Assert.Equal(lease.Revision + 1, extended.PendingRevision);
        Assert.Equal(originalExpiry + 3600_000, Scalar(fixture.Path,
            "SELECT pending_expires_at FROM leases WHERE id=$id", ("$id", lease.Id)));
        var applyCommand = Text(fixture.Path,
            "SELECT command_id FROM command_outbox WHERE rental_id=$id AND type='ApplyLease' AND status='pending'", ("$id", lease.Id));
        fixture.Sync("boot", new CommandResult(applyCommand, "applied", lease.Revision + 1));
        Assert.Equal(lease.Revision + 1, Scalar(fixture.Path,
            "SELECT revision FROM leases WHERE id=$id", ("$id", lease.Id)));

        var shortened = fixture.Db.AdjustMachineLeaseTime("pc", -600, lease.Revision + 1, "adjust-2", "admin-1");
        Assert.Equal(lease.Revision + 2, shortened.PendingRevision);
        Assert.Equal(originalExpiry + 3600_000 - 600_000, Scalar(fixture.Path,
            "SELECT pending_expires_at FROM leases WHERE id=$id", ("$id", lease.Id)));
    }

    [Fact]
    public async Task AdminPairingQueuesPinWithoutPersistingTheSecret()
    {
        using var fixture = new LeaseFixture();
        var lease = await fixture.Active();
        var boot = Text(fixture.Path, "SELECT boot_id FROM leases WHERE id=$id", ("$id", lease.Id));
        var until = fixture.Time.Now.AddSeconds(20).ToUnixTimeMilliseconds();
        const string pairingId = "00000000000000000000000000000001";
        Execute(fixture.Path,
            "INSERT INTO pairing_offers(machine_id,rental_id,revision,boot_id,valid_until,ids) VALUES('pc',$rental,$revision,$boot,$until,$ids)",
            ("$rental", lease.Id), ("$revision", lease.Revision), ("$boot", boot), ("$until", until),
            ("$ids", "[\"" + pairingId + "\"]"));

        var offer = fixture.Db.GetAdminPairingOffer("pc", "admin-1");
        Assert.NotNull(offer);
        Assert.Equal(lease.Id, offer!.RentalId);
        Assert.Equal([pairingId], offer.PairingIds);

        var queued = fixture.Db.QueueAdminPairing("pc", "admin-1",
            new AdminPairingRequestDto(pairingId, "1234", "pair-request-1"));
        Assert.Equal("queued", queued.Status);
        Assert.Equal(0L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM command_outbox WHERE payload LIKE '%1234%'"));
        Assert.Equal("queued", fixture.Db.GetAdminPairingStatus("pc", queued.CommandId, "admin-1").Status);

        var retried = fixture.Db.QueueAdminPairing("pc", "admin-1",
            new AdminPairingRequestDto(pairingId, "1234", "pair-request-1"));
        Assert.Equal(queued.CommandId, retried.CommandId);
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM command_outbox WHERE type='PairPin'"));
        Assert.Throws<InvalidOperationException>(() => fixture.Db.QueueAdminPairing("pc", "admin-1",
            new AdminPairingRequestDto("00000000000000000000000000000002", "1234", "pair-request-2")));
    }
}
