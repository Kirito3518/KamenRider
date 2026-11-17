using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using KamenRaider.Contracts;

namespace KamenRaider.Agent.Sunshine;

// Loads the local protected credential only when a ready lease needs pairing. Missing
// credentials do not prevent Agent enrollment or heartbeat; pairing remains unavailable.
// The cached session is invalidated when the configured port or the applied fleet credential
// revision changes, so a rotated fleet credential takes effect without a process restart.
[SupportedOSPlatform("windows")]
public sealed class LazySunshinePairingSession(
    WindowsSunshineCredentialStore credentialStore,
    Func<int> sunshinePort,
    IPairingFence fence,
    Func<PairingScope?> scope,
    TimeProvider time,
    ILogger<LazySunshinePairingSession> logger,
    Func<long>? appliedCredentialRevision = null,
    Func<SunshineManagementCredential?>? credentialLoader = null) : ISunshinePairingSession, IDisposable
{
    private readonly object sync = new();
    private readonly Func<long> currentAppliedRevision = appliedCredentialRevision ?? (static () => 0);
    private readonly Func<SunshineManagementCredential?> loadCredential = credentialLoader ?? (() => credentialStore.Load());
    private PairingSession? session;
    private SunshineClient? client;
    private int sessionPort;
    private long sessionRevision;
    private DateTimeOffset lastWarning = DateTimeOffset.MinValue;
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

    public Task<bool> ClearAsync(CancellationToken ct)
    {
        var current = GetSession();
        return current is null ? Task.FromResult(false) : current.ClearAsync(ct);
    }

    private PairingSession? GetSession()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var port = sunshinePort();
            var revision = CurrentAppliedRevision();
            if (session is not null && sessionPort == port && sessionRevision == revision) return session;

            client?.Dispose();
            client = null;
            session = null;
            sessionPort = 0;
            sessionRevision = 0;
            if (port is < 1 or > 65535) return null;

            SunshineManagementCredential? credential;
            try
            {
                credential = loadCredential();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or CryptographicException or System.ComponentModel.Win32Exception or JsonException or ArgumentException)
            {
                var now = time.GetUtcNow();
                if (now - lastWarning >= TimeSpan.FromMinutes(1))
                {
                    logger.LogWarning("Sunshine pairing credentials are unavailable ({ErrorType}).", ex.GetType().Name);
                    lastWarning = now;
                }
                return null;
            }

            if (credential is null) return null;
            try
            {
                client = new SunshineClient(new Uri($"https://localhost:{port}"), new NetworkCredential(credential.UserName, credential.Password));
                session = new PairingSession(client, fence, scope, time);
                sessionPort = port;
                sessionRevision = revision;
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

    private long CurrentAppliedRevision()
    {
        try
        {
            return currentAppliedRevision();
        }
        catch (Exception)
        {
            // Keep the cached session when the revision cannot be read; the credential file itself
            // is untouched and a later call can observe the rotation.
            return sessionRevision;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            client?.Dispose();
            client = null;
            session = null;
        }
    }
}
