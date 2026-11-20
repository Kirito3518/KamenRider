using System.Globalization;

namespace KamenRaider.Agent.Parsec;

/// <summary>
/// Builds the desktop scripts from the embedded templates.
/// </summary>
/// <remarks>
/// The scripts are the owner's LordX3d agent-parsec PowerShell/WinRT UI automation, ported
/// through 0jay-v2 <c>agent/parsec_script.go</c>. They live in <c>Scripts/</c> as resources so
/// they can be reviewed as PowerShell; this builder only substitutes protected, single-quoted
/// values and the default login geometry. No credential is ever placed in a script: the login
/// script reads it from the agent's named pipe and deletes itself first.
/// </remarks>
public static class ParsecScripts
{
    public const string WorkDirectoryPlaceholder = "__WORK_DIR__";
    public const string PipeNamePlaceholder = "__PIPE_NAME__";
    public const string ParsecExecutablePlaceholder = "__PARSEC_EXE__";
    public const string CommandPlaceholder = "__COMMAND__";

    private static readonly Lazy<string> LoginTemplate = new(() => NormalizeLineEndings(ParsecResources.ReadText(ParsecResources.LoginScript)));
    private static readonly Lazy<string> CleanupTemplate = new(() => NormalizeLineEndings(ParsecResources.ReadText(ParsecResources.CleanupScript)));
    private static readonly Lazy<string> LauncherTemplate = new(() => NormalizeLineEndings(ParsecResources.ReadText(ParsecResources.Launcher)));

    /// <summary>Builds the login script for one attempt.</summary>
    public static string BuildLoginScript(string workDirectory, string pipeName, string parsecPath, ParsecLoginLayout layout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(parsecPath);
        ArgumentNullException.ThrowIfNull(layout);
        if (!layout.IsValid())
            throw new ArgumentException("The Parsec login layout is outside its reference screen.", nameof(layout));

        var script = LoginTemplate.Value;
        script = Replace(script, WorkDirectoryPlaceholder, SingleQuote(workDirectory));
        script = Replace(script, PipeNamePlaceholder, SingleQuote(pipeName));
        script = Replace(script, ParsecExecutablePlaceholder, SingleQuote(parsecPath));
        script = Replace(script, "__REF_WIDTH__", Integer(layout.ReferenceWidth));
        script = Replace(script, "__REF_HEIGHT__", Integer(layout.ReferenceHeight));
        script = Replace(script, "__EMAIL_X__", Integer(layout.EmailPos.X));
        script = Replace(script, "__EMAIL_Y__", Integer(layout.EmailPos.Y));
        script = Replace(script, "__PASSWORD_X__", Integer(layout.PasswordPos.X));
        script = Replace(script, "__PASSWORD_Y__", Integer(layout.PasswordPos.Y));
        script = Replace(script, "__LOGIN_X__", Integer(layout.LoginButtonPos.X));
        script = Replace(script, "__LOGIN_Y__", Integer(layout.LoginButtonPos.Y));
        script = Replace(script, "__NOTIFY_X__", Integer(layout.NotificationRegion.X));
        script = Replace(script, "__NOTIFY_Y__", Integer(layout.NotificationRegion.Y));
        script = Replace(script, "__NOTIFY_WIDTH__", Integer(layout.NotificationRegion.Width));
        script = Replace(script, "__NOTIFY_HEIGHT__", Integer(layout.NotificationRegion.Height));
        return script;
    }

    /// <summary>Builds the rental-end cleanup script; it takes no credential and no pipe.</summary>
    public static string BuildCleanupScript(string workDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);
        return Replace(CleanupTemplate.Value, WorkDirectoryPlaceholder, SingleQuote(workDirectory));
    }

    /// <summary>
    /// Builds the VBScript launcher. Scheduling <c>powershell.exe</c> directly opens a console
    /// window on Windows 11, steals the foreground from Parsec and sends every click and key
    /// there; window style 0 keeps PowerShell invisible inside the user's desktop.
    /// </summary>
    public static string BuildLauncher(string powerShellPath, string scriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(powerShellPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        var command = $"\"{powerShellPath}\" -NoLogo -NoProfile -STA -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"";
        return Replace(LauncherTemplate.Value, CommandPlaceholder, command.Replace("\"", "\"\"", StringComparison.Ordinal));
    }

    /// <summary>PowerShell single-quoted literal: the only value escaping a path needs in these scripts.</summary>
    public static string SingleQuote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static string Replace(string template, string placeholder, string value) =>
        template.Replace(placeholder, value, StringComparison.Ordinal);

    private static string Integer(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
}
