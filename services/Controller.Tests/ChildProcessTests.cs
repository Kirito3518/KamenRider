using System.Diagnostics;
namespace Controller.Tests;

public class ChildProcessTests
{
    private static ContainedProcess Start(string script)
    {
        var start = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);
        return ContainedProcess.Start(start);
    }

    [Fact]
    public async Task Timeout_KillsStalledProcessTree()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pid");
        using var process = Start($"$child = Start-Process powershell.exe -ArgumentList '-NoProfile', '-Command', 'Start-Sleep -Seconds 60' -WindowStyle Hidden -PassThru; [IO.File]::WriteAllText('{pidFile}', $child.Id); Start-Sleep -Seconds 60");
        Process? child = null;
        try
        {
            using var readiness = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(pidFile)) await Task.Delay(50, readiness.Token);
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile, readiness.Token)));
            var execution = ChildProcess.RunAsync(process, TimeSpan.FromMilliseconds(200));
            Assert.Same(execution, await Task.WhenAny(execution, Task.Delay(TimeSpan.FromSeconds(5))));
            await Assert.ThrowsAsync<TimeoutException>(() => execution);
            Assert.True(process.Process.HasExited);
            Assert.True(child.WaitForExit(5000), "Descendant process survived timeout cleanup.");
        }
        finally
        {
            if (!process.Process.HasExited) process.Terminate();
            if (child is not null) { if (!child.HasExited) child.Kill(entireProcessTree: true); child.Dispose(); }
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task Timeout_KillsPipeHoldingDescendantAfterParentExits()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pid");
        using var process = Start($"$start = New-Object Diagnostics.ProcessStartInfo 'powershell.exe'; $start.UseShellExecute = $false; $start.Arguments = '-NoProfile -Command Start-Sleep -Seconds 60'; $child = [Diagnostics.Process]::Start($start); [IO.File]::WriteAllText('{pidFile}', $child.Id); exit 0");
        Process? child = null;
        try
        {
            using var readiness = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!process.Process.HasExited) await Task.Delay(50, readiness.Token);
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile, readiness.Token)));
            var execution = ChildProcess.RunAsync(process, TimeSpan.FromMilliseconds(200));
            Assert.Same(execution, await Task.WhenAny(execution, Task.Delay(TimeSpan.FromSeconds(5))));
            await Assert.ThrowsAsync<TimeoutException>(() => execution);
            Assert.True(child.WaitForExit(1000), "Pipe-holding descendant survived after parent exit.");
        }
        finally
        {
            if (!process.Process.HasExited) process.Terminate();
            if (child is not null) { if (!child.HasExited) child.Kill(entireProcessTree: true); child.Dispose(); }
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task Output_DrainsBothPipesWithoutDeadlock()
    {
        using var process = Start("[Console]::Error.Write(('e' * 200000)); [Console]::Out.Write(('o' * 200000))");
        try
        {
            var execution = ChildProcess.RunAsync(process, TimeSpan.FromSeconds(10));
            Assert.Same(execution, await Task.WhenAny(execution, Task.Delay(TimeSpan.FromSeconds(15))));
            var result = await execution;
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(new string('o', 200000), result.Output);
            Assert.Equal(new string('e', 200000), result.Error);
        }
        finally { if (!process.Process.HasExited) process.Terminate(); }
    }
}
