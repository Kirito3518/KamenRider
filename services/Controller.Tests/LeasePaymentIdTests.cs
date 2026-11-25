using System.Net;
using System.Net.Http.Json;
using KamenRaider.Contracts;
using KamenRaider.Controller;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Controller.Tests;

/// <summary>
/// The web app identifies a rental payment by the create idempotency key it sent. The Controller
/// must expose that key as LeaseDto.PaymentId without changing the database schema, and must let
/// the web app read a single lease by its Controller-issued id.
/// </summary>
public class LeasePaymentIdTests
{
    [Fact]
    public async Task CreateIdempotencyKeyIsExposedAsPaymentIdOnEveryRead()
    {
        using var f = new LeaseFixture();
        var lease = await f.Create("payment-123");

        Assert.Equal("payment-123", lease.PaymentId);
        Assert.Equal(lease.Id, (await f.Create("payment-123")).Id);
        Assert.Equal("payment-123", (await f.Create("payment-123")).PaymentId);
        Assert.Equal("payment-123", f.Db.GetLease(lease.Id)!.PaymentId);
        Assert.Equal("payment-123", Assert.Single(f.Db.GetUserLeases("alice")).PaymentId);
        Assert.Equal("payment-123", Assert.Single(f.Db.GetAllLeases(10)).PaymentId);
    }

    [Fact]
    public async Task DistinctRentalsKeepTheirOwnPaymentIds()
    {
        using var f = new LeaseFixture();
        f.AddRentableMachine("pc2");
        var first = await f.Leases.CreateLeaseAsync("alice", "pc", 60, "payment-alice", default);
        var second = await f.Leases.CreateLeaseAsync("bob", "pc2", 60, "payment-bob", default);

        Assert.Equal("payment-alice", first.PaymentId);
        Assert.Equal("payment-bob", second.PaymentId);
        var all = f.Db.GetAllLeases(10);
        Assert.Equal(new[] { "payment-bob", "payment-alice" }, all.Select(item => item.PaymentId));
        Assert.Equal(new[] { "payment-alice" }, f.Db.GetUserLeases("alice").Select(item => item.PaymentId));
    }

    [Fact]
    public void LeaseWithoutARecordedCreateRequestHasNoPaymentId()
    {
        using var f = new LeaseFixture();
        // Rows written before the create request was recorded (or by a future non-web caller) have no
        // create: lease_request row; reading them must be null-safe instead of throwing.
        Execute(f.Path,
            "INSERT INTO leases(id,user_id,machine_id,state,duration_seconds,preparation_deadline,allocation_held) VALUES('legacy-lease','alice','pc','preparing',60,$deadline,0)",
            ("$deadline", long.MaxValue));

        var lease = f.Db.GetLease("legacy-lease");

        Assert.NotNull(lease);
        Assert.Null(lease!.PaymentId);
    }

    [Fact]
    public async Task LeaseLookupIsLimitedToTheOwnerOrAnAdmin()
    {
        using var f = new LeaseFixture();
        var lease = await f.Create("create-key");
        await using var app = ControllerHost.Build([], b => {
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
                ["InternalUrl"] = "http://127.0.0.1:5101",
                ["AgentUrl"] = "https://127.0.0.1:5102",
                ["DatabasePath"] = f.Path,
                ["ServiceCredential"] = "secret",
            });
            b.WebHost.UseTestServer(); b.Services.AddTransient<IStartupFilter, LoopbackTestFilter>();
            b.Services.AddSingleton(f.Db);
        });
        await app.StartAsync();
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/internal/leases/{lease.Id}")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Service-Credential", "secret");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/internal/leases/{lease.Id}")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "bob");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/internal/leases/{lease.Id}")).StatusCode);

        client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Id");
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "alice");
        var owner = await client.GetFromJsonAsync<LeaseDto>($"/internal/leases/{lease.Id}");
        Assert.Equal(lease.Id, owner!.Id);
        Assert.Equal("create-key", owner.PaymentId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/internal/leases/missing-lease")).StatusCode);

        client.DefaultRequestHeaders.Remove("X-Authenticated-Actor-Id");
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "admin-user");
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role", "admin");
        var admin = await client.GetFromJsonAsync<LeaseDto>($"/internal/leases/{lease.Id}");
        Assert.Equal(lease.Id, admin!.Id);
        Assert.Equal("create-key", admin.PaymentId);
        await app.StopAsync();
    }

    [Fact]
    public async Task ExpiredApplyLeaseCommandReleasesThePendingRevisionLock()
    {
        using var f = new LeaseFixture();
        var lease = await f.Active();
        var pending = await f.Leases.ExtendLeaseAsync(lease.Id, 30, 1, "extend-once", default);
        Assert.Equal(2, pending.PendingRevision);

        // An extension command that can no longer be acknowledged (expired outbox row) must not keep
        // the optimistic revision lock forever: the next lease read has to release it.
        ExpireApplyLeaseCommands(f.Path, lease.Id);

        var unlocked = f.Db.GetLease(lease.Id)!;
        Assert.Equal("active", unlocked.State);
        Assert.Equal(1, unlocked.Revision);
        Assert.Null(unlocked.PendingRevision);

        // With the lock released the renter can still cancel the lease at the same revision.
        var cancelled = await f.Leases.CancelUserLeaseAsync(lease.Id, "alice", 1, "cancel-after-expiry", default);
        Assert.Equal("ending", cancelled.State);
    }

    [Fact]
    public async Task ExtendAfterAnExpiredCommandReusesTheDeadOutboxRow()
    {
        using var f = new LeaseFixture();
        var lease = await f.Active();
        var pending = await f.Leases.ExtendLeaseAsync(lease.Id, 30, 1, "extend-once", default);
        Assert.Equal(2, pending.PendingRevision);
        var firstCommandId = Convert.ToString(Scalar(f.Path,
            "SELECT command_id FROM command_outbox WHERE rental_id=$id AND type='ApplyLease'",
            ("$id", lease.Id)));

        ExpireApplyLeaseCommands(f.Path, lease.Id);
        Assert.Null(f.Db.GetLease(lease.Id)!.PendingRevision);

        // The released lock must not make the next extension collide with the dead outbox row's
        // UNIQUE(rental_id,type,lease_revision): the row is revived and re-queued instead.
        var second = await f.Leases.ExtendLeaseAsync(lease.Id, 60, 1, "extend-again", default);
        Assert.Equal(2, second.PendingRevision);
        Assert.Equal(firstCommandId, Convert.ToString(Scalar(f.Path,
            "SELECT command_id FROM command_outbox WHERE rental_id=$id AND type='ApplyLease'",
            ("$id", lease.Id))));
        Assert.Equal(1L, Convert.ToInt64(Scalar(f.Path,
            "SELECT count(*) FROM command_outbox WHERE rental_id=$id AND type='ApplyLease'",
            ("$id", lease.Id))));
        Assert.Equal("pending", Convert.ToString(Scalar(f.Path,
            "SELECT status FROM command_outbox WHERE rental_id=$id AND type='ApplyLease'",
            ("$id", lease.Id))));
    }

    private static void ExpireApplyLeaseCommands(string path, string leaseId) =>
        Execute(path, "UPDATE command_outbox SET valid_until=0 WHERE rental_id=$id AND type='ApplyLease' AND status='pending'", ("$id", leaseId));

    private static object? Scalar(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command.ExecuteScalar();
    }

    private static void Execute(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        Assert.True(command.ExecuteNonQuery() > 0, "The test fixture statement did not modify any row.");
    }
}
