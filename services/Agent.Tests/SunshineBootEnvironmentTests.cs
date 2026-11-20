using KamenRaider.Agent.Startup;
using KamenRaider.Agent.Sunshine;
using KamenRaider.Contracts;

namespace Agent.Tests;

/// <summary>
/// Boot cleanup is pairing cleanup: unpair every trusted Moonlight client. Restoration and
/// desktop-health adapters are not qualified and stay advisory.
/// </summary>
public sealed class SunshineBootEnvironmentTests
{
    private static ReconciliationChallenge Challenge => new("token", "boot", null, null, DateTimeOffset.UtcNow.AddMinutes(1));

    [Fact]
    public async Task RestorationAndHealthAreUnavailableWithoutTouchingPairing()
    {
        var session = new RecordingPairingSession(clearResult: true);
        var environment = new SunshineBootEnvironment(() => session);

        Assert.Equal(BootEvidenceStatus.Unavailable, await environment.VerifyRestorationAsync(Challenge, default));
        Assert.Equal(BootEvidenceStatus.Unavailable, await environment.VerifyHealthAsync(Challenge, default));
        Assert.Equal(0, session.ClearCalls);
    }

    [Fact]
    public async Task CleanupPassesWhenSunshineUnpairsEveryClient()
    {
        var session = new RecordingPairingSession(clearResult: true);
        var environment = new SunshineBootEnvironment(() => session);

        Assert.Equal(BootEvidenceStatus.Passed, await environment.ClearStreamAccessAsync(Challenge, default));
        Assert.Equal(1, session.ClearCalls);
    }

    [Fact]
    public async Task CleanupIsBlockedWhenSunshineRefusesToUnpair()
    {
        var session = new RecordingPairingSession(clearResult: false);
        var environment = new SunshineBootEnvironment(() => session);

        Assert.Equal(BootEvidenceStatus.Blocked, await environment.ClearStreamAccessAsync(Challenge, default));
    }

    [Fact]
    public async Task CleanupTransportFailureIsReportedUnavailable()
    {
        var session = new RecordingPairingSession(clearResult: true) { FailClear = true };
        var environment = new SunshineBootEnvironment(() => session);

        Assert.Equal(BootEvidenceStatus.Unavailable, await environment.ClearStreamAccessAsync(Challenge, default));
    }

    [Fact]
    public async Task MissingHardwareEvidenceAdaptersCannotAffirmAnyCheck()
    {
        var environment = new UnavailableBootEnvironment();

        Assert.Equal(BootEvidenceStatus.Unavailable, await environment.VerifyRestorationAsync(Challenge, default));
        Assert.Equal(BootEvidenceStatus.Unavailable, await environment.ClearStreamAccessAsync(Challenge, default));
        Assert.Equal(BootEvidenceStatus.Unavailable, await environment.VerifyHealthAsync(Challenge, default));
    }

    private sealed class RecordingPairingSession(bool clearResult) : ISunshinePairingSession
    {
        public int ClearCalls { get; private set; }
        public bool FailClear { get; init; }

        public Task<PairingOffer?> CreateOfferAsync(CancellationToken ct) => Task.FromResult<PairingOffer?>(null);
        public Task<string[]> ListAsync(CancellationToken ct) => Task.FromResult(Array.Empty<string>());
        public Task<CommandResult> ExecuteAsync(CommandEnvelope command, CancellationToken ct) =>
            Task.FromResult(new CommandResult(command.CommandId, "failed", command.LeaseRevision, "PairingUnavailable"));

        public Task<bool> ClearAsync(CancellationToken ct)
        {
            ClearCalls++;
            if (FailClear) throw new IOException("sunshine unreachable");
            return Task.FromResult(clearResult);
        }
    }
}
