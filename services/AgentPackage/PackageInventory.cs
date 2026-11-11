using System.Collections.ObjectModel;

namespace KamenRaider.AgentPackage;

public sealed record PackageNode(string Path, bool IsDirectory, long Length, string? Sha256);

public sealed class PackageInventory
{
    internal PackageInventory(string archiveHash, string version, string targetRuntime, IEnumerable<PackageNode> nodes, long totalBytes,
        string digest, string sourceCommit, bool sourceDirty, bool releaseBuild, int agentUpdateChallengeProtocol)
    {
        ArchiveHash = archiveHash;
        Version = version;
        TargetRuntime = targetRuntime;
        Nodes = new ReadOnlyCollection<PackageNode>(nodes.ToArray());
        TotalBytes = totalBytes;
        InventoryDigest = digest;
        SourceCommit = sourceCommit;
        SourceDirty = sourceDirty;
        ReleaseBuild = releaseBuild;
        AgentUpdateChallengeProtocol = agentUpdateChallengeProtocol;
    }

    public string ArchiveHash { get; }
    public string Version { get; }
    public string TargetRuntime { get; }
    public IReadOnlyList<PackageNode> Nodes { get; }
    public long TotalBytes { get; }
    public string InventoryDigest { get; }
    public string SourceCommit { get; }
    public bool SourceDirty { get; }
    public bool ReleaseBuild { get; }
    public int AgentUpdateChallengeProtocol { get; }
}

public sealed class PackageValidationException : Exception
{
    public PackageValidationException(string category, string message) : base(message) => Category = category;
    public string Category { get; }
}
