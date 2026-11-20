using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace KamenRaider.Agent.Parsec;

/// <summary>
/// Runs one PowerShell script in the signed-in user's desktop, the way LordX3d agent-parsec's
/// <c>lib/interactive-task.js</c> does it (ported through 0jay-v2
/// <c>agent/parsec_interactive_windows.go</c>).
/// </summary>
/// <remarks>
/// <para>
/// The script and an invisible WScript launcher go into a folder of their own under
/// <c>%ProgramData%\KamenRaider\parsec\&lt;random&gt;</c> with a protected DACL: SYSTEM and
/// Administrators full control, the desktop user modify, nobody else. A one-shot scheduled task
/// is registered to run the launcher as that user in their interactive session
/// (<c>schtasks /create /ru &lt;user&gt; /it</c> + <c>/run</c>), the Agent waits for the script's
/// status file, then deletes the task and the folder.
/// </para>
/// <para>
/// This replaces the retired <c>CreateProcessAsUser</c> helper: on the live test machine that
/// path took the Agent service down within a minute of every Parsec connect. Task Scheduler starts
/// the script as an ordinary process of the user, entirely outside the service.
/// </para>
/// <para>
/// The login credential never touches the disk. It is served as one line on a one-instance,
/// outbound-only, local-only named pipe whose DACL lets SYSTEM, Administrators and the desktop
/// user in; the script reads the line and deletes itself at once. Status/steps travel through a
/// plain file in the protected folder and are deleted with it.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ParsecInteractiveRunner : IParsecDesktopAutomation
{
    /// <summary>agent-parsec waits 70 s for the login script.</summary>
    public const int LoginScriptTimeoutSeconds = ParsecAutomationDefaults.LoginScriptTimeoutSeconds;

    /// <summary>The cleanup script is a few file operations; agent-parsec did not wait.</summary>
    public const int CleanupScriptTimeoutSeconds = ParsecAutomationDefaults.CleanupScriptTimeoutSeconds;

    /// <summary>The script tries to connect to the credential pipe for 20 s; the Agent waits 25 s.</summary>
    internal const int PipeConnectTimeoutSeconds = ParsecAutomationDefaults.PipeConnectTimeoutSeconds;

    private const int SchtasksTimeoutSeconds = 30;
    private const int SchtasksDeleteTimeoutSeconds = 15;
    private const int MaximumSteps = 60;
    private const string TaskNamePrefix = "KamenRaiderParsec-";
    private const string PipeNamePrefix = "kamenraider-parsec-";

    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private readonly ILogger<ParsecInteractiveRunner> logger;

    public ParsecInteractiveRunner(ILogger<ParsecInteractiveRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Parsec desktop automation requires Windows.");
        this.logger = logger;
    }

    /// <summary>Where per-attempt folders live. Internal so tests can point it at a temp folder.</summary>
    internal string WorkRoot { get; set; } = DefaultWorkRoot();

    /// <summary>The wait for the login script; internal so a test can shorten it.</summary>
    internal TimeSpan LoginTimeout { get; set; } = TimeSpan.FromSeconds(LoginScriptTimeoutSeconds);

    /// <summary>The wait for the cleanup script; internal so a test can shorten it.</summary>
    internal TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(CleanupScriptTimeoutSeconds);

    public async Task<ParsecInteractiveResult> RunLoginAsync(
        ParsecLoginScriptRequest request,
        ReadOnlyMemory<byte> secretLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureWindows();

        var user = FindDesktopUser();
        var id = CreateRandomId();
        var workDirectory = CreateWorkDirectory(user, id);
        NamedPipeServerStream? pipe = null;
        CancellationTokenSource? pipeLifetime = null;
        Task? pipeTask = null;
        try
        {
            pipe = CreateSecretPipe(PipeNamePrefix + id, user.Sid);
            var scriptPath = Path.Combine(workDirectory, "task.ps1");
            var launcherPath = Path.Combine(workDirectory, "task.vbs");
            WriteScriptFile(
                scriptPath,
                ParsecScripts.BuildLoginScript(workDirectory, PipeNamePrefix + id, request.ParsecPath, request.Layout),
                withByteOrderMark: true);
            WriteScriptFile(launcherPath, ParsecScripts.BuildLauncher(PowerShellPath, scriptPath), withByteOrderMark: false);

            pipeLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pipeTask = ServeSecretAsync(pipe, secretLine, pipeLifetime.Token);
            return await RunDesktopScriptAsync(user, workDirectory, LoginTimeout, pipeTask, pipeLifetime, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            pipeLifetime?.Cancel();
            if (pipeTask is not null)
            {
                try { await pipeTask.ConfigureAwait(false); }
                catch (Exception) { /* already reported through the wait, or cancelled by us */ }
            }
            pipe?.Dispose();
            TryDeleteDirectory(workDirectory);
        }
    }

    public async Task<ParsecInteractiveResult> RunCleanupAsync(CancellationToken cancellationToken)
    {
        EnsureWindows();
        var user = FindDesktopUser();
        var id = CreateRandomId();
        var workDirectory = CreateWorkDirectory(user, id);
        try
        {
            var scriptPath = Path.Combine(workDirectory, "task.ps1");
            var launcherPath = Path.Combine(workDirectory, "task.vbs");
            WriteScriptFile(scriptPath, ParsecScripts.BuildCleanupScript(workDirectory), withByteOrderMark: true);
            WriteScriptFile(launcherPath, ParsecScripts.BuildLauncher(PowerShellPath, scriptPath), withByteOrderMark: false);
            return await RunDesktopScriptAsync(user, workDirectory, CleanupTimeout, pipeTask: null, pipeLifetime: null, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    public Task<ParsecBootSessionCleanupResult> CleanupBootSessionFilesAsync(CancellationToken cancellationToken)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        var cleared = 0;
        var failures = 0;

        foreach (var directory in EnumerateParsecDataDirectories())
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            try
            {
                if (ClearSessionTextFile(Path.Combine(directory, "config.txt")))
                    cleared++;
                if (ClearSessionJsonFile(Path.Combine(directory, "config.json")))
                    cleared++;
                var userBin = Path.Combine(directory, "user.bin");
                if (File.Exists(userBin))
                {
                    File.Delete(userBin);
                    cleared++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                failures++;
                logger.LogDebug("A Parsec session file could not be cleared ({ErrorType}).", exception.GetType().Name);
            }
        }

        // A per-run folder the Agent did not get to delete (crash, power loss): no credential is
        // ever in it, but it carries an ACL for a user that may have changed. Start clean.
        try
        {
            if (Directory.Exists(WorkRoot))
            {
                foreach (var directory in Directory.EnumerateDirectories(WorkRoot))
                    TryDeleteDirectory(directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures++;
            logger.LogDebug("The Parsec work root could not be swept ({ErrorType}).", exception.GetType().Name);
        }

        return Task.FromResult(new ParsecBootSessionCleanupResult(cleared, failures));
    }

    /// <summary>
    /// Finds the user signed in at the console: <c>WTSGetActiveConsoleSessionId</c> plus
    /// <c>WTSQuerySessionInformation</c>. This is the equivalent of agent-parsec's
    /// <c>Win32_ComputerSystem.UserName</c> without the System.Management package or a slow WMI
    /// query in the Agent's start-up path, and it names exactly the desktop a scheduled task with
    /// <c>/it</c> will run in.
    /// </summary>
    internal static ParsecDesktopUser FindDesktopUser()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == uint.MaxValue)
            throw new ParsecDesktopUnavailableException();
        var userName = QuerySessionString(sessionId, WtsUserName);
        if (string.IsNullOrWhiteSpace(userName))
            throw new ParsecDesktopUnavailableException();
        var domain = QuerySessionString(sessionId, WtsDomainName);
        var name = FormatUserName(domain, userName);
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
            return new ParsecDesktopUser(name, sid);
        }
        catch (Exception exception) when (exception is IdentityNotMappedException or SystemException or ArgumentException)
        {
            throw new ParsecDesktopUnavailableException();
        }
    }

    /// <summary>"DOMAIN\User" exactly as WMI's Win32_ComputerSystem.UserName spells it.</summary>
    internal static string FormatUserName(string? domain, string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        return string.IsNullOrWhiteSpace(domain) ? userName : domain + "\\" + userName;
    }

    /// <summary>16 random hex bytes name a task/folder/pipe that cannot collide or be guessed.</summary>
    internal static string CreateRandomId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    internal static string DefaultWorkRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "KamenRaider",
        "parsec");

    internal static string PowerShellPath => Path.Combine(WindowsPath, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    internal static string WScriptPath => Path.Combine(WindowsPath, "System32", "wscript.exe");

    internal static string SchtasksPath => Path.Combine(WindowsPath, "System32", "schtasks.exe");

    private static string WindowsPath => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private async Task<ParsecInteractiveResult> RunDesktopScriptAsync(
        ParsecDesktopUser user,
        string workDirectory,
        TimeSpan timeout,
        Task? pipeTask,
        CancellationTokenSource? pipeLifetime,
        CancellationToken cancellationToken)
    {
        var taskName = TaskNamePrefix + Path.GetFileName(workDirectory);
        try
        {
            await CreateAndRunTaskAsync(taskName, user.Name, Path.Combine(workDirectory, "task.vbs"), cancellationToken)
                .ConfigureAwait(false);
            return await WaitForScriptResultAsync(workDirectory, timeout, pipeTask, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pipeLifetime?.Cancel();
            if (pipeTask is not null)
            {
                try { await pipeTask.ConfigureAwait(false); }
                catch (Exception) { /* reported by the wait or cancelled by us */ }
            }
            await DeleteTaskAsync(taskName).ConfigureAwait(false);
        }
    }

    private async Task<ParsecInteractiveResult> WaitForScriptResultAsync(
        string workDirectory,
        TimeSpan timeout,
        Task? pipeTask,
        CancellationToken cancellationToken)
    {
        var statusPath = Path.Combine(workDirectory, "status.json");
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);
        while (true)
        {
            if (File.Exists(statusPath))
            {
                try
                {
                    var status = File.ReadAllBytes(statusPath);
                    if (status.Length > 0)
                        return new ParsecInteractiveResult(status, ReadSteps(workDirectory));
                }
                catch (IOException)
                {
                    // The script is still moving status.json.tmp over status.json; poll again.
                }
            }

            if (pipeTask is { IsCompleted: true })
            {
                try { await pipeTask.ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                pipeTask = null; // the credential was handed over; only the script status is left
            }

            try
            {
                await Task.Delay(StatusPollInterval, wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new ParsecScriptTimeoutException();
            }
        }
    }

    private async Task CreateAndRunTaskAsync(string taskName, string userName, string launcherPath, CancellationToken cancellationToken)
    {
        // The WScript launcher hides PowerShell; scheduling powershell.exe directly opens a
        // terminal window on Windows 11 and steals the foreground from Parsec.
        var command = $"\"{WScriptPath}\" //B //Nologo \"{launcherPath}\"";
        await RunSchtasksAsync(
            SchtasksTimeoutSeconds,
            cancellationToken,
            "/create", "/tn", taskName,
            "/tr", command,
            "/sc", "once", "/st", "00:00", "/f",
            "/ru", userName, "/it").ConfigureAwait(false);
        await RunSchtasksAsync(
            SchtasksTimeoutSeconds,
            cancellationToken,
            "/run", "/tn", taskName).ConfigureAwait(false);
    }

    private async Task DeleteTaskAsync(string taskName)
    {
        try
        {
            await RunSchtasksAsync(
                SchtasksDeleteTimeoutSeconds,
                CancellationToken.None,
                "/delete", "/tn", taskName, "/f").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ParsecTaskFailedException or OperationCanceledException)
        {
            // The task may never have been created, or Task Scheduler is unavailable enough that
            // failing to delete must not turn a login result into a service problem.
            logger.LogDebug("The Parsec desktop task could not be deleted ({ErrorType}).", exception.GetType().Name);
        }
    }

    private static async Task RunSchtasksAsync(int timeoutSeconds, CancellationToken cancellationToken, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(SchtasksPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new ParsecTaskFailedException("Task Scheduler could not be started.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new ParsecTaskFailedException("Task Scheduler could not be started.");
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                throw new ParsecTaskFailedException("Task Scheduler did not finish in time.");
            }
            if (process.ExitCode != 0)
                throw new ParsecTaskFailedException($"Task Scheduler command '{arguments[0]}' failed with exit code {process.ExitCode}.");
        }
    }

    private static async Task ServeSecretAsync(NamedPipeServerStream pipe, ReadOnlyMemory<byte> secretLine, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(PipeConnectTimeoutSeconds));
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ParsecScriptTimeoutException();
        }

        var buffer = new byte[secretLine.Length + 1];
        try
        {
            secretLine.Span.CopyTo(buffer);
            buffer[^1] = (byte)'\n';
            await pipe.WriteAsync(buffer.AsMemory(0, buffer.Length), timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>
    /// Creates the one-instance, outbound-only pipe whose DACL is the SDDL equivalent of
    /// agent-parsec's <c>D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;&lt;user&gt;)</c>.
    /// </summary>
    private static NamedPipeServerStream CreateSecretPipe(string name, SecurityIdentifier userSid)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(SystemSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(AdministratorsSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(userSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            name,
            PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 4096,
            outBufferSize: 4096,
            security);
    }

    private string CreateWorkDirectory(ParsecDesktopUser user, string id)
    {
        var root = WorkRoot;
        Directory.CreateDirectory(root);
        ApplyDirectorySecurity(root, userSid: null);

        var workDirectory = Path.Combine(root, id);
        Directory.CreateDirectory(workDirectory);
        ApplyDirectorySecurity(workDirectory, user.Sid);
        return workDirectory;
    }

    private static void ApplyDirectorySecurity(string path, SecurityIdentifier? userSid)
    {
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (userSid is not null)
            security.AddAccessRule(new FileSystemAccessRule(userSid, FileSystemRights.Modify, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void WriteScriptFile(string path, string content, bool withByteOrderMark) =>
        File.WriteAllText(path, content, new UTF8Encoding(withByteOrderMark));

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("A Parsec desktop work folder could not be removed ({ErrorType}).", exception.GetType().Name);
        }
    }

    private static IReadOnlyList<string> ReadSteps(string workDirectory)
    {
        var path = Path.Combine(workDirectory, "steps.log");
        string[] lines;
        try
        {
            if (!File.Exists(path))
                return Array.Empty<string>();
            lines = File.ReadAllLines(path, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
        var steps = new List<string>(Math.Min(lines.Length, MaximumSteps));
        foreach (var line in lines)
        {
            var step = line.Trim().TrimStart('\uFEFF');
            if (step.Length > 0)
                steps.Add(step);
        }
        return steps.Count > MaximumSteps ? steps.GetRange(steps.Count - MaximumSteps, MaximumSteps) : steps;
    }

    /// <summary>
    /// Removes the <c>app_session_id=</c> line from a Parsec <c>config.txt</c>. Returns true when
    /// the file existed and the marker was removed. Internal so the file rules can be tested
    /// without a desktop.
    /// </summary>
    internal static bool ClearSessionTextFile(string path)
    {
        if (!File.Exists(path))
            return false;
        var lines = File.ReadAllLines(path);
        var kept = new List<string>(lines.Length);
        var removed = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("app_session_id=", StringComparison.OrdinalIgnoreCase))
            {
                removed = true;
                continue;
            }
            kept.Add(line);
        }
        if (!removed)
            return false;
        File.WriteAllLines(path, kept);
        return true;
    }

    /// <summary>
    /// Removes the <c>app_session_id</c> property from a Parsec <c>config.json</c>, keeping every
    /// other property. Returns true when the property was present. Internal for the same reason
    /// as <see cref="ClearSessionTextFile"/>.
    /// </summary>
    internal static bool ClearSessionJsonFile(string path)
    {
        if (!File.Exists(path))
            return false;
        var text = File.ReadAllText(path);
        if (text.IndexOf("app_session_id", StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return false;
        }
        if (node is not JsonObject root)
            return false;
        var removed = false;
        foreach (var name in root.Select(property => property.Key).ToArray())
        {
            if (string.Equals(name, "app_session_id", StringComparison.OrdinalIgnoreCase))
                removed |= root.Remove(name);
        }
        if (!removed)
            return false;
        File.WriteAllText(path, root.ToJsonString());
        return true;
    }

    private static IEnumerable<string> EnumerateParsecDataDirectories()
    {
        var directories = new List<string>(8);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
            directories.Add(Path.Combine(programData, "Parsec"));

        var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (!string.IsNullOrEmpty(drive))
        {
            var usersRoot = Path.Combine(drive, "Users");
            try
            {
                foreach (var profile in Directory.EnumerateDirectories(usersRoot))
                {
                    var name = Path.GetFileName(profile);
                    if (string.Equals(name, "Public", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "Default User", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "All Users", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var parsec = Path.Combine(profile, "AppData", "Roaming", "Parsec");
                    if (Directory.Exists(parsec))
                        directories.Add(parsec);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A locked-down or half-created profile must not stop the ProgramData sweep.
            }
        }
        return directories;
    }

    private static string? QuerySessionString(uint sessionId, int informationClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, informationClass, out var buffer, out var bytesReturned) ||
            buffer == IntPtr.Zero)
            return null;
        try
        {
            // bytesReturned counts the trailing UTF-16 NUL.
            return bytesReturned <= sizeof(char) ? null : Marshal.PtrToStringUni(buffer, (bytesReturned / sizeof(char)) - 1);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Parsec desktop automation requires Windows.");
    }

    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr server,
        uint sessionId,
        int informationClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}

/// <summary>The signed-in desktop user and the SID the work folder and pipe ACLs name.</summary>
public sealed record ParsecDesktopUser(string Name, SecurityIdentifier Sid);
