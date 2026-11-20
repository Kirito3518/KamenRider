using System.Reflection;
using System.Text;

namespace KamenRaider.Agent.Parsec;

/// <summary>
/// Access to the resources embedded in the Agent assembly for the Parsec desktop automation.
/// </summary>
/// <remarks>
/// The login/cleanup PowerShell scripts, the invisible VBScript launcher, the default login
/// geometry and the OCR translation tables are embedded instead of shipped as files next to
/// the service: a diskless fleet only has the Agent binary, and a resource cannot be edited
/// beside the service by a standard user. <c>Agent.csproj</c> assigns the logical names below.
/// </remarks>
public static class ParsecResources
{
    public const string LayoutJson = "KamenRaider.Agent.Parsec.Scripts.parsec-layout.json";
    public const string TranslateJson = "KamenRaider.Agent.Parsec.Scripts.parsec-translate.json";
    public const string LoginScript = "KamenRaider.Agent.Parsec.Scripts.parsec-login.ps1";
    public const string CleanupScript = "KamenRaider.Agent.Parsec.Scripts.parsec-cleanup.ps1";
    public const string Launcher = "KamenRaider.Agent.Parsec.Scripts.parsec-launch.vbs";

    private static readonly Assembly Owner = typeof(ParsecResources).Assembly;

    /// <summary>The logical names of every embedded Parsec resource, for diagnostics and tests.</summary>
    public static IReadOnlyList<string> Names { get; } = new[]
    {
        LayoutJson,
        TranslateJson,
        LoginScript,
        CleanupScript,
        Launcher
    };

    /// <summary>Reads one embedded UTF-8 resource.</summary>
    /// <exception cref="InvalidOperationException">The resource is missing from the build.</exception>
    public static string ReadText(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        using var stream = Owner.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded Parsec resource '{name}' is missing from the Agent build.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
