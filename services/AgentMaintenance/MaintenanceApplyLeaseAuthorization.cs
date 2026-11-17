using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KamenRaider.AgentMaintenance;

/// <summary>
/// Boot identity and monotonic uptime for local lease reconciliation. The values must match
/// the Agent's own Windows boot clock: native boot identifier and GetTickCount64 (which
/// includes sleep/hibernation), never wall-clock time or Environment.TickCount64.
/// </summary>
public interface IMaintenanceLeaseEnvironment
{
    string ReadBootId();
    long ReadUptimeMilliseconds();
}

[SupportedOSPlatform("windows")]
public sealed class WindowsMaintenanceLeaseEnvironment : IMaintenanceLeaseEnvironment
{
    private readonly IMaintenanceBootIdentityProvider bootIdentity = new WindowsMaintenanceBootIdentityProvider();

    public string ReadBootId() => bootIdentity.ReadBootId();

    public long ReadUptimeMilliseconds()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The maintenance lease clock requires Windows.");
        return checked((long)GetTickCount64());
    }

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}

/// <summary>
/// Production local lease authorization for the maintenance apply command.
///
/// The Controller reservation requires no held allocation and the durable start ACK proves the
/// chain is held, but absence of a local lease file is not proof that no lease exists (the Agent
/// can be between commands). Before every service/install transition the helper therefore
/// re-reads the validated maintenance journal (same chain/head/generation/binding and the
/// Controller start acknowledgement phase) and the Agent's protected lease.json, treating a
/// missing file as no lease while corrupt, unreadable or still-live current-boot lease state
/// remains held.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMaintenanceApplyLeaseAuthorization : IMaintenanceApplyLeaseAuthorization
{
    public const string LocalLeaseFileName = MaintenanceLocalLeaseEvidence.LeaseFileName;

    private readonly IMaintenanceJournalStore journalStore;
    private readonly IMaintenanceLeaseEnvironment environment;
    private readonly object gate = new();
    private MaintenanceCommandRequest? boundRequest;

    public WindowsMaintenanceApplyLeaseAuthorization()
        : this(new WindowsMaintenanceJournalStore(), DefaultAgentDataDirectory, new WindowsMaintenanceLeaseEnvironment())
    {
    }

    internal WindowsMaintenanceApplyLeaseAuthorization(
        IMaintenanceJournalStore journalStore,
        string agentDataDirectory,
        IMaintenanceLeaseEnvironment environment)
    {
        this.journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(agentDataDirectory);
        var directory = Path.GetFullPath(agentDataDirectory);
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("The Agent data directory must be an absolute path.", nameof(agentDataDirectory));
        AgentDataDirectory = directory;
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public static string DefaultAgentDataDirectory
    {
        get
        {
            var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(commonData) || !Path.IsPathFullyQualified(commonData))
                throw new InvalidOperationException("The native CommonApplicationData path is unavailable.");
            return Path.Combine(Path.GetFullPath(commonData), "KamenRaider", "Agent");
        }
    }

    public string AgentDataDirectory { get; }

    public string LeasePath => MaintenanceLocalLeaseEvidence.LeasePath(AgentDataDirectory);

    public void VerifyRequestBinding(MaintenanceCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validated = LocalUpdateEngineRequestValidation.Validate(request, "apply");
        VerifyControllerHoldAndLease(validated);
        lock (gate)
            boundRequest = validated;
    }

    public void VerifyHeld(string operationId)
    {
        var request = BoundRequest(operationId);
        VerifyControllerHoldAndLease(request);
    }

    public void VerifyHeldAndQuiesced(string operationId)
    {
        // Quiescence itself is verified by the service lifecycle adapter. The lease and journal
        // evidence are re-read here because the Agent can race the helper between stop and copy.
        VerifyHeld(operationId);
    }

    private void VerifyControllerHoldAndLease(MaintenanceCommandRequest request)
    {
        if (request.ChainId is null || request.HeadOperationId is null || request.Generation is null)
            throw new InvalidOperationException("The apply request has no maintenance chain identity; the operation remains held.");
        _ = MaintenanceJournalHoldEvidence.ReadAndVerify(
            journalStore,
            request.ChainId,
            request.HeadOperationId,
            request.Generation.Value,
            request.OperationId,
            request.TargetVersion,
            request.TargetRuntime,
            request.TargetPackageSha256);
        MaintenanceLocalLeaseEvidence.EnsureDrained(AgentDataDirectory, environment);
    }

    private MaintenanceCommandRequest BoundRequest(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        lock (gate)
            return boundRequest is { } request && string.Equals(request.OperationId, operationId, StringComparison.Ordinal)
                ? request
                : throw new InvalidOperationException(
                    "The maintenance apply lease authorization is not bound to this operation; the operation remains held.");
    }
}
