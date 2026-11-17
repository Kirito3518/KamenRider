namespace KamenRaider.AgentMaintenance;

public interface IMaintenanceSourceBindingPreparation
{
    void Prepare(
        MaintenanceLifecycleRequest request,
        MaintenanceJournal journal,
        IMaintenanceApplyProcessExclusionLease exclusion,
        IMaintenanceSourceBindingAuthorization authorization);
}

public sealed class MaintenanceSourceBindingPreparation(
    LocalUpdateEngine engine,
    WindowsMaintenanceFence fence,
    string trustedSunshineExecutablePath,
    MaintenanceServiceLayout serviceLayout,
    IMaintenanceWindowsServiceApi services,
    IMaintenanceProcessInspector processes) : IMaintenanceSourceBindingPreparation
{
    public void Prepare(
        MaintenanceLifecycleRequest request,
        MaintenanceJournal journal,
        IMaintenanceApplyProcessExclusionLease exclusion,
        IMaintenanceSourceBindingAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(exclusion);
        ArgumentNullException.ThrowIfNull(authorization);

        var snapshot = journal.Read()
            ?? throw new InvalidOperationException("The maintenance journal is missing; source binding remains held.");
        var binding = MaintenanceLifecycleValidation.CreateBinding(request);
        var headOperation = MaintenanceJournal.RequireHead(snapshot, request.OperationId);
        if (snapshot.ChainId != request.ChainId || snapshot.HeadOperationId != request.OperationId ||
            snapshot.Generation != request.Generation ||
            headOperation.Binding != binding ||
            headOperation.Phase is not ("controllerStarted" or "targetValidated" or "servicesQuiesced"))
            throw new InvalidOperationException("Target preparation does not match the current started journal head.");

        exclusion.VerifyContinuouslyHeld(request.OperationId);
        authorization.VerifyRequestBinding(request);
        authorization.VerifyHeld(request.OperationId);

        var target = engine.ValidateLifecycleArchive(request);
        if (target.Version != request.TargetVersion || target.TargetRuntime != request.TargetRuntime ||
            !string.Equals(target.ArchiveHash, request.TargetPackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The target archive does not match the immutable started operation.");

        var operation = headOperation;
        if (operation.Phase == "controllerStarted")
            operation = MaintenanceJournal.Head(journal.Advance(request.OperationId, "targetValidated"));

        var fencePhase = operation.Phase == "servicesQuiesced" ? "servicesQuiesced" : "targetValidated";
        var sunshinePath = ValidateSunshinePath(trustedSunshineExecutablePath);
        var heldFence = FenceRecord(binding, fencePhase);
        fence.Hold(heldFence, sunshinePath);
        fence.VerifyHeld(heldFence, sunshinePath);

        var stopPrerequisites = new StopPrerequisites(request, binding, journal, exclusion, authorization,
            fence, heldFence, sunshinePath);
        var quiescer = new MaintenanceServiceQuiescer(services, processes, stopPrerequisites,
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50));
        quiescer.QuiesceAsync(serviceLayout, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static AgentMaintenanceFenceRecord FenceRecord(MaintenanceOperationBinding binding, string phase) =>
        new(1, "held", binding.OperationId, binding.ChainId, binding.HeadOperationId, binding.Generation, phase);

    private static string ValidateSunshinePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetFileName(path), "sunshine.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The trusted Sunshine path is not an absolute sunshine.exe path.");
        return Path.GetFullPath(path);
    }

    private sealed class StopPrerequisites(
        MaintenanceLifecycleRequest request,
        MaintenanceOperationBinding binding,
        MaintenanceJournal journal,
        IMaintenanceApplyProcessExclusionLease exclusion,
        IMaintenanceSourceBindingAuthorization authorization,
        WindowsMaintenanceFence fence,
        AgentMaintenanceFenceRecord initialHeldFence,
        string sunshinePath) : IMaintenanceStopPrerequisites
    {
        public Task VerifyBeforeStopAsync(CancellationToken cancellationToken)
        {
            VerifyStillHeldAsync(cancellationToken).GetAwaiter().GetResult();
            return Task.CompletedTask;
        }

        public Task VerifyStillHeldAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            exclusion.VerifyContinuouslyHeld(binding.OperationId);
            authorization.VerifyRequestBinding(request);
            authorization.VerifyHeld(binding.OperationId);
            var current = journal.Read()
                ?? throw new InvalidOperationException("The maintenance journal disappeared while services were stopping.");
            var currentHead = MaintenanceJournal.RequireHead(current, binding.OperationId);
            if (current.HeadOperationId != binding.OperationId || current.Generation != binding.Generation ||
                currentHead.Binding != binding ||
                currentHead.Phase is not ("targetValidated" or "servicesQuiesced"))
                throw new InvalidOperationException("The maintenance journal changed while services were stopping.");
            var expectedFence = currentHead.Phase == "servicesQuiesced"
                ? FenceRecord(binding, "servicesQuiesced")
                : initialHeldFence;
            fence.VerifyHeld(expectedFence, sunshinePath);
            return Task.CompletedTask;
        }

        public Task MarkServicesQuiescedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyStillHeldAsync(cancellationToken).GetAwaiter().GetResult();
            var current = MaintenanceJournal.Head(journal.Read()!);
            if (current.Phase == "targetValidated")
                _ = journal.Advance(binding.OperationId, "servicesQuiesced");
            var quiescedFence = FenceRecord(binding, "servicesQuiesced");
            fence.Hold(quiescedFence, sunshinePath);
            fence.VerifyHeld(quiescedFence, sunshinePath);
            exclusion.VerifyContinuouslyHeld(binding.OperationId);
            authorization.VerifyHeld(binding.OperationId);
            return Task.CompletedTask;
        }
    }
}
