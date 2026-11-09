using System.Collections.ObjectModel;

namespace KamenRaider.AgentPackage;

public sealed record PackageNode(string Path, bool IsDirectory, long Length, string? Sha256);

public sealed class PackageInventory
{
    internal PackageInventory(string archiveHash, string version, string targetRuntime, IEnumerable<PackageNode> nodes, long totalBytes, string digest)
    {
        ArchiveHash = archiveHash;
        Version = version;
        TargetRuntime = targetRuntime;
        Nodes = new ReadOnlyCollection<PackageNode>(nodes.ToArray());
        TotalBytes = totalBytes;
        InventoryDigest = digest;
    }

    public string ArchiveHash { get; }
    public string Version { get; }
    public string TargetRuntime { get; }
    public IReadOnlyList<PackageNode> Nodes { get; }
    public long TotalBytes { get; }
    public string InventoryDigest { get; }
}

public sealed class PackageValidationException : Exception
{
    public PackageValidationException(string category, string message) : base(message) => Category = category;
    public string Category { get; }
}
