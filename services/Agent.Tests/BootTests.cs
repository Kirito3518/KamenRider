using KamenRaider.Agent.Configuration;
using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Startup;
using KamenRaider.Agent.Sunshine;
using KamenRaider.Contracts;

namespace Agent.Tests;

/// <summary>
/// 0jay-style boot readiness: configuration, boot challenge and lease reconciliation still gate
/// readiness, the Sunshine local endpoints must be live, and the boot cleanup (unpair-all) must
/// pass. Restoration and desktop-health evidence are reported as advisory diagnostics only.
/// </summary>
public class BootTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RestorationAndDesktopHealthAreAdvisoryDiagnosticsOnly(bool restored, bool healthy)
    {
        using var f = new Fixture();
        f.Environment.Restored = restored;
        f.Environment.Healthy = healthy;

        Assert.Equal(Readiness.Ready, await f.Run());

        Assert.Equal("passed", f.Diagnostics(ReadinessDiagnosticIds.BootChallenge));
        Assert.Equal("passed", f.Diagnostics(ReadinessDiagnosticIds.StreamFence));
        Assert.Equal(restored ? "passed" : "blocked", f.Diagnostics(ReadinessDiagnosticIds.Restoration));
        Assert.Equal(healthy ? "passed" : "blocked", f.Diagnostics(ReadinessDiagnosticIds.DesktopHealth));

        var proof = f.Client.Requests[^1];
        Assert.Equal(restored, proof.RestorationVerified);
        Assert.Equal(healthy, proof.HealthVerified);
        Assert.True(proof.FencedCleanupVerified);
        Assert.True(proof.StreamReady);
    }

    [Fact]
    public async Task MissingStreamCleanupCannotReportReady()
    {
        using var f = new Fixture();
        f.Environment.Cleaned = false;

        Assert.Equal(Readiness.NotReady, await f.Run());

        Assert.Single(f.Client.Requests);
        Assert.Equal("blocked", f.Diagnostics(ReadinessDiagnosticIds.StreamFence));
        Assert.Equal(BootEvidenceStatus.Blocked, f.Environment.LastCleanStatus);
    }

    [Fact]
    public async Task OfflineBootCannotReportReady()
    {
        using var f = new Fixture();
        f.Client.Offline = true;

        Assert.Equal(Readiness.NotReady, await f.Run());

        Assert.Empty(f.Client.Requests);
        Assert.Equal(0, f.Liveness.ProbeCalls);
    }

    [Fact]
    public async Task ReadyRequiresSunshineLiveness()
    {
        using var f = new Fixture();
        f.Liveness.Ready = false;

        Assert.Equal(Readiness.NotReady, await f.Run());
        Assert.Single(f.Client.Requests);
        Assert.Equal("blocked", f.Diagnostics(ReadinessDiagnosticIds.StreamFence));
        Assert.Equal("blocked", f.Diagnostics(ReadinessDiagnosticIds.RuntimeHealth));

        f.Liveness.Ready = true;
        Assert.Equal(Readiness.Ready, await f.Run());
        Assert.Equal(3, f.Client.Requests.Count);
        Assert.Equal("passed", f.Diagnostics(ReadinessDiagnosticIds.StreamFence));
        Assert.Equal("passed", f.Diagnostics(ReadinessDiagnosticIds.RuntimeHealth));
        Assert.True(f.Client.Requests[^1].StreamReady);
    }

    [Fact]
    public async Task LivenessProbeExceptionIsAReadinessFault()
    {
        using var f = new Fixture();
        f.Liveness.Throw = true;

        Assert.Equal(Readiness.Fault, await f.Run());

        Assert.Single(f.Client.Requests);
        Assert.Equal("blocked", f.Diagnostics(ReadinessDiagnosticIds.StreamFence));
        Assert.Equal("blocked", f.Diagnostics(ReadinessDiagnosticIds.RuntimeHealth));
    }

    [Fact]
    public async Task CurrentConfigurationMustBeAppliedBeforeProofSubmission()
    {
        using var f = new Fixture();

        Assert.Equal(Readiness.Ready, await f.Run());

        Assert.Equal(2, f.Client.Requests.Count);
        var proof = f.Client.Requests[^1];
        Assert.Equal(7, proof.AppliedConfigRevision);
        Assert.Equal("os-boot", proof.BootId);
        Assert.True(proof.FencedCleanupVerified);
        Assert.Equal("passed", f.Diagnostics(ReadinessDiagnosticIds.ConfigurationRevision));
    }

    [Fact]
    public async Task BootChallengeIsRequiredForReadiness()
    {
        using var f = new Fixture();
        f.Client.NoChallenge = true;

        Assert.Equal(Readiness.NotReady, await f.Run());
        Assert.Single(f.Client.Requests);
        Assert.Equal("blocked", f.Diagnostics(ReadinessDiagnosticIds.BootChallenge));
        Assert.Equal("unknown", f.Diagnostics(ReadinessDiagnosticIds.Restoration));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InvalidBootChallengeIsReportedAsBlocked(bool expired, bool wrongBoot)
    {
        using var f = new Fixture();
        f.Client.ExpiredChallenge = expired;
        f.Client.MismatchedBoot = wrongBoot;

        Assert.Equal(Readiness.NotReady, await f.Run());

        Assert.Single(f.Client.Requests);
        Assert.Equal("blocked", f.Diagnostics(ReadinessDiagnosticIds.BootChallenge));
    }

    [Fact]
    public async Task UnavailableBaselineIsAnAdvisoryDiagnostic()
    {
        using var f = new Fixture(baselineValidated: false);

        Assert.Equal(Readiness.Ready, await f.Run());

        Assert.Equal("unavailable", f.Diagnostics(ReadinessDiagnosticIds.Baseline));
        Assert.Equal("passed", f.Diagnostics(ReadinessDiagnosticIds.BootChallenge));
        Assert.False(f.Client.Requests[0].BaselineValidated);
    }

    [Fact]
    public async Task CleanupRunsBeforeLivenessAndResumeDurabilityProof()
    {
        using var f = new Fixture();
        f.Client.Resume = true;
        f.Importer.Result = true;
        f.Environment.Events = f.Events;
        f.Liveness.Events = f.Events;
        f.Importer.Events = f.Events;

        Assert.Equal(Readiness.Ready, await f.Run());

        var cleanupIndex = f.Events.IndexOf("cleanup");
        var livenessIndex = f.Events.IndexOf("liveness");
        var importIndex = f.Events.IndexOf("lease-import");
        Assert.True(cleanupIndex >= 0);
        Assert.True(livenessIndex > cleanupIndex);
        Assert.True(importIndex > livenessIndex);
        Assert.True(f.Events.IndexOf("old-lease-clear") < importIndex);
    }

    [Fact]
    public async Task SameBootServiceRestartRevalidatesLiveness()
    {
        using var f = new Fixture();
        Assert.Equal(Readiness.Ready, await f.Run());

        f.Liveness.Ready = false;
        Assert.Equal(Readiness.NotReady, await f.Reconciler.RevalidateRuntimeAsync(default));
        Assert.Equal("blocked", f.Diagnostics(ReadinessDiagnosticIds.RuntimeHealth));

        f.Liveness.Ready = true;
        Assert.Equal(Readiness.Ready, await f.Reconciler.RevalidateRuntimeAsync(default));
        Assert.Equal("passed", f.Diagnostics(ReadinessDiagnosticIds.RuntimeHealth));
        Assert.Equal(3, f.Liveness.ProbeCalls);
    }

    [Fact]
    public async Task RuntimeLivenessExceptionReportsFaultAndBlockedDiagnostics()
    {
        using var f = new Fixture();
        Assert.Equal(Readiness.Ready, await f.Run());

        f.Liveness.Throw = true;
        Assert.Equal(Readiness.Fault, await f.Reconciler.RevalidateRuntimeAsync(default));

        Assert.Equal("blocked", f.Diagnostics(ReadinessDiagnosticIds.StreamFence));
        Assert.Equal("blocked", f.Diagnostics(ReadinessDiagnosticIds.RuntimeHealth));
    }

    [Fact]
    public async Task RevalidationBeforeReconciliationIsNotReady()
    {
        using var f = new Fixture();

        Assert.Equal(Readiness.NotReady, await f.Reconciler.RevalidateRuntimeAsync(default));

        Assert.False(f.Reconciler.HasRuntimeEvidence);
        Assert.Equal("unknown", f.Diagnostics(ReadinessDiagnosticIds.RuntimeHealth));
    }

    [Fact]
    public async Task BootChangeInvalidatesRuntimeEvidence()
    {
        using var f = new Fixture();
        Assert.Equal(Readiness.Ready, await f.Run());

        f.Clock.BootId = "next-boot";

        Assert.Equal(Readiness.NotReady, await f.Reconciler.RevalidateRuntimeAsync(default));
        Assert.False(f.Reconciler.HasRuntimeEvidence);
        Assert.Equal("blocked", f.Diagnostics(ReadinessDiagnosticIds.BootChallenge));
    }

    [Fact]
    public async Task ConfigurationChangeRequiresFreshBootProof()
    {
        using var f = new Fixture();
        Assert.Equal(Readiness.Ready, await f.Run());

        await f.Configuration.ApplyAsync(new(8, 6001, 6002), default);

        Assert.Equal(Readiness.NotReady, await f.Reconciler.RevalidateRuntimeAsync(default));
        Assert.False(f.Reconciler.HasRuntimeEvidence);
        Assert.Equal("blocked", f.Diagnostics(ReadinessDiagnosticIds.ConfigurationRevision));
    }

    [Fact]
    public async Task PositiveBootProofReportsEveryAdvisoryCheck()
    {
        using var f = new Fixture();

        Assert.Equal(Readiness.Ready, await f.Run());

        Assert.All(ReadinessDiagnosticIds.Canonical, id => Assert.Equal("passed", f.Diagnostics(id)));
    }

    [Fact]
    public async Task ProofIsBoundToExactControllerChallenge()
    {
        using var f = new Fixture();

        Assert.Equal(Readiness.Ready, await f.Run());

        Assert.Equal("generation-1", f.Client.Requests[^1].Challenge?.Token);
        Assert.Equal("allocation-1", f.Client.Requests[^1].Challenge?.AllocationId);
        Assert.Equal(4, f.Client.Requests[^1].Challenge?.LeaseRevision);
        Assert.Equal("generation-1", f.Environment.LastChallenge?.Token);
    }

    [Fact]
    public async Task ResumeCannotBecomeReadyUntilLeaseIsDurablyRestored()
    {
        using var f = new Fixture();
        f.Client.Resume = true;
        f.Importer.Result = false;

        Assert.Equal(Readiness.NotReady, await f.Run());
        f.Importer.Result = true;
        Assert.Equal(Readiness.Ready, await f.Run());

        Assert.Equal(2, f.Importer.Calls);
        Assert.Equal(f.Client.LastResponse!.Lease, f.Importer.Lease);
        Assert.Equal(f.Client.LastResponse.ServerTimeUtc, f.Importer.ControllerTime);
    }

    [Fact]
    public async Task LeaseExpiryDuringImportCannotReportReady()
    {
        using var f = new Fixture();
        f.Client.Resume = true;
        f.Importer.Result = true;
        f.Importer.OnImport = () => f.Clock.Uptime = 120010;

        Assert.Equal(Readiness.NotReady, await f.Run());
    }

    [Fact]
    public async Task AvailableMachineDoesNotImportAnActiveLease()
    {
        using var f = new Fixture();

        Assert.Equal(Readiness.Ready, await f.Run());

        Assert.Equal(0, f.Importer.Calls);
        Assert.Equal(1, f.Importer.ClearCalls);
        Assert.Equal(f.Clock.BootId, f.Importer.ClearedBootId);
    }

    [Fact]
    public async Task AvailableMachineCannotBecomeReadyUntilOldLeaseSnapshotIsCleared()
    {
        using var f = new Fixture();
        f.Importer.ClearResult = false;

        Assert.Equal(Readiness.NotReady, await f.Run());

        Assert.Equal(1, f.Importer.ClearCalls);
        Assert.Equal(0, f.Importer.Calls);
        Assert.Equal("blocked", f.Diagnostics(ReadinessDiagnosticIds.LeaseReconciliation));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ResumeLeaseMustMatchAllocationAndRevisionBoundToChallenge(bool mismatchAllocation, bool mismatchRevision)
    {
        using var f = new Fixture();
        f.Client.Resume = true;
        f.Client.MismatchAllocation = mismatchAllocation;
        f.Client.MismatchRevision = mismatchRevision;
        f.Importer.Result = true;

        Assert.Equal(Readiness.NotReady, await f.Run());

        Assert.Equal(0, f.Importer.Calls);
        Assert.Equal("blocked", f.Diagnostics(ReadinessDiagnosticIds.LeaseReconciliation));
    }

    [Fact]
    public async Task ActiveLeaseIsNotImportedBeforeStreamCleanupPasses()
    {
        using var f = new Fixture();
        f.Client.Resume = true;
        f.Environment.Cleaned = false;
        f.Importer.Result = true;

        Assert.Equal(Readiness.NotReady, await f.Run());

        Assert.Equal(0, f.Importer.Calls);
        Assert.Single(f.Client.Requests);
    }

    [Fact]
    public async Task BlockedCleanupStillReportsLaterAdvisoryChecks()
    {
        using var f = new Fixture();
        f.Environment.Cleaned = false;

        Assert.Equal(Readiness.NotReady, await f.Run());

        Assert.Equal("blocked", f.Diagnostics(ReadinessDiagnosticIds.StreamFence));
        Assert.Equal("passed", f.Diagnostics(ReadinessDiagnosticIds.DesktopHealth));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "kr-boot-tests-" + Guid.NewGuid().ToString("N"));

        public Fixture(bool baselineValidated = true)
        {
            Directory.CreateDirectory(directory);
            Configuration = new AppliedConfiguration(Path.Combine(directory, "config"));
            Directory.CreateDirectory(Path.Combine(directory, "config"));
            Baseline = new BaselineEvidence(baselineValidated ? BootEvidenceStatus.Passed : BootEvidenceStatus.Unavailable);
            Client = new Client(Clock);
            Reconciler = new BootReconciler(Clock, Configuration, Client, Environment, Importer, Baseline, Liveness);
        }

        public Clock Clock { get; } = new();
        public Client Client { get; }
        public Environment Environment { get; } = new();
        public Importer Importer { get; } = new();
        public Liveness Liveness { get; } = new();
        public List<string> Events { get; } = [];
        public BaselineEvidence Baseline { get; }
        public AppliedConfiguration Configuration { get; }
        public BootReconciler Reconciler { get; }

        public Task<Readiness> Run() => Reconciler.ReconcileAsync(default);

        public string Diagnostics(string checkId) =>
            Reconciler.ReadinessDiagnostics.Single(diagnostic => diagnostic.CheckId == checkId).Status;

        public void Dispose()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class Clock : TimeProvider, IBootClock
    {
        public string BootId { get; set; } = "os-boot";
        public long Uptime { get; set; } = 10;
        public long UptimeMilliseconds => Uptime;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-29T00:00:00Z");
    }

    private sealed class Client(Clock clock) : IBootReconciliationClient
    {

        public bool Offline { get; set; }
        public bool Resume { get; set; }
        public bool NoChallenge { get; set; }
        public bool ExpiredChallenge { get; set; }
        public bool MismatchedBoot { get; set; }
        public bool MismatchAllocation { get; set; }
        public bool MismatchRevision { get; set; }
        public List<ReconcileRequest> Requests { get; } = [];
        public ReconcileResponse? LastResponse { get; private set; }

        public Task<ReconcileResponse> ReconcileAsync(ReconcileRequest request, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("controller offline");
            Requests.Add(request);
            var serverTime = clock.GetUtcNow();
            var lease = Resume
                ? new ActiveLeaseConfirmation("rental", 1, clock.BootId, serverTime.AddMinutes(1))
                : null;
            var allocation = Resume ? (MismatchAllocation ? "another-rental" : "rental") : "allocation-1";
            var revision = Resume ? (MismatchRevision ? 2 : 1) : 4;
            var challengeBoot = MismatchedBoot ? "other-boot" : clock.BootId;
            var challengeExpiry = ExpiredChallenge ? serverTime.AddSeconds(-1) : serverTime.AddSeconds(30);
            var challenge = NoChallenge
                ? null
                : new ReconciliationChallenge("generation-1", challengeBoot, allocation, revision, challengeExpiry);
            var action = request.Challenge is null ? "Hold" : (Resume ? "ResumeLease" : "PrepareAvailable");
            LastResponse = new ReconcileResponse(serverTime, new(7, 5000, 6000), lease, action, challenge);
            return Task.FromResult(LastResponse);
        }
    }

    private sealed class Environment : IBootEnvironment
    {
        public bool Restored { get; set; } = true;
        public bool Cleaned { get; set; } = true;
        public bool Healthy { get; set; } = true;
        public List<string>? Events { get; set; }
        public BootEvidenceStatus LastCleanStatus { get; private set; } = BootEvidenceStatus.Unknown;
        public ReconciliationChallenge? LastChallenge { get; private set; }

        public Task<BootEvidenceStatus> VerifyRestorationAsync(ReconciliationChallenge challenge, CancellationToken ct)
        {
            Events?.Add("restoration");
            return Task.FromResult(Restored ? BootEvidenceStatus.Passed : BootEvidenceStatus.Blocked);
        }

        public Task<BootEvidenceStatus> ClearStreamAccessAsync(ReconciliationChallenge challenge, CancellationToken ct)
        {
            Events?.Add("cleanup");
            LastChallenge = challenge;
            LastCleanStatus = Cleaned ? BootEvidenceStatus.Passed : BootEvidenceStatus.Blocked;
            return Task.FromResult(LastCleanStatus);
        }

        public Task<BootEvidenceStatus> VerifyHealthAsync(ReconciliationChallenge challenge, CancellationToken ct)
        {
            Events?.Add("desktop-health");
            return Task.FromResult(Healthy ? BootEvidenceStatus.Passed : BootEvidenceStatus.Blocked);
        }
    }

    private sealed class Importer : IReconciledLeaseImporter
    {
        public bool Result { get; set; }
        public bool ClearResult { get; set; } = true;
        public int Calls { get; private set; }
        public int ClearCalls { get; private set; }
        public string? ClearedBootId { get; private set; }
        public ActiveLeaseConfirmation? Lease { get; private set; }
        public DateTimeOffset ControllerTime { get; private set; }
        public Action? OnImport { get; set; }
        public List<string>? Events { get; set; }

        public Task<bool> ClearAsync(string bootId, CancellationToken ct)
        {
            Events?.Add("old-lease-clear");
            ClearCalls++;
            ClearedBootId = bootId;
            return Task.FromResult(ClearResult);
        }

        public Task<bool> ImportAsync(ActiveLeaseConfirmation lease, DateTimeOffset controllerTime, long requestStartedUptime, CancellationToken ct)
        {
            Events?.Add("lease-import");
            Calls++;
            Lease = lease;
            ControllerTime = controllerTime;
            OnImport?.Invoke();
            return Task.FromResult(Result);
        }
    }

    private sealed class Liveness : ISunshineLiveness
    {
        public bool Ready { get; set; } = true;
        public bool Throw { get; set; }
        public int ProbeCalls { get; private set; }
        public List<string>? Events { get; set; }

        public SunshineLivenessSnapshot Current { get; private set; } = SunshineLivenessSnapshot.Unknown;

        public Task<SunshineLivenessSnapshot> ProbeOnceAsync(CancellationToken ct)
        {
            ProbeCalls++;
            Events?.Add("liveness");
            if (Throw) throw new IOException("liveness transport failed");
            Current = new(Ready, Ready ? SunshineLivenessProbe.ReasonReady : SunshineLivenessProbe.ReasonStreamSilent,
                DateTimeOffset.UtcNow, 47989, 47984);
            return Task.FromResult(Current);
        }
    }

    private sealed class BaselineEvidence(BootEvidenceStatus status) : IBootBaselineEvidence
    {
        public BootEvidenceStatus Status => status;
    }
}
