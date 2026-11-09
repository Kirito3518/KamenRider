using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Agents;
using KamenRaider.Controller.Machines;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Controller.Tests;

public sealed class AgentUpdateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProofFromAnotherOutcomeOrOperationCannotConfirmTheCurrentOutcome(bool nextOperation)
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        AddRelease(fixture.Path, "3.0.0");
        fixture.SyncVersion("1.0.0");
        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0");
        fixture.Db.StartAgentUpdate("pc", update.Ticket);
        fixture.Db.ReportAgentUpdateStatus("pc", update.Ticket, "awaitingHeartbeat");
        var request = new SyncRequest("boot", "192.0.2.1", "001122334455", 3, "ready", []) { AgentVersion = "0.9.0" };
        var oldProof = fixture.Db.Sync("pc", request).AgentUpdateChallenge;
        Assert.NotNull(oldProof);
        if (nextOperation)
        {
            fixture.Db.Sync("pc", request with { AgentVersion = "2.0.0", AgentUpdateChallenge = oldProof });
            update = fixture.Db.ReserveAgentUpdate("pc", "3.0.0");
            fixture.Db.StartAgentUpdate("pc", update.Ticket);
        }
        fixture.Db.ReportAgentUpdateStatus("pc", update.Ticket, "rollingBack");
        var previous = nextOperation ? "2.0.0" : "1.0.0";

        var response = fixture.Db.Sync("pc", request with { AgentVersion = previous, AgentUpdateChallenge = oldProof });

        Assert.Equal("rollingBack", OperationStatus(fixture.Path, update.OperationId));
        fixture.Db.Sync("pc", request with { AgentVersion = previous, AgentUpdateChallenge = response.AgentUpdateChallenge });
        Assert.Equal("rolledBack", OperationStatus(fixture.Path, update.OperationId));
    }

    [Theory]
    [InlineData("awaitingHeartbeat", "2.0.0")]
    [InlineData("rollingBack", "1.0.0")]
    public void RepeatedOutcomeReportInvalidatesEarlierConfirmation(string status, string version)
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0");
        fixture.Db.StartAgentUpdate("pc", update.Ticket);
        fixture.Db.ReportAgentUpdateStatus("pc", update.Ticket, status);
        var request = new SyncRequest("boot", "192.0.2.1", "001122334455", 3, "ready", []) { AgentVersion = "0.9.0" };
        var firstChallenge = fixture.Db.Sync("pc", request).AgentUpdateChallenge;
        Assert.NotNull(firstChallenge);
        fixture.Db.ReportAgentUpdateStatus("pc", update.Ticket, status);

        var response = fixture.Db.Sync("pc", request with { AgentVersion = version, AgentUpdateChallenge = firstChallenge });

        Assert.Equal(status, OperationStatus(fixture.Path, update.OperationId));
        Assert.NotEqual(firstChallenge, response.AgentUpdateChallenge);
        fixture.Db.Sync("pc", request with { AgentVersion = version, AgentUpdateChallenge = response.AgentUpdateChallenge });
        Assert.Equal(status == "rollingBack" ? "rolledBack" : "completed", OperationStatus(fixture.Path, update.OperationId));
    }

    [Fact]
    public void ADelayedHeartbeatWithoutPostOutcomeProofCannotResolveAnUpdate()
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0");
        fixture.Db.StartAgentUpdate("pc", update.Ticket);
        var delayed = new SyncRequest("boot", "192.0.2.1", "001122334455", 3, "ready", []) { AgentVersion = "1.0.0" };
        fixture.Db.ReportAgentUpdateStatus("pc", update.Ticket, "rollingBack");

        fixture.Db.Sync("pc", delayed);

        Assert.Equal("rollingBack", OperationStatus(fixture.Path, update.OperationId));
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OldConfirmationCannotResolveAfterControllerRestartOrRecoveryTicketRotation(bool restart)
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0");
        fixture.Db.StartAgentUpdate("pc", update.Ticket);
        fixture.Db.ReportAgentUpdateStatus("pc", update.Ticket, "rollingBack");
        var response = fixture.SyncVersion("0.9.0");
        var json = JsonSerializer.SerializeToElement(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var staleChallenge = json.TryGetProperty("agentUpdateChallenge", out var challenge) ? challenge.GetString() : "no-challenge";
        if (restart) fixture.Reopen();
        else
        {
            var recovery = fixture.Db.RenewAgentUpdateTicket("pc", update.OperationId);
            fixture.Db.StartAgentUpdate("pc", recovery.Ticket);
            fixture.Db.ReportAgentUpdateStatus("pc", recovery.Ticket, "rollingBack");
        }
        var request = JsonSerializer.Deserialize<SyncRequest>(JsonSerializer.Serialize(new {
            bootId = "boot", localIp = "192.0.2.1", macAddress = "001122334455", appliedConfigRevision = 3,
            readiness = "ready", commandResults = Array.Empty<object>(), agentVersion = "1.0.0", agentUpdateChallenge = staleChallenge,
        }), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        fixture.Db.Sync("pc", request);

        Assert.Equal("rollingBack", OperationStatus(fixture.Path, update.OperationId));
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public void LatestUnversionedHeartbeatCannotRefreshAnOlderVersionForReservation()
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        fixture.Sync();

        Assert.Throws<InvalidOperationException>(() => fixture.Db.ReserveAgentUpdate("pc", "2.0.0"));
        Assert.Null(Assert.Single(fixture.Db.List()).AgentUpdateOperationId);
    }

    [Fact]
    public async Task PendingMachineMayBeUpdatedButStillCannotRent()
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        using (var connection = Open(fixture.Path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE machines SET approval_status='pending' WHERE machine_id='pc'";
            command.ExecuteNonQuery();
        }
        fixture.SyncVersion("1.0.0");

        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0");

        var machine = Assert.Single(fixture.Db.List());
        Assert.Equal("pending", machine.ApprovalStatus);
        Assert.Equal("ready", machine.Readiness);
        Assert.Equal(update.OperationId, machine.AgentUpdateOperationId);
        Assert.False(machine.CanRent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Create());
    }

    [Fact]
    public void MissingReleaseArtifactCannotAcquireAnUpdateLock()
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0", missing: true);
        fixture.SyncVersion("1.0.0");

        Assert.Throws<InvalidOperationException>(() => fixture.Db.ReserveAgentUpdate("pc", "2.0.0"));
        Assert.Null(Assert.Single(fixture.Db.List()).AgentUpdateOperationId);
        Assert.True(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void LegacyPublishedArtifactWithoutChallengeCapabilityCannotAcquireAnUpdateLock(bool legacyManifest, bool legacyAssembly)
    {
        using var fixture = new LeaseFixture();
        var bytes = AgentUpdateTestPackage.Create("2.3.4", legacyAssembly, legacyManifest);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        AddRelease(fixture.Path, "2.3.4", hash, bytes.Length);
        var path = ControllerReleasePath(fixture.Path, hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        fixture.SyncVersion("1.0.0");

        Assert.Throws<InvalidOperationException>(() => fixture.Db.ReserveAgentUpdate("pc", "2.3.4"));
        Assert.Null(Assert.Single(fixture.Db.List()).AgentUpdateOperationId);
        Assert.True(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public void CorruptPublishedArtifactCannotAcquireAnUpdateLock()
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        var release = Assert.Single(fixture.Db.ListAgentReleases());
        File.WriteAllText(ControllerReleasePath(fixture.Path, release.PackageSha256), "corrupt");
        fixture.SyncVersion("1.0.0");

        Assert.Throws<InvalidOperationException>(() => fixture.Db.ReserveAgentUpdate("pc", "2.0.0"));
        Assert.Null(Assert.Single(fixture.Db.List()).AgentUpdateOperationId);
        Assert.True(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public async Task MachineListDistinguishesHeldLeaseFromUnreadyMachine()
    {
        using var fixture = new LeaseFixture();
        var revision = Assert.Single(fixture.Db.List()).ConfigRevision;
        fixture.Db.Sync("pc", new SyncRequest("boot", "192.0.2.1", "001122334455", revision, "notReady", []));
        await using var host = await StartHost(fixture.Db);

        using var unready = JsonDocument.Parse(await host.Client.GetStringAsync("/internal/machines"));
        var unreadyMachine = unready.RootElement[0];
        Assert.False(unreadyMachine.GetProperty("canRent").GetBoolean());
        Assert.False(unreadyMachine.GetProperty("allocationHeld").GetBoolean());

        fixture.Sync();
        await fixture.Create();
        using var allocated = JsonDocument.Parse(await host.Client.GetStringAsync("/internal/machines"));
        Assert.True(allocated.RootElement[0].GetProperty("allocationHeld").GetBoolean());
    }

    [Fact]
    public async Task ReservationRejectsTheInstalledVersionWithoutLockingMachine()
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "1.0.0");
        fixture.SyncVersion("1.0.0");
        await using var host = await StartHost(fixture.Db);

        using var response = await Reserve(host.Client, "pc", "1.0.0");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Theory]
    [InlineData("installing")]
    [InlineData("failed")]
    [InlineData("rollingBack")]
    public async Task TargetHeartbeatCannotCompleteAnUnconfirmedTransaction(string status)
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0");
        fixture.Db.StartAgentUpdate("pc", update.Ticket);
        if (status != "installing") fixture.Db.ReportAgentUpdateStatus("pc", update.Ticket, status);

        fixture.SyncVersion("2.0.0");

        Assert.Equal(status, OperationStatus(fixture.Path, update.OperationId));
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Create("unconfirmed-update"));
    }

    [Fact]
    public async Task HeartbeatMustFollowItsTransactionReportToReleaseTheLock()
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0");
        fixture.Db.StartAgentUpdate("pc", update.Ticket);

        fixture.SyncVersion("2.0.0");
        Assert.Equal("installing", OperationStatus(fixture.Path, update.OperationId));
        fixture.Db.ReportAgentUpdateStatus("pc", update.Ticket, "awaitingHeartbeat");
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Create("before-target-heartbeat"));
        fixture.SyncVersion("2.0.0");
        Assert.Equal("completed", OperationStatus(fixture.Path, update.OperationId));
        Assert.True(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public async Task RollbackHeartbeatMustFollowRollbackReportAndTargetCannotCompleteDuringRollback()
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        var update = fixture.Db.ReserveAgentUpdate("pc", "2.0.0");
        fixture.Db.StartAgentUpdate("pc", update.Ticket);

        fixture.SyncVersion("1.0.0");
        fixture.Db.ReportAgentUpdateStatus("pc", update.Ticket, "rollingBack");
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
        fixture.SyncVersion("2.0.0");
        Assert.Equal("rollingBack", OperationStatus(fixture.Path, update.OperationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Create("during-rollback"));
        fixture.SyncVersion("1.0.0");
        Assert.Equal("rolledBack", OperationStatus(fixture.Path, update.OperationId));
        Assert.True(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public async Task UpdateReservationRejectsMachineWithHeldLease()
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        await fixture.Create();
        await using var host = await StartHost(fixture.Db);

        using var response = await Reserve(host.Client, "pc", "2.0.0");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task UpdateReservationRequiresFreshHeartbeatWithKnownAgentVersion()
    {
        using var unversioned = new LeaseFixture();
        AddRelease(unversioned.Path, "2.0.0");
        await using var unversionedHost = await StartHost(unversioned.Db);
        using var noVersion = await Reserve(unversionedHost.Client, "pc", "2.0.0");
        Assert.Equal(HttpStatusCode.Conflict, noVersion.StatusCode);

        using var stale = new LeaseFixture();
        AddRelease(stale.Path, "2.0.0");
        stale.SyncVersion("1.0.0");
        stale.Time.Now = stale.Time.Now.AddSeconds(21);
        await using var staleHost = await StartHost(stale.Db);
        using var staleHeartbeat = await Reserve(staleHost.Client, "pc", "2.0.0");
        Assert.Equal(HttpStatusCode.Conflict, staleHeartbeat.StatusCode);
    }

    [Fact]
    public void ReleaseMetadataIsImmutableAndListedWithoutThePrivatePackagePath()
    {
        using var fixture = new LeaseFixture();
        var publishedAt = fixture.Time.Now;
        var release = new AgentReleasePublication("2.0.0", "stable", "release notes", new string('a', 64),
            Path.Combine(Path.GetTempPath(), "private-agent-package.zip"), 1024, "commit-id", false, true);

        var saved = fixture.Db.PublishAgentRelease(release);

        Assert.Equal("2.0.0", saved.Version);
        Assert.Equal("stable", saved.Channel);
        Assert.Equal(new string('A', 64), saved.PackageSha256);
        Assert.Equal(1024, saved.PackageSizeBytes);
        Assert.Equal(publishedAt, saved.PublishedAtUtc);
        Assert.DoesNotContain("PackagePath", typeof(AgentReleaseDto).GetProperties().Select(property => property.Name));
        Assert.Equal(saved, Assert.Single(fixture.Db.ListAgentReleases()));
        Assert.Throws<SqliteException>(() => fixture.Db.PublishAgentRelease(release with { Channel = "beta", ReleaseNotes = "replacement" }));
    }

    [Fact]
    public async Task LeaseCreationAndUpdateReservationSerializeOnTheDatabase()
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        using var secondConnection = new ControllerDatabase(fixture.Path, clock: fixture.Time);
        await using var host = await StartHost(secondConnection);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> TryCreateLease()
        {
            await start.Task;
            try
            {
                await fixture.Leases.CreateTrialLeaseAsync("alice", "pc", 60, "race", default);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        async Task<HttpStatusCode> TryReserveUpdate()
        {
            await start.Task;
            using var response = await Reserve(host.Client, "pc", "2.0.0");
            return response.StatusCode;
        }

        var leaseAttempt = Task.Run(TryCreateLease);
        var updateAttempt = Task.Run(TryReserveUpdate);
        start.SetResult();
        var leaseWon = await leaseAttempt;
        var updateStatus = await updateAttempt;

        Assert.Equal(leaseWon ? HttpStatusCode.Conflict : HttpStatusCode.OK, updateStatus);
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public async Task MaintenanceLockMakesMachineUnavailableAndBlocksLeaseCreation()
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        await using var host = await StartHost(fixture.Db);

        using var response = await Reserve(host.Client, "pc", "2.0.0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Create("maintenance-lock"));
    }

    [Fact]
    public async Task ExpiredUnstartedTicketExpiresReservationAndAllowsANewOperation()
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        AddRelease(fixture.Path, "3.0.0");
        fixture.SyncVersion("1.0.0");
        await using var host = await StartHost(fixture.Db);

        string firstOperationId;
        using (var first = await Reserve(host.Client, "pc", "2.0.0"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            firstOperationId = await ReadString(first, "operationId");
        }
        fixture.Time.Now = fixture.Time.Now.AddMinutes(10);
        fixture.SyncVersion("1.0.0");

        using var second = await Reserve(host.Client, "pc", "3.0.0");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotEqual(firstOperationId, await ReadString(second, "operationId"));
        Assert.Equal("expired", OperationStatus(fixture.Path, firstOperationId));
    }

    [Fact]
    public async Task ExpiredUnstartedTicketReleasesTheLockForLeaseCreation()
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        await using var host = await StartHost(fixture.Db);
        using var reservation = await Reserve(host.Client, "pc", "2.0.0");
        var operationId = await ReadString(reservation, "operationId");
        fixture.Time.Now = fixture.Time.Now.AddMinutes(10);
        fixture.SyncVersion("1.0.0");

        Assert.True(Assert.Single(fixture.Db.List()).CanRent);
        var lease = await fixture.Create("after-ticket-expiry");

        Assert.Equal("preparing", lease.State);
        Assert.Equal("expired", OperationStatus(fixture.Path, operationId));
        Assert.Equal("expired", Assert.Single(fixture.Db.List()).AgentUpdateStatus);
    }

    [Fact]
    public async Task StartedFailedOperationKeepsMaintenanceLockUntilRecovery()
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        AddRelease(fixture.Path, "3.0.0");
        fixture.SyncVersion("1.0.0");
        await using var host = await StartHost(fixture.Db);
        using var initial = await Reserve(host.Client, "pc", "2.0.0");
        var operationId = await ReadString(initial, "operationId");
        SetStartedStatus(fixture.Path, operationId, "failed");

        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
        Assert.Equal("failed", Assert.Single(fixture.Db.List()).AgentUpdateStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Create("failed-operation-lock"));
        using var otherReservation = await Reserve(host.Client, "pc", "3.0.0");
        Assert.Equal(HttpStatusCode.Conflict, otherReservation.StatusCode);
    }

    [Fact]
    public async Task RecoveryRenewsTicketForTheSameFailedOperation()
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        fixture.SyncVersion("1.0.0");
        await using var host = await StartHost(fixture.Db);
        using var initial = await Reserve(host.Client, "pc", "2.0.0");
        var operationId = await ReadString(initial, "operationId");
        var firstTicket = await ReadString(initial, "ticket");
        SetStartedStatus(fixture.Path, operationId, "failed");

        using var recovery = await host.Client.PostAsync($"/internal/machines/pc/agent-updates/{operationId}/recovery-ticket", content: null);

        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
        Assert.Equal(operationId, await ReadString(recovery, "operationId"));
        Assert.NotEqual(firstTicket, await ReadString(recovery, "ticket"));
        Assert.Equal("failed", OperationStatus(fixture.Path, operationId));
        Assert.False(Assert.Single(fixture.Db.List()).CanRent);
    }

    [Fact]
    public async Task PublicUpdateEndpointsBindStartAndOnePackageDownloadToTheCurrentTicket()
    {
        using var fixture = new LeaseFixture();
        var package = AgentUpdateTestPackage.Create("2.0.0");
        var packageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(package));
        AddRelease(fixture.Path, "2.0.0", packageHash, package.Length);
        var storeRoot = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "agent-releases");
        var packageDirectory = Path.Combine(storeRoot, "sha256", packageHash[..2].ToLowerInvariant());
        Directory.CreateDirectory(packageDirectory);
        await File.WriteAllBytesAsync(Path.Combine(packageDirectory, packageHash.ToLowerInvariant() + ".zip"), package);
        fixture.SyncVersion("1.0.0");
        await using var host = await StartHost(fixture.Db, storeRoot);
        using var reservation = await Reserve(host.Client, "pc", "2.0.0");
        var operationId = await ReadString(reservation, "operationId");
        var ticket = await ReadString(reservation, "ticket");

        using var absentTicket = await host.Client.GetAsync($"/agent/machines/pc/agent-updates/{operationId}/package");
        Assert.Equal(HttpStatusCode.Unauthorized, absentTicket.StatusCode);

        var request = new
        {
            ticket,
            releaseVersion = "2.0.0",
            targetVersion = "2.0.0",
            targetPackageSha256 = packageHash,
        };
        using var wrongMachine = await host.Client.PostAsJsonAsync($"/agent/machines/other/agent-updates/{operationId}/start", request);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongMachine.StatusCode);
        using var wrongRelease = await host.Client.PostAsJsonAsync($"/agent/machines/pc/agent-updates/{operationId}/start", request with { releaseVersion = "3.0.0" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongRelease.StatusCode);
        using var wrongHash = await host.Client.PostAsJsonAsync($"/agent/machines/pc/agent-updates/{operationId}/start", request with { targetPackageSha256 = new string('C', 64) });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongHash.StatusCode);

        using var started = await host.Client.PostAsJsonAsync($"/agent/machines/pc/agent-updates/{operationId}/start", request);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var repeatedStart = await host.Client.PostAsJsonAsync($"/agent/machines/pc/agent-updates/{operationId}/start", request);
        Assert.Equal(HttpStatusCode.Unauthorized, repeatedStart.StatusCode);

        var statusReport = new
        {
            ticket,
            releaseVersion = "2.0.0",
            targetVersion = "2.0.0",
            targetPackageSha256 = packageHash,
            status = "awaitingHeartbeat",
        };
        using var wrongStatusIdentity = await host.Client.PostAsJsonAsync($"/agent/machines/pc/agent-updates/{operationId}/status", statusReport with { targetVersion = "3.0.0" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongStatusIdentity.StatusCode);
        using var reported = await host.Client.PostAsJsonAsync($"/agent/machines/pc/agent-updates/{operationId}/status", statusReport);
        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        Assert.Equal("no-store", reported.Headers.CacheControl?.ToString());
        using var repeatedReport = await host.Client.PostAsJsonAsync($"/agent/machines/pc/agent-updates/{operationId}/status", statusReport);
        Assert.Equal(HttpStatusCode.OK, repeatedReport.StatusCode);

        using var wrongDownloadRelease = CreatePackageRequest("3.0.0", "2.0.0", packageHash, ticket, operationId);
        using var rejectedDownload = await host.Client.SendAsync(wrongDownloadRelease);
        Assert.Equal(HttpStatusCode.Unauthorized, rejectedDownload.StatusCode);
        using var download = await host.Client.SendAsync(CreatePackageRequest("2.0.0", "2.0.0", packageHash, ticket, operationId));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("no-store", download.Headers.CacheControl?.ToString());
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(package, await download.Content.ReadAsByteArrayAsync());

        using var repeatedDownload = await host.Client.SendAsync(CreatePackageRequest("2.0.0", "2.0.0", packageHash, ticket, operationId));
        Assert.Equal(HttpStatusCode.Unauthorized, repeatedDownload.StatusCode);
        using var reopenedDatabase = new ControllerDatabase(fixture.Path, clock: fixture.Time);
        Assert.Throws<UnauthorizedAccessException>(() => reopenedDatabase.ConsumeAgentUpdatePackageDownload(
            "pc", operationId, ticket, "2.0.0", "2.0.0", packageHash));
    }

    [Fact]
    public async Task ExpiredUpdateTicketCannotStartAndRecoveryResetsTheDownloadAllowanceForItsReplacement()
    {
        using var fixture = new LeaseFixture();
        var package = AgentUpdateTestPackage.Create("2.0.0");
        var packageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(package));
        AddRelease(fixture.Path, "2.0.0", packageHash, package.Length);
        var storeRoot = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "agent-releases");
        var packageDirectory = Path.Combine(storeRoot, "sha256", packageHash[..2].ToLowerInvariant());
        Directory.CreateDirectory(packageDirectory);
        await File.WriteAllBytesAsync(Path.Combine(packageDirectory, packageHash.ToLowerInvariant() + ".zip"), package);
        fixture.SyncVersion("1.0.0");
        await using var host = await StartHost(fixture.Db, storeRoot);

        using (var expiredReservation = await Reserve(host.Client, "pc", "2.0.0"))
        {
            var expiredOperationId = await ReadString(expiredReservation, "operationId");
            var expiredTicket = await ReadString(expiredReservation, "ticket");
            fixture.Time.Now = fixture.Time.Now.AddMinutes(10);
            using var expiredStart = await host.Client.PostAsync($"/agent/machines/pc/agent-updates/{expiredOperationId}/start", CreateStartContent(expiredTicket, packageHash));
            Assert.Equal(HttpStatusCode.Unauthorized, expiredStart.StatusCode);
        }

        fixture.SyncVersion("1.0.0");
        using var reservation = await Reserve(host.Client, "pc", "2.0.0");
        var operationId = await ReadString(reservation, "operationId");
        var oldTicket = await ReadString(reservation, "ticket");
        using var start = await host.Client.PostAsync($"/agent/machines/pc/agent-updates/{operationId}/start", CreateStartContent(oldTicket, packageHash));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using var firstDownload = await host.Client.SendAsync(CreatePackageRequest("2.0.0", "2.0.0", packageHash, oldTicket, operationId));
        Assert.Equal(HttpStatusCode.OK, firstDownload.StatusCode);
        Assert.Equal("failed", fixture.Db.ReportAgentUpdateStatus("pc", oldTicket, "failed").Status);

        using var recovery = await host.Client.PostAsync($"/internal/machines/pc/agent-updates/{operationId}/recovery-ticket", content: null);
        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
        var newTicket = await ReadString(recovery, "ticket");
        Assert.NotEqual(oldTicket, newTicket);
        using var oldTicketDownload = await host.Client.SendAsync(CreatePackageRequest("2.0.0", "2.0.0", packageHash, oldTicket, operationId));
        Assert.Equal(HttpStatusCode.Unauthorized, oldTicketDownload.StatusCode);
        using var recoveredStart = await host.Client.PostAsync($"/agent/machines/pc/agent-updates/{operationId}/start", CreateStartContent(newTicket, packageHash));
        Assert.Equal(HttpStatusCode.OK, recoveredStart.StatusCode);
        using var recoveredDownload = await host.Client.SendAsync(CreatePackageRequest("2.0.0", "2.0.0", packageHash, newTicket, operationId));
        Assert.Equal(HttpStatusCode.OK, recoveredDownload.StatusCode);
    }

    [Fact]
    public async Task TargetHeartbeatCompletesOperationAndVerifiedRollbackHeartbeatResolvesRollback()
    {
        using var fixture = new LeaseFixture();
        AddRelease(fixture.Path, "2.0.0");
        AddRelease(fixture.Path, "3.0.0");
        fixture.SyncVersion("1.0.0");
        await using var host = await StartHost(fixture.Db);

        using (var targetReservation = await Reserve(host.Client, "pc", "2.0.0"))
        {
            var operationId = await ReadString(targetReservation, "operationId");
            var ticket = await ReadString(targetReservation, "ticket");
            Assert.Equal("installing", fixture.Db.StartAgentUpdate("pc", ticket).Status);
            Assert.Throws<UnauthorizedAccessException>(() => fixture.Db.StartAgentUpdate("pc", ticket));
            Assert.Equal("awaitingHeartbeat", fixture.Db.ReportAgentUpdateStatus("pc", ticket, "awaitingHeartbeat").Status);
            Assert.False(Assert.Single(fixture.Db.List()).CanRent);
            fixture.SyncVersion("2.0.0");
            Assert.Equal("completed", OperationStatus(fixture.Path, operationId));
            Assert.True(Assert.Single(fixture.Db.List()).CanRent);
        }

        using (var rollbackReservation = await Reserve(host.Client, "pc", "3.0.0"))
        {
            var operationId = await ReadString(rollbackReservation, "operationId");
            var ticket = await ReadString(rollbackReservation, "ticket");
            fixture.Db.StartAgentUpdate("pc", ticket);
            Assert.Equal("rollingBack", fixture.Db.ReportAgentUpdateStatus("pc", ticket, "rollingBack").Status);
            fixture.SyncVersion("0.9.0");
            Assert.Equal("rollingBack", OperationStatus(fixture.Path, operationId));
            Assert.False(Assert.Single(fixture.Db.List()).CanRent);

            fixture.SyncVersion("2.0.0");

            Assert.Equal("rolledBack", OperationStatus(fixture.Path, operationId));
            Assert.True(Assert.Single(fixture.Db.List()).CanRent);
        }
    }

    private static async Task<TestHost> StartHost(ControllerDatabase database, string? storeRoot = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(database);
        var artifactRoot = storeRoot ?? Path.Combine(Path.GetTempPath(), "agent-update-tests", Guid.NewGuid().ToString("N"));
        builder.Services.AddSingleton(new AgentReleaseStore(artifactRoot));
        var app = builder.Build();
        app.MapMachines();
        app.MapAgentReleases();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "admin");
        client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role", "admin");
        return new TestHost(app, client, artifactRoot);
    }

    private static Task<HttpResponseMessage> Reserve(HttpClient client, string machineId, string releaseVersion) =>
        client.PostAsJsonAsync($"/internal/machines/{machineId}/agent-updates", new { releaseVersion });

    private static JsonContent CreateStartContent(string ticket, string packageHash) =>
        JsonContent.Create(new { ticket, releaseVersion = "2.0.0", targetVersion = "2.0.0", targetPackageSha256 = packageHash });

    private static HttpRequestMessage CreatePackageRequest(string releaseVersion, string targetVersion, string packageHash, string ticket, string operationId = "placeholder")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/agent/machines/pc/agent-updates/{operationId}/package");
        request.Headers.Add("X-Agent-Update-Ticket", ticket);
        request.Headers.Add("X-Agent-Update-Release-Version", releaseVersion);
        request.Headers.Add("X-Agent-Update-Target-Version", targetVersion);
        request.Headers.Add("X-Agent-Update-Package-SHA256", packageHash);
        return request;
    }

    private static void AddRelease(string path, string version, string? packageHash = null, long packageSize = 100, bool missing = false)
    {
        var bytes = AgentUpdateTestPackage.Create(version);
        var hash = packageHash ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var package = ControllerReleasePath(path, hash);
        if (packageHash is null && !missing)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(package)!);
            File.WriteAllBytes(package, bytes);
            packageSize = bytes.Length;
        }
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO agent_releases(version,channel,release_notes,package_sha256,package_path,package_size_bytes,source_commit,source_dirty,release_build,package_format,published_at_unix_ms) VALUES($version,'stable','release notes',$hash,$package,$size,'test-commit',0,1,'AgentPackage.zip',1)";
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$package", package);
        command.Parameters.AddWithValue("$size", packageSize);
        command.ExecuteNonQuery();
    }

    private static string ControllerReleasePath(string database, string hash) => Path.Combine(Path.GetDirectoryName(database)!, "agent-releases", "sha256", hash[..2].ToLowerInvariant(), hash.ToLowerInvariant() + ".zip");

    private static string OperationStatus(string path, string operationId)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM agent_update_operations WHERE operation_id=$id";
        command.Parameters.AddWithValue("$id", operationId);
        return (string)command.ExecuteScalar()!;
    }

    private static void SetStartedStatus(string path, string operationId, string status)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_update_operations SET status=$status,started_at_unix_ms=created_at_unix_ms,ticket_started_at_unix_ms=created_at_unix_ms WHERE operation_id=$id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$id", operationId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static async Task<string> ReadString(HttpResponseMessage response, string name)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty(name).GetString()!;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class TestHost(WebApplication app, HttpClient client, string artifactRoot) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
        }
    }
}

internal static class LeaseFixtureAgentVersionExtensions
{
    public static SyncResponse SyncVersion(this LeaseFixture fixture, string version)
    {
        var machine = Assert.Single(fixture.Db.List());
        var request = new SyncRequest("boot", "192.0.2.1", "001122334455", machine.ConfigRevision, "ready", [])
        {
            AgentVersion = version,
        };
        var response = fixture.Db.Sync("pc", request);
        return response.AgentUpdateChallenge is null ? response : fixture.Db.Sync("pc", request with { AgentUpdateChallenge = response.AgentUpdateChallenge });
    }
}
