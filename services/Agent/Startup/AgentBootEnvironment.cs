using KamenRaider.Agent.Parsec;
using KamenRaider.Agent.Sunshine;
using KamenRaider.Contracts;

namespace KamenRaider.Agent.Startup;

/// <summary>
/// Boot cleanup for the 0jay-style agent: Sunshine unpair must succeed (it is the stream gate);
/// deletion of any leftover Parsec session files is best effort because a fresh boot has no
/// interactive desktop yet. Restoration/desktop-health adapters remain advisory diagnostics.
/// </summary>
public sealed class AgentBootEnvironment(
    Func<ISunshinePairingSession> pairing,
    Func<ParsecManager?> parsec,
    ILogger<AgentBootEnvironment>? logger = null) : IBootEnvironment
{
    public Task<BootEvidenceStatus> VerifyRestorationAsync(ReconciliationChallenge challenge, CancellationToken ct)
        => Task.FromResult(BootEvidenceStatus.Unavailable);

    public async Task<BootEvidenceStatus> ClearStreamAccessAsync(ReconciliationChallenge challenge, CancellationToken ct)
    {
        var cleared = BootEvidenceStatus.Unavailable;
        try
        {
            cleared = await pairing().ClearAsync(ct) ? BootEvidenceStatus.Passed : BootEvidenceStatus.Blocked;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            cleared = BootEvidenceStatus.Unavailable;
        }

        try
        {
            var manager = parsec();
            if (manager is not null)
                await manager.CleanupBootSessionFilesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger?.LogWarning("Boot-time Parsec session cleanup failed ({ErrorType}).", exception.GetType().Name);
        }

        return cleared;
    }

    public Task<BootEvidenceStatus> VerifyHealthAsync(ReconciliationChallenge challenge, CancellationToken ct)
        => Task.FromResult(BootEvidenceStatus.Unavailable);
}
