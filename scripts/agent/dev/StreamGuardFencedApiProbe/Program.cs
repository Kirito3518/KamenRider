using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Principal;
using System.Text.Json;
using KamenRaider.Agent.Sunshine;

internal static class Program
{
    private const string PipePath = @"\\.\pipe\" + StreamGuardPipeClient.DefaultPipeName;
    private const int SunshinePort = 47990;

    private sealed record ProbeResult(
        string Status,
        int? HttpStatus,
        bool TlsValidated,
        string? FailureCategory,
        DateTimeOffset FinishedAtUtc);

    private static async Task<int> Main(string[] args)
    {
        var status = "ProbeFailed";
        int? httpStatus = null;
        var tlsValidated = false;
        string? failureCategory = null;

        try
        {
            if (args.Length != 0)
                throw new InvalidOperationException("Unexpected helper arguments.");
            if (!OperatingSystem.IsWindows() || !IsSystem())
                throw new UnauthorizedAccessException("The protected helper must run as LocalSystem on Windows.");
            await WaitForGuardPipeAsync();

            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "KamenRaider",
                "Agent");
            var credentialStore = AuthenticatedSunshineApiProbe.CreateCredentialStore(dataDirectory);
            await using var pipe = new StreamGuardPipeClient(connectTimeout: TimeSpan.FromSeconds(30));
            AuthenticatedSunshineApiProbeResult? apiResult = null;

            var completed = await pipe.RunFencedCleanupAsync(async cancellationToken =>
            {
                apiResult = await AuthenticatedSunshineApiProbe.ReadClientsAsync(
                    credentialStore,
                    SunshinePort,
                    cancellationToken);
            }, CancellationToken.None);

            if (completed && apiResult is not null)
            {
                status = apiResult.Status;
                httpStatus = apiResult.HttpStatus;
                tlsValidated = apiResult.TlsValidated;
                failureCategory = apiResult.FailureCategory;
            }
            else
            {
                status = "FenceRejected";
                failureCategory = "FenceRejected";
            }
        }
        catch (Exception exception)
        {
            status = "ProbeFailed";
            failureCategory = SafeFailureCategory(exception);
        }

        var result = new ProbeResult(status, httpStatus, tlsValidated, failureCategory, DateTimeOffset.UtcNow);
        var resultPath = Path.Combine(AppContext.BaseDirectory, "result.json");
        var temporaryResultPath = Path.Combine(AppContext.BaseDirectory, $".result-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(
                temporaryResultPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, result);
                await output.FlushAsync();
                output.Flush(flushToDisk: true);
            }

            // The stage directory is private to this one-shot task. A same-directory move
            // makes the fixed result name visible only after the complete JSON is flushed.
            File.Move(temporaryResultPath, resultPath);
        }
        catch
        {
            try { File.Delete(temporaryResultPath); }
            catch { }
            return 2;
        }

        return status == "RequestCompleted" ? 0 : 1;
    }

    private static bool IsSystem() =>
        WindowsIdentity.GetCurrent().User?.Value == "S-1-5-18";

    private static async Task WaitForGuardPipeAsync()
    {
        var timer = Stopwatch.StartNew();
        var limit = TimeSpan.FromSeconds(30);
        while (timer.Elapsed < limit)
        {
            var remaining = limit - timer.Elapsed;
            var waitMilliseconds = (uint)Math.Clamp((long)remaining.TotalMilliseconds, 1, 250);
            if (WaitNamedPipe(PipePath, waitMilliseconds)) return;

            var error = Marshal.GetLastWin32Error();
            // No instance is normal while Guard is closing its previous session.
            // WaitNamedPipe returns immediately in that case, regardless of its timeout.
            if (error is not (2 or 121 or 231))
                throw new Win32Exception(error, "Unable to wait for the Stream Guard pipe.");
            remaining = limit - timer.Elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException("The Stream Guard pipe did not become available.");
    }

    private static string SafeFailureCategory(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is AuthenticationException)
                return "TlsValidationFailed";
        }

        return exception switch
        {
            TimeoutException or TaskCanceledException => "TimedOut",
            UnauthorizedAccessException => "AccessDenied",
            IOException => "PipeOrTransportFailed",
            InvalidOperationException => "FenceRejected",
            _ => exception.GetType().Name
        };
    }

    [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string name, uint timeoutMilliseconds);
}
