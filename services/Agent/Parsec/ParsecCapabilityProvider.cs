using System.Security.Cryptography;
using KamenRaider.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KamenRaider.Agent.Parsec;

/// <summary>
/// Detects a machine-wide Parsec install and advertises the boot-only public key while it exists.
/// </summary>
/// <remarks>
/// Ported from 0jay-v2 <c>agent/parsec_manager.go</c>'s availability monitor and
/// <c>agent/parsec_interactive_windows.go</c>'s <c>findParsecExecutable</c>. The web offers
/// "connect Parsec" only when the heartbeat carries this capability, so the Agent re-checks
/// <c>C:\Program Files\Parsec\parsecd.exe</c> every 60 seconds and reports
/// <see cref="MachineParsecCapability.Unavailable"/> (which clears the stored key) as soon as the
/// file is gone.
///
/// The key pair is generated once per Agent process and never leaves memory. A failure to
/// generate it only makes Parsec logins unavailable; it never stops the service.
/// </remarks>
public sealed class ParsecCapabilityProvider : BackgroundService
{
    /// <summary>How often the installed executable is re-checked.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    /// <summary>The executable agent-parsec looks for.</summary>
    public const string ExecutableFileName = "parsecd.exe";

    /// <summary>The product folder under Program Files.</summary>
    public const string ProductFolderName = "Parsec";

    private readonly ILogger<ParsecCapabilityProvider> logger;
    private readonly TimeProvider time;
    private readonly string[]? roots;
    private readonly ParsecKeyPair? keys;
    private MachineParsecCapability current = MachineParsecCapability.Unavailable;
    private string? executablePath;

    public ParsecCapabilityProvider(ILogger<ParsecCapabilityProvider> logger, TimeProvider? time = null)
        : this(logger, time, roots: null) { }

    internal ParsecCapabilityProvider(ILogger<ParsecCapabilityProvider> logger, TimeProvider? time, IEnumerable<string>? roots)
    {
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
        this.roots = roots?.ToArray();
        try
        {
            keys = new ParsecKeyPair();
        }
        catch (Exception exception) when (exception is CryptographicException or PlatformNotSupportedException)
        {
            logger.LogError("The ephemeral Parsec key could not be generated ({ErrorType}); Parsec logins are unavailable.", exception.GetType().Name);
        }
        Refresh();
    }

    /// <summary>The boot key pair, or null when generation failed (Parsec logins are unavailable).</summary>
    public ParsecKeyPair? Keys => keys;

    /// <summary>
    /// The capability advertised in the signed heartbeat: the boot key and whether
    /// <c>parsecd.exe</c> is installed. Safe to read from any thread.
    /// </summary>
    public MachineParsecCapability Current => Volatile.Read(ref current);

    /// <summary>The detected executable path, or null when Parsec is not installed.</summary>
    public string? ExecutablePath => Volatile.Read(ref executablePath);

    /// <summary>Whether a Parsec login can be offered right now.</summary>
    public bool Available => Current.Available;

    /// <summary>Re-runs the install probe. Never throws into the caller.</summary>
    public void Refresh()
    {
        try
        {
            RefreshCore();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogDebug("The Parsec install probe failed ({ErrorType}).", exception.GetType().Name);
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // The first heartbeat can arrive before the timer's first tick; probe synchronously so
        // the capability is never wrong on the first sync after boot.
        Refresh();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                Refresh();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    public override void Dispose()
    {
        keys?.Dispose();
        base.Dispose();
    }

    private void RefreshCore()
    {
        var path = FindExecutable(roots ?? ProgramFileRoots());
        Volatile.Write(ref executablePath, path);
        var available = path is not null && keys is not null;
        var capability = available && keys is not null
            ? new MachineParsecCapability(true, keys.BootKeyId, keys.PublicKey)
            : MachineParsecCapability.Unavailable;
        var previous = Interlocked.Exchange(ref current, capability);
        if (previous.Available != available)
        {
            if (available)
                logger.LogInformation("Parsec found; Parsec logins are offered.");
            else
                logger.LogInformation("Parsec not found; Parsec logins are not offered.");
        }
    }

    /// <summary>Looks only at the machine-wide install locations agent-parsec looked at.</summary>
    internal static string? FindExecutable(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var path = Path.Combine(root, ProductFolderName, ExecutableFileName);
            try
            {
                if (File.Exists(path))
                    return path;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // An unreadable Program Files candidate just means "not found here".
            }
        }
        return null;
    }

    private static string[] ProgramFileRoots()
    {
        var candidates = new string?[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"C:\Program Files"
        };
        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => candidate!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
