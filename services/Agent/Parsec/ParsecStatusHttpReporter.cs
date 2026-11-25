using System.Net;
using System.Text;
using System.Text.Json;
using KamenRaider.Agent.Identity;
using KamenRaider.Contracts;
using Microsoft.Extensions.Logging;

namespace KamenRaider.Agent.Parsec;

/// <summary>
/// Signed HTTPS delivery of Parsec attempt statuses to the Controller, the way 0jay-v2's
/// <c>parsecStatusHTTPReporter</c> posted to <c>/api/agent/parsec-status</c>.
/// </summary>
/// <remarks>
/// The request is authenticated exactly like <see cref="KamenRaider.Agent.Sync.SyncClient"/>:
/// POST <c>/agent/parsec-status</c> with the JSON body, <c>X-Agent-Id</c>,
/// <c>X-Agent-Timestamp</c>, <c>X-Agent-Nonce</c> and <c>X-Agent-Signature</c> over the exact
/// body bytes. Each retry re-signs with a fresh nonce, because the Controller treats a reused
/// nonce as a replay.
///
/// The delivery budget is six seconds per attempt and three bounded attempts; every failure is
/// swallowed and reported as <c>false</c> so a network problem can never throw into an attempt.
/// The Controller acknowledges an accepted report with <c>204 No Content</c>, so any 2xx status is
/// treated as acceptance (an optional <c>{"accepted":false}</c> body still rejects a stale
/// attempt). Nothing about the credential is ever placed in the body: the report carries only the
/// attempt id, the boot key id, the state, the error code and the sanitised message.
/// </remarks>
public sealed class ParsecStatusHttpReporter : IParsecStatusReporter
{
    /// <summary>The signed Controller route, relative to the configured Controller URL.</summary>
    public const string Route = "/agent/parsec-status";

    /// <summary>0jay-parsec's six-second status-report context.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Bounded retries: one initial attempt plus two.</summary>
    public const int MaximumAttempts = 3;

    private const int MaximumResponseBytes = 1024;
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500)
    };

    private readonly HttpClient http;
    private readonly Uri controller;
    private readonly IAgentRequestSigner identity;
    private readonly TimeProvider time;
    private readonly ILogger<ParsecStatusHttpReporter>? logger;

    public ParsecStatusHttpReporter(
        HttpClient http,
        Uri controller,
        IAgentRequestSigner identity,
        TimeProvider? time = null,
        ILogger<ParsecStatusHttpReporter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(identity);
        if (!controller.IsAbsoluteUri || controller.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The Controller URL must be an absolute HTTPS URI.", nameof(controller));
        this.http = http;
        this.controller = controller;
        this.identity = identity;
        this.time = time ?? TimeProvider.System;
        this.logger = logger;
    }

    /// <summary>Posts one status. Never throws; <c>false</c> means the Controller did not accept it.</summary>
    public async Task<bool> ReportAsync(ParsecStatusReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        Uri endpoint;
        try
        {
            endpoint = new Uri(controller, Route);
        }
        catch (UriFormatException)
        {
            return false;
        }

        string body;
        try
        {
            body = JsonSerializer.Serialize(report, ParsecJsonContext.Default.ParsecStatusReport);
        }
        catch (NotSupportedException)
        {
            return false;
        }

        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
                return false;
            try
            {
                var outcome = await SendOnceAsync(endpoint, body, cancellationToken).ConfigureAwait(false);
                if (outcome is not null)
                    return outcome.Value;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                // The per-attempt budget expired; a bounded retry may still succeed.
            }
            catch (HttpRequestException exception)
            {
                logger?.LogDebug("Parsec status report did not reach the Controller ({ErrorType}).", exception.GetType().Name);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger?.LogDebug("Parsec status report failed ({ErrorType}).", exception.GetType().Name);
                return false;
            }

            if (attempt + 1 >= MaximumAttempts)
                break;
            try
            {
                await Task.Delay(RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)], time, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// One signed POST. <c>true</c>/<c>false</c> are final answers from the Controller
    /// (accepted / no longer current); <c>null</c> means a transient failure worth retrying.
    /// </summary>
    /// <remarks>
    /// The Controller's <c>/agent/parsec-status</c> route returns <c>204 No Content</c> on
    /// success, so the HTTP status is the acceptance signal. A 2xx response is accepted even
    /// when the body is empty or absent; a JSON body may still veto it with
    /// <c>{"accepted":false}</c> (an alternate Controller rejecting a stale attempt). 4xx is a
    /// final rejection and 408/429/5xx are transient.
    /// </remarks>
    private async Task<bool?> SendOnceAsync(Uri endpoint, string body, CancellationToken cancellationToken)
    {
        var timestamp = time.GetUtcNow();
        var nonce = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Agent-Id", identity.MachineId);
        request.Headers.Add("X-Agent-Timestamp", timestamp.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Agent-Nonce", nonce);
        request.Headers.Add("X-Agent-Signature", identity.Sign("POST", Route, timestamp, nonce, body));
        request.Headers.Accept.ParseAdd("application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AttemptTimeout);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
            (int)response.StatusCode >= 500)
            return null;
        if ((int)response.StatusCode is < 200 or >= 300)
            return false;

        var content = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
        // The Controller answers 204 No Content; an absent/oversized body on a 2xx is still an
        // acceptance. Only an explicit JSON `accepted` flag may veto it.
        if (content is null || content.Length == 0)
            return true;
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("accepted", out var accepted))
                return true;
            return accepted.ValueKind is JsonValueKind.True;
        }
        catch (JsonException)
        {
            // A 2xx status is authoritative even when an alternate Controller's body is not JSON.
            return true;
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaximumResponseBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            length += read;
        }
        if (length > MaximumResponseBytes)
            return null;
        return buffer.AsSpan(0, length).ToArray();
    }
}
