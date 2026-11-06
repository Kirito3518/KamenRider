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
        public Exception? FailNextBlock;
        public Action? AfterVerify;
        public ManualResetEventSlim? AllowEntered;
        public ManualResetEventSlim? ContinueAllow;
        public bool ApplyAllowBeforeDelay;

        public Task SetBlockedAsync(bool blocked, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add(blocked ? "firewall:block" : "firewall:allow");
            if (blocked && FailNextBlock is { } blockError)
            {
                FailNextBlock = null;
                return Task.FromException(blockError);
            }
            if (!blocked && FailNextAllow is { } error)
            {
                FailNextAllow = null;
                return Task.FromException(error);
            }
            if (!blocked && AllowEntered is { } entered)
            {
                if (ApplyAllowBeforeDelay)
                    Blocked = false;
                entered.Set();
                if (ContinueAllow is null || !ContinueAllow.Wait(TimeSpan.FromSeconds(10)))
                    return Task.FromException(new TimeoutException("The test did not release the delayed firewall allow."));
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

    private sealed class GrantHistoryStore : IStreamGrantHistoryStore
    {
        private StreamGrantHistorySnapshot snapshot = new([], null);
        public Exception? SaveFailure;
        public ManualResetEventSlim? SaveEntered;
        public ManualResetEventSlim? ContinueSave;

        public StreamGrantHistorySnapshot Load() => new(snapshot.Grants.ToArray(), snapshot.CurrentLease);

        public void Save(StreamGrantHistorySnapshot history)
        {
            if (SaveFailure is { } error) throw error;
            if (SaveEntered is { } entered)
            {
                entered.Set();
                if (ContinueSave is null || !ContinueSave.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The test did not release the blocked history write.");
            }
            snapshot = new(history.Grants.ToArray(), history.CurrentLease);
        }
    }

    private sealed class Fixture
    {
        public readonly List<string> Events = [];
        public readonly Clock Clock = new();
        public readonly Ingress Ingress;
        public readonly Sunshine Sunshine;
        public readonly GrantHistoryStore History = new();
        public readonly StreamGateCoordinator Gate;

        public Fixture()
        {
            Ingress = new(Events);
            Sunshine = new(Events);
            Gate = new(Ingress, Sunshine, Clock, Clock, History);
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
    public async Task BlockRejectsAnAllowRequestThatStartsWhileClosureIsInProgress()
    {
        var f = new Fixture();
        using var verificationEntered = new ManualResetEventSlim();
        using var continueVerification = new ManualResetEventSlim();
        f.Ingress.AfterVerify = () =>
        {
            f.Ingress.AfterVerify = null;
            verificationEntered.Set();
            if (!continueVerification.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The test did not release the block verification.");
        };

        var block = Task.Run(() => f.Gate.BlockAsync(default));
        Assert.True(verificationEntered.Wait(TimeSpan.FromSeconds(5)));
        var staleAllow = f.Gate.AllowAsync(f.Grant(), default);
        continueVerification.Set();
        await block;

        await Assert.ThrowsAsync<InvalidOperationException>(() => staleAllow);
        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.Null(f.Gate.CurrentGrant);
        Assert.DoesNotContain("firewall:allow", f.Events);
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
    public async Task ReplayedOlderRevisionCannotReopenAfterBlockClearsCurrentGrant()
    {
        var f = new Fixture();
        var original = f.Grant();
        await f.Gate.AllowAsync(original, default);
        await f.Gate.AllowAsync(f.Grant(revision: 2, deadline: 121000, expirySeconds: 120), default);
        await f.Gate.BlockAsync(default);
        f.Events.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Gate.AllowAsync(original, default));

        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.Null(f.Gate.CurrentGrant);
        Assert.DoesNotContain("firewall:allow", f.Events);
    }

    [Fact]
    public async Task ReplayedOlderRevisionCannotReopenAfterGuardProcessRestart()
    {
        var clock = new Clock();
        var history = new GrantHistoryStore();
        var firstEvents = new List<string>();
        var firstGate = new StreamGateCoordinator(new Ingress(firstEvents), new Sunshine(firstEvents), clock, clock, history);
        var original = new StreamLeaseGrant("rental", 1, "boot", clock.Now.AddSeconds(60), 61000);
        var latest = new StreamLeaseGrant("rental", 2, "boot", clock.Now.AddSeconds(120), 121000);
        await firstGate.AllowAsync(original, default);
        await firstGate.AllowAsync(latest, default);
        await firstGate.BlockAsync(default);

        var restartedEvents = new List<string>();
        var restartedIngress = new Ingress(restartedEvents);
        var restartedSunshine = new Sunshine(restartedEvents);
        var restartedGate = new StreamGateCoordinator(restartedIngress, restartedSunshine, clock, clock, history);
        await restartedGate.BlockAsync(default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => restartedGate.AllowAsync(original, default));

        Assert.True(restartedIngress.Blocked);
        Assert.False(restartedSunshine.Running);
        Assert.Null(restartedGate.CurrentGrant);
        Assert.DoesNotContain("firewall:allow", restartedEvents);
    }

    [Fact]
    public async Task EarlierRentalCannotReopenAfterNewRentalEvenAfterGuardProcessRestart()
    {
        var clock = new Clock();
        var history = new GrantHistoryStore();
        var firstEvents = new List<string>();
        var firstGate = new StreamGateCoordinator(new Ingress(firstEvents), new Sunshine(firstEvents), clock, clock, history);
        var earlierRental = new StreamLeaseGrant("rental-a", 1, "boot", clock.Now.AddSeconds(60), 61000);
        var laterRental = new StreamLeaseGrant("rental-b", 1, "boot", clock.Now.AddSeconds(120), 121000);
        await firstGate.AllowAsync(earlierRental, default);
        await firstGate.BlockAsync(default);
        await firstGate.AllowAsync(laterRental, default);
        await firstGate.BlockAsync(default);

        var restartedEvents = new List<string>();
        var restartedIngress = new Ingress(restartedEvents);
        var restartedSunshine = new Sunshine(restartedEvents);
        var restartedGate = new StreamGateCoordinator(restartedIngress, restartedSunshine, clock, clock, history);
        await restartedGate.BlockAsync(default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => restartedGate.AllowAsync(earlierRental, default));

        Assert.True(restartedIngress.Blocked);
        Assert.False(restartedSunshine.Running);
        Assert.Null(restartedGate.CurrentGrant);
        Assert.DoesNotContain("firewall:allow", restartedEvents);
    }

    [Fact]
    public async Task GrantHistoryWriteFailureKeepsSunshineClosed()
    {
        var f = new Fixture();
        f.History.SaveFailure = new IOException("history is unavailable");

        await Assert.ThrowsAsync<IOException>(() => f.Gate.AllowAsync(f.Grant(), default));

        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.Null(f.Gate.CurrentGrant);
        Assert.DoesNotContain("firewall:allow", f.Events);
    }

    [Fact]
    public async Task NewRentalCanOpenAfterPreviousRentalIsBlocked()
    {
        var f = new Fixture();
        await f.Gate.AllowAsync(f.Grant(), default);
        await f.Gate.BlockAsync(default);
        var nextRental = f.Grant() with { RentalId = "next-rental" };

        await f.Gate.AllowAsync(nextRental, default);

        Assert.False(f.Ingress.Blocked);
        Assert.True(f.Sunshine.Running);
        Assert.Equal(nextRental, f.Gate.CurrentGrant);
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
    public async Task ExpiryClosesOpenGateWhileActiveGrantHistoryWriteIsBlocked()
    {
        var f = new Fixture();
        var original = f.Grant();
        await f.Gate.AllowAsync(original, default);
        f.Events.Clear();
        f.History.SaveEntered = new ManualResetEventSlim();
        f.History.ContinueSave = new ManualResetEventSlim();
        var extension = f.Grant(revision: 2, deadline: 121000, expirySeconds: 120);

        var renewal = Task.Run(() => f.Gate.AllowAsync(extension, default));
        Assert.True(f.History.SaveEntered.Wait(TimeSpan.FromSeconds(5)));
        f.Clock.AdvanceTo(original.DeadlineUptimeMilliseconds);

        var enforcement = Task.Run(() => f.Gate.EnforceAsync(default));
        var closedBeforeWriteReleased = SpinWait.SpinUntil(
            () => f.Ingress.Blocked && !f.Sunshine.Running,
            TimeSpan.FromSeconds(5));
        var enforcementWaitsForRenewal = !enforcement.IsCompleted;
        f.History.ContinueSave.Set();
        await enforcement;
        await Assert.ThrowsAsync<InvalidOperationException>(() => renewal);

        Assert.True(closedBeforeWriteReleased, "Expiry enforcement must close the open stream before history persistence finishes.");
        Assert.True(enforcementWaitsForRenewal, "Expiry enforcement must wait for the interrupted renewal to unwind.");
        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.Null(f.Gate.CurrentGrant);
        Assert.DoesNotContain("firewall:allow", f.Events);
    }

    [Fact]
    public async Task BlockClosesImmediatelyAndWaitsForActiveGrantHistoryWriteToUnwind()
    {
        var f = new Fixture();
        await f.Gate.AllowAsync(f.Grant(), default);
        f.Events.Clear();
        f.History.SaveEntered = new ManualResetEventSlim();
        f.History.ContinueSave = new ManualResetEventSlim();
        var extension = f.Grant(revision: 2, deadline: 121000, expirySeconds: 120);

        var renewal = Task.Run(() => f.Gate.AllowAsync(extension, default));
        Assert.True(f.History.SaveEntered.Wait(TimeSpan.FromSeconds(5)));

        var block = Task.Run(() => f.Gate.BlockAsync(default));
        var closedBeforeWriteReleased = SpinWait.SpinUntil(
            () => f.Ingress.Blocked && !f.Sunshine.Running,
            TimeSpan.FromSeconds(5));
        var blockWaitsForInterruptedAllow = !block.IsCompleted;
        f.History.ContinueSave.Set();
        await block;
        var renewalError = await Record.ExceptionAsync(() => renewal);

        Assert.True(closedBeforeWriteReleased, "BlockAsync must close ingress before the history write finishes.");
        Assert.True(blockWaitsForInterruptedAllow, "BlockAsync must not report closure before the interrupted allow operation unwinds.");
        Assert.IsType<InvalidOperationException>(renewalError);
        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.Null(f.Gate.CurrentGrant);
        Assert.DoesNotContain("firewall:allow", f.Events);
    }

    [Fact]
    public async Task BlockDoesNotReturnBeforeDelayedFirewallAllowUnwinds()
    {
        var f = new Fixture();
        f.Ingress.AllowEntered = new ManualResetEventSlim();
        f.Ingress.ContinueAllow = new ManualResetEventSlim();
        var allow = Task.Run(() => f.Gate.AllowAsync(f.Grant(), default));
        Assert.True(f.Ingress.AllowEntered.Wait(TimeSpan.FromSeconds(5)));
        var stopCallsBeforeBlock = f.Sunshine.StopCalls;

        var block = Task.Run(() => f.Gate.BlockAsync(default));
        var closeStartedBeforeAllowReleased = SpinWait.SpinUntil(
            () => f.Sunshine.StopCalls > stopCallsBeforeBlock,
            TimeSpan.FromSeconds(5));
        var blockWaitsForAllow = !block.IsCompleted;
        f.Ingress.ContinueAllow.Set();
        await block;
        var allowError = await Record.ExceptionAsync(() => allow);

        Assert.True(closeStartedBeforeAllowReleased, "BlockAsync must request the firewall block and Sunshine stop while Allow is in flight.");
        Assert.True(blockWaitsForAllow, "BlockAsync must wait for the delayed firewall allow and its cleanup to finish.");
        Assert.NotNull(allowError);
        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.Null(f.Gate.CurrentGrant);
    }

    [Fact]
    public async Task BlockDrainsAndRetriesWhenImmediateCloseFailsDuringDelayedFirewallAllow()
    {
        var f = new Fixture();
        f.Ingress.AllowEntered = new ManualResetEventSlim();
        f.Ingress.ContinueAllow = new ManualResetEventSlim();
        f.Ingress.ApplyAllowBeforeDelay = true;
        var allow = Task.Run(() => f.Gate.AllowAsync(f.Grant(), default));
        Assert.True(f.Ingress.AllowEntered.Wait(TimeSpan.FromSeconds(5)));
        f.Events.Clear();
        f.Ingress.FailNextBlock = new IOException("The first close attempt failed.");

        var block = Task.Run(() => f.Gate.BlockAsync(default));
        var failedCloseObserved = SpinWait.SpinUntil(
            () => f.Events.Contains("firewall:verify-allowed") && f.Events.Contains("sunshine:stop"),
            TimeSpan.FromSeconds(5));
        var blockWaitsForAllow = !block.IsCompleted;
        f.Ingress.ContinueAllow.Set();
        var allowError = await Record.ExceptionAsync(() => allow);
        var blockError = await Record.ExceptionAsync(() => block);

        Assert.True(failedCloseObserved, "The injected close failure must happen before releasing the in-flight allow.");
        Assert.Null(f.Ingress.FailNextBlock);
        Assert.True(blockWaitsForAllow, "BlockAsync must drain the interrupted allow and retry closure after an immediate close failure.");
        Assert.NotNull(allowError);
        Assert.Null(blockError);
        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.Null(f.Gate.CurrentGrant);
    }

    [Fact]
    public async Task ExpiryDoesNotReturnBeforeDelayedFirewallAllowUnwinds()
    {
        var f = new Fixture();
        var grant = f.Grant();
        f.Ingress.AllowEntered = new ManualResetEventSlim();
        f.Ingress.ContinueAllow = new ManualResetEventSlim();
        var allow = Task.Run(() => f.Gate.AllowAsync(grant, default));
        Assert.True(f.Ingress.AllowEntered.Wait(TimeSpan.FromSeconds(5)));
        f.Clock.AdvanceTo(grant.DeadlineUptimeMilliseconds);
        var stopCallsBeforeExpiry = f.Sunshine.StopCalls;

        var enforcement = Task.Run(() => f.Gate.EnforceAsync(default));
        var closeStartedBeforeAllowReleased = SpinWait.SpinUntil(
            () => f.Sunshine.StopCalls > stopCallsBeforeExpiry,
            TimeSpan.FromSeconds(5));
        var enforcementWaitsForAllow = !enforcement.IsCompleted;
        f.Ingress.ContinueAllow.Set();
        await enforcement;
        var allowError = await Record.ExceptionAsync(() => allow);

        Assert.True(closeStartedBeforeAllowReleased, "Expiry enforcement must close ingress and stop Sunshine while Allow is in flight.");
        Assert.True(enforcementWaitsForAllow, "Expiry enforcement must wait for the delayed firewall allow and cleanup to finish.");
        Assert.NotNull(allowError);
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

    [Fact]
    public async Task FencedMaintenanceStartsSunshineOnlyBehindBlockAndStopsItBeforeReturning()
    {
        var f = new Fixture();
        await f.Gate.AllowAsync(f.Grant(), default);
        f.Events.Clear();

        await f.Gate.BeginFencedMaintenanceAsync(default);
        Assert.True(f.Ingress.Blocked);
        Assert.True(f.Sunshine.Running);
        Assert.Null(f.Gate.CurrentGrant);
        Assert.True(f.Events.IndexOf("firewall:block") < f.Events.IndexOf("sunshine:stop"));
        Assert.True(f.Events.IndexOf("sunshine:stop") < f.Events.IndexOf("sunshine:start"));
        Assert.True(f.Events.IndexOf("sunshine:start") < f.Events.LastIndexOf("firewall:verify-blocked"));

        await f.Gate.EndFencedMaintenanceAsync(default);

        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.Null(f.Gate.CurrentGrant);
        Assert.True(f.Events.LastIndexOf("firewall:block") < f.Events.LastIndexOf("sunshine:stop"));
    }

    [Fact]
    public async Task MaintenanceDeadlineClosesSunshineAndLeavesIngressBlocked()
    {
        var f = new Fixture();
        await f.Gate.BeginFencedMaintenanceAsync(default);
        f.Clock.AdvanceTo(46000);

        await f.Gate.EnforceAsync(default);

        Assert.True(f.Ingress.Blocked);
        Assert.False(f.Sunshine.Running);
        Assert.Null(f.Gate.CurrentGrant);
    }

    [Fact]
    public async Task PairingVerificationRequiresTheExactCurrentOpenLease()
    {
        var f = new Fixture();
        var grant = f.Grant();
        await f.Gate.AllowAsync(grant, default);
        var scope = new PairingScope(grant.RentalId, grant.Revision, grant.BootId, grant.ExpiresAtUtc, grant.DeadlineUptimeMilliseconds);

        Assert.True(await f.Gate.VerifyOpenForLeaseAsync(scope, default));
        Assert.False(await f.Gate.VerifyOpenForLeaseAsync(scope with { Revision = grant.Revision + 1 }, default));
        Assert.False(await f.Gate.VerifyOpenForLeaseAsync(scope with { DeadlineUptimeMilliseconds = grant.DeadlineUptimeMilliseconds + 1 }, default));

        f.Ingress.Blocked = true;
        Assert.False(await f.Gate.VerifyOpenForLeaseAsync(scope, default));
        Assert.True(f.Ingress.Blocked);
    }
}
