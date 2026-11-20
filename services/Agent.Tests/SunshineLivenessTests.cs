using System.Net;
using System.Net.Sockets;
using System.Text;
using KamenRaider.Agent;
using KamenRaider.Agent.Configuration;
using KamenRaider.Agent.Sunshine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agent.Tests;

/// <summary>
/// 0jay-style Sunshine liveness: two local endpoints must answer, the monitor absorbs a short
/// miss streak (3 misses), and the probe treats anything that is not Sunshine as not-ready.
/// The web-UI side is HTTPS with Sunshine's self-signed certificate, so the probe's web
/// classification (authorized / not-set-up / unreachable) is only exercised against a closed
/// port here; the stream side is exercised against a fake loopback HTTP server.
/// </summary>
public sealed class SunshineLivenessTests
{
    // ---------------------------------------------------------------- probe

    [Theory]
    [InlineData(0, 47984)]
    [InlineData(47989, 0)]
    [InlineData(65536, 47984)]
    [InlineData(47989, 65536)]
    public async Task InvalidPortsAreRejectedWithoutTouchingTheNetwork(int moonlightPort, int sunshinePort)
    {
        using var probe = new SunshineLivenessProbe(TimeSpan.FromSeconds(2));

        var result = await probe.ProbeAsync(moonlightPort, sunshinePort, default);

        Assert.False(result.StreamAnswers);
        Assert.False(result.WebUiAnswers);
        Assert.False(result.Ready);
        Assert.Equal(SunshineLivenessProbe.ReasonInvalidPort, result.Reason);
    }

    [Fact]
    public async Task ClosedPortsReportTheStreamSideFirst()
    {
        var moonlightPort = FreePort();
        var sunshinePort = FreePort();
        using var probe = new SunshineLivenessProbe(TimeSpan.FromSeconds(2));

        var result = await probe.ProbeAsync(moonlightPort, sunshinePort, default);

        Assert.False(result.StreamAnswers);
        Assert.False(result.WebUiAnswers);
        Assert.Equal(SunshineLivenessProbe.ReasonStreamSilent, result.Reason);
    }

    [Fact]
    public async Task GameStreamServerInfoOnTheStreamPortIsRecognized()
    {
        await using var stream = await LoopbackServer.StartAsync(200, "<serverinfo><root /></serverinfo>");
        var closedSunshinePort = FreePort();
        using var probe = new SunshineLivenessProbe(TimeSpan.FromSeconds(2));

        var result = await probe.ProbeAsync(stream.Port, closedSunshinePort, default);

        Assert.True(result.StreamAnswers);
        Assert.False(result.WebUiAnswers);
        Assert.False(result.Ready);
        Assert.Equal(SunshineLivenessProbe.ReasonWebUiSilent, result.Reason);
        Assert.Equal("/serverinfo", stream.LastPath);
    }

    [Fact]
    public async Task RootDocumentIsAlsoAcceptedAsGameStreamEvidence()
    {
        await using var stream = await LoopbackServer.StartAsync(200, "<root><serverinfo /></root>");
        var closedSunshinePort = FreePort();
        using var probe = new SunshineLivenessProbe(TimeSpan.FromSeconds(2));

        var result = await probe.ProbeAsync(stream.Port, closedSunshinePort, default);

        Assert.True(result.StreamAnswers);
    }

    [Fact]
    public async Task SomethingElseAnsweringTheMoonlightPortIsNotProofOfSunshine()
    {
        await using var stream = await LoopbackServer.StartAsync(200, "hello from another service");
        var closedSunshinePort = FreePort();
        using var probe = new SunshineLivenessProbe(TimeSpan.FromSeconds(2));

        var result = await probe.ProbeAsync(stream.Port, closedSunshinePort, default);

        Assert.False(result.StreamAnswers);
        Assert.False(result.Ready);
        Assert.Equal(SunshineLivenessProbe.ReasonStreamSilent, result.Reason);
    }

    // ---------------------------------------------------------------- monitor hysteresis

    [Fact]
    public void MonitorStartsUnknownUntilTheFirstProbe()
    {
        using var fixture = new MonitorFixture();

        Assert.False(fixture.Monitor.Current.Ready);
        Assert.Equal(SunshineLivenessSnapshot.Unknown, fixture.Monitor.Current);
        Assert.Equal(0, fixture.Probe.Calls);
    }

    [Fact]
    public async Task InitialMissIsReportedNotReadyWithTheProbeReason()
    {
        using var fixture = new MonitorFixture();
        fixture.Probe.Result = new(false, false, SunshineLivenessProbe.ReasonWebUiSilent);

        var snapshot = await fixture.Monitor.ProbeOnceAsync(default);

        Assert.False(snapshot.Ready);
        Assert.Equal(SunshineLivenessProbe.ReasonWebUiSilent, snapshot.Reason);
        Assert.Equal(47989, snapshot.MoonlightPort);
        Assert.Equal(47984, snapshot.SunshinePort);
        Assert.Same(snapshot, fixture.Monitor.Current);
    }

    [Fact]
    public async Task ReadyProbeReportsReady()
    {
        using var fixture = new MonitorFixture();

        var snapshot = await fixture.Monitor.ProbeOnceAsync(default);

        Assert.True(snapshot.Ready);
        Assert.Equal(SunshineLivenessProbe.ReasonReady, snapshot.Reason);
        Assert.True(fixture.Monitor.Current.Ready);
    }

    [Fact]
    public async Task OneOrTwoMissesKeepAReadyMachineOnTheShelfButTheThirdTakesItOff()
    {
        using var fixture = new MonitorFixture();
        Assert.True((await fixture.Monitor.ProbeOnceAsync(default)).Ready);

        fixture.Probe.Result = new(false, false, SunshineLivenessProbe.ReasonStreamSilent);

        var firstMiss = await fixture.Monitor.ProbeOnceAsync(default);
        Assert.True(firstMiss.Ready);
        Assert.Equal(SunshineLivenessProbe.ReasonReady, firstMiss.Reason);

        var secondMiss = await fixture.Monitor.ProbeOnceAsync(default);
        Assert.True(secondMiss.Ready);

        var thirdMiss = await fixture.Monitor.ProbeOnceAsync(default);
        Assert.False(thirdMiss.Ready);
        Assert.Equal(SunshineLivenessProbe.ReasonStreamSilent, thirdMiss.Reason);
        Assert.False(fixture.Monitor.Current.Ready);
    }

    [Fact]
    public async Task RepeatedMissesPastTheThresholdKeepReportingNotReady()
    {
        using var fixture = new MonitorFixture();
        fixture.Probe.Result = new(false, false, SunshineLivenessProbe.ReasonStreamSilent);
        for (var miss = 0; miss < SunshineLivenessMonitor.NotReadyAfterMisses; miss++)
            await fixture.Monitor.ProbeOnceAsync(default);

        Assert.False((await fixture.Monitor.ProbeOnceAsync(default)).Ready);
    }

    [Fact]
    public async Task ASuccessResetsTheMissStreak()
    {
        using var fixture = new MonitorFixture();
        fixture.Probe.Result = new(false, false, SunshineLivenessProbe.ReasonStreamSilent);
        await fixture.Monitor.ProbeOnceAsync(default);
        await fixture.Monitor.ProbeOnceAsync(default);
        await fixture.Monitor.ProbeOnceAsync(default);
        Assert.False(fixture.Monitor.Current.Ready);

        fixture.Probe.Result = new(true, true, SunshineLivenessProbe.ReasonReady);
        Assert.True((await fixture.Monitor.ProbeOnceAsync(default)).Ready);

        fixture.Probe.Result = new(false, false, SunshineLivenessProbe.ReasonWebUiSilent);
        Assert.True((await fixture.Monitor.ProbeOnceAsync(default)).Ready);
    }

    [Fact]
    public async Task ProbeExceptionIsARecordedMissNotAnUnhandledFault()
    {
        using var fixture = new MonitorFixture();
        Assert.True((await fixture.Monitor.ProbeOnceAsync(default)).Ready);

        fixture.Probe.Throw = true;
        var snapshot = await fixture.Monitor.ProbeOnceAsync(default);

        // Hysteresis keeps the last ready state for a transient probe exception as well.
        Assert.True(snapshot.Ready);
        Assert.Equal(SunshineLivenessProbe.ReasonReady, snapshot.Reason);
    }

    [Fact]
    public async Task AppliedConfigurationOverridesTheBootstrapPorts()
    {
        using var fixture = new MonitorFixture();
        await fixture.Configuration.ApplyAsync(new(2, 5001, 6001), default);

        var snapshot = await fixture.Monitor.ProbeOnceAsync(default);

        Assert.Equal(5001, snapshot.MoonlightPort);
        Assert.Equal(6001, snapshot.SunshinePort);
        Assert.Equal(5001, fixture.Probe.LastMoonlightPort);
        Assert.Equal(6001, fixture.Probe.LastSunshinePort);
    }

    private sealed class MonitorFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "sunshine-liveness-" + Guid.NewGuid().ToString("N"));

        public MonitorFixture()
        {
            Directory.CreateDirectory(directory);
            Configuration = new AppliedConfiguration(directory);
            Monitor = new SunshineLivenessMonitor(
                Probe,
                Options.Create(new AgentOptions { MoonlightPort = 47989, SunshinePort = 47984 }),
                Configuration,
                NullLogger<SunshineLivenessMonitor>.Instance);
        }

        public FakeProbe Probe { get; } = new();
        public AppliedConfiguration Configuration { get; }
        public SunshineLivenessMonitor Monitor { get; }

        public void Dispose()
        {
            Monitor.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeProbe : ISunshineLivenessProbe
    {
        public SunshineProbeResult Result { get; set; } = new(true, true, SunshineLivenessProbe.ReasonReady);
        public bool Throw { get; set; }
        public int Calls { get; private set; }
        public int LastMoonlightPort { get; private set; } = -1;
        public int LastSunshinePort { get; private set; } = -1;

        public Task<SunshineProbeResult> ProbeAsync(int moonlightPort, int sunshinePort, CancellationToken ct)
        {
            Calls++;
            LastMoonlightPort = moonlightPort;
            LastSunshinePort = sunshinePort;
            if (Throw) throw new IOException("probe transport failed");
            return Task.FromResult(Result);
        }
    }

    // ---------------------------------------------------------------- fake loopback HTTP server

    /// <summary>Minimal single-response HTTP server used where the real probe does HTTP.</summary>
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        private readonly int status;
        private readonly string body;

        private LoopbackServer(int status, string body)
        {
            this.status = status;
            this.body = body;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Task.Run(AcceptAsync);
        }

        public static Task<LoopbackServer> StartAsync(int status, string body) => Task.FromResult(new LoopbackServer(status, body));

        public int Port { get; }
        public string? LastPath { get; private set; }

        private async Task AcceptAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    try
                    {
                        await HandleAsync(client);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                    {
                        // The probe aborts connections once it has its answer; that is expected.
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            await using var stream = client.GetStream();
            var requestBytes = new List<byte>(1024);
            var buffer = new byte[1];
            while (requestBytes.Count < 8192)
            {
                if (await stream.ReadAsync(buffer, stop.Token) == 0) break;
                requestBytes.Add(buffer[0]);
                var count = requestBytes.Count;
                if (count >= 4 &&
                    requestBytes[count - 4] == '\r' && requestBytes[count - 3] == '\n' &&
                    requestBytes[count - 2] == '\r' && requestBytes[count - 1] == '\n')
                    break;
            }

            var request = Encoding.ASCII.GetString(requestBytes.ToArray());
            var requestLine = request.Split("\r\n", StringSplitOptions.None)[0].Split(' ');
            LastPath = requestLine.Length > 1 ? requestLine[1] : null;

            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var headers = new StringBuilder()
                .Append("HTTP/1.1 ").Append(status).Append(status == 200 ? " OK" : " Status").Append("\r\n")
                .Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n")
                .Append("Content-Type: text/xml\r\n")
                .Append("Connection: close\r\n")
                .Append("\r\n");

            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), stop.Token);
            await stream.WriteAsync(bodyBytes, stop.Token);
            await stream.FlushAsync(stop.Token);
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
            }
            stop.Dispose();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
