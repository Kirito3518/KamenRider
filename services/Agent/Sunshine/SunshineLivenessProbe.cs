using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace KamenRaider.Agent.Sunshine;

/// <summary>
/// One local probe of the two Sunshine endpoints a renter needs. The web UI probe is
/// loopback-only and tolerates Sunshine's self-signed certificate; a request can never
/// leave the machine because the URI host is fixed to 127.0.0.1.
/// </summary>
public sealed record SunshineProbeResult(bool StreamAnswers, bool WebUiAnswers, string? Reason)
{
    public bool Ready => StreamAnswers && WebUiAnswers;
}

public interface ISunshineLivenessProbe
{
    Task<SunshineProbeResult> ProbeAsync(int moonlightPort, int sunshinePort, CancellationToken ct);
}

public sealed class SunshineLivenessProbe : ISunshineLivenessProbe, IDisposable
{
    public const string ReasonReady = "sunshine-ready";
    public const string ReasonStreamSilent = "SUNSHINE_STREAM_PORT_SILENT";
    public const string ReasonWebUiSilent = "SUNSHINE_UNREACHABLE";
    public const string ReasonWebUiUnauthorized = "SUNSHINE_CREDENTIALS_REJECTED";
    public const string ReasonWebUiNotSetUp = "SUNSHINE_WEB_UI_NOT_SET_UP";
    public const string ReasonInvalidPort = "SUNSHINE_PORT_INVALID";

    private readonly HttpClient streamHttp;
    private readonly HttpClient webHttp;

    public SunshineLivenessProbe(TimeSpan? timeout = null, HttpMessageHandler? streamHandler = null, HttpMessageHandler? webHandler = null)
    {
        streamHttp = new HttpClient(streamHandler ?? new HttpClientHandler())
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
            MaxResponseContentBufferSize = 65536
        };
        webHttp = new HttpClient(webHandler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                request.RequestUri is { } uri &&
                (uri.Host == "localhost" ||
                 IPAddress.TryParse(uri.Host.Trim('[', ']'), out var host) && IPAddress.IsLoopback(host))
        })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
            MaxResponseContentBufferSize = 65536
        };
    }

    public async Task<SunshineProbeResult> ProbeAsync(int moonlightPort, int sunshinePort, CancellationToken ct)
    {
        if (moonlightPort is < 1 or > 65535 || sunshinePort is < 1 or > 65535)
            return new(false, false, ReasonInvalidPort);

        var streamTask = ProbeStreamPortAsync(moonlightPort, ct);
        var webTask = ProbeWebUiAsync(sunshinePort, ct);
        await Task.WhenAll(streamTask, webTask).ConfigureAwait(false);

        var stream = streamTask.Result;
        var web = webTask.Result;
        if (stream && web.Answers) return new(true, true, ReasonReady);
        var reason = !stream ? ReasonStreamSilent : web.Reason;
        return new(stream, web.Answers, reason);
    }

    private async Task<bool> ProbeStreamPortAsync(int port, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/serverinfo");
            using var response = await streamHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return false;
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Length > 65536) return false;
            // GameStream /serverinfo is Sunshine's own XML document. Something else on the
            // port (or a web UI that merely answers) is not proof that the stream side is up.
            return body.Contains("<serverinfo", StringComparison.OrdinalIgnoreCase) ||
                   body.Contains("<root", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            return false;
        }
    }

    private async Task<WebProbe> ProbeWebUiAsync(int port, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}/api/apps");
            using var response = await webHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(false, ReasonWebUiUnauthorized);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location?.ToString() ?? "";
                return new(false, location.Contains("/welcome", StringComparison.OrdinalIgnoreCase)
                    ? ReasonWebUiNotSetUp
                    : ReasonWebUiSilent);
            }
            if (!response.IsSuccessStatusCode)
                return new(false, ReasonWebUiSilent);
            return new(true, ReasonReady);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            return new(false, ReasonWebUiSilent);
        }
    }

    private sealed record WebProbe(bool Answers, string? Reason);

    public void Dispose()
    {
        streamHttp.Dispose();
        webHttp.Dispose();
    }
}
