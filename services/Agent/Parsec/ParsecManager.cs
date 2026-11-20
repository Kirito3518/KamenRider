using System.Security.Cryptography;
using KamenRaider.Contracts;
using Microsoft.Extensions.Logging;

namespace KamenRaider.Agent.Parsec;

/// <summary>The immediate answer to a <c>parsec_connect</c> command.</summary>
/// <remarks>
/// The desktop login itself always continues in the background and reports through
/// <see cref="IParsecStatusReporter"/>; this only says whether the attempt was accepted, so the
/// command handler can answer at once like 0jay-v2's agent did. <see cref="ErrorCode"/> is set
/// (and a FAILED status is also reported) when the attempt was refused before the desktop script
/// ran.
/// </remarks>
public sealed record ParsecStartResult(bool Accepted, string? ErrorCode = null)
{
    /// <summary>The attempt is running in the background; watch the status reports.</summary>
    public static ParsecStartResult Started { get; } = new(true);

    /// <summary>The attempt was refused; the matching FAILED status has been reported.</summary>
    public static ParsecStartResult Refused(string errorCode) => new(false, errorCode);
}

/// <summary>
/// The renter's Parsec account login, built into the Agent the way LordX3d agent-parsec was.
/// </summary>
/// <remarks>
/// <para>
/// Ported from 0jay-v2 <c>agent/parsec_manager.go</c>:
/// </para>
/// <list type="bullet">
/// <item><description>
/// one login at a time (agent-parsec's BUSY guard); the desktop script runs in the background so
/// the command answers immediately; STARTING is reported, then the script's result mapped through
/// <see cref="ParsecOutcomeTranslator"/>.
/// </description></item>
/// <item><description>
/// the sealed secret is opened in memory only, turned into the named-pipe line and cleared before
/// any desktop work starts.
/// </description></item>
/// <item><description>
/// <see cref="CleanupAsync"/> is the rental-end script (close parsecd in the user session, delete
/// <c>app_session_id</c>/<c>user.bin</c>, clear the clipboard); the boot-time
/// <see cref="CleanupBootSessionFilesAsync"/> needs no desktop because a rebooted machine has no
/// interactive session yet.
/// </description></item>
/// <item><description>
/// <see cref="InUse"/> covers both "a login is running" and "a login went through and the
/// rental-end cleanup has not run since": the renter plays through Parsec, which carries its own
/// microphone, so the mic bridge must leave Parsec alone while that is true.
/// </description></item>
/// </list>
/// <para>
/// Nothing here may take the service down: every background body catches its own errors, reports
/// FAILED/INTERNAL with a short reason, and logs the stack. The page/test-visible phrases are the
/// same Thai texts agent-parsec used.
/// </para>
/// </remarks>
public sealed class ParsecManager : IDisposable
{
    /// <summary>A desktop login may take up to the script's 70 s plus 20 s of task start-up.</summary>
    private static readonly TimeSpan LoginDeadline = TimeSpan.FromSeconds(ParsecAutomationDefaults.LoginScriptTimeoutSeconds + 20);

    /// <summary>0jay-parsec's cleanup wait was 20 s; allow 15 s more for Task Scheduler.</summary>
    private static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(ParsecAutomationDefaults.CleanupScriptTimeoutSeconds + 15);

    /// <summary>0jay-parsec's per-report context.</summary>
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromSeconds(6);

    private const int ShortAttemptIdLength = 12;

    private readonly ParsecCapabilityProvider capability;
    private readonly IParsecStatusReporter reporter;
    private readonly IParsecDesktopAutomation automation;
    private readonly ILogger<ParsecManager> logger;
    private readonly TimeProvider time;
    private readonly Func<string?>? executablePath;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly object workGate = new();
    private readonly List<Task> backgroundWork = new();
    private bool loginActive;
    private bool signedIn;
    private bool cleaning;
    private bool disposed;

    public ParsecManager(
        ParsecCapabilityProvider capability,
        IParsecStatusReporter reporter,
        IParsecDesktopAutomation automation,
        ILogger<ParsecManager> logger,
        TimeProvider? time = null,
        Func<string?>? executablePath = null,
        string? loginLayoutJson = null)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(reporter);
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(logger);
        this.capability = capability;
        this.reporter = reporter;
        this.automation = automation;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
        this.executablePath = executablePath;

        // PARSEC_LOGIN_LAYOUT is the service administrator's escape hatch for a machine whose
        // Parsec window is not where agent-parsec measured it. A bad value is logged and the
        // defaults are kept, exactly like agent-parsec.
        var rawLayout = loginLayoutJson ?? Environment.GetEnvironmentVariable(ParsecLayout.OverrideEnvironmentVariable);
        if (ParsecLayout.TryParse(rawLayout, out var layout, out var layoutError))
        {
            Layout = layout;
        }
        else
        {
            logger.LogWarning(
                "PARSEC_LOGIN_LAYOUT is invalid ({Reason}); using agent-parsec's default Parsec login positions.",
                layoutError);
            Layout = ParsecLayout.Default;
        }
    }

    /// <summary>The login geometry this process uses; defaults unless overridden by the runbook setting.</summary>
    public ParsecLoginLayout Layout { get; }

    /// <summary>
    /// Whether the renter is playing through Parsec right now (a login runs, or one succeeded and
    /// the rental-end cleanup has not run since). <c>false</c> once cleanup is requested.
    /// </summary>
    public bool InUse
    {
        get
        {
            lock (gate)
                return loginActive || signedIn;
        }
    }

    /// <summary>Whether a login can be offered now (parsecd.exe detected by the capability provider).</summary>
    public bool Available => ResolveExecutablePath() is not null;

    /// <summary>
    /// Accepts one <c>parsec_connect</c> attempt. The secret is opened here, in memory; the
    /// desktop login then runs in the background and is reported through
    /// <see cref="IParsecStatusReporter"/>. One attempt runs at a time: a second one is reported
    /// FAILED/PARSEC_BUSY and refused.
    /// </summary>
    public Task<ParsecStartResult> StartAsync(
        string attemptId,
        DateTimeOffset deadlineAt,
        ParsecSealedSecret secret,
        CancellationToken cancellationToken)
    {
        // The token belongs to the command dispatch that returns immediately; the desktop
        // attempt must survive it (0jay used context.Background for the same reason), so it is
        // deliberately not observed. The attempt is bounded by the attempt deadline instead.
        _ = cancellationToken;
        return Task.FromResult(StartCore(attemptId, deadlineAt, secret));
    }

    private ParsecStartResult StartCore(string attemptId, DateTimeOffset deadlineAt, ParsecSealedSecret secret)
    {
        // This must never throw into the command dispatcher: a Parsec command failing is a
        // report, never a service problem.
        if (string.IsNullOrWhiteSpace(attemptId) || attemptId.Length > 128 || secret is null)
            return ParsecStartResult.Refused(ParsecErrorCodes.Internal);

        byte[]? plaintext = null;
        byte[]? secretLine = null;
        try
        {
            var keys = capability.Keys;
            if (keys is null)
            {
                ReportInBackground(attemptId, new ParsecReport(ParsecStates.Failed, ParsecErrorCodes.Internal));
                return ParsecStartResult.Refused(ParsecErrorCodes.Internal);
            }

            try
            {
                plaintext = keys.Decrypt(secret);
            }
            catch (Exception exception) when (exception is ParsecSecretException or ObjectDisposedException or CryptographicException)
            {
                // Sealed for another boot key, or tampered with. Nothing about it is logged.
                logger.LogWarning("A sealed Parsec secret could not be opened; the attempt is reported FAILED (INTERNAL).");
                ReportInBackground(attemptId, new ParsecReport(ParsecStates.Failed, ParsecErrorCodes.Internal));
                return ParsecStartResult.Refused(ParsecErrorCodes.Internal);
            }

            try
            {
                if (!ParsecCredentials.TryBuildPipeLine(plaintext, out secretLine))
                {
                    ReportInBackground(attemptId, new ParsecReport(ParsecStates.Failed, ParsecErrorCodes.InvalidCredentials));
                    return ParsecStartResult.Refused(ParsecErrorCodes.InvalidCredentials);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                plaintext = null;
            }

            var parsecPath = ResolveExecutablePath();
            lock (gate)
            {
                if (disposed)
                {
                    ZeroAndClear(ref secretLine);
                    return ParsecStartResult.Refused(ParsecErrorCodes.Internal);
                }
                if (loginActive)
                {
                    ZeroAndClear(ref secretLine);
                    ReportInBackground(attemptId, new ParsecReport(
                        ParsecStates.Failed,
                        ParsecErrorCodes.Busy,
                        "Agent กำลังเข้าสู่ระบบ Parsec อยู่ กรุณารอสักครู่แล้วลองใหม่"));
                    return ParsecStartResult.Refused(ParsecErrorCodes.Busy);
                }

                loginActive = true;
                var attempt = RunAttemptAsync(attemptId, deadlineAt, secretLine, parsecPath);
                secretLine = null; // ownership passed to the attempt, which clears it
                TrackBackgroundNoLock(attempt);
            }
            return ParsecStartResult.Started;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogError(exception, "A Parsec attempt could not be started.");
            ReportInBackground(attemptId, new ParsecReport(
                ParsecStates.Failed,
                ParsecErrorCodes.Internal,
                "ข้อผิดพลาดภายใน Agent: " + exception.GetType().Name));
            return ParsecStartResult.Refused(ParsecErrorCodes.Internal);
        }
        finally
        {
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
            ZeroAndClear(ref secretLine);
        }
    }

    /// <summary>
    /// Answers a <c>parsec_2fa</c> command. agent-parsec has no two-factor step, so a code is
    /// never asked for; one that arrives ends the attempt with TWO_FACTOR_UNSUPPORTED, and an
    /// unreadable payload is reported FAILED/INTERNAL without echoing anything.
    /// </summary>
    public Task<bool> HandleTwoFactorAsync(string attemptId, ParsecSealedSecret secret, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (string.IsNullOrWhiteSpace(attemptId) || attemptId.Length > 128 || secret is null)
            return Task.FromResult(false);

        var keys = capability.Keys;
        if (keys is null)
        {
            ReportInBackground(attemptId, new ParsecReport(ParsecStates.Failed, ParsecErrorCodes.Internal));
            return Task.FromResult(false);
        }

        byte[]? plaintext = null;
        try
        {
            try
            {
                plaintext = keys.Decrypt(secret);
            }
            catch (Exception exception) when (exception is ParsecSecretException or ObjectDisposedException or CryptographicException)
            {
                ReportInBackground(attemptId, new ParsecReport(ParsecStates.Failed, ParsecErrorCodes.Internal));
                return Task.FromResult(false);
            }

            if (!ParsecCredentials.TryValidateVerificationCode(plaintext))
            {
                ReportInBackground(attemptId, new ParsecReport(ParsecStates.Failed, ParsecErrorCodes.Internal));
                return Task.FromResult(false);
            }

            ReportInBackground(attemptId, new ParsecReport(ParsecStates.Failed, ParsecErrorCodes.TwoFactorUnsupported));
            return Task.FromResult(true);
        }
        finally
        {
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// agent-parsec's rental-end cleanup: close parsecd in the desktop session, forget the account
    /// (<c>app_session_id</c>, <c>user.bin</c> in <c>%APPDATA%\Parsec</c> and
    /// <c>%ProgramData%\Parsec</c>) and empty the clipboard. Best effort; a second request while
    /// one is running is absorbed. Returns whether the script finished.
    /// </summary>
    public async Task<bool> CleanupAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource timeout;
        lock (gate)
        {
            if (disposed)
                return false;
            // The rental is over as soon as the web asks: InUse must be false while cleanup runs,
            // even if the script then fails (agent-parsec cleared the flag the same way).
            signedIn = false;
            if (cleaning)
                return true;
            cleaning = true;
            timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
            timeout.CancelAfter(CleanupDeadline);
        }

        try
        {
            var result = await automation.RunCleanupAsync(timeout.Token).ConfigureAwait(false);
            LogSteps("cleanup", result.Steps);
            try
            {
                var status = ParsecOutcomeTranslator.DecodeScriptStatus(result.StatusJson);
                if (!status.AutomationCompleted)
                {
                    logger.LogWarning("The Parsec rental-end cleanup script stopped early.");
                    return false;
                }
            }
            catch (FormatException)
            {
                logger.LogWarning("The Parsec rental-end cleanup script result could not be read.");
                return false;
            }
            logger.LogInformation("Parsec rental-end cleanup finished.");
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Parsec rental-end cleanup did not finish before its deadline.");
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning("Parsec rental-end cleanup did not finish ({ErrorType}).", exception.GetType().Name);
            return false;
        }
        finally
        {
            timeout.Dispose();
            lock (gate)
                cleaning = false;
        }
    }

    /// <summary>
    /// Clears Parsec's on-disk session markers without a desktop session: the correct action right
    /// after Agent boot, when a previous rental may have been interrupted between writing the
    /// session and its rental-end cleanup. Returns false when a file could not be cleared.
    /// </summary>
    public async Task<bool> CleanupBootSessionFilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await automation.CleanupBootSessionFilesAsync(cancellationToken).ConfigureAwait(false);
            if (result.Failures == 0)
                logger.LogInformation("Parsec boot session cleanup cleared {Count} on-disk session marker(s).", result.SessionFilesCleared);
            else
                logger.LogWarning(
                    "Parsec boot session cleanup cleared {Count} on-disk session marker(s) with {Failures} failure(s).",
                    result.SessionFilesCleared,
                    result.Failures);
            return result.Failures == 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning("Parsec boot session cleanup failed ({ErrorType}).", exception.GetType().Name);
            return false;
        }
    }

    /// <summary>Waits for every report/attempt the manager has started. Used by tests and shutdown.</summary>
    internal async Task WaitForBackgroundWorkAsync()
    {
        while (true)
        {
            Task[] snapshot;
            lock (workGate)
                snapshot = backgroundWork.ToArray();
            if (snapshot.Length == 0)
                return;
            try
            {
                await Task.WhenAll(snapshot).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Each tracked task already logged its own failure.
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
        }
        // Cancel only: disposing the source would make background attempts that are already
        // unwinding throw ObjectDisposedException instead of reporting.
        lifetime.Cancel();
    }

    private async Task RunAttemptAsync(string attemptId, DateTimeOffset deadlineAt, byte[] secretLine, string? parsecPath)
    {
        try
        {
            if (!await ReportAsync(attemptId, new ParsecReport(ParsecStates.Starting), lifetime.Token).ConfigureAwait(false))
            {
                LogShortId("Parsec attempt {Attempt}: the Controller no longer holds it; login skipped.", attemptId);
                return;
            }

            if (string.IsNullOrEmpty(parsecPath))
            {
                await ReportAsync(attemptId, ParsecOutcomeTranslator.RuntimeFailure(ParsecErrorCodes.AppNotFound), lifetime.Token).ConfigureAwait(false);
                return;
            }

            var deadline = time.GetUtcNow() + LoginDeadline;
            if (deadlineAt != default && deadlineAt < deadline)
                deadline = deadlineAt;
            var remaining = deadline - time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                await ReportAsync(attemptId, ParsecOutcomeTranslator.InteractiveFailure(new ParsecScriptTimeoutException()), lifetime.Token)
                    .ConfigureAwait(false);
                return;
            }

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            attempt.CancelAfter(remaining);
            ParsecReport report;
            try
            {
                report = await RunLoginScriptAsync(attemptId, parsecPath, secretLine, attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }

            if (report.Status == ParsecStates.Ready)
            {
                lock (gate)
                    signedIn = true;
            }
            logger.LogInformation(
                "Parsec attempt {Attempt}: {Status} {ErrorCode}",
                ShortAttemptId(attemptId),
                report.Status,
                report.ErrorCode);
            await ReportAsync(attemptId, report, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // The Agent is stopping; nothing left to report to.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A bug in one attempt must never take the service (or the next attempt) down. The
            // stack goes to the Agent log; the web gets a short, sanitised reason.
            logger.LogError(exception, "A Parsec attempt stopped by an internal error.");
            try
            {
                await ReportAsync(
                    attemptId,
                    new ParsecReport(
                        ParsecStates.Failed,
                        ParsecErrorCodes.Internal,
                        "ข้อผิดพลาดภายใน Agent: " + exception.GetType().Name),
                    lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Reporting already swallows its own failures; this is the last line of defence.
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretLine);
            lock (gate)
                loginActive = false;
        }
    }

    private async Task<ParsecReport> RunLoginScriptAsync(
        string attemptId,
        string parsecPath,
        byte[] secretLine,
        CancellationToken cancellationToken)
    {
        ParsecInteractiveResult result;
        try
        {
            result = await automation.RunLoginAsync(
                new ParsecLoginScriptRequest(parsecPath, Layout),
                secretLine,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ParsecAutomationException or OperationCanceledException)
        {
            return ParsecOutcomeTranslator.InteractiveFailure(exception);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogError(exception, "The Parsec desktop script failed.");
            return ParsecOutcomeTranslator.InteractiveFailure(exception);
        }

        LogSteps("login", result.Steps);
        try
        {
            var status = ParsecOutcomeTranslator.DecodeScriptStatus(result.StatusJson);
            return ParsecOutcomeTranslator.ScriptOutcome(status);
        }
        catch (FormatException)
        {
            return new ParsecReport(
                ParsecStates.Failed,
                ParsecErrorCodes.HostNotReady,
                "อ่านผลลัพธ์จากหน้าจอเครื่องไม่ได้");
        }
    }

    private async Task<bool> ReportAsync(string attemptId, ParsecReport report, CancellationToken cancellationToken)
    {
        var bootKeyId = capability.Keys?.BootKeyId ?? string.Empty;
        var message = report.Message is null ? null : ParsecOutcomeTranslator.SanitizeMessage(report.Message);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            timeout.CancelAfter(ReportTimeout);
            return await reporter.ReportAsync(
                new ParsecStatusReport(attemptId, bootKeyId, report.Status, report.ErrorCode, message),
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Delivery is best effort: an unreachable Controller must not fail the attempt.
            logger.LogWarning("A Parsec status report failed ({ErrorType}).", exception.GetType().Name);
            return false;
        }
    }

    private void ReportInBackground(string attemptId, ParsecReport report)
    {
        TrackBackground(async () =>
        {
            try
            {
                await ReportAsync(attemptId, report, lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger.LogWarning("A Parsec status report failed ({ErrorType}).", exception.GetType().Name);
            }
        });
    }

    private void TrackBackground(Func<Task> workItem)
    {
        Task task;
        try
        {
            task = workItem();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning("Parsec background work could not start ({ErrorType}).", exception.GetType().Name);
            return;
        }
        TrackBackgroundNoLock(task);
    }

    private void TrackBackgroundNoLock(Task task)
    {
        lock (workGate)
            backgroundWork.Add(task);
        _ = task.ContinueWith(
            completed =>
            {
                lock (workGate)
                    backgroundWork.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void LogSteps(string kind, IReadOnlyList<string>? steps)
    {
        if (steps is null)
            return;
        foreach (var step in steps)
        {
            // The scripts write paths and screen geometry into steps.log; scrub e-mail addresses
            // and IPs before anything reaches the Agent log.
            var safe = ParsecOutcomeTranslator.SanitizeMessage(step);
            if (safe.Length > 0)
                logger.LogDebug("Parsec {Kind} script: {Step}", kind, safe);
        }
    }

    private string? ResolveExecutablePath()
    {
        try
        {
            return executablePath is not null ? executablePath() : capability.ExecutablePath;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogDebug("The Parsec executable path is unavailable ({ErrorType}).", exception.GetType().Name);
            return null;
        }
    }

    private void LogShortId(string message, string attemptId) => logger.LogDebug(message, ShortAttemptId(attemptId));

    private static string ShortAttemptId(string attemptId) =>
        attemptId.Length <= ShortAttemptIdLength ? attemptId : attemptId[..ShortAttemptIdLength];

    private static void ZeroAndClear(ref byte[]? buffer)
    {
        if (buffer is null)
            return;
        CryptographicOperations.ZeroMemory(buffer);
        buffer = null;
    }
}
