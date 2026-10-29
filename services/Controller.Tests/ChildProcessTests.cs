using System.Diagnostics;
namespace Controller.Tests;

public class ChildProcessTests
{
    private static Process Start(string script)
    {
        var start = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);
        return Process.Start(start)!;
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
            Assert.True(process.HasExited);
            Assert.True(child.WaitForExit(5000), "Descendant process survived timeout cleanup.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
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
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}

internal static class ChildProcess
{
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(Process process, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(output, error).WaitAsync(deadline.Token);
            return (process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanup.Token);
            try { await Task.WhenAll(output, error).WaitAsync(cleanup.Token); }
            catch (OperationCanceledException) { }
            throw new TimeoutException($"Child process exceeded {timeout} and was terminated.");
        }
    }
}
