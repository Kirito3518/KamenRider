using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KamenRaider.Agent.Hardware;

/// <summary>
/// Windows implementation of <see cref="IHardwareSource"/>. The readings are display-only diagnostics:
/// they are bounded by <see cref="HostHardwareCollector"/> and never grant approval, readiness, or rental
/// eligibility. Throwing is allowed here; the collector converts every failure into an empty field.
/// </summary>
public sealed class WindowsHostHardwareSource : IHardwareSource
{
    private const string CentralProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const int MaximumDisplayAdapters = 6;

    public string MachineName => Environment.MachineName;

    public string? ProcessorName
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Windows hardware collection is available only on Windows.");
            using var key = Registry.LocalMachine.OpenSubKey(CentralProcessorKey);
            return key?.GetValue("ProcessorNameString") as string;
        }
    }

    public int LogicalProcessorCount => Environment.ProcessorCount;

    public IReadOnlyList<string> GraphicsAdapterNames
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Windows hardware collection is available only on Windows.");
            var names = new List<string>(MaximumDisplayAdapters);
            using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (classKey is null) return names;
            for (var index = 0; index < MaximumDisplayAdapters; index++)
            {
                using var adapterKey = classKey.OpenSubKey(index.ToString("D4", CultureInfo.InvariantCulture));
                if (adapterKey?.GetValue("DriverDesc") is string description && !string.IsNullOrWhiteSpace(description))
                    names.Add(description);
            }

            return names;
        }
    }

    public long TotalPhysicalMemoryBytes
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Windows hardware collection is available only on Windows.");
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read physical memory.");
            if (status.TotalPhys is 0 or > long.MaxValue)
                throw new InvalidDataException("Windows returned an invalid physical memory reading.");
            return (long)status.TotalPhys;
        }
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailablePhys;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
