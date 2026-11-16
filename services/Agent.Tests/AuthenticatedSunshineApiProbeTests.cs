using System.Net;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using KamenRaider.Agent.Sunshine;

namespace Agent.Tests;

[SupportedOSPlatform("windows")]
public sealed class AuthenticatedSunshineApiProbeTests
{
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kr-sunshine-probe-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class TestProtector : ISunshineCredentialProtector
    {
        private static readonly byte[] Prefix = [0x4B, 0x52, 0x50, 0x31];

        public byte[] Protect(ReadOnlySpan<byte> plaintext) => [.. Prefix, .. plaintext];

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
        {
            if (!ciphertext.StartsWith(Prefix)) throw new InvalidDataException("Invalid test ciphertext.");
            return ciphertext[Prefix.Length..].ToArray();
        }
    }

    private sealed class RecordingTransport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task ReadOnlyProbeUsesStoredCredentialsForTheClientListRoute()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var store = NewStore(directory.Path);
        store.Save(new("probe-user", "fixture-password"));
        using var transport = new RecordingTransport(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"true\",\"named_certs\":[]}")
        });

        var result = await AuthenticatedSunshineApiProbe.ReadClientsAsync(store, 47990, default, transport);

        Assert.Equal("RequestCompleted", result.Status);
        Assert.Equal(200, result.HttpStatus);
        Assert.False(result.TlsValidated); // The test handler does not perform TLS.
        var request = Assert.Single(transport.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://localhost:47990/api/clients/list", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("probe-user:fixture-password")), request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task ReadOnlyProbeReportsAuthenticationRejectionWithoutLeakingCredentialOrResponseBody()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var store = NewStore(directory.Path);
        store.Save(new("probe-user", "fixture-password"));
        using var transport = new RecordingTransport(_ => new(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("server says fixture-password is wrong")
        });

        var result = await AuthenticatedSunshineApiProbe.ReadClientsAsync(store, 47990, default, transport);

        Assert.Equal("ProbeFailed", result.Status);
        Assert.Equal(401, result.HttpStatus);
        Assert.False(result.TlsValidated); // The test handler does not perform TLS.
        Assert.Equal("HttpStatusRejected", result.FailureCategory);
        var output = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("fixture-password", output, StringComparison.Ordinal);
        Assert.DoesNotContain("server says", output, StringComparison.Ordinal);
        var request = Assert.Single(transport.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("probe-user:fixture-password")), request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task ReadOnlyProbeReportsMissingCredentialWithoutMakingARequest()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var store = NewStore(directory.Path);
        using var transport = new RecordingTransport(_ => throw new InvalidOperationException("Request must not be sent."));

        var result = await AuthenticatedSunshineApiProbe.ReadClientsAsync(store, 47990, default, transport);

        Assert.Equal("ProbeFailed", result.Status);
        Assert.Null(result.HttpStatus);
        Assert.False(result.TlsValidated);
        Assert.Equal("CredentialsUnavailable", result.FailureCategory);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task ReadOnlyProbeSanitizesTransportFailureWithoutReportingAnHttpResponse()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var store = NewStore(directory.Path);
        store.Save(new("probe-user", "fixture-password"));
        using var transport = new RecordingTransport(_ => throw new HttpRequestException("private transport detail"));

        var result = await AuthenticatedSunshineApiProbe.ReadClientsAsync(store, 47990, default, transport);

        Assert.Equal("ProbeFailed", result.Status);
        Assert.Null(result.HttpStatus);
        Assert.False(result.TlsValidated);
        Assert.Equal("HttpsRequestFailed", result.FailureCategory);
        var output = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("fixture-password", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private transport detail", output, StringComparison.Ordinal);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task ReadOnlyProbeReportsNestedTlsFailureWithoutStatusOrSensitiveDetails()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var store = NewStore(directory.Path);
        store.Save(new("probe-user", "fixture-password"));
        using var transport = new RecordingTransport(_ => throw new HttpRequestException(
            "private request detail",
            new AuthenticationException("private TLS detail")));

        var result = await AuthenticatedSunshineApiProbe.ReadClientsAsync(store, 47990, default, transport);

        Assert.Equal("ProbeFailed", result.Status);
        Assert.Null(result.HttpStatus);
        Assert.False(result.TlsValidated);
        Assert.Equal("TlsValidationFailed", result.FailureCategory);
        var output = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("fixture-password", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private request detail", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private TLS detail", output, StringComparison.Ordinal);
        Assert.Single(transport.Requests);
    }

    private static WindowsSunshineCredentialStore NewStore(string dataDirectory)
    {
        var currentUser = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Windows user SID is unavailable.");
        return new(dataDirectory, currentUser, new TestProtector());
    }
}
