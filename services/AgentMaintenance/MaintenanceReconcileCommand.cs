using System.Runtime.Versioning;

namespace KamenRaider.AgentMaintenance;

public interface IMaintenanceReconcileExecutor
{
    AgentMaintenanceCommandResult Execute(
        MaintenanceCommandRequest request,
        IMaintenanceApplyProcessExclusionLease exclusion);
}

public sealed class MaintenanceApplyReconcileExecutor(IMaintenanceApplyWithHeldExclusion apply)
    : IMaintenanceReconcileExecutor
{
    public AgentMaintenanceCommandResult Execute(
        MaintenanceCommandRequest request,
        IMaintenanceApplyProcessExclusionLease exclusion)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(exclusion);
        return apply.ExecuteWithHeldExclusion(exclusion, request);
    }
}

public sealed class MaintenanceReconcileCommand(
    LocalUpdateEngine engine,
    IMaintenanceJournalStore journalStore,
    IMaintenanceApplyProcessExclusionProvider exclusionProvider,
    IMaintenanceReconcileExecutor executor) : IMaintenanceReconcileCommand
{
    private static readonly HashSet<string> RecoverableCurrentPhases = new(StringComparer.Ordinal)
    {
        "sourceBound",
        "swapIntent",
        "sourceMoved",
        "targetPlaced",
        "rollbackIntent",
        "targetMovedToFailed",
        "sourceRestored",
        "rollbackServicesStarted",
        "servicesStarted"
    };

    [SupportedOSPlatform("windows")]
    public static MaintenanceReconcileCommand CreateFailClosed(LocalUpdateEngine engine)
    {
        var exclusion = new UnqualifiedLegacyProcessExclusionProvider();
        var apply = MaintenanceApplyCommand.CreateFailClosed(engine);
        return new(engine, new WindowsMaintenanceJournalStore(), exclusion,
            new MaintenanceApplyReconcileExecutor(apply));
    }

    public AgentMaintenanceCommandResult Execute()
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(journalStore);
        ArgumentNullException.ThrowIfNull(exclusionProvider);
        ArgumentNullException.ThrowIfNull(executor);

        var exclusion = exclusionProvider.Acquire();
        if (exclusion is null)
            return new("unavailable", "legacyProcessExclusionUnqualified", null, null, null, null, null, null);

        using (exclusion)
        {
            MaintenanceCommandRequest request;
            MaintenanceJournalOperation operation;
            try
            {
                request = engine.ReadRequest("apply");
                operation = new MaintenanceJournal(journalStore, request.ChainId!).VerifyApplyRequestBinding(request);
            }
            catch (InvalidDataException)
            {
                return new("unavailable", "journalUnverifiable", null, null, null, null, null, null);
            }
            catch (InvalidOperationException)
            {
                return new("unavailable", "reconciliationPrecondition", null, null, null, null, null, null);
            }

            if (!RecoverableCurrentPhases.Contains(operation.Phase))
                return new("unavailable", "reconciliationPhaseUnsupported", null, null, null, null, null, null);

            exclusion.VerifyContinuouslyHeld(request.OperationId);
            return executor.Execute(request, exclusion);
        }
    }
}
