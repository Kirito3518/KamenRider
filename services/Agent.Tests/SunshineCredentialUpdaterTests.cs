using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using KamenRaider.Agent.Identity;
using KamenRaider.Agent.Sunshine;
using KamenRaider.Contracts;
using KamenRaider.Controller.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent.Tests;

public class SunshineCredentialUpdaterTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AppliesDeliveryAndPersistsTheRevision()
    {
        using var fixture = new Fixture();

        var applied = await fixture.Updater.TryUpdateAsync(1, CancellationToken.None);

        Assert.True(applied);
        Assert.True(fixture.Handler.SignatureVerified);
        Assert.Equal([0L], fixture.Handler.KnownRevisions);
        Assert.Equal(1, fixture.Applied.Revision);
        Assert.Equal(new SunshineManagementCredential("fleet-user", "fleet-secret-1"), fixture.Credentials.Saved);
        Assert.True(fixture.Credentials.ReplaceRequested);
    }

    [Fact]
    public async Task RotationReplacesTheStoredCredentialWithoutARestart()
    {
        using var fixture = new Fixture();

        Assert.True(await fixture.Updater.TryUpdateAsync(1, CancellationToken.None));
        Assert.True(await fixture.Updater.TryUpdateAsync(2, CancellationToken.None));

        Assert.Equal([0L, 1L], fixture.Handler.KnownRevisions);
        Assert.Equal(2, fixture.Applied.Revision);
        Assert.Equal(new SunshineManagementCredential("fleet-user", "fleet-secret-2"), fixture.Credentials.Saved);
    }

    [Fact]
    public async Task AlreadyCurrentDoesNotWriteOrRotate()
    {
        using var fixture = new Fixture(_ => Status(HttpStatusCode.NoContent));

        Assert.False(await fixture.Updater.TryUpdateAsync(1, CancellationToken.None));
        Assert.Equal(0, fixture.Applied.Revision);
        Assert.Null(fixture.Credentials.Saved);
        Assert.Equal(0, fixture.Applied.SaveCalls);
    }

    [Fact]
    public async Task NotConfiguredDoesNotWriteOrRotate()
    {
        using var fixture = new Fixture(_ => Status(HttpStatusCode.NotFound));

        Assert.False(await fixture.Updater.TryUpdateAsync(2, CancellationToken.None));
        Assert.Equal(0, fixture.Applied.Revision);
        Assert.Null(fixture.Credentials.Saved);
        Assert.Equal(0, fixture.Applied.SaveCalls);
    }

    [Fact]
    public async Task RejectedIdentityDoesNotWriteOrRotate()
    {
        using var fixture = new Fixture(_ => Status(HttpStatusCode.Conflict));

        Assert.False(await fixture.Updater.TryUpdateAsync(3, CancellationToken.None));
        Assert.Equal(0, fixture.Applied.Revision);
        Assert.Null(fixture.Credentials.Saved);
    }

    [Fact]
    public async Task ZeroFleetRevisionDoesNothing()
    {
        using var fixture = new Fixture();

        Assert.False(await fixture.Updater.TryUpdateAsync(0, CancellationToken.None));
        Assert.Empty(fixture.Handler.KnownRevisions);
        Assert.Equal(0, fixture.Applied.Revision);
    }

    [Fact]
    public async Task UpToDateRevisionDoesNothing()
    {
        using var fixture = new Fixture();
        fixture.Applied.Revision = 5;

        Assert.False(await fixture.Updater.TryUpdateAsync(5, CancellationToken.None));
        Assert.Empty(fixture.Handler.KnownRevisions);
    }

    [Fact]
    public async Task ManuallyProvisionedCredentialWithoutAppliedRevisionIsReplacedByFleetCredential()
    {
        using var fixture = new Fixture();
        fixture.Credentials.Existing = new SunshineManagementCredential("manual-user", "manual-password");

        Assert.True(await fixture.Updater.TryUpdateAsync(1, CancellationToken.None));

        Assert.Equal(new SunshineManagementCredential("fleet-user", "fleet-secret-1"), fixture.Credentials.Saved);
        Assert.Equal(1, fixture.Applied.Revision);
    }

    [Fact]
    public async Task FailedFetchBacksOffThenRetriesAfterTheDelay()
    {
        var failures = 0;
        using var fixture = new Fixture(request =>
        {
            failures++;
            return failures == 1
                ? Status(HttpStatusCode.InternalServerError)
                : Delivery(request + 1);
        });

        Assert.False(await fixture.Updater.TryUpdateAsync(1, CancellationToken.None));
        Assert.Single(fixture.Handler.KnownRevisions);

        // Within the bounded backoff window no second request is made.
        Assert.False(await fixture.Updater.TryUpdateAsync(1, CancellationToken.None));
        Assert.Single(fixture.Handler.KnownRevisions);

        fixture.Time.Advance(TimeSpan.FromSeconds(11));
        Assert.True(await fixture.Updater.TryUpdateAsync(1, CancellationToken.None));
        Assert.Equal(2, fixture.Handler.KnownRevisions.Count);
        Assert.Equal(1, fixture.Applied.Revision);
    }

    [Fact]
    public async Task StaleDeliveryIsNotApplied()
    {
        using var fixture = new Fixture(request => Delivery(request));

        Assert.False(await fixture.Updater.TryUpdateAsync(1, CancellationToken.None));
        Assert.Equal(0, fixture.Applied.Revision);
        Assert.Null(fixture.Credentials.Saved);
    }

    [Fact]
    public async Task AppliedRevisionStoreFailureIsRetriedWithoutCredentialWrite()
    {
        using var fixture = new Fixture();
        fixture.Applied.ThrowOnRead = true;

        Assert.False(await fixture.Updater.TryUpdateAsync(4, CancellationToken.None));
        Assert.Empty(fixture.Handler.KnownRevisions);
        Assert.Null(fixture.Credentials.Saved);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RealProtectedStoresApplyTheFleetCredentialAndOnlyPersistTheRevision()
    {
        if (!OperatingSystem.IsWindows() || !WindowsAclProbe.CanSetProtectedAcl) return;
        var directory = Path.Combine(Path.GetTempPath(), "kr-sunshine-updater-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var signer = new TestSigner();
            using var handler = new RecordingHandler(signer, _ => Delivery(6));
            using var http = new HttpClient(handler);
            var currentUser = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Windows user SID is unavailable.");
            var appliedStore = new SunshineAppliedCredentialStore(directory, currentUser);
            var credentialStore = new WindowsSunshineCredentialStore(directory, currentUser, new TestProtector());
            var updater = new SunshineCredentialUpdater(
                new SunshineCredentialClient(http, signer),
                new Uri("https://controller.test"),
                appliedStore,
                credentialStore,
                new FakeTimeProvider(),
                NullLogger<SunshineCredentialUpdater>.Instance);

            Assert.True(await updater.TryUpdateAsync(6, CancellationToken.None));

            Assert.Equal(6, appliedStore.Read());
            Assert.Equal(new SunshineManagementCredential("fleet-user", "fleet-secret-6"), credentialStore.Load());
            var revisionFile = File.ReadAllText(appliedStore.FilePath);
            Assert.Equal("{\"revision\":6}", revisionFile);
            Assert.DoesNotContain("fleet-secret-6", revisionFile, StringComparison.Ordinal);
            Assert.DoesNotContain("fleet-user", revisionFile, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailureLoggingNeverIncludesCredentialValues()
    {
        var logger = new CapturingLogger();
        using var fixture = new Fixture(logger: logger);
        fixture.Credentials.ThrowOnSave = new InvalidOperationException("cannot save fleet-secret-1 for fleet-user");

        Assert.False(await fixture.Updater.TryUpdateAsync(1, CancellationToken.None));

        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain("fleet-secret-1", message, StringComparison.Ordinal);
            Assert.DoesNotContain("fleet-user", message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RejectedStatusLogsOnlyTheCategory()
    {
        var logger = new CapturingLogger();
        using var fixture = new Fixture(_ => Status(HttpStatusCode.Conflict), logger);

        Assert.False(await fixture.Updater.TryUpdateAsync(1, CancellationToken.None));

        Assert.Contains(logger.Messages, message => message.Contains("notApprovedOrQuarantined", StringComparison.Ordinal));
        Assert.All(logger.Messages, message => Assert.DoesNotContain("fleet-secret", message, StringComparison.Ordinal));
    }

    private static HttpResponseMessage Delivery(long revision) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new SunshineCredentialDelivery("fleet-user", "fleet-secret-" + revision, revision), JsonOptions),
            Encoding.UTF8,
            "application/json")
    };

    private static HttpResponseMessage Status(HttpStatusCode statusCode) => new(statusCode);

    private sealed class Fixture : IDisposable
    {
        public FakeTimeProvider Time { get; } = new();
        public TestSigner Signer { get; } = new();
        public RecordingHandler Handler { get; }
        public HttpClient Http { get; }
        public FakeRevisionStore Applied { get; } = new();
        public FakeCredentialSink Credentials { get; } = new();
        public SunshineCredentialUpdater Updater { get; }

        public Fixture(Func<long, HttpResponseMessage>? responder = null, ILogger<SunshineCredentialUpdater>? logger = null)
        {
            Handler = new RecordingHandler(Signer, responder ?? (knownRevision => Delivery(knownRevision + 1)));
            Http = new HttpClient(Handler);
            Updater = new SunshineCredentialUpdater(
                new SunshineCredentialClient(Http, Signer),
                new Uri("https://controller.test"),
                Applied,
                Credentials,
                Time,
                logger ?? NullLogger<SunshineCredentialUpdater>.Instance);
        }

        public void Dispose()
        {
            Http.Dispose();
            Signer.Dispose();
        }
    }

    private sealed class FakeRevisionStore : ISunshineCredentialRevisionStore
    {
        public long Revision { get; set; }
        public int SaveCalls { get; private set; }
        public bool ThrowOnRead { get; set; }

        public long Read() => ThrowOnRead ? throw new InvalidDataException("revision store unavailable") : Revision;

        public void Save(long revision)
        {
            SaveCalls++;
            Revision = revision;
        }
    }

    private sealed class FakeCredentialSink : ISunshineManagementCredentialSink
    {
        public SunshineManagementCredential? Existing { get; set; }
        public SunshineManagementCredential? Saved { get; private set; }
        public bool ReplaceRequested { get; private set; }
        public Exception? ThrowOnSave { get; set; }

        public void Save(SunshineManagementCredential credential, bool replace)
        {
            if (ThrowOnSave is not null) throw ThrowOnSave;
            if (Existing is not null && !replace) throw new IOException("replacement requires explicit request");
            ReplaceRequested = replace;
            Saved = credential;
            Existing = credential;
        }
    }

    private sealed class CapturingLogger : ILogger<SunshineCredentialUpdater>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-11-17T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class TestProtector : ISunshineCredentialProtector
    {
        private static readonly byte[] Prefix = [0x4B, 0x52, 0x43, 0x31];

        public byte[] Protect(ReadOnlySpan<byte> plaintext) => [.. Prefix, .. plaintext];

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
        {
            if (!ciphertext.StartsWith(Prefix)) throw new InvalidDataException("Invalid test ciphertext.");
            return ciphertext[Prefix.Length..].ToArray();
        }
    }

    private sealed class TestSigner : IAgentRequestSigner, IDisposable
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public string MachineId { get; } = Guid.NewGuid().ToString("N");
        public string PublicKey => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        public string Sign(string method, string path, DateTimeOffset timestamp, string nonce, string body) =>
            Convert.ToBase64String(key.SignData(
                Encoding.UTF8.GetBytes($"{MachineId}\n{method}\n{path}\n{timestamp.ToUnixTimeSeconds()}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}"),
                HashAlgorithmName.SHA256));

        public void Dispose() => key.Dispose();
    }

    private sealed class RecordingHandler(TestSigner signer, Func<long, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<long> KnownRevisions { get; } = [];
        public bool SignatureVerified { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var timestamp = request.Headers.GetValues("X-Agent-Timestamp").Single();
            var nonce = request.Headers.GetValues("X-Agent-Nonce").Single();
            SignatureVerified = new AgentAuthentication().VerifySignature(
                signer.MachineId, signer.PublicKey, "POST", SunshineCredentialClient.Path, timestamp, nonce,
                Encoding.UTF8.GetBytes(body), request.Headers.GetValues("X-Agent-Signature").Single()) is not null;
            using var json = JsonDocument.Parse(body);
            var knownRevision = json.RootElement.GetProperty("knownRevision").GetInt64();
            KnownRevisions.Add(knownRevision);
            return responder(knownRevision);
        }
    }
}
