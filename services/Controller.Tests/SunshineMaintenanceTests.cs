using System.Net;
using System.Net.Http.Json;
using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Machines;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Controller.Tests;

/// <summary>
/// Machine-scoped Sunshine maintenance commands (RestartSunshine / UnpairSunshine) that replace
/// the retired Stream Guard maintenance script: migration 026 schema, queueing, audit,
/// delivery/result handling and the internal admin endpoints.
/// </summary>
public sealed class SunshineMaintenanceTests
{
    private static readonly string[] MigrationsThroughVersionTwentyFive =
    [
        "001-machines.sql", "002-leases-commands.sql", "003-pairing.sql", "004-boot-challenges.sql",
        "005-config-acks.sql", "006-enrollment-tickets.sql", "007-admin-vnc-sessions.sql",
        "008-vnc-approved-target.sql", "009-agent-version.sql", "010-completed-leases.sql",
        "011-readiness-diagnostics.sql", "012-agent-releases.sql", "013-agent-update-downloads.sql",
        "014-agent-maintenance-chains.sql", "015-agent-sync-proof.sql", "016-agent-sync-challenge-rate-limit.sql",
        "017-agent-release-contract.sql", "018-agent-update-source-binding.sql", "019-controller-restore-quarantine.sql",
        "020-agent-update-recovery-intents.sql", "021-agent-update-recovery-successors.sql",
        "022-agent-update-confirmations.sql", "023-pc-control-commands.sql", "024-machine-profile-sunshine.sql",
        "025-lease-ended-at.sql",
    ];

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

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static long Scalar(string path, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string[] Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var rows = command.ExecuteReader();
        var columns = new List<string>();
        while (rows.Read()) columns.Add(rows.GetString(1));
        return columns.ToArray();
    }

    [Fact]
    public void VersionTwentyFiveDatabaseGainsMachineScopedSunshineCommandsAndPreservesRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sunshine-maintenance-{Guid.NewGuid():N}.db");
        try
        {
            CreateVersionTwentyFiveDatabase(path);
            using (var before = Open(path))
            {
                Assert.Equal(25L, Scalar(before, "PRAGMA user_version"));
                Assert.Equal(1L, Scalar(before, "SELECT COUNT(*) FROM machine_admin_actions"));
            }

            using (new ControllerDatabase(path)) { }

            using (var verify = Open(path))
            {
                Assert.Equal(27L, Scalar(verify, "PRAGMA user_version"));
                // Existing outbox and audit rows keep their identity and payload.
                Assert.Equal(42L, Scalar(verify, "SELECT rowid FROM command_outbox WHERE command_id='command'"));
                Assert.Equal("ApplyLease", Scalar(verify, "SELECT type FROM command_outbox WHERE command_id='command'"));
                Assert.Equal("powerOff", Scalar(verify, "SELECT action_type FROM machine_admin_actions WHERE action_id='action-1'"));
                Assert.Equal("command", Scalar(verify, "SELECT command_id FROM machine_admin_actions WHERE action_id='action-1'"));

                using (var legacyResults = verify.CreateCommand())
                {
                    legacyResults.CommandText = """
                        INSERT INTO command_outbox(command_id,type,rental_id,machine_id,lease_revision,boot_id,valid_until,payload,status)
                          VALUES('restart','RestartSunshine',NULL,'pc',0,'boot',999999,'{}','pending');
                        INSERT INTO machine_admin_actions(action_id,machine_id,actor_id,request_id,action_type,command_id,payload_digest,created_at_unix_ms)
                          VALUES('action-2','pc','admin-1','request-2','unpairSunshine','restart','AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',2000);
                        """;
                    Assert.Equal(2, legacyResults.ExecuteNonQuery());
                }

                // Machine-scoped rows must be rental-free with revision zero; lease-scoped rows still require both.
                Assert.Throws<SqliteException>(() => Execute(verify,
                    "INSERT INTO command_outbox(command_id,type,rental_id,machine_id,lease_revision,boot_id,valid_until,payload,status) VALUES('bad-1','UnpairSunshine','live','pc',0,'boot',1,'{}','pending')"));
                Assert.Throws<SqliteException>(() => Execute(verify,
                    "INSERT INTO command_outbox(command_id,type,rental_id,machine_id,lease_revision,boot_id,valid_until,payload,status) VALUES('bad-2','RestartSunshine',NULL,'pc',1,'boot',1,'{}','pending')"));
                Assert.Throws<SqliteException>(() => Execute(verify,
                    "INSERT INTO command_outbox(command_id,type,rental_id,machine_id,lease_revision,boot_id,valid_until,payload,status) VALUES('bad-3','ApplyLease',NULL,'pc',0,'boot',1,'{}','pending')"));
                Assert.Throws<SqliteException>(() => Execute(verify,
                    "INSERT INTO machine_admin_actions(action_id,machine_id,actor_id,request_id,action_type,command_id,payload_digest,created_at_unix_ms) VALUES('bad-4','pc','admin-1','request-4','maintenanceMode',NULL,'" + new string('B', 64) + "',1)"));

                // Append-only audit triggers were recreated with the rebuilt table.
                Assert.Throws<SqliteException>(() => Execute(verify, "UPDATE machine_admin_actions SET actor_id='other' WHERE action_id='action-1'"));
                Assert.Throws<SqliteException>(() => Execute(verify, "DELETE FROM machine_admin_actions WHERE action_id='action-1'"));

                foreach (var index in new[]
                {
                    "machine_sunshine_pending", "machine_shutdown_pending", "lease_command_revision",
                    "machine_admin_action_machine"
                })
                    Assert.Equal(1L, Scalar(verify, $"SELECT COUNT(*) FROM sqlite_schema WHERE type='index' AND name='{index}'"));
                using var foreignKeys = verify.CreateCommand();
                foreignKeys.CommandText = "PRAGMA foreign_key_check";
                using var rows = foreignKeys.ExecuteReader();
                Assert.False(rows.Read());
                ControllerDatabaseMaintenance.ValidateCanonicalSchema(verify, 27);
                ControllerDatabaseMaintenance.ValidateSunshineMaintenanceSchema(verify);
            }

            // Reopening an already migrated database must not re-run the table rebuild.
            using (new ControllerDatabase(path)) { }
            using (var restarted = Open(path))
            {
                Assert.Equal(27L, Scalar(restarted, "PRAGMA user_version"));
                Assert.Equal(2L, Scalar(restarted, "SELECT COUNT(*) FROM command_outbox"));
                Assert.Equal(2L, Scalar(restarted, "SELECT COUNT(*) FROM machine_admin_actions"));
                Assert.DoesNotContain("migration_rowid", Columns(restarted, "command_outbox"));
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void QueueingSunshineMaintenanceWritesTheCommandAndAnAuditRow()
    {
        using var fixture = new LeaseFixture();
        var queued = fixture.Db.QueueSunshineMaintenanceCommand("pc",
            new AdminSunshineCommandRequestDto(AdminSunshineCommandRequestDto.Restart, false, "sunshine-request-1"), "admin-1");
        Assert.Equal("requested", queued.Status);
        Assert.Equal("restart", queued.Action);
        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM command_outbox WHERE command_id=$command AND type='RestartSunshine' AND rental_id IS NULL AND lease_revision=0 AND boot_id='boot' AND status='pending' AND payload='{\"force\":false}'",
            ("$command", queued.CommandId)));
        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM machine_admin_actions WHERE machine_id='pc' AND actor_id='admin-1' AND request_id='sunshine-request-1' AND action_type='restartSunshine' AND command_id=$command AND length(payload_digest)=64",
            ("$command", queued.CommandId)));

        // The exact request replay returns the original command instead of queuing another.
        var retried = fixture.Db.QueueSunshineMaintenanceCommand("pc",
            new AdminSunshineCommandRequestDto(AdminSunshineCommandRequestDto.Restart, false, "sunshine-request-1"), "admin-1");
        Assert.Equal(queued.CommandId, retried.CommandId);
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM command_outbox WHERE type='RestartSunshine'"));
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM machine_admin_actions WHERE request_id='sunshine-request-1'"));

        // One request id cannot be reused for the other action type.
        Assert.Throws<InvalidOperationException>(() => fixture.Db.QueueSunshineMaintenanceCommand("pc",
            new AdminSunshineCommandRequestDto(AdminSunshineCommandRequestDto.Unpair, true, "sunshine-request-1"), "admin-1"));

        // The command is delivered to the Agent boot that was online when it was queued.
        var delivered = Assert.Single(fixture.Sync().Commands);
        Assert.Equal("RestartSunshine", delivered.Type);
        Assert.Equal(string.Empty, delivered.RentalId);
        Assert.Equal(0L, delivered.LeaseRevision);
        Assert.Equal("boot", delivered.BootId);
        fixture.Sync("boot", new CommandResult(delivered.CommandId, "applied", 0));
        Assert.Equal("applied", fixture.Db.GetSunshineMaintenanceStatus("pc", queued.CommandId, "admin-1").Status);
    }

    [Fact]
    public async Task UnpairSunshineRequiresForceWhileARentalIsActive()
    {
        using var fixture = new LeaseFixture();
        await fixture.Active();
        Assert.Throws<InvalidOperationException>(() => fixture.Db.QueueSunshineMaintenanceCommand("pc",
            new AdminSunshineCommandRequestDto(AdminSunshineCommandRequestDto.Unpair, false, "unpair-safe"), "admin-1"));

        var queued = fixture.Db.QueueSunshineMaintenanceCommand("pc",
            new AdminSunshineCommandRequestDto(AdminSunshineCommandRequestDto.Unpair, true, "unpair-forced"), "admin-1");
        Assert.Equal("requested", queued.Status);
        Assert.Equal("unpair", queued.Action);
        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM command_outbox WHERE type='UnpairSunshine' AND rental_id IS NULL AND lease_revision=0"));
        Assert.Equal(1L, Scalar(fixture.Path,
            "SELECT COUNT(*) FROM machine_admin_actions WHERE action_type='unpairSunshine' AND request_id='unpair-forced'"));

        var delivered = Assert.Single(fixture.Sync().Commands);
        Assert.Equal("UnpairSunshine", delivered.Type);
        fixture.Sync("boot", new CommandResult(delivered.CommandId, "failed", 0));
        Assert.Equal("failed", fixture.Db.GetSunshineMaintenanceStatus("pc", queued.CommandId, "admin-1").Status);
    }

    [Fact]
    public void SunshineMaintenanceRejectsOfflineAndUnknownMachines()
    {
        using var fixture = new LeaseFixture();
        Assert.Throws<KeyNotFoundException>(() => fixture.Db.QueueSunshineMaintenanceCommand("missing",
            new AdminSunshineCommandRequestDto(AdminSunshineCommandRequestDto.Restart, false, "offline-1"), "admin-1"));
        fixture.Time.Now = fixture.Time.Now.AddSeconds(30);
        Assert.Throws<InvalidOperationException>(() => fixture.Db.QueueSunshineMaintenanceCommand("pc",
            new AdminSunshineCommandRequestDto(AdminSunshineCommandRequestDto.Restart, false, "offline-2"), "admin-1"));
    }

    [Fact]
    public void PendingSunshineCommandWithAnotherActionBlocksNewActions()
    {
        using var fixture = new LeaseFixture();
        var restart = fixture.Db.QueueSunshineMaintenanceCommand("pc",
            new AdminSunshineCommandRequestDto(AdminSunshineCommandRequestDto.Restart, false, "restart-pending"), "admin-1");
        Assert.Throws<InvalidOperationException>(() => fixture.Db.QueueSunshineMaintenanceCommand("pc",
            new AdminSunshineCommandRequestDto(AdminSunshineCommandRequestDto.Unpair, false, "unpair-blocked"), "admin-1"));

        // A replay of the original request is still idempotent while the command is pending.
        var retried = fixture.Db.QueueSunshineMaintenanceCommand("pc",
            new AdminSunshineCommandRequestDto(AdminSunshineCommandRequestDto.Restart, false, "restart-pending"), "admin-1");
        Assert.Equal(restart.CommandId, retried.CommandId);
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM command_outbox WHERE type IN ('RestartSunshine','UnpairSunshine') AND status='pending'"));
    }

    [Fact]
    public void ExpiredSunshineCommandIsReportedExpired()
    {
        using var fixture = new LeaseFixture();
        var queued = fixture.Db.QueueSunshineMaintenanceCommand("pc",
            new AdminSunshineCommandRequestDto(AdminSunshineCommandRequestDto.Restart, false, "expire-me"), "admin-1");
        fixture.Time.Now = fixture.Time.Now.AddSeconds(61);
        Assert.Equal("expired", fixture.Db.GetSunshineMaintenanceStatus("pc", queued.CommandId, "admin-1").Status);
        // After expiry the pinned request still narrows an idempotent retry to that command.
        var retried = fixture.Db.QueueSunshineMaintenanceCommand("pc",
            new AdminSunshineCommandRequestDto(AdminSunshineCommandRequestDto.Restart, false, "expire-me"), "admin-1");
        Assert.Equal(queued.CommandId, retried.CommandId);
        Assert.Equal("expired", retried.Status);
    }

    [Fact]
    public void SunshineCommandIsDeliveredAndAppliedWhileTheMachineIsNotReady()
    {
        using var fixture = new LeaseFixture();
        var queued = fixture.Db.QueueSunshineMaintenanceCommand("pc",
            new AdminSunshineCommandRequestDto(AdminSunshineCommandRequestDto.Unpair, false, "not-ready-1"), "admin-1");
        var blocked = fixture.Db.Sync("pc", new SyncRequest("boot", "192.0.2.1", "001122334455", 3, "notReady", []));
        var delivered = Assert.Single(blocked.Commands);
        Assert.Equal("UnpairSunshine", delivered.Type);

        fixture.Db.Sync("pc", new SyncRequest("boot", "192.0.2.1", "001122334455", 3, "notReady",
            [new CommandResult(delivered.CommandId, "applied", 0)]));
        Assert.Equal("applied", fixture.Db.GetSunshineMaintenanceStatus("pc", queued.CommandId, "admin-1").Status);
    }

    [Fact]
    public async Task InternalSunshineEndpointsRequireAdminHeadersValidateAndExposeStatus()
    {
        using var fixture = new LeaseFixture();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Db);
        using var app = builder.Build();
        app.MapPcControl();
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var unauthenticated = await client.PostAsJsonAsync("/internal/admin/pc-control/pc/sunshine",
            new { action = "restart", force = false });
        Assert.Equal(HttpStatusCode.Forbidden, unauthenticated.StatusCode);

        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "admin-1");
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role", "admin");
        using var invalid = await client.PostAsJsonAsync("/internal/admin/pc-control/pc/sunshine",
            new { action = "explode", force = false });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var unknown = await client.PostAsJsonAsync("/internal/admin/pc-control/missing/sunshine",
            new { action = "restart", force = false });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        using var queued = await client.PostAsJsonAsync("/internal/admin/pc-control/pc/sunshine",
            new { action = "restart", force = false, requestId = "http-restart-1" });
        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        var created = await queued.Content.ReadFromJsonAsync<AdminSunshineCommandStatusDto>();
        Assert.NotNull(created);
        Assert.Equal("requested", created!.Status);
        Assert.Equal("restart", created.Action);

        using var status = await client.GetAsync($"/internal/admin/pc-control/pc/sunshine/{created.CommandId}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var current = await status.Content.ReadFromJsonAsync<AdminSunshineCommandStatusDto>();
        Assert.Equal(created.CommandId, current!.CommandId);
        Assert.Equal("requested", current.Status);

        using var missing = await client.GetAsync("/internal/admin/pc-control/pc/sunshine/doesnotexist");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static int Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    private static void CreateVersionTwentyFiveDatabase(string path)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        foreach (var name in MigrationsThroughVersionTwentyFive)
        {
            using var resource = typeof(ControllerDatabase).Assembly
                .GetManifestResourceStream($"KamenRaider.Controller.Data.Migrations.{name}")!;
            using var reader = new StreamReader(resource);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
        }

        using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = """
                INSERT INTO machines(machine_id,public_key,approval_status,display_name,hourly_price,local_ip,mac_address,moonlight_port,sunshine_port)
                  VALUES('pc','key','approved','Legacy PC',500,'10.0.0.2','001122334455',47989,47990);
                INSERT INTO agent_sync_proof_state(machine_id,key_fingerprint) VALUES('pc',NULL);
                INSERT INTO leases(rowid,id,user_id,machine_id,state,revision,duration_seconds,preparation_deadline,starts_at,expires_at,allocation_held)
                  VALUES(17,'live','user','pc','active',1,60,0,1000,61000,1);
                INSERT INTO command_outbox(rowid,command_id,type,rental_id,machine_id,lease_revision,boot_id,valid_until,payload,status)
                  VALUES(42,'command','ApplyLease','live','pc',1,'boot',61000,'{}','applied');
                INSERT INTO machine_admin_actions(action_id,machine_id,actor_id,request_id,action_type,command_id,payload_digest,created_at_unix_ms)
                  VALUES('action-1','pc','admin-1','request-1','powerOff','command','AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',1000);
                PRAGMA user_version=25;
                """;
            seed.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
