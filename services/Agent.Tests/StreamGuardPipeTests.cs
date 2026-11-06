using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Runtime.Versioning;
using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Sunshine;
using Microsoft.Win32.SafeHandles;

namespace Agent.Tests;

public class StreamGuardPipeTests
{
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

    private sealed class DuplexMemoryStream(byte[] input) : Stream
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
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => Written.WriteAsync(buffer, cancellationToken);
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
    public async Task PairingCleanupBlocksStopsStartsOnlyForClearAndStopsBeforeAcknowledging()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"KamenRaider.Test.{Guid.NewGuid():N}";
        var events = new List<string>();
        var ingress = new LifecycleIngress(events);
        var sunshine = new LifecycleSunshine(events);
        var coordinator = new StreamGateCoordinator(ingress, sunshine, TimeProvider.System, new LifecycleClock(), new GrantHistoryStore());
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
    public async Task ClientReconnectsAfterGuardRestartsItsPipeSession()
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
