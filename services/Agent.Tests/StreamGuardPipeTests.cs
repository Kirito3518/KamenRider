using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Runtime.Versioning;
using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Sunshine;
using Microsoft.Win32.SafeHandles;
using Xunit.Abstractions;

namespace Agent.Tests;

public class StreamGuardPipeTests
{
    private readonly ITestOutputHelper output;

    public StreamGuardPipeTests(ITestOutputHelper output) => this.output = output;

    private sealed class RecordingGate : IStreamGuardControl
    {
        public readonly List<string> Calls = [];
        public StreamLeaseGrant? LastGrant;
        public PairingScope? LastScope;
        public Exception? AllowFailure;
        public Exception? BlockFailure;
        public bool VerifyResult = true;
        public bool MaintenanceActive;

        public Task BlockAsync(CancellationToken cancellationToken)
        {
            Calls.Add("block");
            if (BlockFailure is { } error)
            {
                BlockFailure = null;
                return Task.FromException(error);
            }
            return Task.CompletedTask;
        }

        public Task AllowAsync(StreamLeaseGrant grant, CancellationToken cancellationToken)
        {
            Calls.Add("allow");
            LastGrant = grant;
            return AllowFailure is { } error ? Task.FromException(error) : Task.CompletedTask;
        }

        public Task<bool> VerifyOpenForLeaseAsync(PairingScope scope, CancellationToken cancellationToken)
        {
            Calls.Add("verify-open");
            LastScope = scope;
            return Task.FromResult(VerifyResult);
        }

        public Task BeginFencedMaintenanceAsync(CancellationToken cancellationToken)
        {
            Calls.Add("maintenance-begin");
            MaintenanceActive = true;
            return Task.CompletedTask;
        }

        public Task<bool> EndFencedMaintenanceAsync(CancellationToken cancellationToken)
        {
            Calls.Add("maintenance-end");
            MaintenanceActive = false;
            return Task.FromResult(true);
        }
    }

    private sealed class GrantHistoryStore : IStreamGrantHistoryStore
    {
        private StreamGrantHistorySnapshot snapshot = new([], null);

        public StreamGrantHistorySnapshot Load() => new(snapshot.Grants.ToArray(), snapshot.CurrentLease);

        public void Save(StreamGrantHistorySnapshot history) => snapshot = new(history.Grants.ToArray(), history.CurrentLease);
    }

    private sealed class SlowFirewallApi(TimeSpan readDelay, int slowReadCount) : IWindowsFirewallApi
    {
        private readonly FirewallRuleSnapshot rule = new(
            WindowsStreamIngressPolicy.BlockRuleName,
            @"C:\Program Files\Sunshine\sunshine.exe",
            "",
            Enabled: true,
            WindowsStreamIngressPolicy.InboundDirection,
            WindowsStreamIngressPolicy.BlockAction,
            WindowsStreamIngressPolicy.AllProfiles,
            WindowsStreamIngressPolicy.AnyProtocol,
            "",
            "",
            "*",
            "*",
            "All",
            InterfacesUnrestricted: true,
            EdgeTraversal: false,
            OverrideBlockRules: false);
        private int readCount;

        public List<DateTimeOffset> ReadStartedAtUtc { get; } = [];
        public List<DateTimeOffset> ReadCompletedAtUtc { get; } = [];

        public FirewallPolicySnapshot ReadPolicyState()
        {
            ReadStartedAtUtc.Add(DateTimeOffset.UtcNow);
            if (Interlocked.Increment(ref readCount) <= slowReadCount)
                Thread.Sleep(readDelay);
            ReadCompletedAtUtc.Add(DateTimeOffset.UtcNow);
            return new(2, true, true, true, 0, new Dictionary<string, bool>
            {
                ["Domain"] = true,
                ["Private"] = true,
                ["Public"] = true
            });
        }

        public FirewallRuleSnapshot? FindRule(string name) =>
            string.Equals(name, rule.Name, StringComparison.Ordinal) ? rule : null;

        public void AddRule(FirewallRuleDefinition definition) => throw new InvalidOperationException("The managed block rule should already exist.");

        public void SetRuleEnabled(string name, bool enabled) => throw new InvalidOperationException("The managed block rule is already enabled.");
    }

    private sealed class DuplexMemoryStream(byte[] input, bool failResponseWrites = false) : Stream
    {
        private readonly MemoryStream reader = new(input);
        public readonly MemoryStream Written = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => Written.Flush();
        public override int Read(byte[] buffer, int offset, int count) => reader.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => reader.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (failResponseWrites)
                throw new IOException("Simulated pipe disconnect during response write.");
            Written.Write(buffer, offset, count);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            failResponseWrites
                ? ValueTask.FromException(new IOException("Simulated pipe disconnect during response write."))
                : Written.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                reader.Dispose();
                Written.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task UnsupportedProtocolVersionIsRejectedAndClosesGate()
    {
        var gate = new RecordingGate();
        var request = new StreamGuardPipeRequest(1, Guid.NewGuid().ToString("N"), "block", null);
        using var pipe = await CreateSessionStreamAsync(request);

        await new StreamGuardPipeSession(new StreamGuardPipeRequestHandler(gate), gate).RunAsync(pipe, default);

        var response = await ReadResponseAsync(pipe.Written);
        Assert.False(response.Success);
        Assert.Equal("unsupported_version", response.Error);
        Assert.Contains("block", gate.Calls);
    }

    [Fact]
    public async Task MalformedJsonAndOversizedFramesCloseGate()
    {
        foreach (var input in new[]
        {
            Encoding.UTF8.GetBytes("{not-json}\n"),
            Encoding.UTF8.GetBytes(new string('x', StreamGuardPipeProtocol.MaxFrameBytes + 1) + "\n")
        })
        {
            var gate = new RecordingGate();
            using var pipe = new DuplexMemoryStream(input);

            await new StreamGuardPipeSession(new StreamGuardPipeRequestHandler(gate), gate).RunAsync(pipe, default);

            Assert.Contains("block", gate.Calls);
            Assert.DoesNotContain("allow", gate.Calls);
        }
    }

    [Fact]
    public async Task RejectedStaleGrantReturnsFailureAndClosesGate()
    {
        var gate = new RecordingGate { AllowFailure = new InvalidOperationException("stale grant") };
        var request = new StreamGuardPipeRequest(
            StreamGuardPipeProtocol.Version,
            Guid.NewGuid().ToString("N"),
            "allow",
            new("rental", 1, "boot", DateTimeOffset.UtcNow.AddMinutes(1), 60000));
        using var pipe = await CreateSessionStreamAsync(request);

        await new StreamGuardPipeSession(new StreamGuardPipeRequestHandler(gate), gate).RunAsync(pipe, default);

        var response = await ReadResponseAsync(pipe.Written);
        Assert.False(response.Success);
        Assert.Equal("gate_rejected", response.Error);
        Assert.Contains("allow", gate.Calls);
        Assert.Contains("block", gate.Calls);
    }

    [Fact]
    public async Task FailedBlockAcknowledgmentIsNegativeAfterClosedStateRetry()
    {
        var gate = new RecordingGate { BlockFailure = new IOException("first close failed") };
        var request = new StreamGuardPipeRequest(StreamGuardPipeProtocol.Version, Guid.NewGuid().ToString("N"), "block", null);
        using var pipe = await CreateSessionStreamAsync(request);

        await new StreamGuardPipeSession(new StreamGuardPipeRequestHandler(gate), gate).RunAsync(pipe, default);

        var response = await ReadResponseAsync(pipe.Written);
        Assert.False(response.Success);
        Assert.Equal("gate_rejected", response.Error);
        Assert.Equal(3, gate.Calls.Count(call => call == "block"));
    }

    [Fact]
    public async Task ResponseWriteFailureDoesNotRepeatASuccessfulClose()
    {
        var gate = new RecordingGate();
        var request = new StreamGuardPipeRequest(StreamGuardPipeProtocol.Version, Guid.NewGuid().ToString("N"), "block", null);
        var frame = StreamGuardPipeProtocol.EncodeRequest(request).Append((byte)'\n').ToArray();
        using var pipe = new DuplexMemoryStream(frame, failResponseWrites: true);

        await Assert.ThrowsAsync<IOException>(() => new StreamGuardPipeRequestHandler(gate).HandleSessionAsync(pipe, default));

        Assert.Equal(1, gate.Calls.Count(call => call == "block"));
    }

    [Fact]
    public async Task ClientReceivesAcknowledgmentForAValidCloseThatTakesOverFiveSeconds()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"KamenRaider.Test.{Guid.NewGuid():N}";
        var readDelay = TimeSpan.FromMilliseconds(1900);
        var firewall = new SlowFirewallApi(readDelay, slowReadCount: 3);
        var sunshineEvents = new List<string>();
        var gate = new StreamGateCoordinator(
            new WindowsStreamIngressPolicy(
                firewall,
                new("SunshineService", @"C:\Program Files\Sunshine\tools\sunshinesvc.exe", @"C:\Program Files\Sunshine\sunshine.exe")),
            new LifecycleSunshine(sunshineEvents),
            TimeProvider.System,
            new LifecycleClock(),
            new GrantHistoryStore(),
            new TestMaintenanceFence());
        var server = CreateCurrentUserPipeServer(pipeName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cancellation.Token);
            await new StreamGuardPipeSession(new StreamGuardPipeRequestHandler(gate), gate).RunAsync(server, cancellation.Token);
        }, cancellation.Token);
        await using var client = new StreamGuardPipeClient(pipeName);
        var clientStartedAtUtc = DateTimeOffset.UtcNow;
        var clientTimer = Stopwatch.StartNew();
        var clientError = await Record.ExceptionAsync(() => client.BlockAsync(default));
        clientTimer.Stop();
        await client.DisposeAsync();
        var serverError = await Record.ExceptionAsync(() => serve);
        await server.DisposeAsync();

        output.WriteLine("client-start={0:O}; policy-read-1-start={1:O}; policy-read-3-complete={2:O}; client-elapsed={3:F3}s",
            clientStartedAtUtc,
            firewall.ReadStartedAtUtc[0],
            firewall.ReadCompletedAtUtc[2],
            clientTimer.Elapsed.TotalSeconds);
        Assert.Null(clientError);
        Assert.Null(serverError);
        Assert.Equal(4, firewall.ReadStartedAtUtc.Count); // close path's three checks plus the post-disconnect verification
        Assert.True(firewall.ReadCompletedAtUtc[2] - firewall.ReadStartedAtUtc[0] >= TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "sunshine:stop", "sunshine:verify-stopped", "sunshine:verify-stopped" }, sunshineEvents);
    }

    [Fact]
    public async Task RequestIdMemoryBoundClosesSessionAfterTheConfiguredLimit()
    {
        var gate = new RecordingGate();
        var requests = Enumerable.Range(0, 3)
            .Select(_ => new StreamGuardPipeRequest(StreamGuardPipeProtocol.Version, Guid.NewGuid().ToString("N"), "block", null))
            .ToArray();
        using var pipe = await CreateSessionStreamAsync(requests);

        await new StreamGuardPipeSession(new StreamGuardPipeRequestHandler(gate, maxRequestIdsPerSession: 2), gate)
            .RunAsync(pipe, default);

        var responses = await ReadResponsesAsync(pipe.Written);
        Assert.Equal(3, responses.Length);
        Assert.True(responses[0].Success);
        Assert.True(responses[1].Success);
        Assert.False(responses[2].Success);
        Assert.Equal("session_request_limit", responses[2].Error);
        Assert.Equal(4, gate.Calls.Count(call => call == "block"));
    }

    [Fact]
    public async Task VerifyOpenRequestReturnsTheExactLeaseResultWithoutBlocking()
    {
        var gate = new RecordingGate();
        var scope = new PairingScope("rental", 3, "boot", DateTimeOffset.UtcNow.AddMinutes(2), 121000);
        var request = new StreamGuardPipeRequest(StreamGuardPipeProtocol.Version, Guid.NewGuid().ToString("N"), "verify-open", null, scope);
        using var pipe = await CreateSessionStreamAsync(request);

        await new StreamGuardPipeSession(new StreamGuardPipeRequestHandler(gate), gate).RunAsync(pipe, default);

        var response = await ReadResponseAsync(pipe.Written);
        Assert.True(response.Success);
        Assert.True(response.Verified);
        Assert.Equal(scope, gate.LastScope);
        Assert.Equal(new[] { "verify-open", "block" }, gate.Calls);
    }

    [Fact]
    public async Task BlockRequestWithUnexpectedPairingScopeIsRejected()
    {
        var gate = new RecordingGate();
        var scope = new PairingScope("rental", 3, "boot", DateTimeOffset.UtcNow.AddMinutes(2), 121000);
        var request = new StreamGuardPipeRequest(StreamGuardPipeProtocol.Version, Guid.NewGuid().ToString("N"), "block", null, scope);
        using var pipe = await CreateSessionStreamAsync(request);

        await new StreamGuardPipeSession(new StreamGuardPipeRequestHandler(gate), gate).RunAsync(pipe, default);

        var response = await ReadResponseAsync(pipe.Written);
        Assert.False(response.Success);
        Assert.Equal("invalid_request", response.Error);
        Assert.Contains("block", gate.Calls);
    }

    [Fact]
    public async Task PairingCleanupRunsOnlyInsideGuardMaintenanceAndAlwaysEndsIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pipeName = $"KamenRaider.Test.{Guid.NewGuid():N}";
        var gate = new RecordingGate();
        var server = CreateCurrentUserPipeServer(pipeName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cancellation.Token);
            await new StreamGuardPipeSession(new StreamGuardPipeRequestHandler(gate), gate).RunAsync(server, cancellation.Token);
        }, cancellation.Token);
        await using var client = new StreamGuardPipeClient(pipeName, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
        var scope = new PairingScope("rental", 3, "boot", DateTimeOffset.UtcNow.AddMinutes(2), 121000);

        Assert.True(await client.VerifyOpenForLeaseAsync(scope, default));
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clearedUnderMaintenance = false;
        var cleanup = client.RunFencedCleanupAsync(async _ =>
        {
            clearedUnderMaintenance = gate.MaintenanceActive;
            gate.Calls.Add("clear-api");
            cleanupEntered.SetResult();
            await releaseCleanup.Task;
        }, default);
        await cleanupEntered.Task;
        var allow = client.AllowAsync(new("rental", 3, "boot", scope.ExpiresAtUtc, scope.DeadlineUptimeMilliseconds), default);
        await Task.Delay(50);
        Assert.False(allow.IsCompleted);
        releaseCleanup.SetResult();
        Assert.True(await cleanup);
        await allow;
        await client.DisposeAsync();
        await serve;

        Assert.True(clearedUnderMaintenance);
        Assert.False(gate.MaintenanceActive);
        Assert.Equal(new[] { "verify-open", "maintenance-begin", "clear-api", "maintenance-end", "allow", "block" }, gate.Calls);
        await server.DisposeAsync();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task LostMaintenanceEndAcknowledgmentDoesNotReplayTheCommand()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"KamenRaider.Test.{Guid.NewGuid():N}";
        var gate = new RecordingGate();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var secondListenerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serve = Task.Run(async () =>
        {
            await using (var firstServer = CreateCurrentUserPipeServer(pipeName))
            {
                await firstServer.WaitForConnectionAsync(cancellation.Token);
                var begin = StreamGuardPipeProtocol.DecodeRequest(
                    (await StreamGuardPipeProtocol.ReadFrameAsync(firstServer, cancellation.Token))!);
                Assert.Equal("maintenance-begin", begin.Command);
                await gate.BeginFencedMaintenanceAsync(cancellation.Token);
                await StreamGuardPipeProtocol.WriteFrameAsync(firstServer,
                    new StreamGuardPipeResponse(StreamGuardPipeProtocol.Version, begin.RequestId, true, null), cancellation.Token);

                var end = StreamGuardPipeProtocol.DecodeRequest(
                    (await StreamGuardPipeProtocol.ReadFrameAsync(firstServer, cancellation.Token))!);
                Assert.Equal("maintenance-end", end.Command);
                Assert.True(await gate.EndFencedMaintenanceAsync(cancellation.Token));
            }

            await using var secondServer = CreateCurrentUserPipeServer(pipeName);
            secondListenerReady.SetResult();
            try
            {
                await secondServer.WaitForConnectionAsync(cancellation.Token);
                var replay = StreamGuardPipeProtocol.DecodeRequest(
                    (await StreamGuardPipeProtocol.ReadFrameAsync(secondServer, cancellation.Token))!);
                Assert.Equal("maintenance-end", replay.Command);
                await gate.EndFencedMaintenanceAsync(cancellation.Token);
                await StreamGuardPipeProtocol.WriteFrameAsync(secondServer,
                    new StreamGuardPipeResponse(StreamGuardPipeProtocol.Version, replay.RequestId, true, null), cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }, cancellation.Token);
        await using var client = new StreamGuardPipeClient(pipeName, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));

        var cleanup = client.RunFencedCleanupAsync(_ => Task.CompletedTask, default);
        await secondListenerReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cleanupError = await Record.ExceptionAsync(() => cleanup);
        cancellation.Cancel();
        await serve;
        await client.DisposeAsync();

        Assert.IsType<InvalidOperationException>(cleanupError);
        Assert.Equal(new[] { "maintenance-begin", "maintenance-end" }, gate.Calls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PairingCleanupBlocksStopsStartsOnlyForClearAndStopsBeforeAcknowledging()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"KamenRaider.Test.{Guid.NewGuid():N}";
        var events = new List<string>();
        var ingress = new LifecycleIngress(events);
        var sunshine = new LifecycleSunshine(events);
        var coordinator = new StreamGateCoordinator(ingress, sunshine, TimeProvider.System, new LifecycleClock(), new GrantHistoryStore(), new TestMaintenanceFence());
        var server = CreateCurrentUserPipeServer(pipeName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cancellation.Token);
            await new StreamGuardPipeSession(new StreamGuardPipeRequestHandler(coordinator), coordinator).RunAsync(server, cancellation.Token);
        }, cancellation.Token);
        await using var client = new StreamGuardPipeClient(pipeName, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));

        var grant = new StreamLeaseGrant("rental", 3, "boot", DateTimeOffset.UtcNow.AddMinutes(2), 120000);
        await client.AllowAsync(grant, default);
        Assert.False(ingress.Blocked);
        Assert.True(sunshine.Running);
        events.Clear();

        var pairing = new PairingSession(new LifecyclePairingApi(events, () => ingress.Blocked && sunshine.Running), client, () => null, TimeProvider.System);
        Assert.True(await pairing.ClearAsync(default));

        Assert.Equal(new[]
        {
            "firewall:block",
            "sunshine:stop",
            "firewall:verify-blocked",
            "sunshine:verify-stopped",
            "sunshine:start",
            "firewall:verify-blocked",
            "sunshine:verify-running",
            "api:clear",
            "firewall:block",
            "sunshine:stop",
            "firewall:verify-blocked",
            "sunshine:verify-stopped"
        }, events);
        Assert.Null(coordinator.CurrentGrant);
        Assert.True(ingress.Blocked);
        Assert.False(sunshine.Running);

        await client.DisposeAsync();
        await serve;
        await server.DisposeAsync();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task MismatchedPairingScopeClosesAnOpenGateBeforeThePipeReportsFailure()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"KamenRaider.Test.{Guid.NewGuid():N}";
        var events = new List<string>();
        var ingress = new LifecycleIngress(events);
        var sunshine = new LifecycleSunshine(events);
        var coordinator = new StreamGateCoordinator(ingress, sunshine, TimeProvider.System, new LifecycleClock(), new GrantHistoryStore(), new TestMaintenanceFence());
        var server = CreateCurrentUserPipeServer(pipeName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cancellation.Token);
            await new StreamGuardPipeSession(new StreamGuardPipeRequestHandler(coordinator), coordinator).RunAsync(server, cancellation.Token);
        }, cancellation.Token);
        await using var client = new StreamGuardPipeClient(pipeName, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));

        var grant = new StreamLeaseGrant("rental", 3, "boot", DateTimeOffset.UtcNow.AddMinutes(2), 120000);
        await client.AllowAsync(grant, default);
        Assert.False(ingress.Blocked);
        Assert.True(sunshine.Running);

        var mismatched = new PairingScope("another-rental", grant.Revision, grant.BootId, grant.ExpiresAtUtc, grant.DeadlineUptimeMilliseconds);
        Assert.False(await client.VerifyOpenForLeaseAsync(mismatched, default));

        Assert.True(ingress.Blocked);
        Assert.False(sunshine.Running);
        Assert.Null(coordinator.CurrentGrant);

        await client.DisposeAsync();
        await serve;
        await server.DisposeAsync();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PipeSecurityAllowsOnlySystemAndAgentServiceAndDeniesNetwork()
    {
        if (!OperatingSystem.IsWindows()) return;
        var agentSid = new SecurityIdentifier("S-1-5-80-1-2-3-4-5");

        var security = StreamGuardPipeSecurity.CreateAgentPipeSecurity(agentSid);

        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        Assert.Contains(rules, rule => rule.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value && rule.AccessControlType == AccessControlType.Allow);
        Assert.Contains(rules, rule => rule.IdentityReference.Value == agentSid.Value && rule.AccessControlType == AccessControlType.Allow);
        Assert.Contains(rules, rule => rule.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.NetworkSid, null).Value && rule.AccessControlType == AccessControlType.Deny);
        Assert.DoesNotContain(rules, rule => rule.AccessControlType == AccessControlType.Allow &&
            rule.IdentityReference.Value != agentSid.Value &&
            rule.IdentityReference.Value != new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void NativePipeRejectsRemoteClients()
    {
        if (!OperatingSystem.IsWindows()) return;
        var agentSid = new SecurityIdentifier("S-1-5-80-1-2-3-4-5");
        var factory = new WindowsStreamGuardPipeServerFactory(agentSid, $"KamenRaider.Test.{Guid.NewGuid():N}");
        using var pipe = factory.CreateServer();

        Assert.True(GetNamedPipeInfo(pipe.SafePipeHandle, out var flags, out _, out _, out _));
        Assert.NotEqual(0u, flags & 0x00000008u);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ClientKeepsOneSessionAndServerClosesGateAfterDisconnect()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pipeName = $"KamenRaider.Test.{Guid.NewGuid():N}";
        var gate = new RecordingGate();
        var server = CreateCurrentUserPipeServer(pipeName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cancellation.Token);
            await new StreamGuardPipeSession(new StreamGuardPipeRequestHandler(gate), gate).RunAsync(server, cancellation.Token);
        }, cancellation.Token);
        await using var client = new StreamGuardPipeClient(pipeName, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));

        await client.BlockAsync(default);
        await client.BlockAsync(default);
        await client.DisposeAsync();
        await serve;

        Assert.Equal(new[] { "block", "block", "block" }, gate.Calls);
        await server.DisposeAsync();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ClientReconnectsSafelyAfterRequestIdSessionLimit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pipeName = $"KamenRaider.Test.{Guid.NewGuid():N}";
        var gate = new RecordingGate();
        var handler = new StreamGuardPipeRequestHandler(gate, maxRequestIdsPerSession: 1);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new StreamGuardPipeClient(pipeName, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));

        var firstServer = CreateCurrentUserPipeServer(pipeName);
        var firstSession = Task.Run(async () =>
        {
            await firstServer.WaitForConnectionAsync(cancellation.Token);
            await new StreamGuardPipeSession(handler, gate).RunAsync(firstServer, cancellation.Token);
            await firstServer.DisposeAsync();
        }, cancellation.Token);

        await client.BlockAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.BlockAsync(default));
        await firstSession;
        Assert.Contains("block", gate.Calls);

        var secondServer = CreateCurrentUserPipeServer(pipeName);
        var secondSession = Task.Run(async () =>
        {
            await secondServer.WaitForConnectionAsync(cancellation.Token);
            await new StreamGuardPipeSession(handler, gate).RunAsync(secondServer, cancellation.Token);
            await secondServer.DisposeAsync();
        }, cancellation.Token);
        await client.BlockAsync(default);
        await client.DisposeAsync();
        await secondSession;

        Assert.True(gate.Calls.Count(call => call == "block") >= 5);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ClientDoesNotReplayAfterStalePipeWriteFailsAndReconnectsOnTheNextCall()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pipeName = $"KamenRaider.Test.{Guid.NewGuid():N}";
        await using var client = new StreamGuardPipeClient(pipeName, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));

        var firstServer = CreateCurrentUserPipeServer(pipeName);
        var firstServe = RespondToOneRequestAsync(firstServer);
        await client.BlockAsync(default);
        await firstServe;
        await firstServer.DisposeAsync();

        var secondServer = CreateCurrentUserPipeServer(pipeName);
        var secondServe = RespondToOneRequestAsync(secondServer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.BlockAsync(default));
        await client.BlockAsync(default);
        await secondServe;
        await secondServer.DisposeAsync();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ClientRejectsFailedAcknowledgment()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pipeName = $"KamenRaider.Test.{Guid.NewGuid():N}";
        var server = CreateCurrentUserPipeServer(pipeName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cancellation.Token);
            var requestBytes = await StreamGuardPipeProtocol.ReadFrameAsync(server, cancellation.Token);
            var request = StreamGuardPipeProtocol.DecodeRequest(requestBytes!);
            await StreamGuardPipeProtocol.WriteFrameAsync(server,
                new StreamGuardPipeResponse(StreamGuardPipeProtocol.Version, request.RequestId, false, "gate_rejected"), cancellation.Token);
        }, cancellation.Token);
        await using var client = new StreamGuardPipeClient(pipeName, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.BlockAsync(default));
        await serve;
        await server.DisposeAsync();
    }

    private static async Task<DuplexMemoryStream> CreateSessionStreamAsync(params StreamGuardPipeRequest[] requests)
    {
        using var input = new MemoryStream();
        foreach (var request in requests)
            await StreamGuardPipeProtocol.WriteFrameAsync(input, request, default);
        return new(input.ToArray());
    }

    private static async Task<StreamGuardPipeResponse> ReadResponseAsync(MemoryStream stream)
    {
        stream.Position = 0;
        var frame = await StreamGuardPipeProtocol.ReadFrameAsync(stream, default);
        Assert.NotNull(frame);
        return StreamGuardPipeProtocol.DecodeResponse(frame);
    }

    private static async Task<StreamGuardPipeResponse[]> ReadResponsesAsync(MemoryStream stream)
    {
        stream.Position = 0;
        var responses = new List<StreamGuardPipeResponse>();
        while (await StreamGuardPipeProtocol.ReadFrameAsync(stream, default) is { } frame)
            responses.Add(StreamGuardPipeProtocol.DecodeResponse(frame));
        return responses.ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateCurrentUserPipeServer(string pipeName)
    {
        var currentSid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current process SID is missing.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(currentSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, StreamGuardPipeProtocol.MaxFrameBytes,
            StreamGuardPipeProtocol.MaxFrameBytes, security);
    }

    private static async Task RespondToOneRequestAsync(NamedPipeServerStream server)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.WaitForConnectionAsync(cancellation.Token);
        var requestBytes = await StreamGuardPipeProtocol.ReadFrameAsync(server, cancellation.Token);
        var request = StreamGuardPipeProtocol.DecodeRequest(requestBytes!);
        await StreamGuardPipeProtocol.WriteFrameAsync(server,
            new StreamGuardPipeResponse(StreamGuardPipeProtocol.Version, request.RequestId, true, null), cancellation.Token);
    }

    private sealed class LifecycleClock : IBootClock
    {
        public string BootId => "boot";
        public long UptimeMilliseconds => 1000;
    }

    private sealed class LifecycleIngress(List<string> events) : IStreamIngressPolicy
    {
        public bool Blocked { get; private set; }

        public Task SetBlockedAsync(bool blocked, CancellationToken cancellationToken)
        {
            Blocked = blocked;
            events.Add(blocked ? "firewall:block" : "firewall:unblock");
            return Task.CompletedTask;
        }

        public Task<bool> IsBlockedAsync(CancellationToken cancellationToken)
        {
            events.Add(Blocked ? "firewall:verify-blocked" : "firewall:verify-open");
            return Task.FromResult(Blocked);
        }
    }

    private sealed class LifecycleSunshine(List<string> events) : ISunshineServiceControl
    {
        public bool Running { get; private set; }

        public Task StartAndWaitAsync(CancellationToken cancellationToken)
        {
            Running = true;
            events.Add("sunshine:start");
            return Task.CompletedTask;
        }

        public Task StopAndWaitAsync(CancellationToken cancellationToken)
        {
            Running = false;
            events.Add("sunshine:stop");
            return Task.CompletedTask;
        }

        public Task<bool> IsRunningAsync(CancellationToken cancellationToken)
        {
            events.Add(Running ? "sunshine:verify-running" : "sunshine:verify-stopped");
            return Task.FromResult(Running);
        }
    }

    private sealed class LifecyclePairingApi(List<string> events, Func<bool> clearIsFenced) : ISunshinePairingApi
    {
        public Task<string[]> PendingIdsAsync(CancellationToken cancellationToken) => Task.FromResult(Array.Empty<string>());

        public Task<bool> SubmitAsync(string id, string pin, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            if (!clearIsFenced())
                throw new InvalidOperationException("Pairing API cleanup ran outside blocked Sunshine maintenance.");
            events.Add("api:clear");
            return Task.CompletedTask;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeInfo(SafePipeHandle pipe, out uint flags, out uint outputBufferSize,
        out uint inputBufferSize, out uint maximumInstances);
}
