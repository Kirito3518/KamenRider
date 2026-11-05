using KamenRaider.Agent.Leases;

namespace KamenRaider.Agent.Sunshine;

public interface IStreamIngressPolicy
{
    Task SetBlockedAsync(bool blocked, CancellationToken cancellationToken);
    Task<bool> IsBlockedAsync(CancellationToken cancellationToken);
}

public interface ISunshineServiceControl
{
    Task StartAndWaitAsync(CancellationToken cancellationToken);
    Task StopAndWaitAsync(CancellationToken cancellationToken);
    Task<bool> IsRunningAsync(CancellationToken cancellationToken);
}

public sealed class StreamGateCoordinator(
    IStreamIngressPolicy ingress,
    ISunshineServiceControl sunshine,
    TimeProvider time,
    IBootClock boot) : IStreamGate
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private StreamLeaseGrant? currentGrant;
    private bool closedVerified;

    public StreamLeaseGrant? CurrentGrant => Volatile.Read(ref currentGrant);

    public async Task BlockAsync(CancellationToken cancellationToken)
    {
        // Closing the stream is a containment action; cancellation must not skip it.
        _ = cancellationToken;
        await lifecycle.WaitAsync(CancellationToken.None);
        try
        {
            await EnsureClosedAsync();
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task AllowAsync(StreamLeaseGrant grant, CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (!IsCurrentGrant(grant))
                throw new InvalidOperationException("The stream grant is expired or belongs to another OS boot.");

            if (currentGrant is { } previous)
            {
                if (!CanReplace(previous, grant))
                    throw new InvalidOperationException("The stream grant is stale or changes lease scope without reconciliation.");

                if (await IsOpenAndVerifiedAsync(cancellationToken))
                {
                    if (!IsCurrentGrant(grant))
                        throw new InvalidOperationException("The stream grant expired while the active gate was being verified.");

                    Volatile.Write(ref currentGrant, grant);
                    closedVerified = false;
                    return;
                }
            }

            await EnsureClosedAsync();
            if (!IsCurrentGrant(grant))
                throw new InvalidOperationException("The stream grant expired while the gate was being prepared.");

            // The previously verified closed state is no longer valid once a start is attempted,
            // including when StartAndWaitAsync throws after partially starting the service.
            closedVerified = false;
            await sunshine.StartAndWaitAsync(cancellationToken);
            if (!await sunshine.IsRunningAsync(cancellationToken))
                throw new IOException("Sunshine did not reach the running state while ingress was blocked.");
            if (!IsCurrentGrant(grant))
                throw new InvalidOperationException("The stream grant expired while Sunshine was starting.");

            await ingress.SetBlockedAsync(false, cancellationToken);
            if (await ingress.IsBlockedAsync(cancellationToken))
                throw new IOException("The Sunshine firewall block remains enabled.");
            if (!await sunshine.IsRunningAsync(cancellationToken))
                throw new IOException("Sunshine stopped while opening stream ingress.");
            if (!IsCurrentGrant(grant))
                throw new InvalidOperationException("The stream grant expired before ingress was confirmed open.");

            Volatile.Write(ref currentGrant, grant);
            closedVerified = false;
        }
        catch (Exception openError)
        {
            try
            {
                await EnsureClosedAsync();
            }
            catch (Exception closeError)
            {
                Volatile.Write(ref currentGrant, null);
                throw new AggregateException("Opening Sunshine failed and the gate could not be verified closed.", openError, closeError);
            }

            Volatile.Write(ref currentGrant, null);
            throw;
        }
        finally
        {
            lifecycle.Release();
        }
    }

    // The Stream Guard host calls this on a short interval so it can enforce a lease
    // deadline even when its Agent pipe client is still connected but unresponsive.
    public async Task EnforceAsync(CancellationToken cancellationToken)
    {
        if (!await lifecycle.WaitAsync(0, cancellationToken))
            return;

        try
        {
            var grant = CurrentGrant;
            var blocked = await ingress.IsBlockedAsync(cancellationToken);
            var running = await sunshine.IsRunningAsync(cancellationToken);

            if (grant is null)
            {
                if (!blocked || running)
                    await BlockCoreAsync();
                else
                    closedVerified = true;
                return;
            }

            if (!IsCurrentGrant(grant) || blocked || !running)
                await BlockCoreAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception enforcementError)
        {
            try
            {
                await BlockCoreAsync();
            }
            catch (Exception closeError)
            {
                throw new AggregateException("Stream gate enforcement failed and the gate could not be verified closed.", enforcementError, closeError);
            }

            throw new InvalidOperationException("Stream gate enforcement failed; the gate was closed.", enforcementError);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private async Task<bool> IsOpenAndVerifiedAsync(CancellationToken cancellationToken)
    {
        var blocked = await ingress.IsBlockedAsync(cancellationToken);
        var running = await sunshine.IsRunningAsync(cancellationToken);
        return !blocked && running;
    }

    private bool IsCurrentGrant(StreamLeaseGrant? grant) =>
        grant is not null &&
        !string.IsNullOrWhiteSpace(grant.RentalId) &&
        !string.IsNullOrWhiteSpace(grant.BootId) &&
        grant.Revision > 0 &&
        grant.ExpiresAtUtc > time.GetUtcNow() &&
        grant.DeadlineUptimeMilliseconds > boot.UptimeMilliseconds &&
        grant.BootId == boot.BootId;

    private static bool CanReplace(StreamLeaseGrant previous, StreamLeaseGrant next)
    {
        if (previous.RentalId != next.RentalId || previous.BootId != next.BootId || next.Revision < previous.Revision)
            return false;

        if (next.Revision == previous.Revision)
            return next.ExpiresAtUtc == previous.ExpiresAtUtc &&
                next.DeadlineUptimeMilliseconds <= previous.DeadlineUptimeMilliseconds;

        return next.ExpiresAtUtc > previous.ExpiresAtUtc;
    }

    private async Task EnsureClosedAsync()
    {
        if (currentGrant is null && closedVerified)
        {
            var blocked = await ingress.IsBlockedAsync(CancellationToken.None);
            var running = await sunshine.IsRunningAsync(CancellationToken.None);
            if (blocked && !running)
                return;
        }

        await BlockCoreAsync();
    }

    private async Task BlockCoreAsync()
    {
        Volatile.Write(ref currentGrant, null);
        closedVerified = false;
        List<Exception> failures = [];

        try
        {
            // Block first so a service stop timeout cannot leave a new connection path open.
            await ingress.SetBlockedAsync(true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            await sunshine.StopAndWaitAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            if (!await ingress.IsBlockedAsync(CancellationToken.None))
                failures.Add(new IOException("The Sunshine firewall block could not be verified."));
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            if (await sunshine.IsRunningAsync(CancellationToken.None))
                failures.Add(new IOException("Sunshine remained running after the stop request."));
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        if (failures.Count > 0)
            throw new AggregateException("Sunshine ingress and service shutdown could not both be verified.", failures);

        closedVerified = true;
    }
}
