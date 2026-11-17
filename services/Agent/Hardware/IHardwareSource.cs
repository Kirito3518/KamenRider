namespace KamenRaider.Agent.Hardware;

/// <summary>
/// Raw host readings used to build the bounded display hardware profile reported to the Controller.
/// Implementations may throw for an unavailable platform or provider; <see cref="HostHardwareCollector"/>
/// contains every failure so a diagnostic probe can never prevent enrollment or heartbeat.
/// </summary>
public interface IHardwareSource
{
    /// <summary>Windows computer name, for example <c>PC-LAB-01</c>.</summary>
    string MachineName { get; }

    /// <summary>Human readable CPU model from the platform registry, if available.</summary>
    string? ProcessorName { get; }

    /// <summary>Number of logical processors currently visible to the process.</summary>
    int LogicalProcessorCount { get; }

    /// <summary>Display adapter descriptions in registry order; may be empty.</summary>
    IReadOnlyList<string> GraphicsAdapterNames { get; }

    /// <summary>Total physical memory in bytes.</summary>
    long TotalPhysicalMemoryBytes { get; }
}
