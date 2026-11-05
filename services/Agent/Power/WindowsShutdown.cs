using System.Diagnostics;
using System.ComponentModel;
namespace KamenRaider.Agent.Power;
public interface IShutdown {Task RequestImmediateShutdownAsync(CancellationToken ct);}
public sealed class WindowsShutdown:IShutdown {
 public async Task RequestImmediateShutdownAsync(CancellationToken ct){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();using var process=Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"shutdown.exe")){UseShellExecute=false,CreateNoWindow=true,ArgumentList={"/s","/f","/t","0"}})??throw new IOException("Shutdown launch failed");using var killOnCancel=ct.Register(static state=>{try{var child=(Process)state!;if(!child.HasExited)child.Kill();}catch(Exception ex) when(ex is InvalidOperationException or Win32Exception or NotSupportedException){}},process);await process.WaitForExitAsync(ct);if(process.ExitCode!=0)throw new IOException("Shutdown rejected");}
}
