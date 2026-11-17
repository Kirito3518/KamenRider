using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KamenRaider.AgentMaintenance;

/// <summary>
/// Shared durable-hold evidence checks used by the apply command and the lifecycle
/// source-binding command. Both commands must refuse to mutate services unless the validated
/// maintenance journal still names the same chain/head/generation/operation binding and the
/// durable Controller start acknowledgement phase is recorded.
/// </summary>
internal static class MaintenanceJournalHoldEvidence
{
    public static MaintenanceJournalOperation ReadAndVerify(
        IMaintenanceJournalStore journalStore,
        string chainId,
        string headOperationId,
        long generation,
        string operationId,
        string targetVersion,
        string targetRuntime,
        string targetSha256)
    {
        ArgumentNullException.ThrowIfNull(journalStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(chainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(headOperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);

        var snapshot = new MaintenanceJournal(journalStore, chainId).Read()
            ?? throw new InvalidOperationException("The maintenance journal is missing; the Controller start acknowledgement cannot be proven.");
        if (!string.Equals(snapshot.ChainId, chainId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.HeadOperationId, headOperationId, StringComparison.Ordinal) ||
            snapshot.Generation != generation ||
            !string.Equals(headOperationId, operationId, StringComparison.Ordinal))
            throw new InvalidOperationException("The maintenance journal head or generation differs from the request; the operation remains held.");

        var operation = MaintenanceJournal.RequireHead(snapshot, operationId);
        var binding = operation.Binding;
        if (!string.Equals(binding.OperationId, operationId, StringComparison.Ordinal) ||
            !string.Equals(binding.ChainId, chainId, StringComparison.Ordinal) ||
            !string.Equals(binding.HeadOperationId, headOperationId, StringComparison.Ordinal) ||
            binding.Generation != generation ||
            !string.Equals(binding.TargetVersion, targetVersion, StringComparison.Ordinal) ||
            !string.Equals(binding.TargetRuntime, targetRuntime, StringComparison.Ordinal) ||
            !string.Equals(binding.TargetSha256, targetSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The maintenance journal binding differs from the request; the operation remains held.");

        if (!operation.PhaseHistory.Contains("controllerStarted", StringComparer.Ordinal))
            throw new InvalidOperationException(
                "The durable Controller owner-start acknowledgement is not recorded; service mutation remains held.");
        return operation;
    }

    public static void RequireNoReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"The {description} must not be a reparse point.");
    }
}

/// <summary>
/// Shared local lease reconciliation. The Controller reservation must be clear and the Agent
/// must not have a live current-boot lease before the helper mutates services. A missing lease
/// file is treated as no lease; corrupt, unreadable or live state stays held. Another boot's
/// lease cannot have a live session on this boot and is treated as drained history.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MaintenanceLocalLeaseEvidence
{
    public const string LeaseFileName = "lease.json";
    private const int MaximumLeaseBytes = 16 * 1024;
    private const int ReadAttempts = 3;
    private const int ReadRetryDelayMilliseconds = 50;

    private static readonly JsonSerializerOptions LeaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 3
    };
    private static readonly Regex BootIdPattern =
        new(@"\A[0-9a-fA-F]{32}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> LeaseFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "RentalId",
        "Revision",
        "ExpiresAtUtc",
        "BootId",
        "LastCommandId",
        "DeadlineUptimeMilliseconds",
        "RecoveryDeadlineUptimeMilliseconds"
    };

    public static string LeasePath(string agentDataDirectory) =>
        Path.Combine(Path.GetFullPath(agentDataDirectory), LeaseFileName);

    public static void EnsureDrained(string agentDataDirectory, IMaintenanceLeaseEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentDataDirectory);
        ArgumentNullException.ThrowIfNull(environment);
        var bytes = ReadLeaseBytes(LeasePath(agentDataDirectory));
        if (bytes is null)
            return;

        var lease = ParseLease(bytes);
        var currentBootId = environment.ReadBootId();
        if (!string.Equals(lease.BootId, currentBootId, StringComparison.Ordinal))
            return;

        var uptime = environment.ReadUptimeMilliseconds();
        var deadline = lease.DeadlineUptimeMilliseconds;
        if (lease.RecoveryDeadlineUptimeMilliseconds is { } recovery)
            deadline = Math.Min(deadline, recovery);
        if (uptime < deadline)
            throw new InvalidOperationException(
                "A local lease is still live; the maintenance operation remains held until its deadline or Controller reconciliation.");
    }

    private static byte[]? ReadLeaseBytes(string path)
    {
        if (!File.Exists(path))
            return null;

        IOException? last = null;
        for (var attempt = 0; attempt < ReadAttempts; attempt++)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The Agent lease file must not be a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0)
                    throw new InvalidDataException("The Agent lease path must be a regular file.");
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length is <= 0 or > MaximumLeaseBytes)
                    throw new InvalidDataException("The Agent lease file has an invalid size.");
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                return bytes;
            }
            catch (IOException exception)
            {
                last = exception;
                if (attempt + 1 < ReadAttempts)
                    Thread.Sleep(ReadRetryDelayMilliseconds);
            }
        }

        throw new IOException("The Agent lease file could not be read; the operation remains held.", last);
    }

    internal sealed record LocalLeaseSnapshot(
        string RentalId,
        long Revision,
        DateTimeOffset ExpiresAtUtc,
        string BootId,
        string LastCommandId,
        long DeadlineUptimeMilliseconds,
        long? RecoveryDeadlineUptimeMilliseconds);

    private static LocalLeaseSnapshot ParseLease(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 3,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The Agent lease document must be a JSON object.");
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!LeaseFields.Contains(property.Name) || !found.Add(property.Name))
                    throw new InvalidDataException("The Agent lease document contains an unknown or duplicate field.");
            if (!found.Contains("RentalId") || !found.Contains("Revision") || !found.Contains("ExpiresAtUtc") ||
                !found.Contains("BootId") || !found.Contains("LastCommandId") || !found.Contains("DeadlineUptimeMilliseconds"))
                throw new InvalidDataException("The Agent lease document is incomplete.");

            var lease = JsonSerializer.Deserialize<LocalLeaseSnapshot>(bytes, LeaseJsonOptions)
                ?? throw new InvalidDataException("The Agent lease document is empty.");
            if (string.IsNullOrWhiteSpace(lease.RentalId) || lease.RentalId.Length > 128 ||
                lease.Revision <= 0 ||
                string.IsNullOrWhiteSpace(lease.LastCommandId) || lease.LastCommandId.Length > 128 ||
                !BootIdPattern.IsMatch(lease.BootId ?? string.Empty) ||
                lease.DeadlineUptimeMilliseconds <= 0 ||
                lease.RecoveryDeadlineUptimeMilliseconds is <= 0)
                throw new InvalidDataException("The Agent lease document contains invalid values.");
            return lease;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Agent lease document is malformed; the operation remains held.", exception);
        }
    }
}
