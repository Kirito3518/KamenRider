using System.Net;
using System.Text;
using KamenRaider.Controller.RemoteDesktop;

namespace Controller.Tests;

public class AdminVncTargetProbeTests
{
    [Fact]
    public async Task ConnectsToOnePrivateAddressOn5900AndStopsBeforeAuthentication()
    {
        var stream = new ScriptedDuplexStream(Response("RFB 003.008\n", 2, 1, 2));
        IPAddress? connectedAddress = null;
        var connectedPort = 0;
        var probe = new AdminVncTargetProbe((address, port, _) =>
        {
            connectedAddress = address;
            connectedPort = port;
            return Task.FromResult<Stream>(stream);
        });

        var supported = await probe.SupportsReadOnlyGatewayAsync(
            IPAddress.Parse("192.168.1.18"),
            CancellationToken.None);

        Assert.True(supported);
        Assert.Equal(IPAddress.Parse("192.168.1.18"), connectedAddress);
        Assert.Equal(5900, connectedPort);
        Assert.Equal(Encoding.ASCII.GetBytes("RFB 003.008\n"), stream.Written);
    }

    [Fact]
    public async Task RejectsAListenerThatDoesNotOfferVncAuthentication()
    {
        var stream = new ScriptedDuplexStream(Response("RFB 003.008\n", 1, 1));
        var probe = new AdminVncTargetProbe((_, _, _) => Task.FromResult<Stream>(stream));

        var supported = await probe.SupportsReadOnlyGatewayAsync(
            IPAddress.Parse("10.1.2.3"),
            CancellationToken.None);

        Assert.False(supported);
        Assert.Equal(Encoding.ASCII.GetBytes("RFB 003.008\n"), stream.Written);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.0.2.8")]
    [InlineData("203.0.113.8")]
    [InlineData("2001:db8::8")]
    public async Task NeverOpensASocketToLoopbackOrNonPrivateAddresses(string value)
    {
        var connectCalls = 0;
        var probe = new AdminVncTargetProbe((_, _, _) =>
        {
            connectCalls++;
            return Task.FromResult<Stream>(new ScriptedDuplexStream([]));
        });

        var supported = await probe.SupportsReadOnlyGatewayAsync(
            IPAddress.Parse(value),
            CancellationToken.None);

        Assert.False(supported);
        Assert.Equal(0, connectCalls);
    }

    [Fact]
    public async Task ProbeTimeoutBoundsTheTcpAndRfbHandshake()
    {
        var probe = new AdminVncTargetProbe(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        });
        var started = DateTimeOffset.UtcNow;

        var supported = await probe.SupportsReadOnlyGatewayAsync(
            IPAddress.Parse("10.1.2.3"),
            CancellationToken.None);

        Assert.False(supported);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    private static byte[] Response(string version, byte count, params byte[] securityTypes) =>
        [.. Encoding.ASCII.GetBytes(version), count, .. securityTypes];

    private sealed class ScriptedDuplexStream(byte[] response) : Stream
    {
        private readonly MemoryStream written = new();
        private int responseOffset;

        public byte[] Written => written.ToArray();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(response.AsSpan(responseOffset), buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, response.Length - responseOffset);
            response.AsMemory(responseOffset, count).CopyTo(buffer);
            responseOffset += count;
            return ValueTask.FromResult(count);
        }

        public override void Write(byte[] buffer, int offset, int count) => written.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush() => written.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => written.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private int Read(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            var count = Math.Min(source.Length, destination.Length);
            source[..count].CopyTo(destination);
            responseOffset += count;
            return count;
        }
    }
}
