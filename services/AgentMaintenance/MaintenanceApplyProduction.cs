using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace KamenRaider.AgentMaintenance;

/// <summary>
/// Resolves the installed Foundation Sunshine executable from the canonical Program Files
/// location and refuses anything that is not a trusted, regular, single-link native binary
/// matching the host architecture. The path is derived from the deployment layout, never from
/// the request, so a tampered command cannot redirect service control or backup paths.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMaintenanceTrustedSunshinePathProvider : IMaintenanceTrustedSunshinePathProvider
{
    internal const long MinimumExecutableBytes = 1024 * 1024;
    internal const long MaximumExecutableBytes = 512L * 1024 * 1024;
    private const ushort MachineX64 = 0x8664;
    private const ushort MachineArm64 = 0xAA64;

    private readonly string expectedPath;
    private readonly bool enforceTrust;
    private readonly string? requiredProgramFilesRoot;

    public WindowsMaintenanceTrustedSunshinePathProvider()
        : this(DefaultSunshinePath(), enforceTrust: true, ProgramFilesRoot())
    {
    }

    internal WindowsMaintenanceTrustedSunshinePathProvider(
        string expectedPath,
        bool enforceTrust,
        string? requiredProgramFilesRoot = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The trusted Sunshine path provider requires Windows.");
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath);
        this.expectedPath = Path.GetFullPath(expectedPath);
        if (!Path.IsPathFullyQualified(this.expectedPath))
            throw new ArgumentException("The trusted Sunshine path must be absolute.", nameof(expectedPath));
        this.enforceTrust = enforceTrust;
        this.requiredProgramFilesRoot = requiredProgramFilesRoot is null
            ? null
            : Path.GetFullPath(requiredProgramFilesRoot);
    }

    public string GetTrustedSunshineExecutablePath(MaintenanceCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ResolveInstalledExecutablePath();
    }

    /// <summary>
    /// Resolves and validates the installed Sunshine executable without needing a command
    /// request. Used by the production composition for the fixed service layout and fence.
    /// </summary>
    public string ResolveInstalledExecutablePath() =>
        Validate(expectedPath, enforceTrust, requiredProgramFilesRoot);

    internal static string Validate(string path, bool enforceTrust, string? requiredProgramFilesRoot)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(fullPath))
            throw new InvalidDataException("The Sunshine executable path is not absolute.");
        if (!string.Equals(Path.GetFileName(fullPath), "Sunshine.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The trusted Sunshine path does not name Sunshine.exe.");
        var sunshineDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("The Sunshine executable path has no directory.");
        if (!string.Equals(Path.GetFileName(sunshineDirectory), "Sunshine", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Sunshine executable is not in its canonical installation directory.");
        if (requiredProgramFilesRoot is not null &&
            !PathEquals(sunshineDirectory, Path.Combine(requiredProgramFilesRoot, "Sunshine")))
            throw new InvalidDataException("The Sunshine executable is not under the canonical Program Files installation root.");

        if (!Directory.Exists(sunshineDirectory))
            throw new DirectoryNotFoundException("The Sunshine installation directory is missing.");
        EnsureAncestorsHaveNoReparsePoints(sunshineDirectory);
        if ((File.GetAttributes(sunshineDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The Sunshine installation directory must not be a reparse point.");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The trusted Sunshine executable is missing.", fullPath);

        EnsureRegularSingleLinkExecutable(fullPath);
        var length = new FileInfo(fullPath).Length;
        if (length is < MinimumExecutableBytes or > MaximumExecutableBytes)
            throw new InvalidDataException("The trusted Sunshine executable has an unexpected size.");
        if (enforceTrust)
        {
            ValidateTrustedFile(fullPath);
            ValidateTrustedDirectory(sunshineDirectory);
        }
        ValidateNativeMachine(fullPath);
        return fullPath;
    }

    internal static void ValidateNativeMachine(string path)
    {
        var expected = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => MachineX64,
            Architecture.Arm64 => MachineArm64,
            _ => throw new PlatformNotSupportedException("The maintenance helper requires a supported native Windows architecture.")
        };
        var machine = ReadPeMachine(path);
        if (machine != expected)
            throw new InvalidDataException("The trusted Sunshine executable does not match the native host architecture.");
    }

    internal static ushort ReadPeMachine(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[64];
        stream.ReadExactly(header);
        if (header[0] != (byte)'M' || header[1] != (byte)'Z')
            throw new InvalidDataException("The trusted Sunshine executable is not a PE image.");
        var peOffset = BitConverter.ToInt32(header, 0x3C);
        if (peOffset < 64 || peOffset > 4 * 1024 * 1024)
            throw new InvalidDataException("The trusted Sunshine executable has an invalid PE header offset.");
        stream.Position = peOffset;
        var peHeader = new byte[6];
        stream.ReadExactly(peHeader);
        if (peHeader[0] != (byte)'P' || peHeader[1] != (byte)'E' || peHeader[2] != 0 || peHeader[3] != 0)
            throw new InvalidDataException("The trusted Sunshine executable has an invalid PE signature.");
        return BitConverter.ToUInt16(peHeader, 4);
    }

    private static void EnsureRegularSingleLinkExecutable(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The trusted Sunshine executable must not be a reparse point.");
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var identity = MaintenanceInstallationLockSecurity.QueryIdentity(handle);
        MaintenanceInstallationLockSecurity.ValidateHeldFileIdentity(identity);
    }

    private static void ValidateTrustedFile(string path)
    {
        var security = new FileInfo(path)
            .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        ValidateTrustedSecurity(security, path, isDirectory: false);
    }

    private static void ValidateTrustedDirectory(string path)
    {
        var security = new DirectoryInfo(path)
            .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        ValidateTrustedSecurity(security, path, isDirectory: true);
    }

    private static void ValidateTrustedSecurity(FileSystemSecurity security, string path, bool isDirectory)
    {
        var trusted = TrustedInstallPrincipals();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner))
            throw new UnauthorizedAccessException($"The Sunshine {(isDirectory ? "directory" : "executable")} owner is not trusted.");

        var writeSensitive = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.AppendData |
            FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteData;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier)))
        {
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
                continue;
            if (rule.AccessControlType != AccessControlType.Allow)
                throw new UnauthorizedAccessException($"The Sunshine {(isDirectory ? "directory" : "executable")} ACL contains a deny rule.");
            if (rule.IdentityReference is not SecurityIdentifier sid)
                throw new UnauthorizedAccessException($"The Sunshine {(isDirectory ? "directory" : "executable")} ACL contains an untrusted identity.");
            if (trusted.Contains(sid))
                continue;
            if ((rule.FileSystemRights & writeSensitive) != 0)
                throw new UnauthorizedAccessException($"The Sunshine {(isDirectory ? "directory" : "executable")} ACL grants write access to an untrusted principal.");
        }
    }

    // S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464 is the well-known
    // Windows TrustedInstaller service SID; Program Files payloads are normally owned by it.
    private const string TrustedInstallerSidValue =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private static HashSet<SecurityIdentifier> TrustedInstallPrincipals()
    {
        return new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            new(TrustedInstallerSidValue)
        };
    }

    private static void EnsureAncestorsHaveNoReparsePoints(string anywhere)
    {
        var current = Directory.GetParent(anywhere);
        var depth = 0;
        while (current is not null && depth++ < 64)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A Sunshine installation ancestor is a reparse point.");
            current = current.Parent;
        }
    }

    private static string ProgramFilesRoot()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles) || !Path.IsPathFullyQualified(programFiles))
            throw new InvalidOperationException("The native Program Files path is unavailable.");
        return Path.GetFullPath(programFiles);
    }

    private static string DefaultSunshinePath() => Path.Combine(ProgramFilesRoot(), "Sunshine", "Sunshine.exe");

    internal static bool PathEquals(string first, string second) =>
        string.Equals(Path.GetFullPath(first).TrimEnd('\\'), Path.GetFullPath(second).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Adapter that lets the production backup publisher use the apply command's continuously
/// verified authorization instead of a separate weaker check.
/// </summary>
public sealed class HeldMaintenanceBackupAuthorization(IMaintenanceApplyAuthorization authorization) : IMaintenanceBackupAuthorization
{
    private readonly IMaintenanceApplyAuthorization authorization =
        authorization ?? throw new ArgumentNullException(nameof(authorization));

    public void VerifyHeldAndQuiesced(string operationId) => authorization.VerifyHeldAndQuiesced(operationId);

    public void VerifyHeld(string operationId) => authorization.VerifyHeld(operationId);
}

/// <summary>
/// Composes the real maintenance apply operation from the already-verified protected inputs:
/// the fixed Agent installation and backup roots, the protected journal, the verified target
/// extraction tree, the trusted Sunshine installation and the operation-bound authorization.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMaintenanceApplyOperationFactory : IMaintenanceApplyOperationFactory
{
    private readonly WindowsMaintenanceBackupSecurity security;
    private readonly InstalledAgentSourceInspector sourceInspector;
    private readonly IMaintenanceJournalStore journalStore;
    private readonly LocalUpdateEngine engine;

    public WindowsMaintenanceApplyOperationFactory(
        WindowsMaintenanceBackupSecurity security,
        InstalledAgentSourceInspector sourceInspector,
        IMaintenanceJournalStore journalStore,
        LocalUpdateEngine engine)
    {
        this.security = security ?? throw new ArgumentNullException(nameof(security));
        this.sourceInspector = sourceInspector ?? throw new ArgumentNullException(nameof(sourceInspector));
        this.journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public MaintenanceApplyOperation Create(
        MaintenanceCommandRequest request,
        string extractedTargetDirectory,
        string trustedSunshineExecutablePath,
        IMaintenanceApplyAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractedTargetDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedSunshineExecutablePath);
        if (string.IsNullOrWhiteSpace(request.ChainId))
            throw new InvalidOperationException("The apply request has no maintenance chain identity; the operation remains held.");
        if (!string.Equals(Path.GetFullPath(extractedTargetDirectory), new WindowsMaintenanceWorkStore().ExtractionRoot,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The target extraction root is not the fixed protected maintenance work directory.");

        var layout = CreateServiceLayout(security.SourceRoot, trustedSunshineExecutablePath);
        var journal = new MaintenanceJournal(journalStore, request.ChainId);
        _ = journal.VerifyApplyRequestBinding(request);

        var publisher = new MaintenanceBackupPublisher(
            security.SourceRoot, security.BackupRoot, security, new HeldMaintenanceBackupAuthorization(authorization));
        var backupOperation = new MaintenanceBackupOperation(security.SourceRoot, sourceInspector, publisher, journal);
        var fileSystem = new WindowsMaintenanceApplyFileSystem(security, extractedTargetDirectory);
        var services = new WindowsMaintenanceApplyServiceLifecycle(
            new WindowsMaintenanceServiceApi(),
            new WindowsMaintenanceProcessInspector(),
            authorization,
            layout,
            _ => sourceInspector.Inspect().Version);
        var terminalStateVerifier = new MaintenanceTerminalStateVerifier(
            security.SourceRoot, backupOperation, fileSystem, services, engine, engine.ApplyMetadataStore);

        return new MaintenanceApplyOperation(
            security.SourceRoot,
            extractedTargetDirectory,
            journal,
            backupOperation,
            authorization,
            fileSystem,
            services,
            terminalStateVerifier);
    }

    internal static MaintenanceServiceLayout CreateServiceLayout(string installDirectory, string trustedSunshineExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedSunshineExecutablePath);
        var installRoot = Path.GetFullPath(installDirectory);
        if (!string.Equals(Path.GetFileName(installRoot), "Agent", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Agent installation directory is not the canonical layout.");
        var agentExecutable = Path.Combine(installRoot, "Agent.exe");

        var sunshine = Path.GetFullPath(trustedSunshineExecutablePath);
        if (!string.Equals(Path.GetFileName(sunshine), "Sunshine.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The trusted Sunshine path does not name Sunshine.exe.");
        var sunshineDirectory = Path.GetDirectoryName(sunshine)
            ?? throw new InvalidDataException("The trusted Sunshine path has no installation directory.");
        var toolsDirectory = Path.Combine(sunshineDirectory, "tools");
        if (!Directory.Exists(toolsDirectory))
            throw new DirectoryNotFoundException("The Sunshine service tools directory is missing.");
        if ((File.GetAttributes(toolsDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The Sunshine service tools directory must not be a reparse point.");
        var sunshineService = Path.Combine(toolsDirectory, "sunshinesvc.exe");
        if (!File.Exists(sunshineService))
            throw new FileNotFoundException("The Sunshine service host executable is missing.", sunshineService);

        return new MaintenanceServiceLayout(agentExecutable, sunshineService, sunshine);
    }
}
