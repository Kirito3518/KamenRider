using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Authentication;

namespace KamenRaider.Agent.Sunshine;

internal sealed record AuthenticatedSunshineApiProbeResult(
    string Status,
    int? HttpStatus,
    bool TlsValidated,
    string? FailureCategory);

[SupportedOSPlatform("windows")]
internal static class AuthenticatedSunshineApiProbe
{
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(250);

    internal static WindowsSunshineCredentialStore CreateCredentialStore(string dataDirectory) => new(
        dataDirectory,
        WindowsSunshineCredentialStore.ResolveAgentServiceSid(),
        new WindowsDpapiMachineSecretProtector());

    // The management endpoint is served by Sunshine itself. During fenced maintenance the
    // service can report Running before the web server has bound its HTTPS port, so an
    // optional readiness budget retries only connection-level failures. TLS, authentication
    // and protocol failures are deterministic and are reported from the first attempt.
    internal static async Task<AuthenticatedSunshineApiProbeResult> ReadClientsAsync(
        WindowsSunshineCredentialStore credentialStore,
        int port,
        CancellationToken cancellationToken,
        HttpMessageHandler? transport = null,
        TimeSpan? readinessTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        if (port is < 1 or > 65535)
            return Failed("ProbeConfigurationInvalid");

        SunshineManagementCredential? credential;
        try
        {
            credential = credentialStore.Load();
        }
        catch (Exception)
        {
            return Failed("CredentialsUnavailable");
        }

        if (credential is null)
            return Failed("CredentialsUnavailable");

        var readinessBudget = readinessTimeout is { } timeout && timeout > TimeSpan.Zero
            ? timeout
            : (TimeSpan?)null;
        var timer = readinessBudget is null ? null : Stopwatch.StartNew();

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
                return Failed("Cancelled");
            var result = await AttemptAsync(credential, port, cancellationToken, transport);
            if (result.Status == "RequestCompleted" || timer is null || !IsRetryable(result.FailureCategory))
                return result;

            var remaining = readinessBudget!.Value - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return result;

            var delay = remaining < ReadinessPollInterval ? remaining : ReadinessPollInterval;
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failed("Cancelled");
            }
        }
    }

    private static async Task<AuthenticatedSunshineApiProbeResult> AttemptAsync(
        SunshineManagementCredential credential,
        int port,
        CancellationToken cancellationToken,
        HttpMessageHandler? transport)
    {
        var tlsValidatedByDefaultHandler = transport is null;
        try
        {
            using var client = new SunshineClient(
                new Uri($"https://localhost:{port}"),
                new NetworkCredential(credential.UserName, credential.Password),
                transport);

            // This performs only the authenticated GET /api/clients/list. Its result is
            // intentionally discarded so Sunshine client identifiers never reach output.
            _ = await client.PendingIdsAsync(cancellationToken);
            return new("RequestCompleted", 200, tlsValidatedByDefaultHandler, null);
        }
        catch (Exception exception)
        {
            return Failure(exception, tlsValidatedByDefaultHandler);
        }
    }

    // Only a host that is not accepting connections yet is worth retrying. A TLS, HTTP
    // status or malformed-response result does not change by waiting.
    private static bool IsRetryable(string? failureCategory) =>
        failureCategory is "HttpsRequestFailed";

    private static AuthenticatedSunshineApiProbeResult Failure(Exception exception, bool tlsValidatedByDefaultHandler)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is AuthenticationException)
                return Failed("TlsValidationFailed");
        }

        return exception switch
        {
            HttpRequestException { StatusCode: not null } response => new(
                "ProbeFailed",
                (int)response.StatusCode.Value,
                tlsValidatedByDefaultHandler,
                "HttpStatusRejected"),
            HttpRequestException => Failed("HttpsRequestFailed"),
            TaskCanceledException => Failed("TimedOut"),
            OperationCanceledException => Failed("Cancelled"),
            InvalidDataException => new("ProbeFailed", 200, tlsValidatedByDefaultHandler, "InvalidSunshineResponse"),
            _ => Failed("SunshineRequestFailed")
        };
    }

    private static AuthenticatedSunshineApiProbeResult Failed(string category) =>
        new("ProbeFailed", null, false, category);
}
