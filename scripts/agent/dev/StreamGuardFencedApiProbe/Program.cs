using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Principal;
using System.Text.Json;
using KamenRaider.Agent.Sunshine;

internal static class Program
{
    private const string PipePath = @"\\.\pipe\" + StreamGuardPipeClient.DefaultPipeName;
    private const int SunshinePort = 47990;
    private static readonly TimeSpan SetupWindowLimit = TimeSpan.FromSeconds(44);
    private static readonly TimeSpan SetupSignalPollInterval = TimeSpan.FromMilliseconds(100);
    // Stream Guard starts Sunshine before it acknowledges maintenance-begin, but the web
    // server binds its HTTPS listener asynchronously. Bound the wait well inside Guard's
    // 45-second maintenance deadline so maintenance-end is still sent in time.
    private static readonly TimeSpan ManagementReadinessTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ListenerPollInterval = TimeSpan.FromMilliseconds(250);

    private sealed record ProbeResult(
        string Status,
        int? HttpStatus,
        bool TlsValidated,
        string? FailureCategory,
        DateTimeOffset FinishedAtUtc);

    private sealed record SetupReadyMarker(
        string Status,
        DateTimeOffset ReadyAtUtc,
        DateTimeOffset ExpiresAtUtc);

    private static async Task<int> Main(string[] args)
    {
        var status = "ProbeFailed";
        int? httpStatus = null;
        var tlsValidated = false;
        string? failureCategory = null;
        var setupMode = args.Length == 1 && args[0] == "--setup-window";
        var fencedOperationCompleted = false;

        try
        {
            if (args.Length != 0 && !setupMode)
                throw new InvalidOperationException("Unexpected helper arguments.");
            if (!OperatingSystem.IsWindows() || !IsSystem())
                throw new UnauthorizedAccessException("The protected helper must run as LocalSystem on Windows.");

            if (setupMode)
                ClearSetupWindowFiles();

            await WaitForGuardPipeAsync();

            await using var pipe = new StreamGuardPipeClient(connectTimeout: TimeSpan.FromSeconds(30));

            if (setupMode)
            {
                var setupResult = await RunSetupWindowAsync(pipe);
                status = setupResult.Status;
                failureCategory = setupResult.FailureCategory;
                fencedOperationCompleted = setupResult.FencedOperationCompleted;
            }
            else
            {
                var dataDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "KamenRaider",
                    "Agent");
                var credentialStore = AuthenticatedSunshineApiProbe.CreateCredentialStore(dataDirectory);
                AuthenticatedSunshineApiProbeResult? apiResult = null;

                var completed = await pipe.RunFencedCleanupAsync(async cancellationToken =>
                {
                    apiResult = await AuthenticatedSunshineApiProbe.ReadClientsAsync(
                        credentialStore,
                        SunshinePort,
                        cancellationToken,
                        transport: null,
                        readinessTimeout: ManagementReadinessTimeout);
                }, CancellationToken.None);

                if (completed && apiResult is not null)
                {
                    status = apiResult.Status;
                    httpStatus = apiResult.HttpStatus;
                    tlsValidated = apiResult.TlsValidated;
                    failureCategory = apiResult.FailureCategory;
                    fencedOperationCompleted = status == "RequestCompleted";
                }
                else
                {
                    status = "FenceRejected";
                    failureCategory = "FenceRejected";
                }
            }
        }
        catch (Exception exception)
        {
            status = setupMode ? "SetupWindowFailed" : "ProbeFailed";
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

        return fencedOperationCompleted || status == "RequestCompleted" ? 0 : 1;
    }

    private static async Task<(string Status, string? FailureCategory, bool FencedOperationCompleted)> RunSetupWindowAsync(
        StreamGuardPipeClient pipe)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var readyPath = Path.Combine(baseDirectory, "setup-ready.json");
        var donePath = Path.Combine(baseDirectory, "setup-done.signal");
        var startedAtUtc = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        using var setupBudget = new CancellationTokenSource(SetupWindowLimit);
        var status = "SetupWindowFailed";
        string? failureCategory = null;

        try
        {
            var completed = await pipe.RunFencedCleanupAsync(async cancellationToken =>
            {
                var expiresAtUtc = startedAtUtc + SetupWindowLimit;
                await WaitForManagementListenerAsync(ManagementReadinessTimeout, cancellationToken);
                await WriteSetupReadyMarkerAsync(readyPath, expiresAtUtc);

                try
                {
                    var signaled = await WaitForSetupSignalAsync(donePath, timer, cancellationToken);
                    status = signaled ? "SetupWindowCompleted" : "SetupWindowTimedOut";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Return normally so StreamGuardPipeClient's finally can send maintenance-end.
                    status = "SetupWindowTimedOut";
                }
            }, setupBudget.Token);

            if (!completed)
            {
                status = "FenceRejected";
                failureCategory = "FenceRejected";
                return (status, failureCategory, false);
            }

            return (status, failureCategory, true);
        }
        catch (Exception exception)
        {
            return ("SetupWindowFailed", SafeFailureCategory(exception), false);
        }
    }

    private static async Task<bool> WaitForManagementListenerAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < budget)
        {
            if (cancellationToken.IsCancellationRequested)
                return false;

            using var client = new TcpClient();
            using var attemptTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, SunshinePort, attemptTimeout.Token);
                return true;
            }
            catch (Exception)
            {
                // The Sunshine process is starting; retry until the bounded budget expires.
            }

            var remaining = budget - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;

            var delay = remaining < ListenerPollInterval ? remaining : ListenerPollInterval;
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    private static void ClearSetupWindowFiles()
    {
        var baseDirectory = AppContext.BaseDirectory;
        File.Delete(Path.Combine(baseDirectory, "setup-ready.json"));
        File.Delete(Path.Combine(baseDirectory, "setup-done.signal"));
    }

    private static async Task WriteSetupReadyMarkerAsync(string path, DateTimeOffset expiresAtUtc)
    {
        var temporaryPath = Path.Combine(AppContext.BaseDirectory, $".setup-ready-{Guid.NewGuid():N}.tmp");
        var marker = new SetupReadyMarker("Ready", DateTimeOffset.UtcNow, expiresAtUtc);

        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, marker);
                await output.FlushAsync();
                output.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path);
        }
        catch
        {
            try { File.Delete(temporaryPath); }
            catch { }
            throw;
        }
    }

    private static async Task<bool> WaitForSetupSignalAsync(
        string signalPath,
        Stopwatch timer,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (File.Exists(signalPath))
                return true;

            var remaining = SetupWindowLimit - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return false;

            var delay = remaining < SetupSignalPollInterval ? remaining : SetupSignalPollInterval;
            await Task.Delay(delay, cancellationToken);
        }
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
