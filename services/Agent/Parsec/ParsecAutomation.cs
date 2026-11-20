using KamenRaider.Contracts;

namespace KamenRaider.Agent.Parsec;

/// <summary>
/// Runs one PowerShell script in the signed-in user's desktop, the way LordX3d agent-parsec's
/// <c>lib/interactive-task.js</c> did: a protected work folder, a one-shot scheduled task started
/// as that user in their interactive session, a named pipe for the credential line, and a JSON
/// status file as the result.
/// </summary>
/// <remarks>
/// The interface exists so <see cref="ParsecManager"/> can be tested without Task Scheduler, WMI
/// or a live desktop. The production implementation is <see cref="ParsecInteractiveRunner"/>.
/// </remarks>
public interface IParsecDesktopAutomation
{
    /// <summary>
    /// Runs the login script as the desktop user. <paramref name="secretLine"/> is the one JSON
    /// line of agent-parsec base64 credentials; the script reads it from a named pipe, so it is
    /// never written to disk and never logged.
    /// </summary>
    /// <exception cref="ParsecDesktopUnavailableException">Nobody is signed in at the console.</exception>
    /// <exception cref="ParsecScriptTimeoutException">The script did not report in time.</exception>
    /// <exception cref="ParsecTaskFailedException">The desktop task could not be created or run.</exception>
    Task<ParsecInteractiveResult> RunLoginAsync(
        ParsecLoginScriptRequest request,
        ReadOnlyMemory<byte> secretLine,
        CancellationToken cancellationToken);

    /// <summary>Runs the rental-end cleanup script as the desktop user.</summary>
    Task<ParsecInteractiveResult> RunCleanupAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Clears Parsec's on-disk session markers without needing an interactive session, for use at
    /// Agent boot after an interrupted rental. Touches only <c>app_session_id</c> and
    /// <c>user.bin</c>; never credentials.
    /// </summary>
    Task<ParsecBootSessionCleanupResult> CleanupBootSessionFilesAsync(CancellationToken cancellationToken);
}

/// <summary>Everything the login script needs that the Agent knows before the desktop task runs.</summary>
public sealed record ParsecLoginScriptRequest(string ParsecPath, ParsecLoginLayout Layout);

/// <summary>The desktop script's raw result: its status JSON and its sanitised-for-log step lines.</summary>
public sealed record ParsecInteractiveResult(byte[] StatusJson, IReadOnlyList<string> Steps);

/// <summary>Outcome of the boot-time session-marker sweep.</summary>
public sealed record ParsecBootSessionCleanupResult(int SessionFilesCleared, int Failures);

/// <summary>Base class for a desktop-automation failure the manager maps onto a wire error code.</summary>
public class ParsecAutomationException : Exception
{
    public ParsecAutomationException(string message) : base(message) { }
}

/// <summary>
/// The timings agent-parsec used, shared by the manager's deadline and the desktop runner.
/// </summary>
public static class ParsecAutomationDefaults
{
    /// <summary>agent-parsec waits 70 s for the login script.</summary>
    public const int LoginScriptTimeoutSeconds = 70;

    /// <summary>The cleanup script is a few file operations; agent-parsec did not wait.</summary>
    public const int CleanupScriptTimeoutSeconds = 20;

    /// <summary>The script tries to connect to the credential pipe for 20 s; the Agent waits 25 s.</summary>
    public const int PipeConnectTimeoutSeconds = 25;
}

/// <summary>No Windows user is signed in to the console desktop: report HOST_NOT_READY.</summary>
public sealed class ParsecDesktopUnavailableException : ParsecAutomationException
{
    public ParsecDesktopUnavailableException() : base("No Windows user is signed in to the desktop.") { }
}

/// <summary>The desktop script did not report within its window: report TIMEOUT.</summary>
public sealed class ParsecScriptTimeoutException : ParsecAutomationException
{
    public ParsecScriptTimeoutException() : base("Timed out waiting for the desktop script.") { }
}

/// <summary>Task Scheduler could not create or run the desktop task: report HOST_NOT_READY.</summary>
public sealed class ParsecTaskFailedException : ParsecAutomationException
{
    public ParsecTaskFailedException(string message) : base(message) { }
}
