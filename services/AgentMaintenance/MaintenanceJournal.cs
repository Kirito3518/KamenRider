using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KamenRaider.Contracts;
using Microsoft.Win32.SafeHandles;

namespace KamenRaider.AgentMaintenance;

public sealed record MaintenanceOperationBinding(
    string OperationId,
    string ChainId,
    string HeadOperationId,
    long Generation,
    string ControllerOrigin,
    string EnrollmentKeyFingerprint,
    int HelperProtocol,
    string TargetRuntime,
    string TargetVersion,
    string TargetSha256);

public sealed record MaintenanceJournalEvidence(
    string? BackupOwnerOperationId = null,
    string? BackupDigest = null,
    string? SourceVersion = null,
    string? SourceInventoryDigest = null,
    string? TargetInventoryDigest = null,
    string? RollbackReason = null,
    string? RestoredBackupDigest = null);

public sealed record MaintenanceSourceChallengeAttempt(
    int AttemptNumber,
    string RequestId,
    string RequestBodyDigest);

public sealed record MaintenanceSourceBindingIntent(
    AgentUpdateSourceEvidence Evidence,
    int AttemptNumber,
    string ChallengeId,
    string Challenge,
    string RuntimeEpoch,
    DateTimeOffset ChallengeExpiresAtUtc,
    string ChallengeRequestBodyDigest,
    string EvidenceSignature,
    string SourceRequestId,
    string BindingBodyDigest);

public sealed record MaintenanceJournalOperation(
    MaintenanceOperationBinding Binding,
    string Phase,
    List<string> PhaseHistory,
    string? BackupOwnerOperationId = null,
    string? BackupDigest = null,
    string? SourceVersion = null,
    string? SourceInventoryDigest = null,
    MaintenanceSourceBindingIntent? SourceBindingIntent = null,
    MaintenanceSourceChallengeAttempt? PendingSourceChallengeAttempt = null,
    List<MaintenanceSourceChallengeAttempt>? RejectedSourceChallengeAttempts = null,
    List<MaintenanceSourceBindingIntent>? RejectedSourceBindingIntents = null,
    string? TargetInventoryDigest = null,
    string? RollbackReason = null,
    string? RestoredBackupDigest = null);

public sealed record MaintenanceJournalSnapshot(
    int SchemaVersion,
    string ChainId,
    string HeadOperationId,
    long Generation,
    List<MaintenanceJournalOperation> Operations);

public interface IMaintenanceJournalStore
{
    MaintenanceJournalSnapshot? Read(string chainId);
    void WriteAtomic(string chainId, MaintenanceJournalSnapshot snapshot);
}

public sealed class MaintenanceJournal(IMaintenanceJournalStore store, string chainId)
{
    private static readonly string[] NormalPhases =
    [
        "inspected",
        "controllerStarted",
        "targetValidated",
        "servicesQuiesced",
        "backupPublished",
        "sourceBound",
        "swapIntent",
        "sourceMoved",
        "targetPlaced",
        "servicesStarted",
        "outcomeReported",
        "controllerConfirmed"
    ];
    private static readonly string[] RollbackTail =
    [
        "rollbackIntent",
        "targetMovedToFailed",
        "sourceRestored",
        "rollbackServicesStarted"
    ];
    private const int MaximumSourceBindingAttempts = 32;

    public MaintenanceJournalSnapshot Start(MaintenanceOperationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(binding);
        ValidateChainId(chainId);
        ValidateBinding(binding);
        if (!string.Equals(binding.ChainId, chainId, StringComparison.Ordinal))
            throw new InvalidOperationException("The operation belongs to another maintenance chain.");

        var current = ReadValidated();
        if (current is not null)
        {
            var existing = current.Operations.SingleOrDefault(entry => entry.Binding.OperationId == binding.OperationId);
            if (existing is not null && existing.Binding == binding)
                return current;
            throw new InvalidOperationException("The maintenance chain already has a journaled operation or this operation binding changed.");
        }

        if (binding.HeadOperationId != binding.OperationId)
            throw new InvalidOperationException("A new maintenance journal must begin at its operation head.");
        var operation = new MaintenanceJournalOperation(binding, "inspected", ["inspected"]);
        var snapshot = new MaintenanceJournalSnapshot(1, chainId, binding.OperationId, binding.Generation, [operation]);
        ValidateSnapshot(snapshot);
        store.WriteAtomic(chainId, snapshot);
        return ReadPublished(snapshot);
    }

    /// <summary>
    /// Appends a successor operation to an existing held chain. The successor names itself as its
    /// own head (the chain head moves to it) and must carry a newer fencing generation. Existing
    /// history stays immutable; only the new head may advance afterwards.
    /// </summary>
    public MaintenanceJournalSnapshot StartSuccessor(MaintenanceOperationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ValidateChainId(chainId);
        ValidateBinding(binding);
        if (!string.Equals(binding.ChainId, chainId, StringComparison.Ordinal))
            throw new InvalidOperationException("The successor belongs to another maintenance chain.");
        var current = ReadValidated()
            ?? throw new InvalidOperationException("A successor requires an existing held maintenance chain journal.");
        var existing = current.Operations.FirstOrDefault(entry =>
            string.Equals(entry.Binding.OperationId, binding.OperationId, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (existing.Binding == binding &&
                string.Equals(current.HeadOperationId, binding.OperationId, StringComparison.Ordinal) &&
                existing.PhaseHistory.Count == 1 && existing.Phase == "inspected")
                return current;
            throw new InvalidOperationException("The journaled successor operation binding changed or is not the current head.");
        }
        var head = Head(current);
        if (binding.HeadOperationId != binding.OperationId)
            throw new InvalidOperationException("A successor operation must be its own chain head.");
        if (binding.Generation <= current.Generation || binding.Generation != checked(head.Binding.Generation + 1))
            throw new InvalidOperationException("A successor requires the next fencing generation.");
        if (head.Phase is not ("controllerConfirmed" or "outcomeReported" or "rollbackServicesStarted"))
            throw new InvalidOperationException("The current chain head is not in a recoverable terminal phase.");
        var operation = new MaintenanceJournalOperation(binding, "inspected", ["inspected"]);
        var snapshot = current with
        {
            HeadOperationId = binding.OperationId,
            Generation = binding.Generation,
            Operations = [.. current.Operations, operation]
        };
        ValidateSnapshot(snapshot);
        store.WriteAtomic(chainId, snapshot);
        return ReadPublished(snapshot);
    }

    /// <summary>
    /// Starts the first operation of a new chain, or starts the successor operation of an
    /// existing recoverable chain. A lifecycle start request for an audited recovery successor
    /// names the same chain with the next fencing generation, so the helper must append the
    /// successor instead of rejecting the already journaled chain.
    /// </summary>
    public MaintenanceJournalSnapshot StartOrSuccessor(MaintenanceOperationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ValidateChainId(chainId);
        ValidateBinding(binding);
        if (!string.Equals(binding.ChainId, chainId, StringComparison.Ordinal))
            throw new InvalidOperationException("The operation belongs to another maintenance chain.");
        var current = ReadValidated();
        if (current is null)
        {
            if (binding.Generation != 1)
                throw new InvalidOperationException(
                    "A held chain without a new-format journal requires an explicit legacy import and cannot start a successor directly.");
            return Start(binding);
        }
        var existing = current.Operations.FirstOrDefault(entry =>
            string.Equals(entry.Binding.OperationId, binding.OperationId, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (existing.Binding == binding) return current;
            throw new InvalidOperationException("The journaled operation binding changed.");
        }
        return StartSuccessor(binding);
    }

    /// <summary>
    /// Begins a legacy-imported chain entry. The old updater did not write a new-format journal,
    /// so the chain starts at the explicit legacyImported phase with the inspected old backup
    /// owner/digest only; earlier phases are never invented.
    /// </summary>
    public MaintenanceJournalSnapshot StartLegacyImported(
        MaintenanceOperationBinding binding,
        MaintenanceJournalEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateChainId(chainId);
        ValidateBinding(binding);
        if (!string.Equals(binding.ChainId, chainId, StringComparison.Ordinal))
            throw new InvalidOperationException("The legacy import belongs to another maintenance chain.");
        if (ReadValidated() is not null)
            throw new InvalidOperationException("Legacy import requires the absence of a new-format journal for this chain.");
        if (binding.HeadOperationId != binding.OperationId)
            throw new InvalidOperationException("A legacy-imported journal must begin at its operation head.");
        if (string.IsNullOrWhiteSpace(evidence.BackupOwnerOperationId) || evidence.BackupOwnerOperationId.Length > 128 ||
            !IsDigest(evidence.BackupDigest) || evidence.SourceVersion is not null ||
            evidence.SourceInventoryDigest is not null || evidence.TargetInventoryDigest is not null ||
            evidence.RollbackReason is not null || evidence.RestoredBackupDigest is not null)
            throw new InvalidOperationException("Legacy import requires only the inspected old backup owner and digest.");
        ValidateChainId(evidence.BackupOwnerOperationId);
        var operation = new MaintenanceJournalOperation(
            binding, "legacyImported", ["legacyImported"], evidence.BackupOwnerOperationId, evidence.BackupDigest);
        var snapshot = new MaintenanceJournalSnapshot(1, chainId, binding.OperationId, binding.Generation, [operation]);
        ValidateSnapshot(snapshot);
        store.WriteAtomic(chainId, snapshot);
        return ReadPublished(snapshot);
    }

    public MaintenanceJournalSnapshot Advance(
        string operationId,
        string nextPhase,
        MaintenanceJournalEvidence? evidence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nextPhase);
        var current = ReadValidated()
            ?? throw new InvalidOperationException("The maintenance journal is missing; the operation remains held.");
        var operation = RequireHead(current, operationId);
        var resolvedEvidence = evidence ?? new MaintenanceJournalEvidence();
        if (operation.Phase == nextPhase)
        {
            if (EvidenceMatches(operation, nextPhase, resolvedEvidence))
                return current;
            throw new InvalidOperationException("A retried journal phase has different evidence.");
        }
        if (!CanAdvance(operation.Phase, nextPhase))
            throw new InvalidOperationException("The maintenance journal phase transition is not allowed.");

        var updated = ApplyEvidence(operation, nextPhase, resolvedEvidence);
        var operations = new List<MaintenanceJournalOperation>(current.Operations);
        operations[^1] = updated;
        var snapshot = current with { Operations = operations };
        ValidateSnapshot(snapshot);
        store.WriteAtomic(chainId, snapshot);
        return ReadPublished(snapshot);
    }

    public MaintenanceJournalSnapshot? Read() => ReadValidated();

    /// <summary>The current head operation is always the last immutable chain entry.</summary>
    public static MaintenanceJournalOperation Head(MaintenanceJournalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Operations is null || snapshot.Operations.Count == 0)
            throw new InvalidDataException("The maintenance journal has no operations.");
        return snapshot.Operations[^1];
    }

    /// <summary>Returns the head operation only when it is the requested operation; otherwise throws.</summary>
    public static MaintenanceJournalOperation RequireHead(MaintenanceJournalSnapshot snapshot, string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var head = Head(snapshot);
        if (!string.Equals(snapshot.HeadOperationId, operationId, StringComparison.Ordinal) ||
            !string.Equals(head.Binding.OperationId, operationId, StringComparison.Ordinal))
            throw new InvalidOperationException("Only the current journal head can advance.");
        return head;
    }

    /// <summary>Returns the requested immutable chain entry or throws when it is not in this chain.</summary>
    public static MaintenanceJournalOperation RequireOperation(MaintenanceJournalSnapshot snapshot, string operationId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        return snapshot.Operations?.FirstOrDefault(entry =>
            string.Equals(entry.Binding.OperationId, operationId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The maintenance journal does not contain this operation.");
    }

    public MaintenanceJournalOperation VerifyApplyRequestBinding(MaintenanceCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Command != "apply" || request.ChainId != chainId ||
            request.ChainId is null || request.HeadOperationId is null || request.Generation is null)
            throw new InvalidOperationException("The apply request does not name this maintenance journal identity.");

        var current = ReadValidated()
            ?? throw new InvalidOperationException("The current maintenance journal is missing; the operation remains held.");
        if (current.ChainId != request.ChainId ||
            !string.Equals(current.HeadOperationId, request.OperationId, StringComparison.Ordinal) ||
            current.Generation != request.Generation)
            throw new InvalidOperationException("The apply request is not bound to the current maintenance journal head and generation.");

        var operation = RequireHead(current, request.OperationId);
        var binding = operation.Binding;
        if (binding.OperationId != request.OperationId || binding.ChainId != request.ChainId ||
            binding.HeadOperationId != request.HeadOperationId || binding.Generation != request.Generation ||
            binding.TargetVersion != request.TargetVersion || binding.TargetRuntime != request.TargetRuntime ||
            !FixedHexEquals(binding.TargetSha256, request.TargetPackageSha256))
            throw new InvalidOperationException("The apply request differs from the immutable journal operation binding.");

        return operation;
    }

    public MaintenanceJournalSnapshot RecordSourceBindingIntent(
        string operationId,
        MaintenanceSourceBindingIntent intent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(intent);
        var current = ReadValidated()
            ?? throw new InvalidOperationException("The maintenance journal is missing; the operation remains held.");
        var operation = RequireHead(current, operationId);
        if (operation.Phase != "backupPublished")
            throw new InvalidOperationException("A source binding intent requires the current operation's published backup phase.");
        if (operation.SourceBindingIntent is not null)
        {
            if (operation.SourceBindingIntent != intent)
                throw new InvalidOperationException("The source binding intent is immutable once recorded.");
            return current;
        }
        if (operation.PendingSourceChallengeAttempt is not { } challengeAttempt ||
            challengeAttempt.AttemptNumber != intent.AttemptNumber ||
            challengeAttempt.RequestId != intent.Evidence.ChallengeRequestId ||
            challengeAttempt.RequestBodyDigest != intent.ChallengeRequestBodyDigest)
            throw new InvalidOperationException("The source-binding intent does not match its durable challenge request attempt.");
        ValidateSourceBindingIntent(operation, intent);

        var updated = operation with { SourceBindingIntent = intent, PendingSourceChallengeAttempt = null };
        var operations = new List<MaintenanceJournalOperation>(current.Operations);
        operations[^1] = updated;
        var snapshot = current with { Operations = operations };
        ValidateSnapshot(snapshot);
        store.WriteAtomic(chainId, snapshot);
        return ReadPublished(snapshot);
    }

    public MaintenanceJournalSnapshot RecordSourceChallengeAttempt(
        string operationId,
        MaintenanceSourceChallengeAttempt attempt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(attempt);
        var current = ReadValidated()
            ?? throw new InvalidOperationException("The maintenance journal is missing; source challenge issuance remains held.");
        var operation = RequireHead(current, operationId);
        if (operation.Phase != "backupPublished")
            throw new InvalidOperationException("A source challenge attempt requires the current operation's published backup phase.");
        if (operation.SourceBindingIntent is not null)
            throw new InvalidOperationException("A new source challenge cannot replace an un-rejected binding intent.");
        if (operation.PendingSourceChallengeAttempt is { } pending)
        {
            if (pending == attempt) return current;
            throw new InvalidOperationException("The pending source challenge request is immutable until a typed renewal rejection.");
        }
        if (attempt.AttemptNumber != NextSourceBindingAttemptNumber(operation))
            throw new InvalidOperationException("The source challenge attempt number is not the next journaled attempt.");
        ValidateSourceChallengeAttempt(operation.Binding, attempt);
        var updated = operation with { PendingSourceChallengeAttempt = attempt };
        var operations = new List<MaintenanceJournalOperation>(current.Operations);
        operations[^1] = updated;
        var snapshot = current with { Operations = operations };
        ValidateSnapshot(snapshot);
        store.WriteAtomic(chainId, snapshot);
        return ReadPublished(snapshot);
    }

    public MaintenanceJournalSnapshot RejectPendingSourceChallengeAttempt(
        string operationId,
        MaintenanceSourceChallengeAttempt expectedAttempt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(expectedAttempt);
        var current = ReadValidated()
            ?? throw new InvalidOperationException("The maintenance journal is missing; source challenge recovery remains held.");
        var operation = RequireHead(current, operationId);
        if (operation.Phase != "backupPublished")
            throw new InvalidOperationException("A source challenge rejection requires the current operation's published backup phase.");
        if (operation.PendingSourceChallengeAttempt != expectedAttempt || operation.SourceBindingIntent is not null)
            throw new InvalidOperationException("The typed source challenge rejection does not match the pending journal attempt.");
        var rejected = operation.RejectedSourceChallengeAttempts ?? [];
        if (rejected.Count >= MaximumSourceBindingAttempts)
            throw new InvalidOperationException("The source challenge renewal limit has been reached; the operation remains held.");
        var updated = operation with
        {
            PendingSourceChallengeAttempt = null,
            RejectedSourceChallengeAttempts = [.. rejected, expectedAttempt]
        };
        var operations = new List<MaintenanceJournalOperation>(current.Operations);
        operations[^1] = updated;
        var snapshot = current with { Operations = operations };
        ValidateSnapshot(snapshot);
        store.WriteAtomic(chainId, snapshot);
        return ReadPublished(snapshot);
    }

    public MaintenanceJournalSnapshot RejectSourceBindingIntent(
        string operationId,
        int expectedAttemptNumber,
        string expectedSourceRequestId,
        string expectedBindingBodyDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var current = ReadValidated()
            ?? throw new InvalidOperationException("The maintenance journal is missing; source binding recovery remains held.");
        var operation = RequireHead(current, operationId);
        if (operation.Phase != "backupPublished")
            throw new InvalidOperationException("A source binding rejection requires the current operation's published backup phase.");
        var intent = operation.SourceBindingIntent
            ?? throw new InvalidOperationException("There is no pending source binding intent to reject.");
        if (intent.AttemptNumber != expectedAttemptNumber || intent.SourceRequestId != expectedSourceRequestId ||
            !FixedHexEquals(intent.BindingBodyDigest, expectedBindingBodyDigest))
            throw new InvalidOperationException("The typed challenge-renewal rejection does not match the current signed binding intent.");
        var rejected = operation.RejectedSourceBindingIntents ?? [];
        if (rejected.Count >= MaximumSourceBindingAttempts)
            throw new InvalidOperationException("The source binding renewal limit has been reached; the operation remains held.");
        var updated = operation with
        {
            SourceBindingIntent = null,
            RejectedSourceBindingIntents = [.. rejected, intent]
        };
        var operations = new List<MaintenanceJournalOperation>(current.Operations);
        operations[^1] = updated;
        var snapshot = current with { Operations = operations };
        ValidateSnapshot(snapshot);
        store.WriteAtomic(chainId, snapshot);
        return ReadPublished(snapshot);
    }

    private MaintenanceJournalOperation ApplyEvidence(
        MaintenanceJournalOperation current,
        string nextPhase,
        MaintenanceJournalEvidence evidence)
    {
        if (nextPhase == "backupPublished")
        {
            if (!string.Equals(evidence.BackupOwnerOperationId, current.Binding.OperationId, StringComparison.Ordinal) ||
                !IsDigest(evidence.BackupDigest) ||
                evidence.SourceVersion is not null || evidence.SourceInventoryDigest is not null)
                throw new InvalidOperationException("A verified backup owner and digest are required before backupPublished.");
            return current with
            {
                Phase = nextPhase,
                PhaseHistory = Append(current.PhaseHistory, nextPhase),
                BackupOwnerOperationId = evidence.BackupOwnerOperationId,
                BackupDigest = evidence.BackupDigest
            };
        }

        if (nextPhase == "sourceBound")
        {
            if (current.BackupOwnerOperationId is null || current.BackupDigest is null ||
                string.IsNullOrWhiteSpace(evidence.SourceVersion) ||
                !IsDigest(evidence.SourceInventoryDigest) ||
                current.SourceBindingIntent is null ||
                current.SourceBindingIntent.Evidence.SourceVersion != evidence.SourceVersion ||
                current.SourceBindingIntent.Evidence.InventoryDigest != evidence.SourceInventoryDigest ||
                evidence.BackupOwnerOperationId is not null || evidence.BackupDigest is not null ||
                string.Equals(evidence.SourceVersion, current.Binding.TargetVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A distinct source version, inventory digest, and published backup are required before sourceBound.");
            return current with
            {
                Phase = nextPhase,
                PhaseHistory = Append(current.PhaseHistory, nextPhase),
                SourceVersion = evidence.SourceVersion,
                SourceInventoryDigest = evidence.SourceInventoryDigest
            };
        }

        if (nextPhase == "swapIntent")
        {
            if (!IsDigest(evidence.TargetInventoryDigest) || evidence.BackupOwnerOperationId is not null ||
                evidence.BackupDigest is not null || evidence.SourceVersion is not null || evidence.SourceInventoryDigest is not null ||
                evidence.RollbackReason is not null || evidence.RestoredBackupDigest is not null)
                throw new InvalidOperationException("A verified target inventory digest is required before the installation swap.");
            return current with
            {
                Phase = nextPhase,
                PhaseHistory = Append(current.PhaseHistory, nextPhase),
                TargetInventoryDigest = evidence.TargetInventoryDigest
            };
        }

        if (nextPhase == "rollbackIntent")
        {
            if (!IsDigest(current.TargetInventoryDigest) || current.BackupDigest is null ||
                evidence.TargetInventoryDigest != current.TargetInventoryDigest ||
                evidence.BackupDigest != current.BackupDigest ||
                evidence.RollbackReason is not ("applyFailed" or "targetStartFailed" or "reconcileAmbiguous") ||
                evidence.BackupOwnerOperationId is not null || evidence.SourceVersion is not null ||
                evidence.SourceInventoryDigest is not null || evidence.RestoredBackupDigest is not null)
                throw new InvalidOperationException("Rollback intent must bind the verified target and immutable original backup.");
            return current with
            {
                Phase = nextPhase,
                PhaseHistory = Append(current.PhaseHistory, nextPhase),
                RollbackReason = evidence.RollbackReason
            };
        }

        if (nextPhase == "sourceRestored")
        {
            if (current.RollbackReason is null || current.BackupDigest is null ||
                evidence.RestoredBackupDigest != current.BackupDigest ||
                evidence.BackupOwnerOperationId is not null || evidence.BackupDigest is not null ||
                evidence.SourceVersion is not null || evidence.SourceInventoryDigest is not null ||
                evidence.TargetInventoryDigest is not null || evidence.RollbackReason is not null)
                throw new InvalidOperationException("The original immutable backup digest must be verified before sourceRestored.");
            return current with
            {
                Phase = nextPhase,
                PhaseHistory = Append(current.PhaseHistory, nextPhase),
                RestoredBackupDigest = evidence.RestoredBackupDigest
            };
        }

        if (evidence != new MaintenanceJournalEvidence())
            throw new InvalidOperationException("Evidence is accepted only for backup, source binding, target swap, and rollback phases.");
        return current with { Phase = nextPhase, PhaseHistory = Append(current.PhaseHistory, nextPhase) };
    }

    private static bool EvidenceMatches(
        MaintenanceJournalOperation operation,
        string phase,
        MaintenanceJournalEvidence evidence) => phase switch
    {
        "backupPublished" => operation.BackupOwnerOperationId == evidence.BackupOwnerOperationId &&
            operation.BackupDigest == evidence.BackupDigest && evidence.SourceVersion is null && evidence.SourceInventoryDigest is null,
        "sourceBound" => operation.SourceVersion == evidence.SourceVersion &&
            operation.SourceInventoryDigest == evidence.SourceInventoryDigest && operation.SourceBindingIntent is not null &&
            operation.SourceBindingIntent.Evidence.SourceVersion == evidence.SourceVersion &&
            operation.SourceBindingIntent.Evidence.InventoryDigest == evidence.SourceInventoryDigest &&
            evidence.BackupOwnerOperationId is null && evidence.BackupDigest is null,
        "swapIntent" => operation.TargetInventoryDigest == evidence.TargetInventoryDigest &&
            evidence.BackupOwnerOperationId is null && evidence.BackupDigest is null && evidence.SourceVersion is null &&
            evidence.SourceInventoryDigest is null && evidence.RollbackReason is null && evidence.RestoredBackupDigest is null,
        "rollbackIntent" => operation.TargetInventoryDigest == evidence.TargetInventoryDigest &&
            operation.BackupDigest == evidence.BackupDigest && operation.RollbackReason == evidence.RollbackReason &&
            evidence.BackupOwnerOperationId is null && evidence.SourceVersion is null && evidence.SourceInventoryDigest is null &&
            evidence.RestoredBackupDigest is null,
        "sourceRestored" => operation.RestoredBackupDigest == evidence.RestoredBackupDigest &&
            evidence.BackupOwnerOperationId is null && evidence.BackupDigest is null && evidence.SourceVersion is null &&
            evidence.SourceInventoryDigest is null && evidence.TargetInventoryDigest is null && evidence.RollbackReason is null,
        _ => evidence == new MaintenanceJournalEvidence()
    };

    private static bool CanAdvance(string current, string next)
    {
        if (current == "legacyImported")
        {
            var legacyNext = Array.IndexOf(NormalPhases, next);
            return legacyNext >= 1;
        }
        var currentIndex = Array.IndexOf(NormalPhases, current);
        var nextIndex = Array.IndexOf(NormalPhases, next);
        if (currentIndex >= 0 && nextIndex == currentIndex + 1)
            return true;
        if (next == "rollbackIntent" && current is "swapIntent" or "sourceMoved" or "targetPlaced")
            return true;
        var rollbackIndex = Array.IndexOf(RollbackTail, current);
        var rollbackNextIndex = Array.IndexOf(RollbackTail, next);
        if (rollbackIndex >= 0 && rollbackNextIndex == rollbackIndex + 1)
            return true;
        if (current == "rollbackServicesStarted" && next == "outcomeReported")
            return true;
        return false;
    }

    private MaintenanceJournalSnapshot? ReadValidated()
    {
        var snapshot = store.Read(chainId);
        if (snapshot is not null)
            ValidateSnapshot(snapshot);
        return snapshot;
    }

    private MaintenanceJournalSnapshot ReadPublished(MaintenanceJournalSnapshot expected)
    {
        var published = ReadValidated();
        if (!SameSnapshot(published, expected))
            throw new IOException("The atomic maintenance journal update could not be verified after publication.");
        return published!;
    }

    private static bool SameSnapshot(MaintenanceJournalSnapshot? actual, MaintenanceJournalSnapshot expected)
    {
        if (actual is null || actual.SchemaVersion != expected.SchemaVersion || actual.ChainId != expected.ChainId ||
            actual.HeadOperationId != expected.HeadOperationId || actual.Generation != expected.Generation ||
            actual.Operations.Count != expected.Operations.Count)
            return false;
        for (var index = 0; index < expected.Operations.Count; index++)
        {
            var first = actual.Operations[index];
            var second = expected.Operations[index];
            if (first.Binding != second.Binding || first.Phase != second.Phase ||
                first.BackupOwnerOperationId != second.BackupOwnerOperationId || first.BackupDigest != second.BackupDigest ||
                first.SourceVersion != second.SourceVersion || first.SourceInventoryDigest != second.SourceInventoryDigest ||
                first.SourceBindingIntent != second.SourceBindingIntent ||
                first.PendingSourceChallengeAttempt != second.PendingSourceChallengeAttempt ||
                first.TargetInventoryDigest != second.TargetInventoryDigest ||
                first.RollbackReason != second.RollbackReason ||
                first.RestoredBackupDigest != second.RestoredBackupDigest ||
                !(first.RejectedSourceChallengeAttempts ?? []).SequenceEqual(second.RejectedSourceChallengeAttempts ?? []) ||
                !(first.RejectedSourceBindingIntents ?? []).SequenceEqual(second.RejectedSourceBindingIntents ?? []) ||
                !first.PhaseHistory.SequenceEqual(second.PhaseHistory, StringComparer.Ordinal))
                return false;
        }
        return true;
    }

    private void ValidateSnapshot(MaintenanceJournalSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != 1 || snapshot.ChainId != chainId || snapshot.Operations is null ||
            snapshot.Operations.Count == 0 || snapshot.Generation <= 0)
            throw new InvalidDataException("The maintenance journal header is invalid or unsupported.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        long previousGeneration = 0;
        for (var index = 0; index < snapshot.Operations.Count; index++)
        {
            var operation = snapshot.Operations[index];
            if (operation is null)
                throw new InvalidDataException("The maintenance journal operation entry is missing.");
            ValidateBinding(operation.Binding);
            if (operation.Binding.ChainId != chainId ||
                !string.Equals(operation.Binding.OperationId, operation.Binding.HeadOperationId, StringComparison.Ordinal) ||
                !seen.Add(operation.Binding.OperationId) ||
                operation.Binding.Generation <= previousGeneration ||
                operation.PhaseHistory is null || operation.PhaseHistory.Count == 0 ||
                operation.PhaseHistory[^1] != operation.Phase ||
                operation.PhaseHistory.Distinct(StringComparer.Ordinal).Count() != operation.PhaseHistory.Count)
                throw new InvalidDataException("The maintenance journal operation head is inconsistent.");
            previousGeneration = operation.Binding.Generation;

            var startedLegacy = index == 0 && operation.PhaseHistory[0] == "legacyImported";
            if ((!startedLegacy && operation.PhaseHistory[0] != "inspected") ||
                operation.PhaseHistory.Where((phase, phaseIndex) => phaseIndex != 0 &&
                    !CanAdvance(operation.PhaseHistory[phaseIndex - 1], phase)).Any())
                throw new InvalidDataException("The maintenance journal phase history is not a valid transition sequence.");

            ValidateOperationFacts(operation, startedLegacy);
        }

        var head = snapshot.Operations[^1];
        if (!string.Equals(snapshot.HeadOperationId, head.Binding.OperationId, StringComparison.Ordinal) ||
            snapshot.Generation != head.Binding.Generation)
            throw new InvalidDataException("The maintenance journal head or generation is inconsistent.");
    }

    private static void ValidateOperationFacts(MaintenanceJournalOperation operation, bool startedLegacy)
    {
        var hasBackupPhase = operation.PhaseHistory.Contains("backupPublished", StringComparer.Ordinal);
        var hasSourcePhase = operation.PhaseHistory.Contains("sourceBound", StringComparer.Ordinal);
        var hasSwapPhase = operation.PhaseHistory.Contains("swapIntent", StringComparer.Ordinal);
        var hasRollbackPhase = operation.PhaseHistory.Contains("rollbackIntent", StringComparer.Ordinal);
        var hasRestorePhase = operation.PhaseHistory.Contains("sourceRestored", StringComparer.Ordinal);

        if (startedLegacy && !hasBackupPhase)
        {
            // The legacy import carries the inspected old backup owner/digest before any new
            // backup publication. Earlier phases are never invented.
            if (string.IsNullOrWhiteSpace(operation.BackupOwnerOperationId) || !IsDigest(operation.BackupDigest))
                throw new InvalidDataException("The legacy-imported journal is missing the inspected old backup owner or digest.");
        }
        else if (hasBackupPhase)
        {
            if (operation.BackupOwnerOperationId != operation.Binding.OperationId || !IsDigest(operation.BackupDigest))
                throw new InvalidDataException("The maintenance journal backup binding is missing or invalid.");
        }
        else if (operation.BackupOwnerOperationId is not null || operation.BackupDigest is not null)
        {
            throw new InvalidDataException("The maintenance journal contains backup facts before backupPublished.");
        }

        if (hasSourcePhase)
        {
            if (string.IsNullOrWhiteSpace(operation.SourceVersion) || !IsDigest(operation.SourceInventoryDigest) ||
                string.Equals(operation.SourceVersion, operation.Binding.TargetVersion, StringComparison.OrdinalIgnoreCase) ||
                operation.SourceBindingIntent is null ||
                operation.SourceBindingIntent.Evidence.SourceVersion != operation.SourceVersion ||
                operation.SourceBindingIntent.Evidence.InventoryDigest != operation.SourceInventoryDigest)
                throw new InvalidDataException("The maintenance journal source binding is missing or invalid.");
        }
        else if (operation.SourceVersion is not null || operation.SourceInventoryDigest is not null)
        {
            throw new InvalidDataException("The maintenance journal contains source facts before sourceBound.");
        }

        if (operation.SourceBindingIntent is not null)
        {
            if (!hasBackupPhase)
                throw new InvalidDataException("The maintenance journal contains source-binding intent before backup publication.");
            ValidateSourceBindingIntent(operation, operation.SourceBindingIntent);
        }
        if (hasSwapPhase)
        {
            if (!IsDigest(operation.TargetInventoryDigest))
                throw new InvalidDataException("The maintenance journal swap intent has no verified target inventory digest.");
        }
        else if (operation.TargetInventoryDigest is not null)
        {
            throw new InvalidDataException("The maintenance journal contains target inventory facts before swap intent.");
        }

        if (hasRollbackPhase)
        {
            if (!hasSwapPhase || operation.RollbackReason is not ("applyFailed" or "targetStartFailed" or "reconcileAmbiguous"))
                throw new InvalidDataException("The maintenance journal rollback intent is incomplete.");
        }
        else if (operation.RollbackReason is not null)
        {
            throw new InvalidDataException("The maintenance journal contains rollback facts before rollback intent.");
        }

        if (hasRestorePhase)
        {
            if (!hasRollbackPhase || !FixedHexEquals(operation.RestoredBackupDigest, operation.BackupDigest))
                throw new InvalidDataException("The maintenance journal restored source does not match its immutable backup.");
        }
        else if (operation.RestoredBackupDigest is not null)
        {
            throw new InvalidDataException("The maintenance journal contains restore facts before sourceRestored.");
        }

        ValidateSourceBindingAttempts(operation, hasBackupPhase);
    }
    private static void ValidateSourceBindingAttempts(MaintenanceJournalOperation operation, bool hasBackupPhase)
    {
        var rejectedChallenges = operation.RejectedSourceChallengeAttempts ?? [];
        var rejectedBindings = operation.RejectedSourceBindingIntents ?? [];
        if (!hasBackupPhase)
        {
            if (operation.SourceBindingIntent is not null || operation.PendingSourceChallengeAttempt is not null ||
                rejectedChallenges.Count != 0 || rejectedBindings.Count != 0)
                throw new InvalidDataException("The maintenance journal contains source-binding attempts before backup publication.");
            return;
        }

        if (rejectedChallenges.Count > MaximumSourceBindingAttempts || rejectedBindings.Count > MaximumSourceBindingAttempts ||
            rejectedChallenges.Any(attempt => attempt is null) || rejectedBindings.Any(intent => intent is null) ||
            operation.SourceBindingIntent is not null && operation.PendingSourceChallengeAttempt is not null)
            throw new InvalidDataException("The maintenance journal source-binding attempt history is invalid or exceeds its limit.");

        foreach (var attempt in rejectedChallenges)
            ValidateSourceChallengeAttempt(operation.Binding, attempt);
        foreach (var intent in rejectedBindings)
            ValidateSourceBindingIntent(operation, intent);
        if (operation.PendingSourceChallengeAttempt is { } pending)
            ValidateSourceChallengeAttempt(operation.Binding, pending);

        var attemptNumbers = rejectedChallenges.Select(attempt => attempt.AttemptNumber)
            .Concat(rejectedBindings.Select(intent => intent.AttemptNumber))
            .Concat(operation.PendingSourceChallengeAttempt is { } pendingAttempt ? [pendingAttempt.AttemptNumber] : [])
            .Concat(operation.SourceBindingIntent is { } intentAttempt ? [intentAttempt.AttemptNumber] : [])
            .Order()
            .ToArray();
        if (!attemptNumbers.SequenceEqual(Enumerable.Range(0, attemptNumbers.Length)) ||
            operation.PendingSourceChallengeAttempt is { } currentPending &&
            currentPending.AttemptNumber != NextSourceBindingAttemptNumber(operation) ||
            operation.SourceBindingIntent is { } currentIntent &&
            currentIntent.AttemptNumber != NextSourceBindingAttemptNumber(operation))
            throw new InvalidDataException("The maintenance journal source-binding attempts are not a contiguous immutable sequence.");
    }

    private static int NextSourceBindingAttemptNumber(MaintenanceJournalOperation operation) =>
        (operation.RejectedSourceChallengeAttempts?.Count ?? 0) + (operation.RejectedSourceBindingIntents?.Count ?? 0);

    private static void ValidateSourceChallengeAttempt(
        MaintenanceOperationBinding binding,
        MaintenanceSourceChallengeAttempt attempt)
    {
        if (attempt.AttemptNumber is < 0 or >= MaximumSourceBindingAttempts ||
            string.IsNullOrWhiteSpace(attempt.RequestId) || attempt.RequestId.Length > 128 ||
            !Regex.IsMatch(attempt.RequestId, "\\A[A-Za-z0-9._-]+\\z", RegexOptions.CultureInvariant) ||
            attempt.RequestId != StableSourceRequestId("source-challenge", binding.OperationId, attempt.AttemptNumber) ||
            !IsDigest(attempt.RequestBodyDigest))
            throw new InvalidDataException("The maintenance source challenge attempt is invalid.");
    }

    private static void ValidateSourceBindingIntent(
        MaintenanceJournalOperation operation,
        MaintenanceSourceBindingIntent intent)
    {
        var binding = operation.Binding;
        var evidence = intent.Evidence ?? throw new InvalidDataException("The maintenance source evidence is missing.");
        var invalidFields = new List<string>();
        void Require(bool valid, string field)
        {
            if (!valid) invalidFields.Add(field);
        }
        Require(operation.BackupOwnerOperationId == binding.OperationId && IsDigest(operation.BackupDigest), "backup binding");
        Require(evidence.OperationId == binding.OperationId && evidence.ChainId == binding.ChainId &&
            evidence.HeadOperationId == binding.HeadOperationId && evidence.Generation == binding.Generation, "operation head");
        Require(FixedHexEquals(evidence.KeyFingerprint, binding.EnrollmentKeyFingerprint), "enrollment fingerprint");
        Require(evidence.TargetVersion == binding.TargetVersion &&
            FixedHexEquals(evidence.TargetPackageSha256, binding.TargetSha256), "target package");
        Require(evidence.HelperProtocol == binding.HelperProtocol && evidence.HelperRuntime == binding.TargetRuntime &&
            IsDigest(evidence.HelperBuildSha256), "helper identity");
        Require(evidence.BackupOwnerOperationId == operation.BackupOwnerOperationId &&
            FixedHexEquals(evidence.BackupDigest, operation.BackupDigest) && IsDigest(evidence.BackupDigest), "backup evidence");
        Require(IsDigest(evidence.InventoryDigest) && IsDigest(evidence.InstalledTreeDigest) &&
            FixedHexEquals(evidence.InventoryDigest, evidence.InstalledTreeDigest), "source inventory");
        Require(!string.Equals(evidence.SourceVersion, binding.TargetVersion, StringComparison.OrdinalIgnoreCase), "source version");
        Require(evidence.ChallengeId == intent.ChallengeId && Guid.TryParseExact(intent.ChallengeId, "N", out _) &&
            !string.IsNullOrWhiteSpace(intent.Challenge) && intent.Challenge.Length <= 4096 &&
            !intent.Challenge.Any(char.IsControl), "challenge");
        Require(evidence.ChallengeRequestId is { Length: > 0 and <= 128 } &&
            evidence.SourceRequestId == intent.SourceRequestId && intent.SourceRequestId is { Length: > 0 and <= 128 } &&
            Regex.IsMatch(intent.SourceRequestId, "\\A[A-Za-z0-9._-]+\\z", RegexOptions.CultureInvariant) &&
            evidence.ChallengeRequestId == StableSourceRequestId("source-challenge", binding.OperationId, intent.AttemptNumber) &&
            intent.SourceRequestId == StableSourceRequestId("source-binding", binding.OperationId, intent.AttemptNumber), "request identifiers");
        Require(!string.IsNullOrWhiteSpace(intent.RuntimeEpoch) && intent.RuntimeEpoch.Length <= 128 &&
            !intent.RuntimeEpoch.Any(char.IsControl) && intent.ChallengeExpiresAtUtc != default, "challenge runtime");
        Require(intent.AttemptNumber is >= 0 and < MaximumSourceBindingAttempts &&
            IsDigest(intent.ChallengeRequestBodyDigest), "challenge request binding");
        Require(!string.IsNullOrWhiteSpace(evidence.NativeBootId) && evidence.NativeBootId.Length <= 256 &&
            !evidence.NativeBootId.Any(char.IsControl) && evidence.JournalPhase == "backupPublished", "native boot and phase");
        Require(intent.EvidenceSignature is { Length: > 0 and <= 1024 } && IsDigest(intent.BindingBodyDigest), "signature and request digest");
        if (invalidFields.Count != 0)
            throw new InvalidDataException("The maintenance source-binding intent is invalid: " + string.Join(", ", invalidFields) + ".");

        try
        {
            _ = Convert.FromBase64String(intent.EvidenceSignature);
            _ = AgentUpdateSourceEvidenceCanonicalizer.CanonicalBytes(evidence);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new InvalidDataException("The maintenance source-binding signature or evidence is malformed.", exception);
        }
    }

    private static bool FixedHexEquals(string? first, string? second) =>
        first is not null && second is not null && IsHexDigest(first) && IsHexDigest(second) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(first.ToUpperInvariant()), Encoding.ASCII.GetBytes(second.ToUpperInvariant()));

    private static void ValidateBinding(MaintenanceOperationBinding binding)
    {
        ValidateChainId(binding.ChainId);
        ValidateChainId(binding.OperationId);
        ValidateChainId(binding.HeadOperationId);
        if (binding.Generation <= 0 || binding.HelperProtocol <= 0 ||
            !Uri.TryCreate(binding.ControllerOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.AbsolutePath != "/" ||
            !string.Equals(origin.GetLeftPart(UriPartial.Authority) + "/", binding.ControllerOrigin, StringComparison.Ordinal) ||
            !IsDigest(binding.EnrollmentKeyFingerprint) ||
            binding.TargetRuntime is not ("win-x64" or "win-arm64") ||
            string.IsNullOrWhiteSpace(binding.TargetVersion) || binding.TargetVersion.Length > 128 ||
            !IsDigest(binding.TargetSha256))
            throw new InvalidDataException("The maintenance operation binding is invalid.");
    }

    private static void ValidateChainId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            !Regex.IsMatch(value, "\\A[A-Za-z0-9._-]+\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("The maintenance chain or operation ID is invalid.");
    }

    private static bool IsDigest(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsHexDigest(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static string StableSourceRequestId(string purpose, string operationId, int attemptNumber)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            "KamenRaider.AgentMaintenance." + purpose + ".v1\n" + operationId + "\n" +
            attemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return purpose + "-" + Convert.ToHexStringLower(digest);
    }

    private static List<string> Append(List<string> values, string value) => [.. values, value];
}

[SupportedOSPlatform("windows")]
public sealed class WindowsMaintenanceJournalStore : IMaintenanceJournalStore
{
    private const int MaximumJournalBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 12
    };
    private readonly string directory;

    public WindowsMaintenanceJournalStore()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The maintenance journal store requires Windows.");
        var data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(data) || !Path.IsPathFullyQualified(data))
            throw new InvalidOperationException("The native CommonApplicationData path is unavailable.");
        directory = Path.Combine(Path.GetFullPath(data), "KamenRaider.AgentMaintenance", "journals");
    }

    public MaintenanceJournalSnapshot? Read(string chainId)
    {
        ValidateChainId(chainId);
        EnsureParentReady(create: false);
        if (!Directory.Exists(directory))
            return null;
        EnsureDirectorySecurity(directory);
        var path = JournalPath(chainId);
        if (!File.Exists(path))
            return null;
        EnsureFileSecurity(path);
        var bytes = ReadSingleLinkFile(path);
        if (bytes.Length is <= 0 or > MaximumJournalBytes)
            throw new InvalidDataException("The maintenance journal has an invalid size.");
        try
        {
            return JsonSerializer.Deserialize<MaintenanceJournalSnapshot>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The maintenance journal is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The maintenance journal is malformed.", ex);
        }
    }

    public void WriteAtomic(string chainId, MaintenanceJournalSnapshot snapshot)
    {
        ValidateChainId(chainId);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ChainId != chainId)
            throw new InvalidDataException("The journal path does not match its chain ID.");
        EnsureParentReady(create: true);
        EnsurePrivateDirectory();
        var path = JournalPath(chainId);
        if (File.Exists(path))
            EnsureFileSecurity(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        if (bytes.Length is <= 0 or > MaximumJournalBytes)
            throw new InvalidDataException("The maintenance journal exceeds its size limit.");

        var temporary = Path.Combine(directory, ".journal-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                SetFileSecurity(temporary);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            EnsureFileSecurity(temporary);
            if (File.Exists(path))
                File.Replace(temporary, path, null, ignoreMetadataErrors: false);
            else
                File.Move(temporary, path);
            EnsureFileSecurity(path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private string JournalPath(string chainId) => Path.Combine(directory, chainId + ".json");

    private void EnsureParentReady(bool create)
    {
        foreach (var ancestor in Ancestors(directory))
            if ((Directory.Exists(ancestor) || File.Exists(ancestor)) &&
                (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("The maintenance journal path contains a reparse point.");

        var parent = Path.GetDirectoryName(directory)!;
        if (create)
        {
            new WindowsMaintenanceFenceStore().EnsureLocationForJournal();
            EnsurePrivateDirectory();
        }
        else if (!Directory.Exists(parent))
            return;

        if (Directory.Exists(parent))
            WindowsMaintenanceFenceStore.ValidateLocationForJournal();
    }

    private void EnsureDirectorySecurity(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new UnauthorizedAccessException("The maintenance journal path is not a regular directory.");
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!IsPrivateSecurity(security, isDirectory: true))
            throw new UnauthorizedAccessException("The maintenance journal directory ACL or owner is unsafe.");
    }

    private void EnsureFileSecurity(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new UnauthorizedAccessException("The maintenance journal is not a regular file.");
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!IsPrivateSecurity(security, isDirectory: false))
            throw new UnauthorizedAccessException("The maintenance journal ACL or owner is unsafe.");
        using var handle = OpenReadHandle(path);
        if (!GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks != 1)
            throw new UnauthorizedAccessException("The maintenance journal must have exactly one hard link.");
    }

    private static bool IsPrivateSecurity(FileSystemSecurity security, bool isDirectory)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (!security.AreAccessRulesProtected || owner is null || (owner != system && owner != admins))
            return false;
        var inheritance = isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (rules.Length != 2)
            return false;
        var identities = new HashSet<SecurityIdentifier>();
        foreach (var rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IsInherited ||
                rule.PropagationFlags != PropagationFlags.None || rule.InheritanceFlags != inheritance ||
                rule.IdentityReference is not SecurityIdentifier sid || !identities.Add(sid) ||
                rule.FileSystemRights != FileSystemRights.FullControl)
                return false;
        }
        return identities.SetEquals([system, admins]);
    }

    private static void SetFileSecurity(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(AdministratorsSid());
        security.AddAccessRule(new FileSystemAccessRule(SystemSid(), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid(), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static DirectorySecurity CreateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(AdministratorsSid());
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(SystemSid(), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid(), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private void EnsurePrivateDirectory()
    {
        if (!Directory.Exists(directory))
        {
            try { new DirectoryInfo(directory).Create(CreateDirectorySecurity()); }
            catch (IOException) when (Directory.Exists(directory)) { }
        }
        EnsureDirectorySecurity(directory);
    }

    private static void ValidateChainId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            !Regex.IsMatch(value, "\\A[A-Za-z0-9._-]+\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("The maintenance chain ID is invalid.");
    }

    private static byte[] ReadSingleLinkFile(string path)
    {
        using var handle = OpenReadHandle(path);
        if (!GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks != 1 ||
            (info.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) != 0)
            throw new UnauthorizedAccessException("The maintenance journal must be a regular single-link file.");
        using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        if (stream.Length is <= 0 or > MaximumJournalBytes)
            return [];
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static SafeFileHandle OpenReadHandle(string path)
    {
        const uint genericRead = 0x80000000;
        const uint shareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
        const uint openExisting = 3;
        const uint openReparsePoint = 0x00200000;
        var handle = CreateFile(path, genericRead, shareReadWriteDelete, IntPtr.Zero, openExisting, openReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("The maintenance journal could not be opened safely.", new System.ComponentModel.Win32Exception(error));
        }
        return handle;
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            yield return current;
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static SecurityIdentifier SystemSid() => new(WellKnownSidType.LocalSystemSid, null);
    private static SecurityIdentifier AdministratorsSid() => new(WellKnownSidType.BuiltinAdministratorsSid, null);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsFileInformation fileInformation);
}
