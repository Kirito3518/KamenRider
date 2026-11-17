using System.Text.Json;
using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Power;
using KamenRaider.Contracts;

namespace Agent.Tests;

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

    sealed class Gate : IStreamGate
    {
        public bool Blocked = true;
        public int Blocks;
        public Task BlockAsync(CancellationToken ct) { Blocks++; Blocked = true; return Task.CompletedTask; }
        public Task AllowAsync(StreamLeaseGrant grant, CancellationToken ct) { Blocked = false; return Task.CompletedTask; }
    }

    sealed class Power : IShutdown
    {
        public int Calls;
        public Task RequestImmediateShutdownAsync(CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    sealed class Fixture : IDisposable
    {
        public string Dir = Path.Combine(Path.GetTempPath(), "kr-pc-control-agent-" + Guid.NewGuid().ToString("N"));
        public Clock Clock = new();
        public Gate Gate = new();
        public Power Power = new();
        public TestMaintenanceFence Fence = new();
        public LeaseStore Store;
        public LeaseEnforcer Enforcer;
        public Fixture() { Store = new(Dir); Enforcer = new(Store, Clock, Clock, Gate, Power, Fence); }
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
    public async Task EndLeaseClosesGateClearsLeaseStoreAndAcknowledges()
    {
        using var f = new Fixture();
        Assert.Equal("applied", (await f.Enforcer.ApplyAsync(f.LeaseCommand(), default)).Status);
        await f.Enforcer.ConfirmAsync(new("rental", 1, "boot", f.Clock.Now.AddSeconds(60)), default);
        Assert.False(f.Gate.Blocked);

        var result = await f.Enforcer.EndLeaseAsync(f.EndCommand(), default);

        Assert.Equal(new CommandResult("end", "applied", 1), result);
        Assert.Null(f.Enforcer.Current);
        Assert.Null(await f.Store.LoadAsync(default));
        Assert.True(f.Gate.Blocked);
        Assert.False(f.Enforcer.CanStream);
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

        f.Fence = new TestMaintenanceFence();
        var enforcer = new LeaseEnforcer(f.Store, f.Clock, f.Clock, f.Gate, f.Power, f.Fence);
        Assert.Equal(new CommandResult("shutdown", "applied", 0), await enforcer.ShutdownMachineAsync(f.ShutdownCommand(), default));
        Assert.Equal(1, f.Power.Calls);
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
        Assert.True(f.Gate.Blocked);
        Assert.Equal(1, f.Power.Calls);
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
    }
}
