using KamenRaider.Agent.Parsec;
using KamenRaider.Agent.Sunshine;

namespace KamenRaider.Agent.Leases;

/// <summary>
/// Lease-end/boot cleanup for the 0jay-style agent: clear Sunshine pairing (the only stream gate)
/// and, only when a Parsec login may have been made, run the rental-end Parsec cleanup. Both are
/// best effort so a failed cleanup can never block a lease end or a shutdown.
/// </summary>
public sealed class AgentStreamCleanup(
    ISunshinePairingSession pairing,
    ParsecManager? parsec,
    ILogger<AgentStreamCleanup>? logger = null) : IStreamCleanup
{
    public async Task<bool> ClearAsync(CancellationToken ct)
    {
        var cleared = false;
        try
        {
            cleared = await pairing.ClearAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger?.LogWarning("Sunshine pairing cleanup failed ({ErrorType}).", exception.GetType().Name);
        }

        if (parsec is not null)
        {
            try
            {
                await parsec.CleanupAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Best effort: a boot-time file cleanup runs next time, and the machine will shut
                // down or be restored before the next renter anyway.
                logger?.LogWarning("Parsec cleanup failed ({ErrorType}).", exception.GetType().Name);
            }
        }

        return cleared;
    }
}
