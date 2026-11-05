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
    private sealed class RecordingGate : IStreamGate
    {
        public readonly List<string> Calls = [];
        public StreamLeaseGrant? LastGrant;
        public Exception? AllowFailure;
        public Exception? BlockFailure;

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
        var request = new StreamGuardPipeRequest(2, Guid.NewGuid().ToString("N"), "block", null);
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

    private static async Task<DuplexMemoryStream> CreateSessionStreamAsync(StreamGuardPipeRequest request)
    {
        using var input = new MemoryStream();
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeInfo(SafePipeHandle pipe, out uint flags, out uint outputBufferSize,
        out uint inputBufferSize, out uint maximumInstances);
}
