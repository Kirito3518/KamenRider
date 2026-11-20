using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Machines;
using KamenRaider.Controller.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Controller.Tests;

/// <summary>
/// Parsec attempt lifecycle: capability advertisement from the signed heartbeat, renter-sealed
/// attempts, one-at-a-time/rate limits, signed Agent status updates, lease-end/timeout cleanup
/// and sealed-payload purging (migration 027).
/// </summary>
public sealed class ParsecTests
{
    private static string Base64Url(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ParsecSealedSecret SealedSecret() => new(Base64Url(32), Base64Url(12), Base64Url(64));

    private static ParsecCapability Capability(string bootKeyId = "boot-key-1") =>
        new(bootKeyId, Base64Url(256), true);

    private static SyncRequest ReadyWithCapability(LeaseFixture fixture, ParsecCapability? capability, string? boot = null)
    {
        var machine = fixture.Db.List().Single(m => m.MachineId == "pc");
        return new SyncRequest(boot ?? machine.BootId ?? "boot", "192.0.2.1", "001122334455",
            machine.ConfigRevision, "ready", []) { Parsec = capability };
    }

    private static Task<LeaseFixtureWithAttempt> ActiveAttempt()
    {
        var fixture = new LeaseFixture();
        return ActiveAttemptCore(fixture);
    }

    private static async Task<LeaseFixtureWithAttempt> ActiveAttemptCore(LeaseFixture fixture)
    {
        try
        {
            var lease = await fixture.Active();
            fixture.Db.Update("pc", new MachineUpdate("PC", 500, 1, 2) { ParsecEnabled = true },
                fixture.Db.List().Single(m => m.MachineId == "pc").ConfigRevision);
            fixture.Db.Sync("pc", ReadyWithCapability(fixture, Capability()));
            var attempt = fixture.Db.QueueParsecAttempt(lease.Id, "alice",
                new ParsecAttemptRequest("alice", "idem-1", SealedSecret()));
            return new LeaseFixtureWithAttempt(fixture, lease.Id, attempt);
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private sealed record LeaseFixtureWithAttempt(LeaseFixture Fixture, string LeaseId, ParsecAttemptDto Attempt) : IDisposable
    {
        public void Dispose() => Fixture.Dispose();
    }

    [Fact]
    public async Task LeaseOwnerSeesCapabilityAndOnlyOwnerMayUseIt()
    {
        using var fixture = new LeaseFixture();
        var lease = await fixture.Active();
        fixture.Db.Update("pc", new MachineUpdate("PC", 500, 1, 2) { ParsecEnabled = true },
            fixture.Db.List().Single(m => m.MachineId == "pc").ConfigRevision);
        fixture.Db.Sync("pc", ReadyWithCapability(fixture, Capability("key-owner")));

        var owner = fixture.Db.GetLeaseParsecState(lease.Id, "alice");
        Assert.True(owner.Available);
        Assert.Equal("key-owner", owner.BootKeyId);
        Assert.NotNull(owner.PublicKey);
        Assert.Null(owner.Attempt);
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Db.GetLeaseParsecState(lease.Id, "bob"));

        // An Agent that stops advertising Parsec removes the capability immediately.
        fixture.Db.Sync("pc", ReadyWithCapability(fixture, null));
        Assert.False(fixture.Db.GetLeaseParsecState(lease.Id, "alice").Available);
    }

    [Fact]
    public async Task DisabledMachineCannotQueueAnAttempt()
    {
        using var fixture = new LeaseFixture();
        var lease = await fixture.Active();
        fixture.Db.Sync("pc", ReadyWithCapability(fixture, Capability()));
        Assert.Throws<InvalidOperationException>(() => fixture.Db.QueueParsecAttempt(lease.Id, "alice",
            new ParsecAttemptRequest("alice", "idem-disabled", SealedSecret())));
    }

    [Fact]
    public async Task AttemptQueuesOneSealedCommandAndIsIdempotent()
    {
        using var context = await ActiveAttempt();
        var fixture = context.Fixture;

        using var connection = Open(fixture.Path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type,rental_id,lease_revision,payload FROM command_outbox WHERE type='ParsecConnect'";
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal("ParsecConnect", row.GetString(0));
        Assert.Equal(context.LeaseId, row.GetString(1));
        Assert.True(row.GetInt64(2) > 0);
        var payload = row.GetString(3);
        Assert.Contains(context.Attempt.AttemptId, payload, StringComparison.Ordinal);
        Assert.Contains("ciphertext", payload, StringComparison.Ordinal);
        row.Close();

        var again = fixture.Db.QueueParsecAttempt(context.LeaseId, "alice",
            new ParsecAttemptRequest("alice", "idem-1", SealedSecret()));
        Assert.Equal(context.Attempt.AttemptId, again.AttemptId);
        Assert.Throws<InvalidOperationException>(() => fixture.Db.QueueParsecAttempt(context.LeaseId, "alice",
            new ParsecAttemptRequest("alice", "idem-2", SealedSecret())));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Db.QueueParsecAttempt(context.LeaseId, "bob",
            new ParsecAttemptRequest("bob", "idem-3", SealedSecret())));
    }

    [Fact]
    public async Task SignedStatusReportUpdatesTheAttemptAndRejectsStaleBootKeys()
    {
        using var context = await ActiveAttempt();
        var fixture = context.Fixture;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Db);
        builder.Services.AddSingleton<AgentAuthentication>();
        await using var app = builder.Build();
        app.MapParsec();
        await app.StartAsync();
        using var client = app.GetTestServer().CreateClient();

        using var stale = SignedStatusRequest(fixture,
            new ParsecStatusReport(context.Attempt.AttemptId, "old-boot-key", ParsecStates.Ready, null, null));
        using var staleResponse = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);

        using var unsigned = new HttpRequestMessage(HttpMethod.Post, "/agent/parsec-status")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        using var unsignedResponse = await client.SendAsync(unsigned);
        Assert.Equal(HttpStatusCode.Unauthorized, unsignedResponse.StatusCode);

        using var starting = SignedStatusRequest(fixture,
            new ParsecStatusReport(context.Attempt.AttemptId, "boot-key-1", ParsecStates.Starting, null, null));
        using var startingResponse = await client.SendAsync(starting);
        Assert.Equal(HttpStatusCode.NoContent, startingResponse.StatusCode);
        Assert.Equal(ParsecStates.Starting,
            fixture.Db.GetParsecAttempt(context.LeaseId, context.Attempt.AttemptId, "alice").State);

        using var ready = SignedStatusRequest(fixture,
            new ParsecStatusReport(context.Attempt.AttemptId, "boot-key-1", ParsecStates.Ready, null,
                "ok"));
        using var readyResponse = await client.SendAsync(ready);
        Assert.Equal(HttpStatusCode.NoContent, readyResponse.StatusCode);
        var final = fixture.Db.GetParsecAttempt(context.LeaseId, context.Attempt.AttemptId, "alice");
        Assert.Equal(ParsecStates.Ready, final.State);
        Assert.Equal("ok", final.Message);
    }

    [Fact]
    public async Task TimeoutFailsTheAttemptAndRemovesTheSealedPayload()
    {
        using var context = await ActiveAttempt();
        var fixture = context.Fixture;
        // Keep the rental alive past the attempt window so the timeout path (not lease end) is tested.
        using (var leaseConnection = Open(fixture.Path))
        using (var lease = leaseConnection.CreateCommand())
        {
            lease.CommandText = "UPDATE leases SET expires_at=expires_at+3600000";
            lease.ExecuteNonQuery();
        }
        fixture.Time.Now = fixture.Time.Now.AddSeconds(ParsecControllerConstants.AttemptWindowSeconds + 5);
        fixture.Db.Sync("pc", ReadyWithCapability(fixture, Capability()));

        var attempt = fixture.Db.GetParsecAttempt(context.LeaseId, context.Attempt.AttemptId, "alice");
        Assert.Equal(ParsecStates.Failed, attempt.State);
        Assert.Equal(ParsecErrorCodes.Timeout, attempt.ErrorCode);

        using var connection = Open(fixture.Path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM command_outbox WHERE type='ParsecConnect' AND payload<>'{}'";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public async Task RentalEndFailsPendingAttemptsAndDropsTheirPayload()
    {
        using var context = await ActiveAttempt();
        var fixture = context.Fixture;
        fixture.Time.Now = fixture.Time.Now.AddSeconds(120);
        fixture.Db.Sync("pc", ReadyWithCapability(fixture, Capability()));
        var attempt = fixture.Db.GetParsecAttempt(context.LeaseId, context.Attempt.AttemptId, "alice");
        Assert.Equal(ParsecStates.Failed, attempt.State);
        Assert.Equal(ParsecErrorCodes.LeaseEnded, attempt.ErrorCode);

        using var connection = Open(fixture.Path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM command_outbox WHERE type='ParsecConnect' AND status='pending'";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    private static HttpRequestMessage SignedStatusRequest(LeaseFixture fixture, ParsecStatusReport report)
    {
        var body = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Guid.NewGuid().ToString("N");
        var signature = Convert.ToBase64String(fixture.AgentKey.SignData(
            Encoding.UTF8.GetBytes($"pc\nPOST\n/agent/parsec-status\n{timestamp}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}"),
            HashAlgorithmName.SHA256));
        var request = new HttpRequestMessage(HttpMethod.Post, "/agent/parsec-status")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Agent-Id", "pc");
        request.Headers.Add("X-Agent-Timestamp", timestamp.ToString());
        request.Headers.Add("X-Agent-Nonce", nonce);
        request.Headers.Add("X-Agent-Signature", signature);
        return request;
    }

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
}

/// <summary>Mirrors the Controller's Parsec attempt window without exposing internals.</summary>
internal static class ParsecControllerConstants
{
    public const int AttemptWindowSeconds = 150;
}
