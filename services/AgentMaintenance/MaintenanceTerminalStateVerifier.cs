using System.Security.Cryptography;
using KamenRaider.AgentPackage;

namespace KamenRaider.AgentMaintenance;

public enum MaintenanceTerminalOutcome
{
    Applied,
    RolledBack
}

public interface IMaintenanceTerminalStateVerifier
{
    Task VerifyAsync(
        MaintenanceOperationBinding binding,
        MaintenanceJournalOperation operation,
        MaintenanceTerminalOutcome expectedOutcome,
        PackageInventory? expectedTarget,
        Action verifyHeld,
        CancellationToken cancellationToken);
}

public sealed class MaintenanceTerminalStateVerifier(
    string installDirectory,
    MaintenanceBackupOperation backupOperation,
    IMaintenanceApplyFileSystem fileSystem,
    IMaintenanceApplyServiceLifecycle services,
    LocalUpdateEngine? engine = null,
    IMaintenanceApplyMetadataStore? applyMetadataStore = null) : IMaintenanceTerminalStateVerifier
{
    public async Task VerifyAsync(
        MaintenanceOperationBinding binding,
        MaintenanceJournalOperation operation,
        MaintenanceTerminalOutcome expectedOutcome,
        PackageInventory? expectedTarget,
        Action verifyHeld,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(backupOperation);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(verifyHeld);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);

        if (operation.Binding != binding || !MatchesOutcomePhase(operation, expectedOutcome))
            throw new InvalidOperationException("The journal phase does not match the requested terminal outcome.");

        verifyHeld();
        cancellationToken.ThrowIfCancellationRequested();
        PackageInventory target;
        if (expectedTarget is not null)
        {
            target = expectedTarget;
        }
        else
        {
            if (engine is null || applyMetadataStore is null)
                throw new InvalidOperationException("The terminal verifier has no immutable protected apply metadata source.");
            var metadata = applyMetadataStore.Read(binding);
            target = engine.ValidateApplyArchive(metadata);
        }
        if (!string.Equals(target.Version, binding.TargetVersion, StringComparison.Ordinal) ||
            !string.Equals(target.TargetRuntime, binding.TargetRuntime, StringComparison.Ordinal) ||
            !FixedHexEquals(target.ArchiveHash, binding.TargetSha256))
            throw new InvalidDataException("The pinned target package no longer matches the terminal journal binding.");

        var backup = backupOperation.VerifyPublishedBackupUnderHold(binding.OperationId, verifyHeld);
        if (!string.Equals(backup.BackupOwnerOperationId, binding.OperationId, StringComparison.Ordinal) ||
            operation.BackupOwnerOperationId != binding.OperationId ||
            !FixedHexEquals(operation.BackupDigest, backup.BackupDigest))
            throw new InvalidDataException("The immutable backup no longer matches the terminal journal binding.");

        string expectedServiceVersion;
        var installPath = Path.GetFullPath(installDirectory);
        if (expectedOutcome == MaintenanceTerminalOutcome.Applied)
        {
            if (!FixedHexEquals(operation.TargetInventoryDigest, target.InventoryDigest))
                throw new InvalidDataException("The terminal target inventory differs from the journaled apply inventory.");
            var config = backup.Nodes.SingleOrDefault(node => !node.IsDirectory &&
                string.Equals(node.RelativePath, "appsettings.json", StringComparison.Ordinal))
                ?? throw new InvalidDataException("The immutable backup is missing the preserved machine configuration.");
            var expectedInstall = PackageVerifier.IncludePreservedAppSettings(
                PackageVerifier.CreateAgentInstallInventory(target), config.Length, config.Sha256!);
            verifyHeld();
            fileSystem.ValidateTargetInstallTree(installPath, expectedInstall);
            expectedServiceVersion = binding.TargetVersion;
        }
        else
        {
            expectedServiceVersion = operation.SourceVersion
                ?? throw new InvalidDataException("The rollback journal is missing its original source version.");
            verifyHeld();
            fileSystem.VerifyBackupTree(installPath, backup);
        }

        await services.VerifyStartedAsync(binding.OperationId, installPath, expectedServiceVersion,
            verifyHeld, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        verifyHeld();
    }

    private static bool MatchesOutcomePhase(MaintenanceJournalOperation operation, MaintenanceTerminalOutcome outcome)
    {
        if (operation.Phase is "servicesStarted" or "rollbackServicesStarted")
            return outcome == MaintenanceTerminalOutcome.Applied
                ? operation.Phase == "servicesStarted"
                : operation.Phase == "rollbackServicesStarted";
        if (operation.Phase != "outcomeReported") return false;
        var rolledBack = operation.PhaseHistory.Contains("rollbackServicesStarted", StringComparer.Ordinal);
        return outcome == MaintenanceTerminalOutcome.RolledBack ? rolledBack : !rolledBack;
    }

    private static bool FixedHexEquals(string? left, string? right) =>
        left is { Length: 64 } && right is { Length: 64 } &&
        left.All(Uri.IsHexDigit) && right.All(Uri.IsHexDigit) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
}
