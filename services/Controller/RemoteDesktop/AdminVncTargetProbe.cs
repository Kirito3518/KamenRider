using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KamenRaider.Controller.RemoteDesktop;

public interface IAdminVncTargetProbe
{
    Task<bool> SupportsReadOnlyGatewayAsync(IPAddress address, CancellationToken cancellationToken);
}

public sealed class AdminVncTargetProbe : IAdminVncTargetProbe
{
    private const int VncPort = 5900;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private readonly Func<IPAddress, int, CancellationToken, Task<Stream>> connect;

    public AdminVncTargetProbe() : this(ConnectAsync)
    {
    }

    internal AdminVncTargetProbe(Func<IPAddress, int, CancellationToken, Task<Stream>> connect)
    {
        this.connect = connect;
    }

    public async Task<bool> SupportsReadOnlyGatewayAsync(IPAddress address, CancellationToken cancellationToken)
    {
        if (!AdminVncTargetAddress.IsPrivate(address))
            return false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await using var stream = await connect(address, VncPort, timeout.Token);
            return await VerifyRfbServerAsync(stream, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException)
        {
            return false;
        }
    }

    internal static async Task<bool> VerifyRfbServerAsync(Stream stream, CancellationToken cancellationToken)
    {
        var serverVersion = new byte[12];
        try
        {
            await stream.ReadExactlyAsync(serverVersion, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            return false;
        }

        if (!IsSupportedRfbVersion(serverVersion))
            return false;

        // Negotiate only the protocol version. The probe closes before sending
        // any security selection or VNC password response.
        await stream.WriteAsync(serverVersion, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var count = new byte[1];
        try
        {
            await stream.ReadExactlyAsync(count, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            return false;
        }

        if (count[0] == 0)
            return false;

        var securityTypes = new byte[count[0]];
        try
        {
            await stream.ReadExactlyAsync(securityTypes, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            return false;
        }

        return securityTypes.Contains((byte)2);
    }

    private static bool IsSupportedRfbVersion(ReadOnlySpan<byte> version) =>
        version.SequenceEqual("RFB 003.007\n"u8) || version.SequenceEqual("RFB 003.008\n"u8);

    private static async Task<Stream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient(address.AddressFamily);
        try
        {
            await client.ConnectAsync(address, port, cancellationToken);
            return client.GetStream();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

internal static class AdminVncTargetAddress
{
    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        if (!TryParse(value, out var address))
            throw new ArgumentException("VNC target must be a private LAN IP address");
        return address.ToString();
    }

    internal static IPAddress Parse(string? value)
    {
        if (!TryParse(value, out var address))
            throw new ArgumentException("VNC target must be a private LAN IP address");
        return address;
    }

    internal static bool TryParse(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrEmpty(value)
            || value.Length > 45
            || value != value.Trim()
            || !IPAddress.TryParse(value, out var parsed)
            || parsed.ToString().Contains('%'))
            return false;

        address = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
        return IsPrivate(address);
    }

    internal static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 10
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168;

        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6UniqueLocal;
    }
}
