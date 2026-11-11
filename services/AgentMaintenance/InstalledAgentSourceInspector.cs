using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KamenRaider.AgentMaintenance;

public sealed record InstalledAgentSourceSnapshot(
    string Version,
    string? AssemblyInformationalVersion,
    int? AgentUpdateChallengeProtocol,
    string DepsSha256,
    string AgentAssemblySha256);

public sealed class InstalledAgentSourceInspector
{
    private const int MaximumDepsBytes = 16 * 1024 * 1024;
    private const int MaximumAssemblyBytes = 128 * 1024 * 1024;
    private static readonly Regex VersionPattern = new(
        "\\A(?:[0-9]+\\.[0-9]+\\.[0-9]+|1\\.0\\.0\\+g[0-9a-f]{12,40})\\z",
        RegexOptions.CultureInvariant);

    private readonly string sourceRoot;
    private readonly IMaintenanceBackupSecurity security;

    public InstalledAgentSourceInspector(string sourceRoot, IMaintenanceBackupSecurity security)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        this.sourceRoot = Path.GetFullPath(sourceRoot);
        this.security = security ?? throw new ArgumentNullException(nameof(security));
    }

    public string SourceRoot => sourceRoot;

    public InstalledAgentSourceSnapshot Inspect()
    {
        security.EnsureSourceRoot(sourceRoot);
        var depsBytes = ReadSourceFile("Agent.deps.json", MaximumDepsBytes);
        var assemblyBytes = ReadSourceFile("Agent.dll", MaximumAssemblyBytes);
        var deps = ReadDeps(depsBytes);
        var assembly = ReadAssembly(assemblyBytes);

        if (assembly.InformationalVersion is not null &&
            !string.Equals(assembly.InformationalVersion, deps.Version, StringComparison.Ordinal))
            throw new InvalidDataException("The installed Agent DLL informational version does not match its unique deps project version.");

        return new(
            deps.Version,
            assembly.InformationalVersion,
            assembly.ChallengeProtocol,
            Hash(depsBytes),
            Hash(assemblyBytes));
    }

    private byte[] ReadSourceFile(string relativeName, int maximumBytes)
    {
        var path = Path.Combine(sourceRoot, relativeName);
        using var stream = security.OpenSourceFile(path);
        if (!stream.CanRead || !stream.CanSeek || stream.Length is <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException($"The installed Agent {relativeName} file is missing, unreadable, or oversized.");
        var bytes = new byte[(int)stream.Length];
        stream.Position = 0;
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException($"The installed Agent {relativeName} changed while it was read.");
        return bytes;
    }

    private static DepsFacts ReadDeps(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 32,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            ValidateNoDuplicateProperties(document.RootElement);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("runtimeTarget", out var runtimeTarget) || runtimeTarget.ValueKind != JsonValueKind.Object ||
                !runtimeTarget.TryGetProperty("name", out var runtimeTargetName) || runtimeTargetName.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(runtimeTargetName.GetString()) ||
                !root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The installed Agent deps manifest is incomplete.");

            string? libraryKey = null;
            string? version = null;
            foreach (var library in libraries.EnumerateObject())
            {
                var separator = library.Name.IndexOf('/');
                var projectName = separator < 0 ? library.Name : library.Name[..separator];
                if (!string.Equals(projectName, "Agent", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (libraryKey is not null || separator <= 0 || separator == library.Name.Length - 1 ||
                    library.Name[..separator] != "Agent" || library.Value.ValueKind != JsonValueKind.Object ||
                    !library.Value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "project")
                    throw new InvalidDataException("The installed Agent deps manifest does not identify exactly one Agent project.");
                libraryKey = library.Name;
                version = library.Name[(separator + 1)..];
            }

            if (libraryKey is null || version is null || version.Length > 64 || !VersionPattern.IsMatch(version))
                throw new InvalidDataException("The installed Agent deps project version is unsupported.");

            var activeTargetName = runtimeTargetName.GetString()!;
            var activeTargets = targets.EnumerateObject()
                .Where(target => string.Equals(target.Name, activeTargetName, StringComparison.Ordinal))
                .ToArray();
            if (activeTargets.Length != 1 || activeTargets[0].Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The installed Agent deps manifest has no unique active runtime target.");

            var targetLibraries = activeTargets[0].Value.EnumerateObject()
                .Where(target => string.Equals(target.Name.Split('/')[0], "Agent", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (targetLibraries.Length != 1 || targetLibraries[0].Name != libraryKey ||
                targetLibraries[0].Value.ValueKind != JsonValueKind.Object ||
                !targetLibraries[0].Value.TryGetProperty("runtime", out var runtime) || runtime.ValueKind != JsonValueKind.Object ||
                !runtime.TryGetProperty("Agent.dll", out var agentDll) || agentDll.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The active Agent deps target does not resolve the unique Agent project to Agent.dll.");

            return new(version);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or DecoderFallbackException)
        {
            throw new InvalidDataException("The installed Agent deps manifest is malformed.", ex);
        }
    }

    private static AssemblyFacts ReadAssembly(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata)
                throw new InvalidDataException("The installed Agent DLL has no managed assembly metadata.");
            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly || reader.GetString(reader.GetAssemblyDefinition().Name) != "Agent")
                throw new InvalidDataException("The installed Agent DLL has an unexpected assembly identity.");

            string? informationalVersion = null;
            int? challengeProtocol = null;
            foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(handle);
                var isVersion = IsAttribute(reader, attribute.Constructor, "AssemblyInformationalVersionAttribute");
                var isMetadata = IsAttribute(reader, attribute.Constructor, "AssemblyMetadataAttribute");
                if (!isVersion && !isMetadata)
                    continue;

                var blob = reader.GetBlobReader(attribute.Value);
                if (blob.ReadUInt16() != 1)
                    throw new InvalidDataException("The installed Agent DLL has malformed assembly metadata.");
                var first = blob.ReadSerializedString();
                var second = isMetadata ? blob.ReadSerializedString() : null;
                if (blob.ReadUInt16() != 0 || blob.RemainingBytes != 0)
                    throw new InvalidDataException("The installed Agent DLL has malformed assembly metadata.");

                if (isVersion)
                {
                    if (informationalVersion is not null || string.IsNullOrWhiteSpace(first) || first.Length > 64 || !VersionPattern.IsMatch(first))
                        throw new InvalidDataException("The installed Agent DLL has an invalid or duplicate informational version.");
                    informationalVersion = first;
                }
                else if (string.Equals(first, "AgentUpdateChallengeProtocol", StringComparison.Ordinal))
                {
                    if (challengeProtocol is not null || second is not ("1" or "2"))
                        throw new InvalidDataException("The installed Agent DLL has an unknown or duplicate update protocol marker.");
                    challengeProtocol = int.Parse(second, System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            return new(informationalVersion, challengeProtocol);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            throw new InvalidDataException("The installed Agent DLL metadata is malformed.", ex);
        }
    }

    private static bool IsAttribute(MetadataReader reader, EntityHandle constructor, string name)
    {
        var type = constructor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default
        };
        if (type.Kind != HandleKind.TypeReference)
            return false;
        var reference = reader.GetTypeReference((TypeReferenceHandle)type);
        if (reader.GetString(reference.Namespace) != "System.Reflection" || reader.GetString(reference.Name) != name ||
            reference.ResolutionScope.Kind != HandleKind.AssemblyReference)
            return false;
        var assembly = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
        return assembly is "System.Runtime" or "System.Private.CoreLib" or "mscorlib";
    }

    private static void ValidateNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("The installed Agent deps manifest has duplicate or case-aliased fields.");
                ValidateNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ValidateNoDuplicateProperties(item);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record DepsFacts(string Version);
    private sealed record AssemblyFacts(string? InformationalVersion, int? ChallengeProtocol);
}

public sealed record MaintenanceBackupOperationResult(
    InstalledAgentSourceSnapshot Source,
    MaintenanceBackupResult Backup);

public sealed class MaintenanceBackupOperation(
    string sourceRoot,
    InstalledAgentSourceInspector sourceInspector,
    MaintenanceBackupPublisher publisher,
    MaintenanceJournal journal)
{
    private static readonly string[] Phases =
    [
        "inspected", "controllerStarted", "targetValidated", "servicesQuiesced", "backupPublished",
        "sourceBound", "swapIntent", "sourceMoved", "targetPlaced", "servicesStarted", "outcomeReported", "controllerConfirmed"
    ];

    public MaintenanceBackupOperationResult Publish(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(sourceInspector);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(journal);
        var snapshot = journal.Read()
            ?? throw new InvalidOperationException("The maintenance journal is missing; backup publication remains held.");
        if (snapshot.Operations.Count != 1 || snapshot.HeadOperationId != operationId ||
            snapshot.Operations[0].Binding.OperationId != operationId || snapshot.Operations[0].Binding.ChainId != snapshot.ChainId)
            throw new InvalidOperationException("The backup operation is not the current journal head.");

        var operation = snapshot.Operations[0];
        var phaseIndex = Array.IndexOf(Phases, operation.Phase);
        var servicesQuiescedIndex = Array.IndexOf(Phases, "servicesQuiesced");
        var backupPublishedIndex = Array.IndexOf(Phases, "backupPublished");
        if (phaseIndex < servicesQuiescedIndex || phaseIndex > backupPublishedIndex)
            throw new InvalidOperationException("The journal must prove service quiescence before backup publication.");
        if (!string.Equals(Path.GetFullPath(sourceRoot), sourceInspector.SourceRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(sourceRoot), publisher.SourceRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The source inspector is not bound to the publisher's fixed installation path.");

        var source = sourceInspector.Inspect();
        if (string.Equals(source.Version, operation.Binding.TargetVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An ordinary Agent update cannot bind a source version equal to its target.");

        var backup = publisher.Publish(operationId);
        if (!string.Equals(backup.BackupOwnerOperationId, operationId, StringComparison.Ordinal) ||
            !ContainsSourceMetadata(backup, source))
            throw new InvalidOperationException("The published backup does not contain the exact source metadata snapshot that was inspected.");

        _ = journal.Advance(operationId, "backupPublished",
            new MaintenanceJournalEvidence(BackupOwnerOperationId: operationId, BackupDigest: backup.BackupDigest));
        return new(source, backup);
    }

    private static bool ContainsSourceMetadata(MaintenanceBackupResult backup, InstalledAgentSourceSnapshot source)
    {
        var deps = backup.Nodes.SingleOrDefault(node => !node.IsDirectory && node.RelativePath == "Agent.deps.json");
        var assembly = backup.Nodes.SingleOrDefault(node => !node.IsDirectory && node.RelativePath == "Agent.dll");
        return string.Equals(deps?.Sha256, source.DepsSha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(assembly?.Sha256, source.AgentAssemblySha256, StringComparison.OrdinalIgnoreCase);
    }
}
