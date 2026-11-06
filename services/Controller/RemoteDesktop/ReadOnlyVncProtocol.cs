using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace KamenRaider.Controller.RemoteDesktop;

internal sealed class ReadOnlyVncProtocol
{
    private const int MaximumServerNameBytes = 1024 * 1024;
    private const int MaximumServerMessageBytes = 64 * 1024 * 1024;
    private const long MaximumFramebufferPixels = 16L * 1024 * 1024;
    private const int ExtendedClipboardPseudoEncoding = unchecked((int)0xC0A1E5CE);
    private const int DesktopSizePseudoEncoding = -223;
    private const int FencePseudoEncoding = -312;
    private const int ContinuousUpdatesPseudoEncoding = -313;

    private enum ServerPhase { Version, SecurityTypeCount, SecurityTypes, Challenge, SecurityResult, WaitClientInit, ServerInitHeader, ServerInitName, Normal }
    private enum ClientPhase { Version, SecuritySelection, AuthenticationResponse, SecurityResult, ClientInit, ServerInit, Normal }

    private readonly object gate = new();
    private readonly List<byte> serverBuffer = [];
    private readonly List<byte> serverMessageBuffer = [];
    private ServerPhase serverPhase = ServerPhase.Version;
    private ClientPhase clientPhase = ClientPhase.Version;
    private int securityTypeCount;
    private byte[] offeredSecurityTypes = [];
    private int serverNameBytes;
    private int framebufferWidth;
    private int framebufferHeight;
    private int bytesPerPixel;

    // Used by protocol tests that only need to advance the server handshake.
    public void ObserveServerBytes(ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            if (serverPhase == ServerPhase.Normal) return;
            serverBuffer.AddRange(bytes.ToArray());
            ProcessServerHandshake();
        }
    }

    // The gateway sends the server handshake unchanged, then parses normal RFB
    // messages so clipboard payloads are dropped before they reach the browser.
    public byte[] FilterServerBytes(ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            if (serverPhase == ServerPhase.Normal)
            {
                if (serverBuffer.Count == 0) return FilterNormalServerBytes(bytes);
                serverBuffer.AddRange(bytes.ToArray());
                var pending = serverBuffer.ToArray();
                serverBuffer.Clear();
                return FilterNormalServerBytes(pending);
            }

            var previousHandshakeBytes = serverBuffer.Count;
            serverBuffer.AddRange(bytes.ToArray());
            var bufferedHandshakeBytes = serverBuffer.Count;
            ProcessServerHandshake();
            if (serverPhase != ServerPhase.Normal) return bytes.ToArray();

            var consumedHandshakeBytes = bufferedHandshakeBytes - serverBuffer.Count;
            var currentHandshakeBytes = Math.Clamp(consumedHandshakeBytes - previousHandshakeBytes, 0, bytes.Length);
            using var output = new MemoryStream(bytes.Length);
            output.Write(bytes[..currentHandshakeBytes]);
            if (serverBuffer.Count > 0)
            {
                output.Write(FilterNormalServerBytes(serverBuffer.ToArray()));
                serverBuffer.Clear();
            }
            return output.ToArray();
        }
    }

    private void ProcessServerHandshake()
    {
        while (true)
        {
            switch (serverPhase)
            {
                case ServerPhase.Version:
                    if (serverBuffer.Count < 12) return;
                    var version = Encoding.ASCII.GetString(serverBuffer.Take(12).ToArray());
                    if (version is not ("RFB 003.007\n" or "RFB 003.008\n"))
                        throw new InvalidDataException("Only RFB 3.7/3.8 is supported by the read-only gateway");
                    ConsumeServerBytes(12);
                    serverPhase = ServerPhase.SecurityTypeCount;
                    break;

                case ServerPhase.SecurityTypeCount:
                    if (serverBuffer.Count < 1) return;
                    securityTypeCount = serverBuffer[0];
                    ConsumeServerBytes(1);
                    if (securityTypeCount == 0)
                        throw new InvalidDataException("VNC server reported a security negotiation failure");
                    serverPhase = ServerPhase.SecurityTypes;
                    break;

                case ServerPhase.SecurityTypes:
                    if (serverBuffer.Count < securityTypeCount) return;
                    offeredSecurityTypes = serverBuffer.Take(securityTypeCount).ToArray();
                    ConsumeServerBytes(securityTypeCount);
                    if (!offeredSecurityTypes.Contains((byte)2))
                        throw new InvalidDataException("VNC server must offer VNC authentication");
                    serverPhase = ServerPhase.Challenge;
                    break;

                case ServerPhase.Challenge:
                    if (serverBuffer.Count < 16) return;
                    ConsumeServerBytes(16);
                    serverPhase = ServerPhase.SecurityResult;
                    break;

                case ServerPhase.SecurityResult:
                    if (serverBuffer.Count < 4) return;
                    var result = BinaryPrimitives.ReadUInt32BigEndian(serverBuffer.Take(4).ToArray());
                    ConsumeServerBytes(4);
                    if (result != 0)
                        throw new InvalidDataException("VNC authentication failed");
                    serverPhase = ServerPhase.WaitClientInit;
                    clientPhase = ClientPhase.ClientInit;
                    break;

                case ServerPhase.WaitClientInit:
                    if (serverBuffer.Count > 0)
                        throw new InvalidDataException("VNC server sent data before client initialization");
                    return;

                case ServerPhase.ServerInitHeader:
                    if (serverBuffer.Count < 24) return;
                    framebufferWidth = BinaryPrimitives.ReadUInt16BigEndian(CollectionsMarshal.AsSpan(serverBuffer)[..2]);
                    framebufferHeight = BinaryPrimitives.ReadUInt16BigEndian(CollectionsMarshal.AsSpan(serverBuffer)[2..4]);
                    ValidateFramebufferDimensions(framebufferWidth, framebufferHeight);
                    bytesPerPixel = ValidatePixelFormat(CollectionsMarshal.AsSpan(serverBuffer).Slice(4, 16));
                    var nameLength = BinaryPrimitives.ReadUInt32BigEndian(CollectionsMarshal.AsSpan(serverBuffer).Slice(20, 4));
                    if (nameLength > MaximumServerNameBytes)
                        throw new InvalidDataException("VNC server name is too large");
                    serverNameBytes = (int)nameLength;
                    ConsumeServerBytes(24);
                    if (serverNameBytes == 0)
                    {
                        serverPhase = ServerPhase.Normal;
                        clientPhase = ClientPhase.Normal;
                        return;
                    }
                    serverPhase = ServerPhase.ServerInitName;
                    break;

                case ServerPhase.ServerInitName:
                    if (serverBuffer.Count < serverNameBytes) return;
                    ConsumeServerBytes(serverNameBytes);
                    serverPhase = ServerPhase.Normal;
                    clientPhase = ClientPhase.Normal;
                    return;

                case ServerPhase.Normal:
                    return;

                default:
                    throw new InvalidDataException("Invalid VNC server state");
            }
        }
    }

    public byte[] FilterClientMessage(ReadOnlySpan<byte> message)
    {
        lock (gate)
        {
            switch (clientPhase)
            {
                case ClientPhase.Version:
                    RequireMessage(message, 12, "client protocol version");
                    if (Encoding.ASCII.GetString(message) is not ("RFB 003.007\n" or "RFB 003.008\n") || serverPhase != ServerPhase.SecurityTypeCount)
                        throw new InvalidDataException("Invalid RFB client protocol version");
                    clientPhase = ClientPhase.SecuritySelection;
                    return message.ToArray();

                case ClientPhase.SecuritySelection:
                    RequireMessage(message, 1, "security selection");
                    if (serverPhase != ServerPhase.Challenge || message[0] != 2 || !offeredSecurityTypes.Contains(message[0]))
                        throw new InvalidDataException("The read-only gateway requires VNC authentication");
                    clientPhase = ClientPhase.AuthenticationResponse;
                    return message.ToArray();

                case ClientPhase.AuthenticationResponse:
                    RequireMessage(message, 16, "VNC authentication response");
                    clientPhase = ClientPhase.SecurityResult;
                    return message.ToArray();

                case ClientPhase.SecurityResult:
                    throw new InvalidDataException("VNC client sent data before authentication completed");

                case ClientPhase.ClientInit:
                    RequireMessage(message, 1, "client initialization");
                    if (serverPhase != ServerPhase.WaitClientInit || message[0] > 1)
                        throw new InvalidDataException("Invalid VNC client initialization");
                    serverPhase = ServerPhase.ServerInitHeader;
                    clientPhase = ClientPhase.ServerInit;
                    return message.ToArray();

                case ClientPhase.ServerInit:
                    throw new InvalidDataException("VNC client sent data before server initialization completed");

                case ClientPhase.Normal:
                    return FilterReadOnlyMessages(message);

                default:
                    throw new InvalidDataException("Invalid VNC client state");
            }
        }
    }

    private byte[] FilterReadOnlyMessages(ReadOnlySpan<byte> message)
    {
        using var allowed = new MemoryStream(message.Length);
        var offset = 0;
        while (offset < message.Length)
        {
            var remaining = message[offset..];
            int length;
            switch (remaining[0])
            {
                case 0: // SetPixelFormat
                    length = 20;
                    if (remaining.Length < length) throw new InvalidDataException("Truncated VNC SetPixelFormat message");
                    var requestedBytesPerPixel = ValidatePixelFormat(remaining.Slice(4, 16));
                    if (requestedBytesPerPixel != bytesPerPixel)
                        throw new InvalidDataException("VNC pixel size cannot change during a read-only session");
                    break;

                case 2: // SetEncodings
                    if (remaining.Length < 4) throw new InvalidDataException("Truncated VNC SetEncodings message");
                    length = checked(4 + BinaryPrimitives.ReadUInt16BigEndian(remaining[2..4]) * 4);
                    if (remaining.Length < length) throw new InvalidDataException("Truncated VNC SetEncodings message");
                    var accepted = new List<int>();
                    var countBytes = new byte[2];
                    var encodingBytes = new byte[4];
                    var encodingCount = BinaryPrimitives.ReadUInt16BigEndian(remaining[2..4]);
                    for (var i = 0; i < encodingCount; i++)
                    {
                        var encoding = BinaryPrimitives.ReadInt32BigEndian(remaining.Slice(4 + i * 4, 4));
                        if (encoding is 0 or DesktopSizePseudoEncoding or FencePseudoEncoding or ContinuousUpdatesPseudoEncoding)
                            accepted.Add(encoding);
                    }
                    allowed.WriteByte(2);
                    allowed.WriteByte(remaining[1]);
                    BinaryPrimitives.WriteUInt16BigEndian(countBytes, checked((ushort)accepted.Count));
                    allowed.Write(countBytes);
                    foreach (var encoding in accepted)
                    {
                        BinaryPrimitives.WriteInt32BigEndian(encodingBytes, encoding);
                        allowed.Write(encodingBytes);
                    }
                    offset += length;
                    continue;

                case 3: // FramebufferUpdateRequest
                case 150: // EnableContinuousUpdates
                    length = 10;
                    break;

                case 248: // Fence; synchronization only, no input
                    if (remaining.Length < 9) throw new InvalidDataException("Truncated VNC Fence message");
                    length = 9 + remaining[8];
                    break;

                default:
                    throw new InvalidDataException($"VNC client message {remaining[0]} is not allowed in a view-only session");
            }

            if (remaining.Length < length)
                throw new InvalidDataException("Truncated VNC client message");
            allowed.Write(remaining[..length]);
            offset += length;
        }
        return allowed.ToArray();
    }

    private byte[] FilterNormalServerBytes(ReadOnlySpan<byte> bytes)
    {
        if (serverMessageBuffer.Count + bytes.Length > MaximumServerMessageBytes)
            throw new InvalidDataException("VNC server message exceeds the configured limit");
        serverMessageBuffer.AddRange(bytes.ToArray());
        var buffered = CollectionsMarshal.AsSpan(serverMessageBuffer);
        using var allowed = new MemoryStream(buffered.Length);
        var offset = 0;
        while (offset < buffered.Length)
        {
            var messageLength = GetServerMessageLength(buffered[offset..]);
            if (messageLength is null) break;
            if (buffered[offset] != 3) allowed.Write(buffered.Slice(offset, messageLength.Value));
            offset += messageLength.Value;
        }
        if (offset > 0) serverMessageBuffer.RemoveRange(0, offset);
        if (serverMessageBuffer.Count > MaximumServerMessageBytes)
            throw new InvalidDataException("VNC server message exceeds the configured limit");
        return allowed.ToArray();
    }

    private int? GetServerMessageLength(ReadOnlySpan<byte> message)
    {
        switch (message[0])
        {
            case 0: // FramebufferUpdate: the gateway only offered Raw and DesktopSize.
                if (message.Length < 4) return null;
                var rectangleCount = BinaryPrimitives.ReadUInt16BigEndian(message[2..4]);
                var offset = 4;
                for (var i = 0; i < rectangleCount; i++)
                {
                    if (message.Length - offset < 12) return null;
                    var x = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset, 2));
                    var y = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset + 2, 2));
                    var width = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset + 4, 2));
                    var height = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset + 6, 2));
                    var encoding = BinaryPrimitives.ReadInt32BigEndian(message.Slice(offset + 8, 4));
                    offset = checked(offset + 12);

                    if (encoding == 0)
                    {
                        if ((long)x + width > framebufferWidth || (long)y + height > framebufferHeight)
                            throw new InvalidDataException("VNC raw rectangle exceeds the current framebuffer");
                        var payloadLength = checked((long)width * height * bytesPerPixel);
                        if (payloadLength > MaximumServerMessageBytes - offset)
                            throw new InvalidDataException("VNC framebuffer update exceeds the configured limit");
                        if (message.Length - offset < payloadLength) return null;
                        offset = checked(offset + (int)payloadLength);
                    }
                    else if (encoding == DesktopSizePseudoEncoding)
                    {
                        ValidateFramebufferDimensions(width, height);
                        framebufferWidth = width;
                        framebufferHeight = height;
                    }
                    else
                    {
                        throw new InvalidDataException($"VNC server framebuffer encoding {encoding} was not offered by the gateway");
                    }

                    if (offset > MaximumServerMessageBytes)
                        throw new InvalidDataException("VNC framebuffer update exceeds the configured limit");
                }
                return offset;

            case 1: // SetColorMapEntries
                if (message.Length < 6) return null;
                var colorCount = BinaryPrimitives.ReadUInt16BigEndian(message[4..6]);
                var colorMapLength = checked(6 + colorCount * 6);
                return message.Length < colorMapLength ? null : colorMapLength;

            case 2: // Bell
                return 1;

            case 3: // ServerCutText, including extended clipboard messages
                if (message.Length < 8) return null;
                var signedClipboardLength = BinaryPrimitives.ReadInt32BigEndian(message[4..8]);
                if (signedClipboardLength < 0)
                    throw new InvalidDataException("Extended VNC clipboard was not negotiated");
                var clipboardLength = (long)signedClipboardLength;
                if (clipboardLength > MaximumServerMessageBytes - 8)
                    throw new InvalidDataException("VNC clipboard message exceeds the configured limit");
                var cutTextLength = checked(8 + (int)clipboardLength);
                return message.Length < cutTextLength ? null : cutTextLength;

            case 150: // EndOfContinuousUpdates
                return 1;

            case 248: // Fence
                if (message.Length < 9) return null;
                var fenceLength = 9 + message[8];
                return message.Length < fenceLength ? null : fenceLength;

            default:
                throw new InvalidDataException($"VNC server message {message[0]} is not supported by the read-only gateway");
        }
    }

    private static void ValidateFramebufferDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaximumFramebufferPixels)
            throw new InvalidDataException("VNC framebuffer dimensions exceed the configured limit");
    }

    private static int ValidatePixelFormat(ReadOnlySpan<byte> format)
    {
        if (format.Length != 16)
            throw new InvalidDataException("Invalid VNC pixel format length");

        var bitsPerPixel = format[0];
        var depth = format[1];
        if (bitsPerPixel != 32 || depth == 0 || depth > bitsPerPixel || format[2] > 1 || format[3] != 1)
            throw new InvalidDataException("VNC must use a 32-bpp true-color pixel format");

        uint usedChannelBits = 0;
        var channelDepth = 0;
        for (var channel = 0; channel < 3; channel++)
        {
            var maximum = BinaryPrimitives.ReadUInt16BigEndian(format.Slice(4 + channel * 2, 2));
            var shift = format[10 + channel];
            var maximumValue = (uint)maximum;
            if (maximum == 0 || (maximumValue & (maximumValue + 1)) != 0)
                throw new InvalidDataException("VNC color channel maximum must be a non-zero sequence of low bits");

            var channelBits = BitOperations.PopCount(maximumValue);
            if (shift >= bitsPerPixel || channelBits > bitsPerPixel - shift)
                throw new InvalidDataException("VNC color channel extends beyond the pixel size");

            var channelMask = maximumValue << shift;
            if ((usedChannelBits & channelMask) != 0)
                throw new InvalidDataException("VNC color channels overlap");

            usedChannelBits |= channelMask;
            channelDepth += channelBits;
        }

        if (channelDepth != depth)
            throw new InvalidDataException("VNC pixel depth does not match its color channels");

        return bitsPerPixel / 8;
    }

    private void ConsumeServerBytes(int count) => serverBuffer.RemoveRange(0, count);

    private static void RequireMessage(ReadOnlySpan<byte> message, int length, string description)
    {
        if (message.Length != length)
            throw new InvalidDataException($"Invalid VNC {description} message length");
    }
}
