using KamenRaider.Agent.Configuration;
using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Sunshine;
using KamenRaider.Contracts;

namespace KamenRaider.Agent.Startup;

public enum Readiness { NotReady, Ready, Fault }
public enum BootEvidenceStatus { Passed, Blocked, Unavailable, Unknown }

public interface IBootReconciliationClient
{
    Task<ReconcileResponse> ReconcileAsync(ReconcileRequest request, CancellationToken ct);
}

public interface IBootBaselineEvidence
{
    BootEvidenceStatus Status { get; }
}

public sealed class UnvalidatedBootBaselineEvidence : IBootBaselineEvidence
{
    public BootEvidenceStatus Status => BootEvidenceStatus.Unavailable;
}

public interface IReconciledLeaseImporter
{
    Task<bool> ImportAsync(ActiveLeaseConfirmation lease, DateTimeOffset controllerTime, long requestStartedUptime, CancellationToken ct);
    Task<bool> ClearAsync(string bootId, CancellationToken ct);
}

// Implementations must independently verify restoration and desktop health where such
// evidence exists, and must actually clear Sunshine pairing/stream access on boot.
// Restoration and desktop evidence are advisory diagnostics in the 0jay-style readiness
// model; only the pairing cleanup is required for readiness.
public interface IBootEnvironment
{
    Task<BootEvidenceStatus> VerifyRestorationAsync(ReconciliationChallenge challenge, CancellationToken ct);
    Task<BootEvidenceStatus> ClearStreamAccessAsync(ReconciliationChallenge challenge, CancellationToken ct);
    Task<BootEvidenceStatus> VerifyHealthAsync(ReconciliationChallenge challenge, CancellationToken ct);
}

public sealed class BootReconciler(
    IBootClock clock,
    AppliedConfiguration configuration,
    IBootReconciliationClient client,
    IBootEnvironment environment,
    IReconciledLeaseImporter leaseImporter,
    IBootBaselineEvidence baselineEvidence,
    ISunshineLiveness liveness)
{
    private static readonly ReadinessDiagnostic[] UnknownDiagnostics = ReadinessDiagnosticIds.Canonical
        .Select(id => new ReadinessDiagnostic(id, ReadinessDiagnosticStatuses.Unknown))
        .ToArray();

    private readonly SemaphoreSlim serial = new(1, 1);
    private ReadinessDiagnostic[] readinessDiagnostics = UnknownDiagnostics.ToArray();
    private string? readyBootId;
    private long readyConfigurationRevision = -1;

    public bool HasRuntimeEvidence => readyBootId == clock.BootId && readyConfigurationRevision == (configuration.Current?.Revision ?? 0);

    public ReadinessDiagnostic[] ReadinessDiagnostics => readinessDiagnostics.ToArray();

    public async Task<Readiness> RevalidateRuntimeAsync(CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try
        {
            var boot = readyBootId;
            if (boot is null)
            {
                SetDiagnostic(ReadinessDiagnosticIds.RuntimeHealth, BootEvidenceStatus.Unknown);
                return await InvalidateReadinessAsync(ct);
            }

            if (baselineEvidence.Status != BootEvidenceStatus.Passed)
                SetDiagnostic(ReadinessDiagnosticIds.Baseline, baselineEvidence.Status);

            if (boot != clock.BootId)
            {
                SetDiagnostic(ReadinessDiagnosticIds.BootChallenge, BootEvidenceStatus.Blocked);
                SetDiagnostic(ReadinessDiagnosticIds.RuntimeHealth, BootEvidenceStatus.Unknown);
                return await InvalidateReadinessAsync(ct);
            }

            if (readyConfigurationRevision != (configuration.Current?.Revision ?? 0))
            {
                SetDiagnostic(ReadinessDiagnosticIds.ConfigurationRevision, BootEvidenceStatus.Blocked);
                SetDiagnostic(ReadinessDiagnosticIds.RuntimeHealth, BootEvidenceStatus.Unknown);
                return await InvalidateReadinessAsync(ct);
            }

            // Liveness is the gate: the two local Sunshine endpoints must answer. A short
            // miss streak is absorbed by the monitor's hysteresis so one probe does not take
            // a working machine off the shelf.
            SunshineLivenessSnapshot snapshot;
            try
            {
                snapshot = await liveness.ProbeOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                SetDiagnostic(ReadinessDiagnosticIds.StreamFence, BootEvidenceStatus.Blocked);
                SetDiagnostic(ReadinessDiagnosticIds.RuntimeHealth, BootEvidenceStatus.Blocked);
                return Readiness.Fault;
            }

            SetDiagnostic(ReadinessDiagnosticIds.StreamFence,
                snapshot.Ready ? BootEvidenceStatus.Passed : BootEvidenceStatus.Blocked);
            SetDiagnostic(ReadinessDiagnosticIds.RuntimeHealth,
                snapshot.Ready ? BootEvidenceStatus.Passed : BootEvidenceStatus.Blocked);
            return snapshot.Ready ? Readiness.Ready : Readiness.NotReady;
        }
        finally
        {
            serial.Release();
        }
    }

    private Task<Readiness> InvalidateReadinessAsync(CancellationToken ct)
    {
        _ = ct;
        readyBootId = null;
        readyConfigurationRevision = -1;
        return Task.FromResult(Readiness.NotReady);
    }

    public async Task<Readiness> ReconcileAsync(CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try
        {
            readinessDiagnostics = UnknownDiagnostics.ToArray();
            readyBootId = null;
            readyConfigurationRevision = -1;

            var baselineStatus = baselineEvidence.Status;
            SetDiagnostic(ReadinessDiagnosticIds.Baseline, baselineStatus);

            var boot = clock.BootId;
            var initialStarted = clock.UptimeMilliseconds;
            var initial = await client.ReconcileAsync(
                new(boot, configuration.Current?.Revision ?? 0, baselineStatus == BootEvidenceStatus.Passed),
                ct);
            await configuration.ApplyAsync(initial.Configuration, ct);
            SetDiagnostic(ReadinessDiagnosticIds.ConfigurationRevision,
                configuration.Current?.Revision == initial.Configuration.Revision
                    ? BootEvidenceStatus.Passed
                    : BootEvidenceStatus.Blocked);

            var challenge = initial.Challenge;
            if (!IsCurrentChallenge(challenge, boot, initial.ServerTimeUtc, initialStarted))
            {
                SetDiagnostic(ReadinessDiagnosticIds.BootChallenge, BootEvidenceStatus.Blocked);
                return Readiness.NotReady;
            }

            SetDiagnostic(ReadinessDiagnosticIds.BootChallenge, BootEvidenceStatus.Passed);

            // Restoration and desktop health are reported, but the 0jay-style model does not
            // require those (not yet qualified) adapters to sell a machine. Pairing cleanup is
            // required: it is what ends the previous renter's access.
            var restoration = await environment.VerifyRestorationAsync(challenge!, ct);
            SetDiagnostic(ReadinessDiagnosticIds.Restoration, restoration);

            var cleaned = await environment.ClearStreamAccessAsync(challenge!, ct);
            SetDiagnostic(ReadinessDiagnosticIds.StreamFence, cleaned);

            var health = await environment.VerifyHealthAsync(challenge!, ct);
            SetDiagnostic(ReadinessDiagnosticIds.DesktopHealth, health);

            if (cleaned != BootEvidenceStatus.Passed)
                return Readiness.NotReady;

            SunshineLivenessSnapshot livenessSnapshot;
            try
            {
                livenessSnapshot = await liveness.ProbeOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                SetDiagnostic(ReadinessDiagnosticIds.StreamFence, BootEvidenceStatus.Blocked);
                SetDiagnostic(ReadinessDiagnosticIds.RuntimeHealth, BootEvidenceStatus.Blocked);
                return Readiness.Fault;
            }

            SetDiagnostic(ReadinessDiagnosticIds.StreamFence,
                livenessSnapshot.Ready ? BootEvidenceStatus.Passed : BootEvidenceStatus.Blocked);
            SetDiagnostic(ReadinessDiagnosticIds.RuntimeHealth,
                livenessSnapshot.Ready ? BootEvidenceStatus.Passed : BootEvidenceStatus.Blocked);
            if (!livenessSnapshot.Ready)
            {
                // Sunshine may still be starting; the next sync cycle retries reconciliation.
                return Readiness.NotReady;
            }

            if (!IsCurrentChallenge(challenge, boot, initial.ServerTimeUtc, initialStarted))
            {
                SetDiagnostic(ReadinessDiagnosticIds.BootChallenge, BootEvidenceStatus.Blocked);
                return Readiness.NotReady;
            }

            var started = clock.UptimeMilliseconds;
            var response = await client.ReconcileAsync(
                new(boot, configuration.Current!.Revision, baselineStatus == BootEvidenceStatus.Passed,
                    restoration == BootEvidenceStatus.Passed, cleaned == BootEvidenceStatus.Passed,
                    health == BootEvidenceStatus.Passed, challenge, StreamReady: livenessSnapshot.Ready),
                ct);
            await configuration.ApplyAsync(response.Configuration, ct);
            var configurationMatches = clock.BootId == boot && response.Configuration.Revision == initial.Configuration.Revision;
            SetDiagnostic(ReadinessDiagnosticIds.ConfigurationRevision,
                configurationMatches ? BootEvidenceStatus.Passed : BootEvidenceStatus.Blocked);
            if (!configurationMatches)
            {
                return Readiness.NotReady;
            }

            if (response.Action == "PrepareAvailable" && response.Lease is null)
            {
                var cleared = await leaseImporter.ClearAsync(boot, ct);
                SetDiagnostic(ReadinessDiagnosticIds.LeaseReconciliation,
                    cleared ? BootEvidenceStatus.Passed : BootEvidenceStatus.Blocked);
                if (!cleared || clock.BootId != boot || response.Configuration.Revision != initial.Configuration.Revision)
                {
                    return Readiness.NotReady;
                }

                readyBootId = boot;
                readyConfigurationRevision = response.Configuration.Revision;
                return Readiness.Ready;
            }

            if (!IsValidResume(response, challenge!, boot, started))
            {
                SetDiagnostic(ReadinessDiagnosticIds.LeaseReconciliation, BootEvidenceStatus.Blocked);
                return Readiness.NotReady;
            }

            var lease = response.Lease!;
            var restored = await leaseImporter.ImportAsync(lease, response.ServerTimeUtc, started, ct);
            var validImport = restored && clock.BootId == boot && lease.ExpiresAtUtc > EstimateServerTime(response.ServerTimeUtc, started);
            SetDiagnostic(ReadinessDiagnosticIds.LeaseReconciliation,
                validImport ? BootEvidenceStatus.Passed : BootEvidenceStatus.Blocked);
            if (!validImport)
            {
                return Readiness.NotReady;
            }

            readyBootId = boot;
            readyConfigurationRevision = response.Configuration.Revision;
            return Readiness.Ready;
        }
        catch (HttpRequestException)
        {
            return Readiness.NotReady;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Readiness.Fault;
        }
        finally
        {
            serial.Release();
        }
    }

    private bool IsCurrentChallenge(ReconciliationChallenge? challenge, string boot, DateTimeOffset serverTime, long requestStartedUptime)
    {
        return challenge is not null &&
            challenge.BootId == boot &&
            challenge.ExpiresAtUtc > EstimateServerTime(serverTime, requestStartedUptime);
    }

    private bool IsValidResume(ReconcileResponse response, ReconciliationChallenge challenge, string boot, long requestStartedUptime)
    {
        return response.Action == "ResumeLease" &&
            response.Lease is { } lease &&
            lease.BootId == boot &&
            challenge.AllocationId is not null &&
            challenge.LeaseRevision is not null &&
            challenge.AllocationId == lease.RentalId &&
            challenge.LeaseRevision.Value == lease.LeaseRevision &&
            lease.ExpiresAtUtc > EstimateServerTime(response.ServerTimeUtc, requestStartedUptime);
    }

    private DateTimeOffset EstimateServerTime(DateTimeOffset serverTime, long requestStartedUptime)
        => serverTime.AddMilliseconds(clock.UptimeMilliseconds - requestStartedUptime);

    private void SetDiagnostic(string checkId, BootEvidenceStatus status)
    {
        var wireStatus = status switch
        {
            BootEvidenceStatus.Passed => ReadinessDiagnosticStatuses.Passed,
            BootEvidenceStatus.Blocked => ReadinessDiagnosticStatuses.Blocked,
            BootEvidenceStatus.Unavailable => ReadinessDiagnosticStatuses.Unavailable,
            _ => ReadinessDiagnosticStatuses.Unknown
        };

        var index = ReadinessDiagnosticIds.Canonical.IndexOf(checkId);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(checkId));
        }

        var updated = readinessDiagnostics.ToArray();
        updated[index] = new ReadinessDiagnostic(checkId, wireStatus);
        readinessDiagnostics = updated;
    }
}
