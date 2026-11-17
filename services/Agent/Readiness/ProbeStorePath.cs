namespace KamenRaider.Agent.Probe;

internal static class ProbeStorePath
{
    private const string ManagedDirectoryName = "KamenRaiderProbe";
    private const string MarkerFileName = "restore-test.bin";

    internal static string BuildManagedFilePath(string parent, string machineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);
        if (!IsCanonicalMachineId(machineId))
            throw new ArgumentException("The managed probe machine ID must be a lowercase non-empty GUID in N format.", nameof(machineId));

        if (!Path.IsPathFullyQualified(parent) || IsUncOrDevicePath(parent) ||
            parent.IndexOfAny(['*', '?']) >= 0 || ContainsAlternateDataStream(parent) || ContainsTraversal(parent))
            throw new ArgumentException("The managed probe parent must be an unambiguous absolute local path.", nameof(parent));

        string fullParent;
        try
        {
            fullParent = Path.GetFullPath(parent);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The managed probe parent is not a valid local path.", nameof(parent), exception);
        }

        var root = Path.GetPathRoot(fullParent);
        if (string.IsNullOrEmpty(root) || string.Equals(fullParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A volume root cannot be used as the managed probe parent.", nameof(parent));

        if (HasAmbiguousComponent(fullParent))
            throw new ArgumentException("The managed probe parent contains a path component with ambiguous Win32 normalization.", nameof(parent));

        return Path.Combine(fullParent, ManagedDirectoryName, machineId, MarkerFileName);
    }

    private static bool IsCanonicalMachineId(string? value)
        => value is not null &&
           Guid.TryParseExact(value, "N", out var parsed) &&
           parsed != Guid.Empty &&
           string.Equals(value, parsed.ToString("N"), StringComparison.Ordinal);

    private static bool IsUncOrDevicePath(string value)
        => value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal);

    private static bool ContainsAlternateDataStream(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == ':' && index != 1)
                return true;
        }

        return false;
    }

    private static bool ContainsTraversal(string value)
        => value.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Any(component => component is "." or "..");

    private static bool HasAmbiguousComponent(string value)
        => value.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(component => component.EndsWith(' ') || component.EndsWith('.'));
}
