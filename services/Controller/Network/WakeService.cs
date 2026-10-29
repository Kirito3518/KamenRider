using System.Net;
using System.Net.Sockets;
namespace KamenRaider.Controller.Network;
public interface IWakeSender {Task SendAsync(string macAddress,string broadcastAddress,CancellationToken ct);}
public sealed class WakeService(string interfaceAddress):IWakeSender {
 public static byte[] MagicPacket(string macAddress) {
 var normalized=macAddress.Replace(":","").Replace("-","");if(normalized.Length!=12)throw new ArgumentException("Six-byte MAC required");
 byte[] mac;try {mac=Convert.FromHexString(normalized);}catch(FormatException){throw new ArgumentException("Invalid MAC");}
 var packet=new byte[102];Array.Fill(packet,(byte)255,0,6);for(var i=6;i<102;i+=6)mac.CopyTo(packet,i);return packet;
 }
 public async Task SendAsync(string macAddress,string broadcastAddress,CancellationToken ct) {
 var local=IPAddress.Parse(interfaceAddress);var broadcast=IPAddress.Parse(broadcastAddress);if(local.AddressFamily!=AddressFamily.InterNetwork||broadcast.AddressFamily!=AddressFamily.InterNetwork)throw new ArgumentException("IPv4 required");
 using var udp=new UdpClient(new IPEndPoint(local,0)){EnableBroadcast=true};await udp.SendAsync(MagicPacket(macAddress),new IPEndPoint(broadcast,9),ct);
 }
}
