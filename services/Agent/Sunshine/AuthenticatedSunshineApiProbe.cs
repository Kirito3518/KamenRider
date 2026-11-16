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
    internal static WindowsSunshineCredentialStore CreateCredentialStore(string dataDirectory) => new(
        dataDirectory,
        WindowsSunshineCredentialStore.ResolveAgentServiceSid(),
        new WindowsDpapiMachineSecretProtector());

    internal static async Task<AuthenticatedSunshineApiProbeResult> ReadClientsAsync(
        WindowsSunshineCredentialStore credentialStore,
        int port,
        CancellationToken cancellationToken,
        HttpMessageHandler? transport = null)
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
