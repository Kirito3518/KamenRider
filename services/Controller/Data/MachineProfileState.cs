using KamenRaider.Contracts;
using Microsoft.Data.Sqlite;

namespace KamenRaider.Controller.Data;

/// <summary>
/// Agent-reported machine profile (hostname, CPU, GPU, RAM). These values are display and diagnostic
/// data only: storing them never changes approval, readiness, configuration revision or rentability.
/// </summary>
public sealed partial class ControllerDatabase
{
    /// <summary>Upper bound for reported RAM (2^40 bytes = 1 TiB); values outside it are rejected.</summary>
    internal const long MaximumReportedRamBytes = 1L << 40;
    private const int MaximumReportedHostnameLength = 64;
    private const int MaximumReportedCpuLength = 128;
    private const int MaximumReportedGpuLength = 256;

    /// <summary>
    /// Trims and bounds Agent-reported hardware. A hostname is required whenever a hardware profile
    /// is reported; CPU and GPU are optional bounded text. Invalid values are rejected so the
    /// Controller never persists unbounded or control-character-bearing Agent input.
    /// </summary>
    internal static HardwareInfo? NormalizeReportedHardware(HardwareInfo? hardware)
    {
        if (hardware is null) return null;
        var hostname = NormalizeReportedProfileText(hardware.Hostname, MaximumReportedHostnameLength, "hostname", required: true);
        var cpu = NormalizeReportedProfileText(hardware.Cpu, MaximumReportedCpuLength, "CPU", required: false);
        var gpu = NormalizeReportedProfileText(hardware.Gpu, MaximumReportedGpuLength, "GPU", required: false);
        if (hardware.RamBytes is < 0 or > MaximumReportedRamBytes)
            throw new ArgumentException("Invalid reported machine RAM size.");
        return new HardwareInfo(hostname, cpu, gpu, hardware.RamBytes);
    }

    private static string NormalizeReportedProfileText(string? value, int maximumLength, string field, bool required)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Any(char.IsControl))
            throw new ArgumentException($"The reported machine {field} contains control characters.");
        if (normalized.Length > maximumLength)
            throw new ArgumentException($"The reported machine {field} is too long.");
        if (required && normalized.Length == 0)
            throw new ArgumentException($"The reported machine {field} is required.");
        return normalized;
    }

    private void StoreReportedHardware(SqliteTransaction transaction, string machineId, HardwareInfo hardware)
    {
        using var update = Command("UPDATE machines SET hostname=$hostname,cpu=$cpu,gpu=$gpu,ram_bytes=$ram WHERE machine_id=$id",
            ("$hostname", hardware.Hostname), ("$cpu", hardware.Cpu), ("$gpu", hardware.Gpu),
            ("$ram", hardware.RamBytes), ("$id", machineId));
        update.Transaction = transaction;
        if (update.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The reported machine hardware could not be stored.");
    }

    private static string? OptionalReportedProfileText(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetString(ordinal);
        return value.Length == 0 ? null : value;
    }
}
