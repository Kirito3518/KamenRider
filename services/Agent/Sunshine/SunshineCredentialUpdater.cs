// The updater is hosted only inside the Windows Agent service; the DPAPI credential store it writes
// is Windows-only by construction.
#pragma warning disable CA1416

namespace KamenRaider.Agent.Sunshine;

/// <summary>
/// Applies the fleet-wide Sunshine management credential when a successful sync response reports a
/// newer fleet revision than the locally applied revision. The credential is saved first with the
/// DPAPI-protected store and only then is the applied revision recorded, so a crash in between causes
/// a harmless idempotent re-fetch rather than a claimed-but-unapplied revision. Failures retry on the
/// next sync signal with a bounded backoff; only exception types and status categories are logged,
/// never credential values, bodies, or hashes.
/// </summary>
public sealed class SunshineCredentialUpdater(
    SunshineCredentialClient client,
    Uri controller,
    ISunshineCredentialRevisionStore appliedStore,
    ISunshineManagementCredentialSink credentialStore,
    TimeProvider time,
    ILogger<SunshineCredentialUpdater> logger)
{
    private const int MaximumBackoffFailures = 4;
    private int failures;
    private DateTimeOffset nextAttemptUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// Returns true only when a newer fleet credential was decoded and both stores were updated.
    /// Never throws for remote, credential, or local-store failures; cancellation is propagated.
    /// </summary>
    public async Task<bool> TryUpdateAsync(long fleetRevision, CancellationToken ct)
    {
        if (fleetRevision <= 0)
        {
            ResetBackoff();
            return false;
        }

        long appliedRevision;
        try
        {
            appliedRevision = appliedStore.Read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Sunshine credential applied revision is unavailable ({ErrorType}); retrying next sync.", ex.GetType().Name);
            ScheduleRetry();
            return false;
        }

        if (fleetRevision <= appliedRevision)
        {
            ResetBackoff();
            return false;
        }

        if (time.GetUtcNow() < nextAttemptUtc) return false;

        try
        {
            var outcome = await client.FetchAsync(controller, appliedRevision, ct);
            switch (outcome)
            {
                case SunshineCredentialFetchOutcome.Delivery delivery:
                    if (delivery.Value.Revision <= appliedRevision)
                    {
                        logger.LogWarning("Sunshine credential delivery did not advance the applied revision; retrying next sync.");
                        ScheduleRetry();
                        return false;
                    }

                    credentialStore.Save(new SunshineManagementCredential(delivery.Value.UserName, delivery.Value.Password), replace: true);
                    appliedStore.Save(delivery.Value.Revision);
                    logger.LogInformation("Applied fleet Sunshine management credential revision {Revision}.", delivery.Value.Revision);
                    ResetBackoff();
                    return true;

                case SunshineCredentialFetchOutcome.AlreadyCurrent:
                    logger.LogWarning("Sunshine credential endpoint reported the current revision while the local applied revision is behind; retrying next sync.");
                    ScheduleRetry();
                    return false;

                case SunshineCredentialFetchOutcome.NotConfigured:
                    logger.LogWarning("Sunshine credential endpoint has no configured fleet credential; retrying next sync.");
                    ScheduleRetry();
                    return false;

                case SunshineCredentialFetchOutcome.Rejected rejected:
                    logger.LogWarning("Sunshine credential fetch was rejected ({StatusCategory}); retrying next sync.", StatusCategory(rejected.StatusCode));
                    ScheduleRetry();
                    return false;

                default:
                    logger.LogWarning("Sunshine credential fetch returned an unsupported outcome; retrying next sync.");
                    ScheduleRetry();
                    return false;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Sunshine credential fetch failed ({ErrorType}); retrying next sync.", ex.GetType().Name);
            ScheduleRetry();
            return false;
        }
    }

    private void ScheduleRetry()
    {
        failures = Math.Min(failures + 1, MaximumBackoffFailures);
        var seconds = Math.Min(60, 5 * (1 << failures));
        nextAttemptUtc = time.GetUtcNow().AddSeconds(seconds);
    }

    private void ResetBackoff()
    {
        failures = 0;
        nextAttemptUtc = DateTimeOffset.MinValue;
    }

    private static string StatusCategory(int statusCode) => statusCode switch
    {
        401 => "unauthorized",
        409 => "notApprovedOrQuarantined",
        _ => "unexpected",
    };
}

#pragma warning restore CA1416
