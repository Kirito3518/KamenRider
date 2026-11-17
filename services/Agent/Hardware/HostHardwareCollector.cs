using KamenRaider.Contracts;

namespace KamenRaider.Agent.Hardware;

/// <summary>
/// Builds the bounded hardware profile reported to the Controller for display and initial naming.
/// The profile is diagnostic data only: it never grants approval, readiness, or rental eligibility.
/// Every reading is best-effort and independently contained: a failed reading yields an empty field
/// (or 0 bytes of RAM), and a host where nothing can be read yields <c>null</c>. The collector never
/// throws, so a broken provider can never prevent enrollment or heartbeat.
/// </summary>
public sealed class HostHardwareCollector(IHardwareSource source)
{
    public const int MaximumHostnameLength = 64;
    public const int MaximumCpuLength = 128;
    public const int MaximumGpuLength = 256;

    public HardwareInfo? Collect()
    {
        var hostname = TryRead(() => Bound(Text(source.MachineName), MaximumHostnameLength));
        var cpu = TryRead(BuildCpu);
        var gpu = TryRead(BuildGpu);
        var ram = TryReadRam();

        if (hostname is null && cpu is null && gpu is null && ram is null) return null;
        return new HardwareInfo(hostname ?? "", cpu ?? "", gpu ?? "", ram ?? 0);
    }

    private string? BuildCpu()
    {
        string name;
        try
        {
            name = Text(source.ProcessorName);
        }
        catch (Exception)
        {
            name = "";
        }

        var logicalProcessors = 0;
        try
        {
            logicalProcessors = source.LogicalProcessorCount;
        }
        catch (Exception)
        {
            // A missing logical processor count still leaves a usable CPU model name.
        }

        if (name.Length == 0 && logicalProcessors <= 0) return null;
        var suffix = logicalProcessors is > 0 and <= 4096
            ? $"{logicalProcessors} logical processors"
            : "";
        if (name.Length == 0) return Bound(suffix, MaximumCpuLength);
        if (suffix.Length == 0) return Bound(name, MaximumCpuLength);
        var combined = $"{name} ({suffix})";
        if (combined.Length <= MaximumCpuLength) return combined;
        // Keep the processor count visible even when the registry model name is unusually long.
        var nameBudget = MaximumCpuLength - suffix.Length - 3;
        if (nameBudget <= 0) return Bound(suffix, MaximumCpuLength);
        return Bound(name, nameBudget) + " (" + suffix + ")";
    }

    private string? BuildGpu()
    {
        var adapters = source.GraphicsAdapterNames;
        if (adapters is null) return "";
        var names = adapters
            .Select(Text)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Bound(string.Join("; ", names), MaximumGpuLength);
    }

    private long? TryReadRam()
    {
        try
        {
            var bytes = source.TotalPhysicalMemoryBytes;
            return bytes > 0 ? bytes : 0;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryRead(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Text(string? value) =>
        value is null
            ? ""
            : new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();

    private static string Bound(string value, int maximumLength)
    {
        if (value.Length <= maximumLength) return value;
        var length = maximumLength;
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length].TrimEnd();
    }
}
