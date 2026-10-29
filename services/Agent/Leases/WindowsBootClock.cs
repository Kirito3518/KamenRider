using System.Runtime.InteropServices;
namespace KamenRaider.Agent.Leases;
// Both values belong to the Windows boot, not the service process or wall clock.
public sealed class WindowsBootClock:IBootClock {
 public string BootId {get;}
 public WindowsBootClock(){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();var info=new BootEnvironment();var status=NtQuerySystemInformation(90,ref info,Marshal.SizeOf<BootEnvironment>(),out _);if(status!=0||info.BootIdentifier==Guid.Empty)throw new IOException("Windows boot identity unavailable");BootId=info.BootIdentifier.ToString("N");}
 public long UptimeMilliseconds=>checked((long)GetTickCount64());
 [StructLayout(LayoutKind.Sequential)] private struct BootEnvironment {public Guid BootIdentifier;public int FirmwareType;public ulong BootFlags;}
 [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int informationClass,ref BootEnvironment information,int length,out int returnLength);
 // Explicit Win32 API includes sleep/hibernation; do not substitute Environment.TickCount64.
 [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
}
