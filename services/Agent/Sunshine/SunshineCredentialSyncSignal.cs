namespace KamenRaider.Agent.Sunshine;

/// <summary>
/// Carries the fleet-wide Sunshine credential revision from a successful sync response to the hosted
/// credential worker without coupling the two loops. Signals coalesce: the worker always observes the
/// newest revision and never queues redundant fetches.
/// </summary>
public sealed class SunshineCredentialSyncSignal
{
    private readonly SemaphoreSlim pending = new(0, 1);
    private readonly object sync = new();
    private long observedRevision;

    /// <summary>Records the fleet revision reported by the newest successful sync response.</summary>
    public void Publish(long fleetRevision)
    {
        lock (sync)
        {
            observedRevision = fleetRevision;
            try
            {
                if (pending.CurrentCount == 0) pending.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another publisher released between the count check and Release; the newest revision is kept.
            }
        }
    }

    /// <summary>Waits until at least one sync response has been observed and returns the newest revision.</summary>
    public async Task<long> WaitAsync(CancellationToken ct)
    {
        await pending.WaitAsync(ct);
        lock (sync) return observedRevision;
    }
}
