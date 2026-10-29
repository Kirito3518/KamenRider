using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
namespace Controller.Tests;

// Start a gated launcher, assign it to a job, then release the gate. Descendants
// belong to the job before their first instruction, even if the launcher exits.
internal sealed class ContainedProcess : IDisposable
{
    private readonly SafeFileHandle job;
    public Process Process { get; }
    private ContainedProcess(SafeFileHandle job, Process process) => (this.job, Process) = (job, process);

    public static ContainedProcess Start(ProcessStartInfo command)
    {
        static string Encoded(string value) => Convert.ToBase64String(Encoding.Unicode.GetBytes(value));
        static string Decode(string value) => $"[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{Encoded(value)}'))";
        static string Quote(string value) => "\"" + Regex.Replace(value, "(\\\\*)\"", "$1$1\\\"").TrimEnd('\\')
            + new string('\\', value.Reverse().TakeWhile(c => c == '\\').Count() * 2) + "\"";
        var arguments = string.Join(" ", command.ArgumentList.Select(Quote));
        var script = $"$null = [Console]::ReadLine(); $start = New-Object Diagnostics.ProcessStartInfo; $start.FileName = ({Decode(command.FileName)}); $start.Arguments = ({Decode(arguments)}); $start.UseShellExecute = $false; $start.CreateNoWindow = $false; $child = [Diagnostics.Process]::Start($start); $child.WaitForExit(); exit $child.ExitCode";
        var launcher = new ProcessStartInfo("powershell.exe") {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = command.WorkingDirectory
        };
        launcher.ArgumentList.Add("-NoProfile");
        launcher.ArgumentList.Add("-OutputFormat");
        launcher.ArgumentList.Add("Text");
        launcher.ArgumentList.Add("-Command");
        launcher.ArgumentList.Add(script);
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); throw new Win32Exception(); }
        Process? process = null;
        try
        {
            process = Process.Start(launcher)!;
            if (!AssignProcessToJobObject(job, process.Handle)) throw new Win32Exception();
            process.StandardInput.WriteLine();
            process.StandardInput.Close();
            return new ContainedProcess(job, process);
        }
        catch
        {
            if (process is not null) { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); }
            job.Dispose();
            throw;
        }
    }

    public void Terminate()
    {
        if (!TerminateJobObject(job, 1)) throw new Win32Exception();
    }
    public void Dispose()
    {
        try { Terminate(); }
        finally { Process.Dispose(); job.Dispose(); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}

internal static class ChildProcess
{
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(ContainedProcess child, TimeSpan timeout)
    {
        var process = child.Process;
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
            child.Terminate();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanup.Token);
            try { await Task.WhenAll(output, error).WaitAsync(cleanup.Token); }
            catch (OperationCanceledException) { }
            throw new TimeoutException($"Child process exceeded {timeout} and its job was terminated.");
        }
    }
}
