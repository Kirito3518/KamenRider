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

public interface IStreamGuardControl : IStreamGate
{
    Task<bool> VerifyOpenForLeaseAsync(PairingScope scope, CancellationToken cancellationToken);
    Task BeginFencedMaintenanceAsync(CancellationToken cancellationToken);
    Task<bool> EndFencedMaintenanceAsync(CancellationToken cancellationToken);
}

public sealed class StreamGateCoordinator(
    IStreamIngressPolicy ingress,
    ISunshineServiceControl sunshine,
    TimeProvider time,
    IBootClock boot,
    IStreamGrantHistoryStore historyStore,
    IMaintenanceFenceReader maintenanceFence) : IStreamGuardControl
{
    private static readonly TimeSpan MaintenanceLimit = TimeSpan.FromSeconds(45);
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim closeSerial = new(1, 1);
    private readonly Dictionary<(string BootId, string RentalId), StreamLeaseGrant> lastGrantByLease = [];
    private StreamLeaseKey? currentLeaseKey;
    private StreamLeaseGrant? openingGrant;
    private long gateGeneration;
    private long blockRequestGeneration;
    private int closeRequestsInProgress;
    private bool grantHistoryLoaded;
    private StreamLeaseGrant? currentGrant;
    private bool closedVerified;
    private bool maintenanceActive;
    private string? maintenanceBootId;
    private long maintenanceDeadlineUptimeMilliseconds;

    public StreamLeaseGrant? CurrentGrant => Volatile.Read(ref currentGrant);

    public async Task BlockAsync(CancellationToken cancellationToken)
    {
        // Closing the stream is a containment action; cancellation must not skip it.
        _ = cancellationToken;
        Interlocked.Increment(ref closeRequestsInProgress);
        Interlocked.Increment(ref blockRequestGeneration);
        try
        {
            if (!await lifecycle.WaitAsync(0, CancellationToken.None))
            {
                await CloseAndDrainContendedOperationAsync();
                return;
            }

            try
            {
                ClearMaintenanceState();
                await EnsureClosedAsync();
            }
            finally
            {
                lifecycle.Release();
            }
        }
        finally
        {
            Interlocked.Increment(ref blockRequestGeneration);
            Interlocked.Decrement(ref closeRequestsInProgress);
        }
    }

    public async Task AllowAsync(StreamLeaseGrant grant, CancellationToken cancellationToken)
    {
        var expectedBlockRequestGeneration = Volatile.Read(ref blockRequestGeneration);
        // Do not queue a new grant ahead of a closer's drain or semaphore-release cleanup.
        if (Volatile.Read(ref closeRequestsInProgress) != 0)
            throw new InvalidOperationException("The stream gate is closing and cannot accept a grant request.");
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref closeRequestsInProgress) != 0 ||
                Volatile.Read(ref blockRequestGeneration) != expectedBlockRequestGeneration)
                throw new InvalidOperationException("The stream gate was blocked while the grant request was waiting.");

            EnsureMaintenanceFenceClear();
            EnsureGrantHistoryLoaded();
            PruneGrantHistory();

            if (maintenanceActive)
                throw new InvalidOperationException("Sunshine is in fenced maintenance and cannot open stream ingress.");

            if (!IsCurrentGrant(grant))
                throw new InvalidOperationException("The stream grant is expired or belongs to another OS boot.");

            var leaseKey = (grant.BootId, grant.RentalId);
            if (lastGrantByLease.TryGetValue(leaseKey, out var lastGrant) && !CanReplace(lastGrant, grant))
                throw new InvalidOperationException("The stream grant is stale or extends a lease without a newer revision.");
            if (currentLeaseKey is { } currentLease && currentLease.BootId == grant.BootId &&
                currentLease.RentalId != grant.RentalId && lastGrantByLease.ContainsKey(leaseKey))
                throw new InvalidOperationException("The stream grant belongs to a rental that was already superseded.");

            if (currentGrant is { } previous)
            {
                if (!CanReplace(previous, grant))
                    throw new InvalidOperationException("The stream grant is stale or changes lease scope without reconciliation.");

                if (await IsOpenAndVerifiedAsync(cancellationToken))
                {
                    EnsureMaintenanceFenceClear();
                    if (!IsCurrentGrant(grant))
                        throw new InvalidOperationException("The stream grant expired while the active gate was being verified.");

                    var expectedGeneration = Volatile.Read(ref gateGeneration);
                    PersistGrant(grant, leaseKey);
                    if (Volatile.Read(ref gateGeneration) != expectedGeneration ||
                        Volatile.Read(ref blockRequestGeneration) != expectedBlockRequestGeneration ||
                        !ReferenceEquals(CurrentGrant, previous) ||
                        !IsCurrentGrant(grant))
                        throw new InvalidOperationException("The active stream grant expired or was closed while its history was being saved.");

                    Volatile.Write(ref currentGrant, grant);
                    closedVerified = false;
                    return;
                }
            }

            await EnsureClosedAsync();
            EnsureMaintenanceFenceClear();
            if (!IsCurrentGrant(grant))
                throw new InvalidOperationException("The stream grant expired while the gate was being prepared.");

            var openingGeneration = Volatile.Read(ref gateGeneration);
            PersistGrant(grant, leaseKey);
            if (Volatile.Read(ref gateGeneration) != openingGeneration ||
                Volatile.Read(ref blockRequestGeneration) != expectedBlockRequestGeneration)
                throw new InvalidOperationException("The stream gate was blocked while the grant history was being saved.");
            Volatile.Write(ref openingGrant, grant);
            if (Volatile.Read(ref gateGeneration) != openingGeneration ||
                Volatile.Read(ref blockRequestGeneration) != expectedBlockRequestGeneration ||
                !IsCurrentGrant(grant) ||
                IsMaintenanceFenceHeld())
                throw new InvalidOperationException("The stream grant expired or was blocked before Sunshine could start.");

            // The previously verified closed state is no longer valid once a start is attempted,
            // including when StartAndWaitAsync throws after partially starting the service.
            closedVerified = false;
            await sunshine.StartAndWaitAsync(cancellationToken);
            EnsureMaintenanceFenceClear();
            if (!await sunshine.IsRunningAsync(cancellationToken))
                throw new IOException("Sunshine did not reach the running state while ingress was blocked.");
            EnsureMaintenanceFenceClear();
            if (Volatile.Read(ref gateGeneration) != openingGeneration ||
                Volatile.Read(ref blockRequestGeneration) != expectedBlockRequestGeneration ||
                !IsCurrentGrant(grant))
                throw new InvalidOperationException("The stream grant expired while Sunshine was starting.");

            await ingress.SetBlockedAsync(false, cancellationToken);
            EnsureMaintenanceFenceClear();
            if (await ingress.IsBlockedAsync(cancellationToken))
                throw new IOException("The Sunshine firewall block remains enabled.");
            EnsureMaintenanceFenceClear();
            if (!await sunshine.IsRunningAsync(cancellationToken))
                throw new IOException("Sunshine stopped while opening stream ingress.");
            if (Volatile.Read(ref gateGeneration) != openingGeneration ||
                Volatile.Read(ref blockRequestGeneration) != expectedBlockRequestGeneration ||
                !IsCurrentGrant(grant) ||
                IsMaintenanceFenceHeld())
                throw new InvalidOperationException("The stream grant expired before ingress was confirmed open.");

            Volatile.Write(ref currentGrant, grant);
            Volatile.Write(ref openingGrant, null);
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
            Interlocked.CompareExchange(ref openingGrant, null, grant);
            lifecycle.Release();
        }
    }

    public async Task<bool> VerifyOpenForLeaseAsync(PairingScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (IsMaintenanceFenceHeld())
                return false;

            var grant = CurrentGrant;
            if (maintenanceActive || grant is null ||
                !string.Equals(grant.RentalId, scope.RentalId, StringComparison.Ordinal) ||
                grant.Revision != scope.Revision ||
                !string.Equals(grant.BootId, scope.BootId, StringComparison.Ordinal) ||
                grant.ExpiresAtUtc != scope.ExpiresAtUtc ||
                grant.DeadlineUptimeMilliseconds != scope.DeadlineUptimeMilliseconds ||
                !IsCurrentGrant(grant))
                return false;

            return await IsOpenAndVerifiedAsync(cancellationToken) &&
                IsCurrentGrant(grant) &&
                !IsMaintenanceFenceHeld();
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task BeginFencedMaintenanceAsync(CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            EnsureMaintenanceFenceClear();
            if (maintenanceActive)
                throw new InvalidOperationException("Fenced Sunshine maintenance is already active.");

            await EnsureClosedAsync();
            EnsureMaintenanceFenceClear();
            maintenanceActive = true;
            maintenanceBootId = boot.BootId;
            maintenanceDeadlineUptimeMilliseconds = checked(boot.UptimeMilliseconds + (long)MaintenanceLimit.TotalMilliseconds);
            closedVerified = false;

            EnsureMaintenanceFenceClear();
            await sunshine.StartAndWaitAsync(cancellationToken);
            EnsureMaintenanceFenceClear();
            if (!await ingress.IsBlockedAsync(cancellationToken))
                throw new IOException("Sunshine ingress became unblocked during fenced maintenance.");
            EnsureMaintenanceFenceClear();
            if (!await sunshine.IsRunningAsync(cancellationToken))
                throw new IOException("Sunshine did not start behind the maintenance firewall block.");
            EnsureMaintenanceFenceClear();
            if (boot.BootId != maintenanceBootId || boot.UptimeMilliseconds >= maintenanceDeadlineUptimeMilliseconds)
                throw new TimeoutException("Fenced Sunshine maintenance expired while starting.");
        }
        catch (Exception beginError)
        {
            ClearMaintenanceState();
            try
            {
                await BlockCoreAsync();
            }
            catch (Exception closeError)
            {
                throw new AggregateException("Fenced Sunshine maintenance failed to start and the gate could not be verified closed.", beginError, closeError);
            }

            throw;
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task<bool> EndFencedMaintenanceAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        await lifecycle.WaitAsync(CancellationToken.None);
        try
        {
            if (!maintenanceActive)
            {
                await EnsureClosedAsync();
                return false;
            }

            ClearMaintenanceState();
            await EnsureClosedAsync();
            return true;
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
        {
            if (IsMaintenanceFenceHeld())
            {
                await CloseAndDrainContendedOperationAsync();
                return;
            }

            var grant = CurrentGrant;
            if (grant is not null && !IsCurrentGrant(grant) &&
                ReferenceEquals(Interlocked.CompareExchange(ref currentGrant, null, grant), grant))
            {
                await CloseAndDrainContendedOperationAsync();
            }
            else
            {
                var opening = Volatile.Read(ref openingGrant);
                if (opening is not null && !IsCurrentGrant(opening) &&
                    ReferenceEquals(Interlocked.CompareExchange(ref openingGrant, null, opening), opening))
                    await CloseAndDrainContendedOperationAsync();
            }
            return;
        }

        try
        {
            if (IsMaintenanceFenceHeld())
            {
                ClearMaintenanceState();
                await BlockCoreAsync();
                return;
            }

            if (maintenanceActive)
            {
                if (maintenanceBootId != boot.BootId || boot.UptimeMilliseconds >= maintenanceDeadlineUptimeMilliseconds)
                {
                    ClearMaintenanceState();
                    await BlockCoreAsync();
                    return;
                }

                var maintenanceBlocked = await ingress.IsBlockedAsync(cancellationToken);
                var maintenanceRunning = await sunshine.IsRunningAsync(cancellationToken);
                if (!maintenanceBlocked || !maintenanceRunning)
                {
                    ClearMaintenanceState();
                    await BlockCoreAsync();
                    throw new InvalidOperationException("Fenced Sunshine maintenance state changed unexpectedly; the gate was closed.");
                }

                return;
            }

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
        if (IsMaintenanceFenceHeld())
            return false;

        var blocked = await ingress.IsBlockedAsync(cancellationToken);
        if (IsMaintenanceFenceHeld())
            return false;
        var running = await sunshine.IsRunningAsync(cancellationToken);
        return !IsMaintenanceFenceHeld() && !blocked && running;
    }

    private bool IsMaintenanceFenceHeld()
    {
        try
        {
            var snapshot = maintenanceFence.Read();
            return !snapshot.IsKnown || snapshot.IsHeld;
        }
        catch
        {
            return true;
        }
    }

    private void EnsureMaintenanceFenceClear()
    {
        if (IsMaintenanceFenceHeld())
            throw new InvalidOperationException("The durable Agent maintenance fence is held or unreadable.");
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

    private void EnsureGrantHistoryLoaded()
    {
        if (grantHistoryLoaded) return;

        var snapshot = historyStore.Load()
            ?? throw new InvalidDataException("The Stream Guard grant history store returned no history.");
        if (snapshot.Grants is null)
            throw new InvalidDataException("The Stream Guard grant history contains no grant collection.");

        foreach (var grant in snapshot.Grants)
        {
            if (grant is null || string.IsNullOrWhiteSpace(grant.BootId) || string.IsNullOrWhiteSpace(grant.RentalId) ||
                grant.Revision <= 0 || grant.DeadlineUptimeMilliseconds <= 0 || grant.ExpiresAtUtc == default)
                throw new InvalidDataException("The Stream Guard grant history contains an invalid lease entry.");

            if (!lastGrantByLease.TryAdd((grant.BootId, grant.RentalId), grant))
                throw new InvalidDataException("The Stream Guard grant history contains duplicate lease entries.");
        }

        if (snapshot.CurrentLease is { } currentLease)
        {
            if (string.IsNullOrWhiteSpace(currentLease.BootId) || string.IsNullOrWhiteSpace(currentLease.RentalId) ||
                currentLease.BootId.Length > 128 || currentLease.RentalId.Length > 128 ||
                !lastGrantByLease.ContainsKey((currentLease.BootId, currentLease.RentalId)))
                throw new InvalidDataException("The Stream Guard grant history contains an invalid current lease key.");
        }

        currentLeaseKey = snapshot.CurrentLease;
        grantHistoryLoaded = true;
    }

    private void PruneGrantHistory()
    {
        foreach (var key in lastGrantByLease
                     .Where(entry => entry.Key.BootId != boot.BootId)
                     .Select(entry => entry.Key)
                     .ToArray())
            lastGrantByLease.Remove(key);

        if (currentLeaseKey?.BootId != boot.BootId)
            currentLeaseKey = null;
    }

    private void PersistGrant(StreamLeaseGrant grant, (string BootId, string RentalId) key)
    {
        var hadPrevious = lastGrantByLease.TryGetValue(key, out var previous);
        var previousCurrentLease = currentLeaseKey;
        lastGrantByLease[key] = grant;
        currentLeaseKey = new(key.BootId, key.RentalId);
        try
        {
            historyStore.Save(new(lastGrantByLease.Values.ToArray(), currentLeaseKey));
        }
        catch
        {
            if (hadPrevious) lastGrantByLease[key] = previous!;
            else lastGrantByLease.Remove(key);
            currentLeaseKey = previousCurrentLease;
            throw;
        }
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

    private async Task CloseAndDrainContendedOperationAsync()
    {
        Interlocked.Increment(ref closeRequestsInProgress);
        try
        {
            Interlocked.Increment(ref blockRequestGeneration);
            Interlocked.Increment(ref gateGeneration);
            Volatile.Write(ref currentGrant, null);
            Volatile.Write(ref openingGrant, null);

            Exception? immediateCloseError = null;
            try
            {
                await BlockCoreAsync();
            }
            catch (Exception ex)
            {
                immediateCloseError = ex;
            }

            // Containment happens immediately above; wait for the interrupted operation to
            // unwind before reporting closure so a delayed firewall allow cannot win afterward.
            await lifecycle.WaitAsync(CancellationToken.None);
            try
            {
                ClearMaintenanceState();
                try
                {
                    await EnsureClosedAsync();
                }
                catch (Exception finalCloseError) when (immediateCloseError is not null)
                {
                    throw new AggregateException(
                        "The immediate stream close failed and the gate could not be verified closed after the in-flight operation drained.",
                        immediateCloseError,
                        finalCloseError);
                }
            }
            finally
            {
                lifecycle.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref closeRequestsInProgress);
        }
    }

    private async Task BlockCoreAsync()
    {
        await closeSerial.WaitAsync(CancellationToken.None);
        try
        {
            Interlocked.Increment(ref gateGeneration);
            ClearMaintenanceState();
            Volatile.Write(ref currentGrant, null);
            Volatile.Write(ref openingGrant, null);
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
        finally
        {
            closeSerial.Release();
        }
    }

    private void ClearMaintenanceState()
    {
        maintenanceActive = false;
        maintenanceBootId = null;
        maintenanceDeadlineUptimeMilliseconds = 0;
    }
}
