using System.Security.Cryptography;
using System.Text.RegularExpressions;
using KamenRaider.AgentPackage;

namespace KamenRaider.AgentMaintenance;

public interface IMaintenanceApplyAuthorization
{
    void VerifyHeldAndQuiesced(string operationId);
    void VerifyHeld(string operationId);
}

public interface IMaintenanceApplyFileSystem
{
    void EnsureProtectedInstallParent(string installDirectory);
    bool DirectoryExists(string path);
    void PrepareTargetInstallTree(string extractedTree, string destination, PackageInventory expected, MaintenanceBackupResult backup);
    void ValidateTargetInstallTree(string path, PackageInventory expected);
    void EnsureOwnedInstallTree(string path);
    void VerifyBackupTree(string path, MaintenanceBackupResult backup);
    void PrepareBackupInstallTree(string backupPath, string destination, MaintenanceBackupResult backup);
    void MoveDirectory(string source, string destination);
    void FlushParent(string path);
}

public interface IMaintenanceApplyServiceLifecycle
{
    Task EnsureStoppedAndVerifyQuiescedAsync(string operationId, CancellationToken cancellationToken);
    Task StartAndVerifyAsync(string operationId, string installDirectory, string expectedVersion, CancellationToken cancellationToken);
}

public sealed record MaintenanceApplyResult(string Outcome, string InstalledVersion, string? RollbackReason);

public sealed class MaintenanceApplyOperation(
    string installDirectory,
    string extractedTargetDirectory,
    MaintenanceJournal journal,
    MaintenanceBackupOperation backupOperation,
    IMaintenanceApplyAuthorization authorization,
    IMaintenanceApplyFileSystem fileSystem,
    IMaintenanceApplyServiceLifecycle services)
{
    public async Task<MaintenanceApplyResult> ApplyAsync(
        string operationId,
        PackageInventory expectedTarget,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(expectedTarget);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(backupOperation);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(services);

        var operation = ReadOperation(operationId);
        var binding = operation.Binding;
        if (expectedTarget.Version != binding.TargetVersion || expectedTarget.TargetRuntime != binding.TargetRuntime ||
            !FixedHexEquals(expectedTarget.ArchiveHash, binding.TargetSha256))
            throw new InvalidDataException("The verified target archive does not match the immutable journal binding.");
        var packageInstallTree = PackageVerifier.CreateAgentInstallInventory(expectedTarget);

        var parent = Path.GetDirectoryName(Path.GetFullPath(installDirectory))
            ?? throw new InvalidDataException("The installed Agent path has no parent.");
        var targetStage = Sibling(parent, ".Agent-stage-", operationId);
        var prior = Sibling(parent, ".Agent-prior-", operationId);
        var failedTarget = Sibling(parent, ".Agent-failed-", operationId);
        var restoreStage = Sibling(parent, ".Agent-restore-", operationId);

        if (operation.Phase is "inspected" or "controllerStarted" or "targetValidated" or "servicesQuiesced" or "backupPublished")
            throw new InvalidOperationException("A signed source binding is required before target apply.");
        if (operation.Phase is "controllerConfirmed" or "outcomeReported")
            throw new InvalidOperationException("The maintenance outcome is already reported or confirmed.");
        if (operation.Phase == "servicesStarted")
            return new("applied", binding.TargetVersion, null);
        if (operation.Phase == "rollbackServicesStarted")
            return new("rolledBack", operation.SourceVersion!, operation.RollbackReason);
        if (operation.Phase is "rollbackIntent" or "targetMovedToFailed" or "sourceRestored")
        {
            return await ContinueRollbackAsync(operationId, expectedTarget, failedTarget,
                restoreStage, cancellationToken).ConfigureAwait(false);
        }
        if (operation.Phase == "targetPlaced")
        {
            authorization.VerifyHeld(operationId);
            await services.EnsureStoppedAndVerifyQuiescedAsync(operationId, cancellationToken).ConfigureAwait(false);
            authorization.VerifyHeldAndQuiesced(operationId);
        }

        var publishedBackup = backupOperation.VerifyPublishedBackup(operationId);
        VerifyBackupBinding(operation, publishedBackup);
        var preservedConfiguration = publishedBackup.Nodes.SingleOrDefault(node =>
            !node.IsDirectory && string.Equals(node.RelativePath, "appsettings.json", StringComparison.Ordinal));
        if (operation.Phase == "sourceBound" && preservedConfiguration is null)
            throw new InvalidDataException("The immutable Agent backup is missing the required machine configuration.");
        var expectedInstallTree = preservedConfiguration is null
            ? packageInstallTree
            : PackageVerifier.IncludePreservedAppSettings(packageInstallTree,
                preservedConfiguration.Length, preservedConfiguration.Sha256!);

        if (operation.Phase == "sourceBound")
        {
            PackageVerifier.ValidateExtracted(extractedTargetDirectory, expectedTarget);
            authorization.VerifyHeldAndQuiesced(operationId);
            fileSystem.EnsureProtectedInstallParent(installDirectory);
            fileSystem.PrepareTargetInstallTree(extractedTargetDirectory, targetStage, expectedTarget, publishedBackup);
            PackageVerifier.ValidateExtracted(targetStage, expectedInstallTree);
            fileSystem.ValidateTargetInstallTree(targetStage, expectedInstallTree);
            operation = journal.Advance(operationId, "swapIntent",
                new MaintenanceJournalEvidence(TargetInventoryDigest: expectedTarget.InventoryDigest)).Operations.Single();
        }

        if (operation.Phase == "swapIntent")
        {
            var backup = backupOperation.VerifyPublishedBackup(operationId);
            VerifyBackupBinding(operation, backup);
            fileSystem.EnsureProtectedInstallParent(installDirectory);
            authorization.VerifyHeldAndQuiesced(operationId);
            var hasInstall = fileSystem.DirectoryExists(installDirectory);
            var hasPrior = fileSystem.DirectoryExists(prior);
            if (hasInstall && !hasPrior)
            {
                fileSystem.VerifyBackupTree(installDirectory, backup);
                fileSystem.MoveDirectory(installDirectory, prior);
                fileSystem.FlushParent(installDirectory);
            }
            else if (!hasInstall && hasPrior)
            {
                fileSystem.VerifyBackupTree(prior, backup);
            }
            else
            {
                throw new InvalidDataException("The source and prior trees do not match a recoverable swap-intent state.");
            }
            operation = journal.Advance(operationId, "sourceMoved").Operations.Single();
        }

        if (operation.Phase == "sourceMoved")
        {
            try
            {
                var backup = publishedBackup;
                VerifyBackupBinding(operation, backup);
                authorization.VerifyHeldAndQuiesced(operationId);
                if (fileSystem.DirectoryExists(installDirectory))
                {
                    PackageVerifier.ValidateExtracted(installDirectory, expectedInstallTree);
                    fileSystem.ValidateTargetInstallTree(installDirectory, expectedInstallTree);
                }
                else
                {
                    if (!fileSystem.DirectoryExists(targetStage))
                        throw new InvalidDataException("The verified target stage is missing after the source move.");
                    PackageVerifier.ValidateExtracted(targetStage, expectedInstallTree);
                    fileSystem.ValidateTargetInstallTree(targetStage, expectedInstallTree);
                    fileSystem.MoveDirectory(targetStage, installDirectory);
                    fileSystem.FlushParent(installDirectory);
                    PackageVerifier.ValidateExtracted(installDirectory, expectedInstallTree);
                    fileSystem.ValidateTargetInstallTree(installDirectory, expectedInstallTree);
                }
                operation = journal.Advance(operationId, "targetPlaced").Operations.Single();
            }
            catch
            {
                var current = ReadOperation(operationId);
                if (current.Phase != "sourceMoved")
                    throw;
                _ = journal.Advance(operationId, "rollbackIntent", new MaintenanceJournalEvidence(
                    BackupDigest: current.BackupDigest,
                    TargetInventoryDigest: current.TargetInventoryDigest,
                    RollbackReason: "applyFailed"));
                return await ContinueRollbackAsync(operationId, expectedTarget, failedTarget,
                    restoreStage, CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (operation.Phase == "targetPlaced")
        {
            PackageVerifier.ValidateExtracted(installDirectory, expectedInstallTree);
            fileSystem.ValidateTargetInstallTree(installDirectory, expectedInstallTree);
            authorization.VerifyHeldAndQuiesced(operationId);
            try
            {
                await services.StartAndVerifyAsync(operationId, installDirectory, binding.TargetVersion, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                var current = ReadOperation(operationId);
                if (current.BackupDigest is null)
                    throw new InvalidDataException("The journaled immutable backup digest is missing.");
                _ = journal.Advance(operationId, "rollbackIntent", new MaintenanceJournalEvidence(
                    BackupDigest: current.BackupDigest,
                    TargetInventoryDigest: current.TargetInventoryDigest,
                    RollbackReason: "targetStartFailed"));
                return await ContinueRollbackAsync(operationId, expectedTarget, failedTarget,
                    restoreStage, CancellationToken.None).ConfigureAwait(false);
            }
            authorization.VerifyHeld(operationId);
            _ = journal.Advance(operationId, "servicesStarted");
            return new("applied", binding.TargetVersion, null);
        }

        if (operation.Phase == "rollbackServicesStarted")
            return new("rolledBack", operation.SourceVersion!, operation.RollbackReason);
        if (operation.Phase == "servicesStarted")
            return new("applied", binding.TargetVersion, null);
        throw new InvalidOperationException("The maintenance journal phase cannot be applied or reconciled.");
    }

    private async Task<MaintenanceApplyResult> ContinueRollbackAsync(
        string operationId,
        PackageInventory expectedTarget,
        string failedTarget,
        string restoreStage,
        CancellationToken cancellationToken)
    {
        var operation = ReadOperation(operationId);
        authorization.VerifyHeld(operationId);
        // A failed start can leave some services running. Stop and verify them before
        // any rename, and repeat this check after a process restart during recovery.
        await services.EnsureStoppedAndVerifyQuiescedAsync(operationId, cancellationToken).ConfigureAwait(false);
        authorization.VerifyHeldAndQuiesced(operationId);
        var backup = backupOperation.VerifyPublishedBackup(operationId);
        VerifyBackupBinding(operation, backup);
        if (operation.Phase == "rollbackIntent")
        {
            var hasInstall = fileSystem.DirectoryExists(installDirectory);
            var hasFailed = fileSystem.DirectoryExists(failedTarget);
            if (hasInstall && !hasFailed)
            {
                fileSystem.EnsureOwnedInstallTree(installDirectory);
                fileSystem.MoveDirectory(installDirectory, failedTarget);
                fileSystem.FlushParent(installDirectory);
            }
            else if (hasInstall && hasFailed)
            {
                throw new InvalidDataException("Both target and failed-target trees exist during rollback reconciliation.");
            }
            else if (hasFailed)
            {
                // A failed target can be the reason rollback began. Recovery only
                // needs to prove it is the operation-owned protected quarantine.
                fileSystem.EnsureOwnedInstallTree(failedTarget);
            }
            _ = journal.Advance(operationId, "targetMovedToFailed");
            operation = ReadOperation(operationId);
        }

        if (operation.Phase == "targetMovedToFailed")
        {
            var hasInstall = fileSystem.DirectoryExists(installDirectory);
            if (hasInstall)
            {
                fileSystem.VerifyBackupTree(installDirectory, backup);
            }
            else
            {
                fileSystem.PrepareBackupInstallTree(backup.DirectoryPath, restoreStage, backup);
                fileSystem.VerifyBackupTree(restoreStage, backup);
                fileSystem.MoveDirectory(restoreStage, installDirectory);
                fileSystem.FlushParent(installDirectory);
                fileSystem.VerifyBackupTree(installDirectory, backup);
            }
            _ = journal.Advance(operationId, "sourceRestored",
                new MaintenanceJournalEvidence(RestoredBackupDigest: backup.BackupDigest));
            operation = ReadOperation(operationId);
        }

        if (operation.Phase == "sourceRestored")
        {
            fileSystem.VerifyBackupTree(installDirectory, backup);
            authorization.VerifyHeldAndQuiesced(operationId);
            await services.StartAndVerifyAsync(operationId, installDirectory, operation.SourceVersion!, cancellationToken).ConfigureAwait(false);
            authorization.VerifyHeld(operationId);
            _ = journal.Advance(operationId, "rollbackServicesStarted");
            return new("rolledBack", operation.SourceVersion!, operation.RollbackReason);
        }

        if (operation.Phase == "rollbackServicesStarted")
            return new("rolledBack", operation.SourceVersion!, operation.RollbackReason);
        throw new InvalidOperationException("The rollback journal phase cannot be reconciled.");
    }

    private MaintenanceJournalOperation ReadOperation(string operationId)
    {
        var snapshot = journal.Read()
            ?? throw new InvalidOperationException("The maintenance journal is missing; the update remains held.");
        if (snapshot.Operations.Count != 1 || snapshot.HeadOperationId != operationId ||
            snapshot.Operations[0].Binding.OperationId != operationId)
            throw new InvalidOperationException("The update is not the current maintenance journal head.");
        return snapshot.Operations[0];
    }

    private static string Sibling(string parent, string prefix, string operationId)
    {
        if (!Regex.IsMatch(operationId, "\\A[A-Za-z0-9._-]{1,128}\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("The maintenance operation ID is invalid.");
        return Path.Combine(parent, prefix + operationId);
    }

    private static void VerifyBackupBinding(MaintenanceJournalOperation operation, MaintenanceBackupResult backup)
    {
        if (operation.BackupOwnerOperationId != operation.Binding.OperationId ||
            backup.BackupOwnerOperationId != operation.Binding.OperationId ||
            !FixedHexEquals(operation.BackupDigest, backup.BackupDigest))
            throw new InvalidDataException("The immutable original backup does not match the journal binding.");
    }

    private static bool FixedHexEquals(string? first, string? second) =>
        first is { Length: 64 } && second is { Length: 64 } &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(first), Convert.FromHexString(second));
}
