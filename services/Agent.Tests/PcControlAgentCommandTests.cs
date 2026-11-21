using System.Text.Json;
using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Power;
using KamenRaider.Agent.Sunshine;
using KamenRaider.Contracts;

namespace Agent.Tests;

/// <summary>
/// PC-control commands are the only stream cut on lease end/expiry/shutdown: the enforcer asks
/// IStreamCleanup (Sunshine unpair-all) before it shuts the machine down, and must not let a failed
/// cleanup strand the lease. An EndLease whose shutdown request fails is reported failed so the
/// Controller keeps the machine leased instead of handing it to a new renter.
/// </summary>
public class PcControlAgentCommandTests
{
    sealed class Clock : TimeProvider, IBootClock
    {
        public string BootId { get; set; } = "boot";
        public long UptimeMilliseconds { get; set; } = 1000;
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-29T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(int seconds) { UptimeMilliseconds += seconds * 1000; Now = Now.AddSeconds(seconds); }
    }

    sealed class Cleanup : IStreamCleanup
    {
        public int Calls;
        public bool Fail;
        public List<string>? Events;
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> ClearAsync(CancellationToken ct)
        {
            Calls++;
            Events?.Add("cleanup");
            try
            {
                await Task.Yield();
                if (Fail) throw new IOException("Sunshine cleanup unavailable");
                return true;
            }
            finally
            {
                Finished.TrySetResult();
            }
        }
    }

    sealed class Power : IShutdown
    {
        public int Calls;
        public bool Fail;
        public List<string>? Events;

        public Task RequestImmediateShutdownAsync(CancellationToken ct)
        {
            Calls++;
            Events?.Add("shutdown");
            return Fail ? Task.FromException(new IOException("Immediate shutdown rejected by the OS")) : Task.CompletedTask;
        }
    }

    sealed class Fixture : IDisposable
    {
        public string Dir = Path.Combine(Path.GetTempPath(), "kr-pc-control-agent-" + Guid.NewGuid().ToString("N"));
        public Clock Clock = new();
        public Cleanup Cleanup = new();
        public Power Power = new();
        public TestMaintenanceFence Fence = new();
        public LeaseStore Store;
        public LeaseEnforcer Enforcer;

        public Fixture()
        {
            Store = new(Dir);
            Enforcer = new(Store, Clock, Clock, Cleanup, Power, Fence);
        }

        public LeaseEnforcer NewEnforcer() => new(Store, Clock, Clock, Cleanup, Power, Fence);

        public CommandEnvelope LeaseCommand(long revision = 1, int expirySeconds = 60, string id = "apply")
            => new(id, "ApplyLease", "rental", revision, Clock.BootId, Clock.Now.AddSeconds(30),
                JsonSerializer.SerializeToElement(new
                {
                    startsAtUtc = DateTimeOffset.Parse("2026-10-29T00:00:00Z"),
                    expiresAtUtc = DateTimeOffset.Parse("2026-10-29T00:00:00Z").AddSeconds(expirySeconds)
                }));

        public CommandEnvelope EndCommand(string id = "end")
            => new(id, "EndLease", "rental", 1, Clock.BootId, Clock.Now.AddSeconds(30), JsonSerializer.SerializeToElement(new { }));

        public CommandEnvelope ShutdownCommand(string boot = "boot", string id = "shutdown", int ttl = 30)
            => new(id, "ShutdownMachine", "", 0, boot, Clock.Now.AddSeconds(ttl), JsonSerializer.SerializeToElement(new { }));

        public void Dispose() { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); }
    }

    [Fact]
    public async Task EndLeaseClearsPairedClientsThenRequestsShutdownAndAcknowledges()
    {
        using var f = new Fixture();
        var events = new List<string>();
        f.Cleanup.Events = events;
        f.Power.Events = events;
        Assert.Equal("applied", (await f.Enforcer.ApplyAsync(f.LeaseCommand(), default)).Status);
        await f.Enforcer.ConfirmAsync(new("rental", 1, "boot", f.Clock.Now.AddSeconds(60)), default);
        Assert.True(f.Enforcer.CanStream);

        var result = await f.Enforcer.EndLeaseAsync(f.EndCommand(), default);

        Assert.Equal(new CommandResult("end", "applied", 1), result);
        Assert.Null(f.Enforcer.Current);
        Assert.Null(await f.Store.LoadAsync(default));
        Assert.Equal(new[] { "cleanup", "shutdown" }, events);
        Assert.Equal(1, f.Cleanup.Calls);
        Assert.Equal(1, f.Power.Calls);
        Assert.False(f.Enforcer.CanStream);
    }

    [Fact]
    public async Task EndLeaseShutdownFailureStillClearsLocalLeaseAndReturnsFailed()
    {
        using var f = new Fixture();
        await f.Enforcer.ApplyAsync(f.LeaseCommand(), default);
        await f.Enforcer.ConfirmAsync(new("rental", 1, "boot", f.Clock.Now.AddSeconds(60)), default);
        f.Power.Fail = true;

        var result = await f.Enforcer.EndLeaseAsync(f.EndCommand(), default);

        // The shutdown never landed, so the Controller must see a failure (stable code, no exception
        // text) and keep the machine leased; the local lease is still cleared to fail closed.
        Assert.Equal("failed", result.Status);
        Assert.Equal("ShutdownFailed", result.ErrorCode);
        Assert.DoesNotContain("rejected", result.ErrorCode);
        Assert.Null(f.Enforcer.Current);
        Assert.Null(await f.Store.LoadAsync(default));
        Assert.Equal(1, f.Cleanup.Calls);
        Assert.Equal(1, f.Power.Calls);
        Assert.False(f.Enforcer.CanStream);
    }

    [Fact]
    public async Task EndLeaseStillAcknowledgesWhenCleanupFails()
    {
        using var f = new Fixture();
        await f.Enforcer.ApplyAsync(f.LeaseCommand(), default);
        await f.Enforcer.ConfirmAsync(new("rental", 1, "boot", f.Clock.Now.AddSeconds(60)), default);
        f.Cleanup.Fail = true;

        var result = await f.Enforcer.EndLeaseAsync(f.EndCommand(), default);

        // The rental has ended: a failed best-effort stream cut must not strand the lease, and the
        // shutdown request is still made.
        Assert.Equal(new CommandResult("end", "applied", 1), result);
        Assert.Null(f.Enforcer.Current);
        Assert.Null(await f.Store.LoadAsync(default));
        Assert.Equal(1, f.Cleanup.Calls);
        Assert.Equal(1, f.Power.Calls);
    }

    [Fact]
    public async Task EndLeaseRejectsWrongBootRevisionOrHeldFenceWithoutClearingState()
    {
        using var f = new Fixture();
        await f.Enforcer.ApplyAsync(f.LeaseCommand(), default);
        Assert.Equal("failed", (await f.Enforcer.EndLeaseAsync(f.EndCommand() with { BootId = "other" }, default)).Status);
        Assert.Equal("failed", (await f.Enforcer.EndLeaseAsync(f.EndCommand() with { LeaseRevision = 2 }, default)).Status);
        f.Fence.SetHeld();
        Assert.Equal("failed", (await f.Enforcer.EndLeaseAsync(f.EndCommand(), default)).Status);
        Assert.NotNull(f.Enforcer.Current);
        Assert.NotNull(await f.Store.LoadAsync(default));
        Assert.Equal(0, f.Cleanup.Calls);
    }

    [Fact]
    public async Task ShutdownMachineRequiresMatchingBootAndMaintenanceFence()
    {
        using var f = new Fixture();
        Assert.Equal("failed", (await f.Enforcer.ShutdownMachineAsync(f.ShutdownCommand(boot: "other"), default)).Status);
        Assert.Equal(0, f.Power.Calls);

        Assert.Equal("failed", (await f.Enforcer.ShutdownMachineAsync(f.ShutdownCommand(ttl: 0), default)).Status);
        Assert.Equal(0, f.Power.Calls);

        f.Fence.SetHeld();
        Assert.Equal("failed", (await f.Enforcer.ShutdownMachineAsync(f.ShutdownCommand(), default)).Status);
        Assert.Equal(0, f.Power.Calls);
        Assert.Equal(0, f.Cleanup.Calls);

        f.Fence = new TestMaintenanceFence();
        var enforcer = f.NewEnforcer();
        Assert.Equal(new CommandResult("shutdown", "applied", 0), await enforcer.ShutdownMachineAsync(f.ShutdownCommand(), default));
        Assert.Equal(1, f.Power.Calls);
        Assert.Equal(1, f.Cleanup.Calls);
    }

    [Fact]
    public async Task ShutdownClearsPairedClientsBeforeRequestingShutdown()
    {
        using var f = new Fixture();
        var events = new List<string>();
        f.Cleanup.Events = events;
        f.Power.Events = events;

        Assert.Equal(new CommandResult("shutdown", "applied", 0), await f.Enforcer.ShutdownMachineAsync(f.ShutdownCommand(), default));

        Assert.Equal(new[] { "cleanup", "shutdown" }, events);
    }

    [Fact]
    public async Task ShutdownProceedsWhenCleanupFails()
    {
        using var f = new Fixture();
        f.Cleanup.Fail = true;

        Assert.Equal(new CommandResult("shutdown", "applied", 0), await f.Enforcer.ShutdownMachineAsync(f.ShutdownCommand(), default));
        Assert.Equal(1, f.Power.Calls);
    }

    [Fact]
    public async Task ExpiryClearsPairedClientsAndShutsDown()
    {
        using var f = new Fixture();
        await f.Enforcer.ApplyAsync(f.LeaseCommand(), default);
        f.Clock.Advance(60);

        await f.Enforcer.EnforceAsync(default);

        Assert.Equal(1, f.Cleanup.Calls);
        Assert.Equal(1, f.Power.Calls);
        Assert.False(f.Enforcer.CanStream);
    }

    [Fact]
    public async Task ExpiryClearsPairedClientsBeforeRequestingShutdown()
    {
        using var f = new Fixture();
        var events = new List<string>();
        f.Cleanup.Events = events;
        f.Power.Events = events;
        await f.Enforcer.ApplyAsync(f.LeaseCommand(), default);
        f.Clock.Advance(60);

        await f.Enforcer.EnforceAsync(default);
        await f.Cleanup.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Expiry starts the unpair-all before it asks the machine to power off (it does not wait for
        // cleanup to finish, but the cleanup is always initiated first).
        Assert.Equal(new[] { "cleanup", "shutdown" }, events);
    }

    [Fact]
    public async Task ExpiryStillShutsDownWhenCleanupFails()
    {
        using var f = new Fixture();
        await f.Enforcer.ApplyAsync(f.LeaseCommand(), default);
        f.Cleanup.Fail = true;
        f.Clock.Advance(60);

        await f.Enforcer.EnforceAsync(default);
        await f.Cleanup.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, f.Power.Calls);
        Assert.Equal("ShutdownRequested", f.Enforcer.Fault);
    }

    [Fact]
    public async Task DispatcherProcessesEndLeaseAndShutdownCommands()
    {
        using var f = new Fixture();
        await f.Enforcer.ApplyAsync(f.LeaseCommand(), default);
        var dispatcher = new KamenRaider.Agent.Sync.LeaseCommandDispatcher(f.Enforcer);
        await dispatcher.AcceptAsync(new SyncResponse(f.Clock.Now, new(1, 5000, 6000), [f.EndCommand(), f.ShutdownCommand()]), default);

        var results = dispatcher.PendingResults;
        Assert.Equal(2, results.Length);
        Assert.Contains(results, result => result.CommandId == "end" && result.Status == "applied");
        Assert.Contains(results, result => result.CommandId == "shutdown" && result.Status == "applied");
        Assert.Null(f.Enforcer.Current);
        Assert.Equal(2, f.Cleanup.Calls);
        // EndLease now requests the immediate shutdown itself; ShutdownMachine asks again.
        Assert.Equal(2, f.Power.Calls);
    }

    [Fact]
    public async Task ShrunkExpiryIsAcceptedButPastExpiryIsRejected()
    {
        using var f = new Fixture();
        Assert.Equal("applied", (await f.Enforcer.ApplyAsync(f.LeaseCommand(expirySeconds: 120), default)).Status);
        var shortened = f.LeaseCommand(revision: 2, expirySeconds: 30, id: "shrink") with
        {
            ValidUntilUtc = f.Clock.Now.AddSeconds(30)
        };
        var shortenedResult = await f.Enforcer.ApplyAsync(shortened, default); Assert.True(shortenedResult.Status == "applied", shortenedResult.ErrorCode);
        Assert.Equal(2, f.Enforcer.Current!.Revision);
        Assert.Equal(f.Clock.Now.AddSeconds(30), f.Enforcer.Current!.ExpiresAtUtc);

        var expired = f.LeaseCommand(revision: 3, expirySeconds: 0, id: "expired") with
        {
            ValidUntilUtc = f.Clock.Now.AddSeconds(30)
        };
        Assert.Equal("failed", (await f.Enforcer.ApplyAsync(expired, default)).Status);
        Assert.Equal(2, f.Enforcer.Current!.Revision);
        Assert.Equal(0, f.Cleanup.Calls);
    }
}
