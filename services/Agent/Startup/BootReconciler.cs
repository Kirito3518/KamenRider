using KamenRaider.Agent.Configuration;
using KamenRaider.Agent.Leases;
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

// Implementations must independently verify restoration for this boot, a real stream fence,
// empty pairing/client state, and desktop/capture/input/audio health. Never infer these from uptime.
public interface IBootEnvironment
{
    Task<BootEvidenceStatus> VerifyRestorationAsync(ReconciliationChallenge challenge, CancellationToken ct);
    Task<BootEvidenceStatus> FenceAndClearAsync(ReconciliationChallenge challenge, CancellationToken ct);
    Task<BootEvidenceStatus> VerifyHealthAsync(ReconciliationChallenge challenge, CancellationToken ct);
    // Recheck volatile runtime health each sync cycle without reusing an expired one-time challenge.
    Task<BootEvidenceStatus> VerifyRuntimeHealthAsync(string bootId, long configurationRevision, CancellationToken ct);
}

public sealed class BootReconciler(
    IBootClock clock,
    IStreamGate gate,
    AppliedConfiguration configuration,
    IBootReconciliationClient client,
    IBootEnvironment environment,
    IReconciledLeaseImporter leaseImporter,
    IBootBaselineEvidence baselineEvidence)
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
            {
                SetDiagnostic(ReadinessDiagnosticIds.Baseline, baselineEvidence.Status);
                SetDiagnostic(ReadinessDiagnosticIds.RuntimeHealth, BootEvidenceStatus.Unknown);
                return await InvalidateReadinessAsync(ct);
            }

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

            SetDiagnostic(ReadinessDiagnosticIds.RuntimeHealth, BootEvidenceStatus.Unknown);
            var runtimeStatus = await environment.VerifyRuntimeHealthAsync(boot, readyConfigurationRevision, ct);
            SetDiagnostic(ReadinessDiagnosticIds.RuntimeHealth, runtimeStatus);
            if (runtimeStatus != BootEvidenceStatus.Passed)
            {
                await gate.BlockAsync(ct);
                return Readiness.NotReady;
            }

            return Readiness.Ready;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            try
            {
                await gate.BlockAsync(ct);
            }
            catch (Exception)
            {
                readyBootId = null;
                readyConfigurationRevision = -1;
            }

            return Readiness.Fault;
        }
        finally
        {
            serial.Release();
        }
    }

    private async Task<Readiness> InvalidateReadinessAsync(CancellationToken ct)
    {
        readyBootId = null;
        readyConfigurationRevision = -1;
        await gate.BlockAsync(ct);
        return Readiness.NotReady;
    }

    public async Task<Readiness> ReconcileAsync(CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try
        {
            readinessDiagnostics = UnknownDiagnostics.ToArray();
            readyBootId = null;
            readyConfigurationRevision = -1;
            await gate.BlockAsync(ct);

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
            if (baselineStatus != BootEvidenceStatus.Passed)
            {
                return Readiness.NotReady;
            }

            var restoration = await environment.VerifyRestorationAsync(challenge!, ct);
            SetDiagnostic(ReadinessDiagnosticIds.Restoration, restoration);
            if (restoration != BootEvidenceStatus.Passed)
            {
                return Readiness.NotReady;
            }

            var fence = await environment.FenceAndClearAsync(challenge!, ct);
            SetDiagnostic(ReadinessDiagnosticIds.StreamFence, fence);
            if (fence != BootEvidenceStatus.Passed)
            {
                return Readiness.NotReady;
            }

            var health = await environment.VerifyHealthAsync(challenge!, ct);
            SetDiagnostic(ReadinessDiagnosticIds.DesktopHealth, health);
            if (health != BootEvidenceStatus.Passed)
            {
                return Readiness.NotReady;
            }

            if (!IsCurrentChallenge(challenge, boot, initial.ServerTimeUtc, initialStarted))
            {
                SetDiagnostic(ReadinessDiagnosticIds.BootChallenge, BootEvidenceStatus.Blocked);
                return Readiness.NotReady;
            }

            var started = clock.UptimeMilliseconds;
            var response = await client.ReconcileAsync(
                new(boot, configuration.Current!.Revision, true, true, true, true, challenge),
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

        var index = Array.IndexOf(ReadinessDiagnosticIds.Canonical, checkId);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(checkId));
        }

        var updated = readinessDiagnostics.ToArray();
        updated[index] = new ReadinessDiagnostic(checkId, wireStatus);
        readinessDiagnostics = updated;
    }
}
