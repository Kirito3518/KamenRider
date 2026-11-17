using KamenRaider.Agent.Identity;
using Microsoft.Extensions.Options;

namespace KamenRaider.Agent.Sunshine;

/// <summary>
/// Hosted worker that waits for a successful sync response reporting the fleet Sunshine management
/// credential revision, then fetches the signed delivery over HTTPS and stores it with the DPAPI
/// machine credential store. A manually provisioned credential with no applied-revision file counts as
/// revision 0, so the configured fleet credential replaces it on the first successful sync; when the
/// fleet revision is 0 this worker does nothing. Rotation takes effect without a restart because
/// <see cref="LazySunshinePairingSession"/> reloads its cached session when the applied revision changes.
/// This worker never runs Sunshine.exe --creds and never edits Sunshine's own configuration.
/// </summary>
public sealed class SunshineCredentialWorker(
    IOptions<AgentOptions> options,
    SunshineCredentialSyncSignal signal,
    SunshineAppliedCredentialStore appliedStore,
    WindowsSunshineCredentialStore credentialStore,
    ILoggerFactory loggers,
    TimeProvider time,
    ILogger<SunshineCredentialWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var identity = DeviceIdentity.LoadOrCreate(settings.DataDirectory);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var updater = new SunshineCredentialUpdater(
            new SunshineCredentialClient(http, identity),
            new Uri(settings.ControllerUrl),
            appliedStore,
            credentialStore,
            time,
            loggers.CreateLogger<SunshineCredentialUpdater>());

        while (!stoppingToken.IsCancellationRequested)
        {
            long fleetRevision;
            try
            {
                fleetRevision = await signal.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await updater.TryUpdateAsync(fleetRevision, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Credential delivery must never take down the Agent service; retry on the next sync.
                logger.LogWarning("Sunshine credential update failed ({ErrorType}); retrying next sync.", ex.GetType().Name);
            }
        }
    }
}
