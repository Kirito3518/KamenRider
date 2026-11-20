using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KamenRaider.Agent.Configuration;
using KamenRaider.Agent.Identity;
using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Power;
using KamenRaider.Agent.Startup;
using KamenRaider.Agent.Sync;
using KamenRaider.Contracts;

namespace Agent.Tests;

public class SyncWorkerHardwareTests
{
    [Fact]
    public async Task SyncRequestIncludesHardwareAndAppliedRevisionAndObservesFleetRevision()
    {
        using var fixture = new Fixture();
        var hardware = new HardwareInfo("PC-LAB-01", "Fake CPU (16 logical processors)", "Fake GPU", 17179869184);
        using var signer = new TestSigner();
        using var transport = new RecordingTransport { FleetRevision = 7 };
        using var http = new HttpClient(transport);
        long observedRevision = -1;
        var cycle = new AgentSyncCycle(
            new SyncClient(http, signer),
            new Uri("https://controller.test"),
            fixture.BootClock,
            fixture.Configuration,
            new LeaseCommandDispatcher(fixture.Enforcer),
            hardware,
            () => 3,
            revision => observedRevision = revision);

        await cycle.RunAsync("boot", "192.0.2.1", "001122334455", Readiness.NotReady, default);

        var request = Assert.Single(transport.Requests);
        Assert.Equal("PC-LAB-01", request.GetProperty("hardware").GetProperty("hostname").GetString());
        Assert.Equal("Fake CPU (16 logical processors)", request.GetProperty("hardware").GetProperty("cpu").GetString());
        Assert.Equal("Fake GPU", request.GetProperty("hardware").GetProperty("gpu").GetString());
        Assert.Equal(17179869184, request.GetProperty("hardware").GetProperty("ramBytes").GetInt64());
        Assert.Equal(3, request.GetProperty("appliedSunshineCredentialRevision").GetInt64());
        Assert.Equal(7, observedRevision);
    }

    [Fact]
    public async Task ZeroFleetCredentialRevisionIsStillObservedWithoutChangingSyncBehavior()
    {
        using var fixture = new Fixture();
        using var signer = new TestSigner();
        using var transport = new RecordingTransport { FleetRevision = 0 };
        using var http = new HttpClient(transport);
        long observedRevision = -1;
        var cycle = new AgentSyncCycle(
            new SyncClient(http, signer),
            new Uri("https://controller.test"),
            fixture.BootClock,
            fixture.Configuration,
            new LeaseCommandDispatcher(fixture.Enforcer),
            new HardwareInfo("PC-LAB-01", "Fake CPU", "Fake GPU", 1024),
            static () => 0,
            revision => observedRevision = revision);

        await cycle.RunAsync("boot", "192.0.2.1", "001122334455", Readiness.NotReady, default);

        Assert.Single(transport.Requests);
        Assert.Equal(0, observedRevision);
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "kr-sync-hardware-" + Guid.NewGuid().ToString("N"));
        public Clock BootClock { get; } = new();
        public AppliedConfiguration Configuration { get; }
        public Cleanup StreamCleanup { get; } = new();
        public LeaseEnforcer Enforcer { get; }

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            var configurationDirectory = Path.Combine(Directory, "config");
            System.IO.Directory.CreateDirectory(configurationDirectory);
            Configuration = new AppliedConfiguration(configurationDirectory);
            Enforcer = new LeaseEnforcer(
                new LeaseStore(Path.Combine(Directory, "leases")),
                BootClock,
                BootClock,
                StreamCleanup,
                new NoopShutdown(),
                new TestMaintenanceFence());
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class Clock : TimeProvider, IBootClock
    {
        public string BootId => "boot";
        public long UptimeMilliseconds { get; private set; } = 1000;
        public DateTimeOffset Now { get; } = DateTimeOffset.Parse("2026-10-29T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Cleanup : KamenRaider.Agent.Sunshine.IStreamCleanup
    {
        public Task<bool> ClearAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class NoopShutdown : IShutdown
    {
        public Task RequestImmediateShutdownAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>In-memory signer producing the same canonical signature bytes as DeviceIdentity.</summary>
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

    private sealed class RecordingTransport : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        public long FleetRevision { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Requests.Add(body.RootElement.Clone());
            var response = new SyncResponse(DateTimeOffset.Parse("2026-10-29T00:00:00Z"), new ConfigurationDto(1, 5000, 6000), [])
            {
                SunshineCredentialRevision = FleetRevision
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
    }
}
