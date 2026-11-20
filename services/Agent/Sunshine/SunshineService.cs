using System.Net;
using KamenRaider.Agent.Configuration;
using KamenRaider.Contracts;
using Microsoft.Extensions.Options;

namespace KamenRaider.Agent.Sunshine;

/// <summary>
/// Local stream cleanup used by lease end/expiry/shutdown and boot reconciliation.
/// It clears Sunshine's trusted clients; no firewall or Windows service is touched.
/// </summary>
public interface IStreamCleanup
{
    Task<bool> ClearAsync(CancellationToken ct);
}

public sealed record SunshineAdminResult(bool Ok, string? ErrorCode);

public interface ISunshineAdmin
{
    Task<SunshineAdminResult> RestartAsync(bool force, CancellationToken ct);
    Task<SunshineAdminResult> UnpairAsync(bool force, CancellationToken ct);
}

/// <summary>
/// Owns the local Sunshine REST client for pairing, cleanup and admin maintenance.
/// The Sunshine process itself is left to its own supervisor and is never stopped here
/// (0jay-style). The cached client is rebuilt when the configured port or the applied
/// fleet credential revision changes.
/// </summary>
public sealed class SunshineService(
    Func<ConfigurationDto?> configuration,
    Func<long> appliedCredentialRevision,
    Func<SunshineManagementCredential?> credentialLoader,
    Func<bool> streamReady,
    Func<PairingScope?> scope,
    ISunshineLiveness liveness,
    IOptions<AgentOptions> options,
    TimeProvider time,
    ILogger<SunshineService> logger) : ISunshinePairingSession, IStreamCleanup, ISunshineAdmin, IDisposable
{
    private static readonly TimeSpan RestartWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private readonly object sync = new();
    private readonly SemaphoreSlim cleanupSerial = new(1, 1);
    private SunshineClient? client;
    private PairingSession? session;
    private int clientPort;
    private long clientRevision;
    private DateTimeOffset lastCredentialWarning = DateTimeOffset.MinValue;
    private bool disposed;

    public async Task<PairingOffer?> CreateOfferAsync(CancellationToken ct)
    {
        var current = GetSession();
        return current is null ? null : await current.CreateOfferAsync(ct);
    }

    public Task<string[]> ListAsync(CancellationToken ct)
    {
        var current = GetSession();
        return current is null ? Task.FromResult(Array.Empty<string>()) : current.ListAsync(ct);
    }

    public Task<CommandResult> ExecuteAsync(CommandEnvelope command, CancellationToken ct)
    {
        var current = GetSession();
        return current is null
            ? Task.FromResult(new CommandResult(command.CommandId, "failed", command.LeaseRevision, "PairingUnavailable"))
            : current.ExecuteAsync(command, ct);
    }

    public async Task<bool> ClearAsync(CancellationToken ct)
    {
        await cleanupSerial.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(CleanupTimeout);
                    var current = GetSession();
                    if (current is not null && await current.ClearAsync(timeout.Token))
                        return true;
                    var direct = GetClient();
                    if (direct is not null)
                    {
                        await direct.ClearAsync(timeout.Token);
                        return true;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Sunshine pairing cleanup attempt {Attempt} failed ({ErrorType}).", attempt + 1, ex.GetType().Name);
                }

                if (attempt == 0)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                }
            }

            return false;
        }
        finally
        {
            cleanupSerial.Release();
        }
    }

    public async Task<SunshineAdminResult> RestartAsync(bool force, CancellationToken ct)
    {
        _ = force; // The admin confirms on screen; Sunshine's own supervisor rebuilds the process.
        var current = GetClient();
        if (current is null) return new(false, "SUNSHINE_PORT_INVALID");
        try
        {
            await current.RestartProcessAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Sunshine restart request failed ({ErrorType}).", ex.GetType().Name);
            return new(false, "RESTART_FAILED");
        }

        var deadline = time.GetUtcNow().Add(RestartWait);
        while (time.GetUtcNow() < deadline)
        {
            var snapshot = await liveness.ProbeOnceAsync(ct);
            if (snapshot.Ready) return new(true, null);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
        }

        // Accepted and sent; the heartbeat will show the result when Sunshine answers again.
        return new(true, "NOT_BACK_YET");
    }

    public async Task<SunshineAdminResult> UnpairAsync(bool force, CancellationToken ct)
    {
        _ = force; // The admin screen has already confirmed; an offline/paired machine is the case this is for.
        var current = GetClient();
        if (current is null) return new(false, "SUNSHINE_PORT_INVALID");
        try
        {
            await current.ClearAsync(ct);
            return new(true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Sunshine unpair request failed ({ErrorType}).", ex.GetType().Name);
            return new(false, "UNPAIR_FAILED");
        }
    }

    private PairingSession? GetSession()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var port = CurrentPort();
            if (port is < 1 or > 65535) return null;
            var revision = CurrentRevision();
            if (session is not null && clientPort == port && clientRevision == revision) return session;

            client?.Dispose();
            client = null;
            session = null;
            clientPort = 0;
            clientRevision = 0;

            var credential = LoadCredential();
            var networkCredential = credential is null ? null : new NetworkCredential(credential.UserName, credential.Password);
            try
            {
                client = new SunshineClient(new Uri($"https://127.0.0.1:{port}"), networkCredential);
                session = new PairingSession(client, streamReady, scope, time);
                clientPort = port;
                clientRevision = revision;
                return session;
            }
            catch (ArgumentException ex)
            {
                client?.Dispose();
                client = null;
                logger.LogWarning("Sunshine pairing endpoint or credentials are invalid ({ErrorType}).", ex.GetType().Name);
                return null;
            }
        }
    }

    private SunshineClient? GetClient()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var port = CurrentPort();
            if (port is < 1 or > 65535) return null;
            var revision = CurrentRevision();
            if (client is not null && clientPort == port && clientRevision == revision) return client;
            client?.Dispose();
            client = null;
            session = null;
            clientPort = 0;
            clientRevision = 0;
            var credential = LoadCredential();
            var networkCredential = credential is null ? null : new NetworkCredential(credential.UserName, credential.Password);
            try
            {
                client = new SunshineClient(new Uri($"https://127.0.0.1:{port}"), networkCredential);
                clientPort = port;
                clientRevision = revision;
                return client;
            }
            catch (ArgumentException ex)
            {
                client?.Dispose();
                client = null;
                logger.LogWarning("Sunshine endpoint or credentials are invalid ({ErrorType}).", ex.GetType().Name);
                return null;
            }
        }
    }

    private int CurrentPort()
    {
        try
        {
            return configuration()?.SunshinePort ?? options.Value.SunshinePort;
        }
        catch (Exception)
        {
            return options.Value.SunshinePort;
        }
    }

    private long CurrentRevision()
    {
        try
        {
            return appliedCredentialRevision();
        }
        catch (Exception)
        {
            return clientRevision;
        }
    }

    private SunshineManagementCredential? LoadCredential()
    {
        try
        {
            return credentialLoader();
        }
        catch (Exception ex)
        {
            // Missing fleet credentials are expected: loopback Sunshine needs none.
            var now = time.GetUtcNow();
            if (now - lastCredentialWarning >= TimeSpan.FromMinutes(1))
            {
                logger.LogWarning("Sunshine fleet credential is unavailable ({ErrorType}); using loopback access.", ex.GetType().Name);
                lastCredentialWarning = now;
            }
            return null;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            session = null;
            client?.Dispose();
            client = null;
        }

        cleanupSerial.Dispose();
    }
}
