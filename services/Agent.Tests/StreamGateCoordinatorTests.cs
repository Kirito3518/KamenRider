using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Sunshine;

namespace Agent.Tests;

public class StreamGateCoordinatorTests
{
    private sealed class Clock : TimeProvider, IBootClock
    {
        public string BootId { get; set; } = "boot";
        public long UptimeMilliseconds { get; set; } = 1000;
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-06T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;

        public void AdvanceTo(long uptimeMilliseconds)
        {
            var elapsed = uptimeMilliseconds - UptimeMilliseconds;
            UptimeMilliseconds = uptimeMilliseconds;
            Now = Now.AddMilliseconds(elapsed);
        }
    }

    private sealed class Ingress(List<string> events) : IStreamIngressPolicy
    {
        public bool Blocked = true;
        public Exception? FailNextAllow;
        public Action? AfterVerify;

        public Task SetBlockedAsync(bool blocked, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add(blocked ? "firewall:block" : "firewall:allow");
            if (!blocked && FailNextAllow is { } error)
            {
                FailNextAllow = null;
                return Task.FromException(error);
            }
            Blocked = blocked;
            return Task.CompletedTask;
        }

        public Task<bool> IsBlockedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add(Blocked ? "firewall:verify-blocked" : "firewall:verify-allowed");
            AfterVerify?.Invoke();
            return Task.FromResult(Blocked);
        }
    }

    private sealed class Sunshine(List<string> events) : ISunshineServiceControl
    {
        public bool Running;
        public int StartCalls;
        public int StopCalls;
        public Action? AfterStart;

        public Task StartAndWaitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("sunshine:start");
            StartCalls++;
            Running = true;
            AfterStart?.Invoke();
            return Task.CompletedTask;
        }

        public Task StopAndWaitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("sunshine:stop");
            StopCalls++;
            Running = false;
            return Task.CompletedTask;
        }

        public Task<bool> IsRunningAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add(Running ? "sunshine:verify-running" : "sunshine:verify-stopped");
            return Task.FromResult(Running);
        }
    }

    private sealed class Fixture
    {
        public readonly List<string> Events = [];
        public readonly Clock Clock = new();
        public readonly Ingress Ingress;
        public readonly Sunshine Sunshine;
        public readonly StreamGateCoordinator Gate;

        public Fixture()
        {
            Ingress = new(Events);
            Sunshine = new(Events);
            Gate = new(Ingress, Sunshine, Clock, Clock);
        }

        public StreamLeaseGrant Grant(long revision = 1, long deadline = 61000, int expirySeconds = 60) =>
            new("rental", revision, "boot", Clock.Now.AddSeconds(expirySeconds), deadline);
    }

    [Fact]
    public async Task BlockActivatesFirewallBlockBeforeStoppingAndVerifyingSunshine()
    {
        var f = new Fixture();

        await f.Gate.BlockAsync(default);

        Assert.True(f.Events.IndexOf("firewall:block") < f.Events.IndexOf("sunshine:stop"));
        Assert.True(f.Events.IndexOf("sunshine:stop") < f.Events.IndexOf("firewall:verify-blocked"));
        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.Null(f.Gate.CurrentGrant);
    }

    [Fact]
    public async Task AllowStartsSunshineWhileBlockedAndRemovesBlockOnlyAfterStart()
    {
        var f = new Fixture();

        await f.Gate.AllowAsync(f.Grant(), default);

        Assert.True(f.Events.IndexOf("firewall:block") < f.Events.IndexOf("sunshine:start"));
        Assert.True(f.Events.IndexOf("sunshine:start") < f.Events.IndexOf("firewall:allow"));
        Assert.True(f.Sunshine.Running);
        Assert.False(f.Ingress.Blocked);
        Assert.Equal(f.Grant(), f.Gate.CurrentGrant);
    }

    [Fact]
    public async Task RepeatedSameGrantDoesNotRestartAnActiveSunshineService()
    {
        var f = new Fixture();
        var grant = f.Grant();
        await f.Gate.AllowAsync(grant, default);
        var stopCallsBeforeRepeat = f.Sunshine.StopCalls;
        f.Events.Clear();

        await f.Gate.AllowAsync(grant, default);

        Assert.DoesNotContain("firewall:block", f.Events);
        Assert.DoesNotContain("firewall:allow", f.Events);
        Assert.DoesNotContain("sunshine:start", f.Events);
        Assert.DoesNotContain("sunshine:stop", f.Events);
        Assert.Equal(1, f.Sunshine.StartCalls);
        Assert.Equal(stopCallsBeforeRepeat, f.Sunshine.StopCalls);
        Assert.Equal(grant, f.Gate.CurrentGrant);
    }

    [Fact]
    public async Task RepeatedGrantClosesIfItExpiresDuringOpenStateVerification()
    {
        var f = new Fixture();
        var grant = f.Grant();
        await f.Gate.AllowAsync(grant, default);
        f.Ingress.AfterVerify = () => f.Clock.AdvanceTo(grant.DeadlineUptimeMilliseconds);

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Gate.AllowAsync(grant, default));

        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.Null(f.Gate.CurrentGrant);
    }

    [Fact]
    public async Task ConfirmedExtensionRefreshesDeadlineWithoutRestartingSunshine()
    {
        var f = new Fixture();
        await f.Gate.AllowAsync(f.Grant(), default);
        var extension = f.Grant(revision: 2, deadline: 121000, expirySeconds: 120);
        f.Events.Clear();

        await f.Gate.AllowAsync(extension, default);

        Assert.DoesNotContain("firewall:block", f.Events);
        Assert.DoesNotContain("firewall:allow", f.Events);
        Assert.DoesNotContain("sunshine:start", f.Events);
        Assert.DoesNotContain("sunshine:stop", f.Events);
        Assert.Equal(1, f.Sunshine.StartCalls);
        Assert.Equal(extension, f.Gate.CurrentGrant);
    }

    [Fact]
    public async Task SameRevisionMayShortenButNeverExtendItsMonotonicDeadline()
    {
        var f = new Fixture();
        var original = f.Grant();
        await f.Gate.AllowAsync(original, default);
        var shortened = original with { DeadlineUptimeMilliseconds = 51000 };
        await f.Gate.AllowAsync(shortened, default);
        var extendedWithoutRevision = original with { DeadlineUptimeMilliseconds = 71000 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Gate.AllowAsync(extendedWithoutRevision, default));

        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.Null(f.Gate.CurrentGrant);
    }

    [Fact]
    public async Task ReplayedOlderRevisionClosesAnOpenGate()
    {
        var f = new Fixture();
        var original = f.Grant();
        await f.Gate.AllowAsync(original, default);
        await f.Gate.AllowAsync(f.Grant(revision: 2, deadline: 121000, expirySeconds: 120), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Gate.AllowAsync(original, default));

        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.Null(f.Gate.CurrentGrant);
    }

    [Fact]
    public async Task CrossBootOrExpiredGrantNeverRemovesFirewallBlock()
    {
        var f = new Fixture();
        f.Clock.BootId = "new-boot";
        var wrongBoot = f.Grant() with { BootId = "boot" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Gate.AllowAsync(wrongBoot, default));

        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.DoesNotContain("firewall:allow", f.Events);
    }

    [Fact]
    public async Task ExpiredUtcGrantNeverOpensWhileItsMonotonicDeadlineIsStillFuture()
    {
        var f = new Fixture();
        var expiredUtc = f.Grant(deadline: 61000, expirySeconds: -1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Gate.AllowAsync(expiredUtc, default));

        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.DoesNotContain("firewall:allow", f.Events);
    }

    [Fact]
    public async Task ExpiryIsEnforcedFromMonotonicUptime()
    {
        var f = new Fixture();
        await f.Gate.AllowAsync(f.Grant(), default);
        f.Clock.AdvanceTo(61000);

        await f.Gate.EnforceAsync(default);

        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.Null(f.Gate.CurrentGrant);
    }

    [Fact]
    public async Task ExpiryIsAlsoEnforcedFromUtc()
    {
        var f = new Fixture();
        var grant = f.Grant();
        await f.Gate.AllowAsync(grant, default);
        f.Clock.Now = grant.ExpiresAtUtc.AddTicks(1);

        await f.Gate.EnforceAsync(default);

        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.Null(f.Gate.CurrentGrant);
    }

    [Fact]
    public async Task DeadlineCrossingDuringServiceStartNeverOpensIngress()
    {
        var f = new Fixture();
        f.Sunshine.AfterStart = () => f.Clock.AdvanceTo(61000);

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Gate.AllowAsync(f.Grant(), default));

        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.DoesNotContain("firewall:allow", f.Events);
        Assert.Null(f.Gate.CurrentGrant);
    }

    [Fact]
    public async Task FailedFirewallOpenLeavesRuleBlockedAndSunshineStopped()
    {
        var f = new Fixture();
        f.Ingress.FailNextAllow = new IOException("firewall update failed");

        await Assert.ThrowsAsync<IOException>(() => f.Gate.AllowAsync(f.Grant(), default));

        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.Null(f.Gate.CurrentGrant);
        Assert.True(f.Events.LastIndexOf("firewall:block") < f.Events.LastIndexOf("sunshine:stop"));
    }

    [Fact]
    public async Task RepeatedBlockKeepsTheVerifiedClosedStateWithoutRepeatingMutations()
    {
        var f = new Fixture();
        await f.Gate.BlockAsync(default);
        f.Events.Clear();

        await f.Gate.BlockAsync(default);

        Assert.DoesNotContain("firewall:block", f.Events);
        Assert.DoesNotContain("sunshine:start", f.Events);
        Assert.DoesNotContain("sunshine:stop", f.Events);
        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
    }

    [Fact]
    public async Task RepeatedBlockRepairsUnexpectedStateChangesAfterLastVerification()
    {
        var f = new Fixture();
        await f.Gate.BlockAsync(default);
        f.Ingress.Blocked = false;
        f.Sunshine.Running = true;
        f.Events.Clear();

        await f.Gate.BlockAsync(default);

        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.True(f.Events.IndexOf("firewall:block") < f.Events.IndexOf("sunshine:stop"));
    }
}
