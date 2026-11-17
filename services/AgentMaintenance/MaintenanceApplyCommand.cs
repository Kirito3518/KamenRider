using System.IO;
using System.Runtime.InteropServices;
using KamenRaider.AgentPackage;

namespace KamenRaider.AgentMaintenance;

public interface IMaintenanceApplyProcessExclusionProvider
{
    // A null result means the provider cannot prove continuous exclusion of the old
    // updater/Agent processes. A non-null lease must retain and recheck that proof.
    IMaintenanceApplyProcessExclusionLease? Acquire();
}

public interface IMaintenanceApplyProcessExclusionLease : IDisposable
{
    // Recheck the held exclusion before each service/install transition.
    void VerifyContinuouslyHeld(string operationId);
}

public interface IMaintenanceApplyLeaseAuthorization : IMaintenanceApplyAuthorization
{
    void VerifyRequestBinding(MaintenanceCommandRequest request);
}

public interface IMaintenanceApplyWithHeldExclusion : IMaintenanceApplyCommand
{
    AgentMaintenanceCommandResult ExecuteWithHeldExclusion(
        IMaintenanceApplyProcessExclusionLease exclusion,
        MaintenanceCommandRequest? expectedRequest = null);
}

public interface IMaintenanceTrustedSunshinePathProvider
{
    string GetTrustedSunshineExecutablePath(MaintenanceCommandRequest request);
}

public interface IMaintenanceApplyOperationFactory
{
    MaintenanceApplyOperation Create(
        MaintenanceCommandRequest request,
        string extractedTargetDirectory,
        string trustedSunshineExecutablePath,
        IMaintenanceApplyAuthorization authorization);
}

public sealed class MaintenanceApplyCommand : IMaintenanceApplyWithHeldExclusion
{
    private readonly LocalUpdateEngine engine;
    private readonly IMaintenanceApplyProcessExclusionProvider exclusionProvider;
    private readonly IMaintenanceApplyLeaseAuthorization leaseAuthorization;
    private readonly IMaintenanceTrustedSunshinePathProvider sunshinePathProvider;
    private readonly IMaintenanceApplyOperationFactory operationFactory;
    private readonly IMaintenanceJournalStore? journalStore;
    private readonly IMaintenanceApplyMetadataStore? applyMetadataStore;
    private readonly IMaintenanceOperationFenceVerifier? fenceVerifier;

    public MaintenanceApplyCommand(
        LocalUpdateEngine engine,
        IMaintenanceApplyProcessExclusionProvider exclusionProvider,
        IMaintenanceApplyLeaseAuthorization leaseAuthorization,
        IMaintenanceTrustedSunshinePathProvider sunshinePathProvider,
        IMaintenanceApplyOperationFactory operationFactory,
        IMaintenanceJournalStore? journalStore = null,
        IMaintenanceApplyMetadataStore? applyMetadataStore = null,
        IMaintenanceOperationFenceVerifier? fenceVerifier = null)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.exclusionProvider = exclusionProvider ?? throw new ArgumentNullException(nameof(exclusionProvider));
        this.leaseAuthorization = leaseAuthorization ?? throw new ArgumentNullException(nameof(leaseAuthorization));
        this.sunshinePathProvider = sunshinePathProvider ?? throw new ArgumentNullException(nameof(sunshinePathProvider));
        this.operationFactory = operationFactory ?? throw new ArgumentNullException(nameof(operationFactory));
        this.journalStore = journalStore;
        this.applyMetadataStore = applyMetadataStore ?? engine.ApplyMetadataStore;
        this.fenceVerifier = fenceVerifier;
    }

    public static MaintenanceApplyCommand CreateFailClosed(
        LocalUpdateEngine engine,
        IMaintenanceApplyProcessExclusionProvider? exclusionProvider = null,
        IMaintenanceApplyLeaseAuthorization? leaseAuthorization = null) =>
        new(engine,
            exclusionProvider ?? new UnqualifiedLegacyProcessExclusionProvider(),
            leaseAuthorization ?? new UnavailableMaintenanceApplyLeaseAuthorization(),
            new UnavailableMaintenanceTrustedSunshinePathProvider(),
            new UnavailableMaintenanceApplyOperationFactory());

    public AgentMaintenanceCommandResult Execute()
    {
        var exclusion = exclusionProvider.Acquire();
        if (exclusion is null)
            return new("unavailable", "legacyProcessExclusionUnqualified", null, null, null, null, null, null);

        using (exclusion)
        {
            return ExecuteWithHeldExclusion(exclusion);
        }
    }

    public AgentMaintenanceCommandResult ExecuteWithHeldExclusion(
        IMaintenanceApplyProcessExclusionLease exclusion,
        MaintenanceCommandRequest? expectedRequest = null)
    {
        ArgumentNullException.ThrowIfNull(exclusion);
        var request = engine.ReadRequest("apply");
        if (expectedRequest is not null && expectedRequest != request)
            throw new InvalidOperationException("The protected apply request changed during reconciliation.");
        VerifyNativeTargetRuntime(request.TargetRuntime);
        if (journalStore is null)
            throw new InvalidOperationException("The protected maintenance journal store is unavailable.");
        var journalOperation = new MaintenanceJournal(journalStore, request.ChainId!).VerifyApplyRequestBinding(request);

        var authorization = new BoundMaintenanceApplyAuthorization(request, journalOperation.Binding,
            exclusion, leaseAuthorization, fenceVerifier);
        authorization.VerifyHeld(request.OperationId);
        var metadata = applyMetadataStore?.GetOrCreate(request)
            ?? throw new InvalidOperationException("The protected operation-bound apply metadata store is unavailable.");
        if (metadata != MaintenanceApplyMetadata.FromRequest(request))
            throw new InvalidOperationException("The protected operation-bound apply metadata differs from the journaled target.");
        var sunshinePath = ValidateTrustedSunshinePath(
            sunshinePathProvider.GetTrustedSunshineExecutablePath(request));
        var (inventory, extractionRoot) = engine.ExtractForApply(request);
        // Extraction is isolated under the operation's protected work path. The
        // apply operation performs the quiescence check immediately before each
        // service or install transition, including recovery from a lost journal write.
        authorization.VerifyHeld(request.OperationId);

        var applyOperation = operationFactory.Create(
            request, extractionRoot, sunshinePath, authorization)
            ?? throw new InvalidOperationException("The maintenance apply operation factory returned no operation.");
        var result = applyOperation.ApplyAsync(request.OperationId, inventory, CancellationToken.None)
            .GetAwaiter().GetResult();
        authorization.VerifyHeld(request.OperationId);

        var applied = string.Equals(result.Outcome, "applied", StringComparison.Ordinal);
        return new AgentMaintenanceCommandResult(
            applied ? "ok" : "failed",
            applied ? null : "rolledBack",
            inventory.ArchiveHash,
            result.InstalledVersion,
            inventory.TargetRuntime,
            inventory.InventoryDigest,
            inventory.TotalBytes,
            null,
            ApplyOutcome: result.Outcome,
            RollbackReason: result.RollbackReason);
    }

    private static string ValidateTrustedSunshinePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("The trusted Sunshine executable path is unavailable or not absolute.");
        var normalized = Path.GetFullPath(path);
        if (!string.Equals(Path.GetFileName(normalized), "sunshine.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The trusted Sunshine path does not name the supported executable.");
        return normalized;
    }

    private static void VerifyNativeTargetRuntime(string targetRuntime)
    {
        var nativeRuntime = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("The maintenance helper requires a supported native Windows architecture.")
        };
        if (!string.Equals(targetRuntime, nativeRuntime, StringComparison.Ordinal))
            throw new InvalidOperationException("The target package runtime does not match the native host architecture.");
    }

    private sealed class BoundMaintenanceApplyAuthorization(
        MaintenanceCommandRequest request,
        MaintenanceOperationBinding binding,
        IMaintenanceApplyProcessExclusionLease exclusion,
        IMaintenanceApplyLeaseAuthorization leaseAuthorization,
        IMaintenanceOperationFenceVerifier? fenceVerifier) : IMaintenanceApplyAuthorization
    {
        public void VerifyHeldAndQuiesced(string operationId)
        {
            VerifyOperationId(operationId);
            exclusion.VerifyContinuouslyHeld(operationId);
            leaseAuthorization.VerifyRequestBinding(request);
            leaseAuthorization.VerifyHeldAndQuiesced(operationId);
            fenceVerifier?.VerifyHeld(binding);
        }

        public void VerifyHeld(string operationId)
        {
            VerifyOperationId(operationId);
            exclusion.VerifyContinuouslyHeld(operationId);
            leaseAuthorization.VerifyRequestBinding(request);
            leaseAuthorization.VerifyHeld(operationId);
            fenceVerifier?.VerifyHeld(binding);
        }

        private void VerifyOperationId(string operationId)
        {
            if (!string.Equals(operationId, request.OperationId, StringComparison.Ordinal))
                throw new InvalidOperationException("The apply authorization belongs to another operation.");
        }
    }
}

public sealed class UnqualifiedLegacyProcessExclusionProvider : IMaintenanceApplyProcessExclusionProvider
{
    // No production path currently proves that a captured legacy process was excluded.
    public IMaintenanceApplyProcessExclusionLease? Acquire() => null;
}

internal sealed class UnavailableMaintenanceApplyLeaseAuthorization : IMaintenanceApplyLeaseAuthorization
{
    public void VerifyRequestBinding(MaintenanceCommandRequest request) =>
        throw new InvalidOperationException("The Controller maintenance lease authorization is unavailable.");

    public void VerifyHeldAndQuiesced(string operationId) =>
        throw new InvalidOperationException("The Controller maintenance lease authorization is unavailable.");

    public void VerifyHeld(string operationId) =>
        throw new InvalidOperationException("The Controller maintenance lease authorization is unavailable.");
}

internal sealed class UnavailableMaintenanceTrustedSunshinePathProvider : IMaintenanceTrustedSunshinePathProvider
{
    public string GetTrustedSunshineExecutablePath(MaintenanceCommandRequest request) =>
        throw new InvalidOperationException("The trusted Sunshine path provider is unavailable.");
}

internal sealed class UnavailableMaintenanceApplyOperationFactory : IMaintenanceApplyOperationFactory
{
    public MaintenanceApplyOperation Create(
        MaintenanceCommandRequest request,
        string extractedTargetDirectory,
        string trustedSunshineExecutablePath,
        IMaintenanceApplyAuthorization authorization) =>
        throw new InvalidOperationException("The production maintenance apply factory is unavailable.");
}
