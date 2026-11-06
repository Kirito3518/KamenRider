using System.Buffers.Binary;
using System.Text;
using KamenRaider.Controller.RemoteDesktop;

namespace Controller.Tests;

public class AdminVncReadOnlyProtocolTests
{
    [Fact]
    public void SupportsRfb38VncAuthenticationAndAllowsOnlyDisplayMessages()
    {
        var protocol = CreateReadyProtocol();
        var safe = new byte[]
        {
            2, 0, 0, 1, 0, 0, 0, 0, // SetEncodings: Raw only
            3, 0, 0, 0, 0, 0, 4, 0, 3, 0, // FramebufferUpdateRequest
            150, 1, 0, 0, 0, 0, 4, 0, 3, 0 // EnableContinuousUpdates
        };

        Assert.Equal(safe, protocol.FilterClientMessage(safe));
    }

    [Fact]
    public void RemovesExtendedClipboardNegotiationAndRejectsEveryClipboardMessage()
    {
        var protocol = CreateReadyProtocol();
        var encodings = new byte[]
        {
            2, 0, 0, 2,
            0, 0, 0, 0, // Raw
            0xC0, 0xA1, 0xE5, 0xCE // Extended clipboard pseudo-encoding
        };
        Assert.Equal(new byte[] { 2, 0, 0, 1, 0, 0, 0, 0 }, protocol.FilterClientMessage(encodings));

        var capabilities = new byte[]
        {
            6, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xF8,
            0x1F, 0, 0, 1, 0, 0, 0, 0
        };
        Assert.Throws<InvalidDataException>(() => protocol.FilterClientMessage(capabilities));

        var clipboardData = new byte[]
        {
            6, 0, 0, 0, 0, 0, 0, 2, (byte)'o', (byte)'k'
        };
        Assert.Throws<InvalidDataException>(() => protocol.FilterClientMessage(clipboardData));
    }

    [Fact]
    public void FiltersServerClipboardWithoutDroppingFramebufferMessages()
    {
        var protocol = CreateReadyProtocol();
        var framebufferUpdate = new byte[]
        {
            0, 0, 0, 1, // One framebuffer rectangle
            0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, // Raw 1x1 rectangle
            1, 2, 3, 4
        };
        var clipboard = new byte[] { 3, 0, 0, 0, 0, 0, 0, 5, 115, 101, 99, 114, 101 };
        var bell = new byte[] { 2 };

        Assert.Equal(framebufferUpdate.Concat(bell), protocol.FilterServerBytes(framebufferUpdate.Concat(clipboard).Concat(bell).ToArray()));
    }

    [Fact]
    public void FiltersFragmentedServerClipboardAndKeepsOnlyRawFramebufferEncodings()
    {
        var protocol = CreateReadyProtocol();
        var clipboard = new byte[] { 3, 0, 0, 0, 0, 0, 0, 5, 115, 101, 99, 114, 101 };
        Assert.Empty(protocol.FilterServerBytes(clipboard.AsSpan(0, 7)));
        Assert.Empty(protocol.FilterServerBytes(clipboard.AsSpan(7)));
        var extendedClipboard = new byte[] { 3, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFC, 1, 2, 3, 4 };
        Assert.Throws<InvalidDataException>(() => protocol.FilterServerBytes(extendedClipboard));
        var malformedExtendedClipboard = new byte[] { 3, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };
        Assert.Throws<InvalidDataException>(() => CreateReadyProtocol().FilterServerBytes(malformedExtendedClipboard));

        var encodings = new byte[]
        {
            2, 0, 0, 6,
            0, 0, 0, 0, // Raw
            0, 0, 0, 2, // RRE
            0, 0, 0, 50, // H.264
            0xFF, 0xFF, 0xFF, 0x21, // DesktopSize
            0xFF, 0xFF, 0xFE, 0xC8, // Fence
            0xFF, 0xFF, 0xE5, 0xCE // Extended clipboard
        };
        var expected = new byte[]
        {
            2, 0, 0, 3,
            0, 0, 0, 0,
            0xFF, 0xFF, 0xFF, 0x21,
            0xFF, 0xFF, 0xFE, 0xC8
        };
        Assert.Equal(expected, protocol.FilterClientMessage(encodings));
    }

    [Fact]
    public void PassesRfbHandshakeAndFiltersClipboardAfterServerInit()
    {
        var protocol = new ReadOnlyVncProtocol();
        var banner = Encoding.ASCII.GetBytes("RFB 003.008\n");
        Assert.Equal(banner, protocol.FilterServerBytes(banner));
        Assert.Equal(banner, protocol.FilterClientMessage(banner));

        var securityOffer = new byte[] { 1, 2 };
        Assert.Equal(securityOffer, protocol.FilterServerBytes(securityOffer));
        Assert.Equal(new byte[] { 2 }, protocol.FilterClientMessage([2]));
        var challenge = new byte[16];
        Assert.Equal(challenge, protocol.FilterServerBytes(challenge));
        Assert.Equal(challenge, protocol.FilterClientMessage(challenge));
        var success = new byte[4];
        Assert.Equal(success, protocol.FilterServerBytes(success));
        Assert.Equal(new byte[] { 1 }, protocol.FilterClientMessage([1]));

        var serverInit = NewServerInit();
        Assert.Equal(serverInit, protocol.FilterServerBytes(serverInit));
        var clipboard = new byte[] { 3, 0, 0, 0, 0, 0, 0, 5, 115, 101, 99, 114, 101 };
        Assert.Empty(protocol.FilterServerBytes(clipboard));
    }

    [Fact]
    public void BuffersPartialRawFramebufferMessagesUntilTheirPayloadIsComplete()
    {
        var protocol = CreateReadyProtocol();
        var framebufferUpdate = new byte[]
        {
            0, 0, 0, 1,
            0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0,
            1, 2, 3, 4
        };

        Assert.Empty(protocol.FilterServerBytes(framebufferUpdate.AsSpan(0, 18)));
        Assert.Equal(framebufferUpdate, protocol.FilterServerBytes(framebufferUpdate.AsSpan(18)));
    }

    [Fact]
    public void RejectsPixelFormatChangeDuringFragmentedFrameBeforeFilteringFollowingClipboard()
    {
        var protocol = CreateReadyProtocol();
        var framebufferUpdate = new byte[]
        {
            0, 0, 0, 1,
            0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0,
            1, 2, 3, 4
        };
        var clipboard = new byte[] { 3, 0, 0, 0, 0, 0, 0, 5, 115, 101, 99, 114, 101 };

        Assert.Empty(protocol.FilterServerBytes(framebufferUpdate.AsSpan(0, 17)));
        var setPixelFormat = new byte[20];
        setPixelFormat[4] = 16;
        setPixelFormat[5] = 16;
        setPixelFormat[7] = 1;
        Assert.Throws<InvalidDataException>(() => protocol.FilterClientMessage(setPixelFormat));

        Assert.Equal(framebufferUpdate, protocol.FilterServerBytes(framebufferUpdate.AsSpan(17).ToArray().Concat(clipboard).ToArray()));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(250)]
    [InlineData(251)]
    [InlineData(255)]
    public void RejectsInputClipboardPowerAndUnknownMessages(byte messageType)
    {
        var protocol = CreateReadyProtocol();
        var input = new byte[] { messageType, 0, 0, 0, 0, 0 };

        Assert.Throws<InvalidDataException>(() => protocol.FilterClientMessage(input));
    }

    [Fact]
    public void AcceptsFragmentedServerHandshakeAndFenceMessages()
    {
        var protocol = new ReadOnlyVncProtocol();
        var banner = Encoding.ASCII.GetBytes("RFB 003.008\n");
        protocol.ObserveServerBytes(banner.AsSpan(0, 5));
        protocol.ObserveServerBytes(banner.AsSpan(5));
        Assert.Equal(banner, protocol.FilterClientMessage(banner));

        protocol.ObserveServerBytes([1]);
        protocol.ObserveServerBytes([2]);
        Assert.Equal(new byte[] { 2 }, protocol.FilterClientMessage([2]));
        protocol.ObserveServerBytes(new byte[16]);
        Assert.Equal(new byte[16], protocol.FilterClientMessage(new byte[16]));
        protocol.ObserveServerBytes([0, 0, 0, 0]);
        Assert.Equal(new byte[] { 1 }, protocol.FilterClientMessage([1]));

        var serverInit = NewServerInit();
        protocol.ObserveServerBytes(serverInit);

        var fence = new byte[] { 248, 0, 0, 0, 0, 0, 0, 0, 2, 9, 10 };
        Assert.Equal(fence, protocol.FilterClientMessage(fence));
    }

    [Fact]
    public void RefusesServersWithoutVncAuthenticationOrModernProtocol()
    {
        var noAuthentication = new ReadOnlyVncProtocol();
        noAuthentication.ObserveServerBytes(Encoding.ASCII.GetBytes("RFB 003.008\n"));
        Assert.Throws<InvalidDataException>(() => noAuthentication.ObserveServerBytes([1, 1]));

        var oldProtocol = new ReadOnlyVncProtocol();
        Assert.Throws<InvalidDataException>(() => oldProtocol.ObserveServerBytes(Encoding.ASCII.GetBytes("RFB 003.003\n")));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void RejectsServerPixelFormatsThatAreNot32Bpp(byte bitsPerPixel)
    {
        var protocol = CreateProtocolAwaitingServerInit();
        var serverInit = NewServerInit();
        serverInit[4] = bitsPerPixel;

        Assert.Throws<InvalidDataException>(() => protocol.ObserveServerBytes(serverInit));
    }

    [Theory]
    [InlineData(5, 0)] // Zero depth
    [InlineData(5, 23)] // Does not account for all 24 color bits
    [InlineData(5, 33)] // Depth cannot exceed bits per pixel
    [InlineData(6, 2)] // Invalid endian flag
    [InlineData(9, 0)] // Zero red range
    [InlineData(9, 240)] // Red maximum is not 2^N - 1
    [InlineData(14, 8)] // Red overlaps green
    [InlineData(14, 31)] // Red extends beyond 32-bit pixel
    public void RejectsServerPixelFormatsWithInvalidRangesOrOverlappingColorChannels(int fieldOffset, int value)
    {
        var protocol = CreateProtocolAwaitingServerInit();
        var serverInit = NewServerInit();
        serverInit[fieldOffset] = checked((byte)value);

        Assert.Throws<InvalidDataException>(() => protocol.ObserveServerBytes(serverInit));
    }

    private static ReadOnlyVncProtocol CreateReadyProtocol()
    {
        var protocol = CreateProtocolAwaitingServerInit();
        protocol.ObserveServerBytes(NewServerInit());
        return protocol;
    }

    private static ReadOnlyVncProtocol CreateProtocolAwaitingServerInit()
    {
        var protocol = new ReadOnlyVncProtocol();
        var banner = Encoding.ASCII.GetBytes("RFB 003.008\n");
        protocol.ObserveServerBytes(banner);
        Assert.Equal(banner, protocol.FilterClientMessage(banner));
        protocol.ObserveServerBytes([1, 2]);
        Assert.Equal(new byte[] { 2 }, protocol.FilterClientMessage([2]));
        protocol.ObserveServerBytes(new byte[16]);
        Assert.Equal(new byte[16], protocol.FilterClientMessage(new byte[16]));
        protocol.ObserveServerBytes([0, 0, 0, 0]);
        Assert.Equal(new byte[] { 1 }, protocol.FilterClientMessage([1]));
        return protocol;
    }

    private static byte[] NewServerInit()
    {
        var serverInit = new byte[24];
        BinaryPrimitives.WriteUInt16BigEndian(serverInit.AsSpan(0, 2), 1024);
        BinaryPrimitives.WriteUInt16BigEndian(serverInit.AsSpan(2, 2), 768);
        serverInit[4] = 32;
        serverInit[5] = 24;
        serverInit[7] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(serverInit.AsSpan(8, 2), 255);
        BinaryPrimitives.WriteUInt16BigEndian(serverInit.AsSpan(10, 2), 255);
        BinaryPrimitives.WriteUInt16BigEndian(serverInit.AsSpan(12, 2), 255);
        serverInit[14] = 16;
        serverInit[15] = 8;
        serverInit[16] = 0;
        return serverInit;
    }
}
