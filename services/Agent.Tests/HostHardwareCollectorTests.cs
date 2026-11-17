using System.Runtime.Versioning;
using KamenRaider.Agent.Hardware;

namespace Agent.Tests;

public class HostHardwareCollectorTests
{
    private sealed class FakeSource : IHardwareSource
    {
        public Func<string> ReadMachineName { get; set; } = () => "PC-LAB-01";
        public Func<string?> ReadProcessorName { get; set; } = () => "Fake CPU 9000";
        public Func<int> ReadLogicalProcessorCount { get; set; } = () => 16;
        public Func<IReadOnlyList<string>> ReadGraphicAdapterNames { get; set; } = () => ["Fake GPU"];
        public Func<long> ReadTotalPhysicalMemory { get; set; } = () => 16L * 1024 * 1024 * 1024;

        public string MachineName => ReadMachineName();
        public string? ProcessorName => ReadProcessorName();
        public int LogicalProcessorCount => ReadLogicalProcessorCount();
        public IReadOnlyList<string> GraphicsAdapterNames => ReadGraphicAdapterNames();
        public long TotalPhysicalMemoryBytes => ReadTotalPhysicalMemory();
    }

    [Fact]
    public void CollectsBoundedDisplayProfileAndIncludesLogicalProcessorCount()
    {
        var source = new FakeSource
        {
            ReadMachineName = () => "  " + new string('H', 200) + "  ",
            ReadProcessorName = () => new string('C', 300),
            ReadLogicalProcessorCount = () => 16,
            ReadGraphicAdapterNames = () => [new string('G', 300), "Secondary GPU"],
            ReadTotalPhysicalMemory = () => 17179869184,
        };

        var hardware = new HostHardwareCollector(source).Collect();

        Assert.NotNull(hardware);
        Assert.Equal(HostHardwareCollector.MaximumHostnameLength, hardware!.Hostname.Length);
        Assert.Equal(HostHardwareCollector.MaximumCpuLength, hardware.Cpu.Length);
        Assert.Contains("16 logical processors", hardware.Cpu);
        Assert.True(hardware.Gpu.Length <= HostHardwareCollector.MaximumGpuLength);
        Assert.Equal(17179869184, hardware.RamBytes);
    }

    [Fact]
    public void JoinsDistinctGraphicAdapters()
    {
        var source = new FakeSource
        {
            ReadGraphicAdapterNames = () => [" Intel Graphics ", "intel graphics", "NVIDIA RTX", "", "  "],
        };

        var hardware = new HostHardwareCollector(source).Collect();

        Assert.NotNull(hardware);
        Assert.Equal("Intel Graphics; NVIDIA RTX", hardware!.Gpu);
    }

    [Fact]
    public void CollectsCpuNameOnlyWhenLogicalProcessorCountFails()
    {
        var source = new FakeSource
        {
            ReadProcessorName = () => "Fake CPU 9000",
            ReadLogicalProcessorCount = () => throw new InvalidOperationException("no processor count"),
        };

        var hardware = new HostHardwareCollector(source).Collect();

        Assert.NotNull(hardware);
        Assert.Equal("Fake CPU 9000", hardware!.Cpu);
    }

    [Fact]
    public void ThrowingSourceYieldsNullWithoutThrowing()
    {
        var source = new FakeSource
        {
            ReadMachineName = () => throw new InvalidOperationException("hostname"),
            ReadProcessorName = () => throw new UnauthorizedAccessException("registry"),
            ReadLogicalProcessorCount = () => throw new PlatformNotSupportedException("cpu"),
            ReadGraphicAdapterNames = () => throw new System.ComponentModel.Win32Exception("gpu"),
            ReadTotalPhysicalMemory = () => throw new System.ComponentModel.Win32Exception("ram"),
        };

        var hardware = new HostHardwareCollector(source).Collect();

        Assert.Null(hardware);
    }

    [Fact]
    public void PartialFailureKeepsReadableFieldsAndEmptyStrings()
    {
        var source = new FakeSource
        {
            ReadProcessorName = () => throw new UnauthorizedAccessException("registry"),
            ReadLogicalProcessorCount = () => throw new UnauthorizedAccessException("cpu"),
            ReadTotalPhysicalMemory = () => throw new System.ComponentModel.Win32Exception("ram"),
        };

        var hardware = new HostHardwareCollector(source).Collect();

        Assert.NotNull(hardware);
        Assert.Equal("PC-LAB-01", hardware!.Hostname);
        Assert.Equal("", hardware.Cpu);
        Assert.Equal("Fake GPU", hardware.Gpu);
        Assert.Equal(0, hardware.RamBytes);
    }

    [Fact]
    public void StripsControlCharactersAndTrimsText()
    {
        var source = new FakeSource
        {
            ReadMachineName = () => "  PC\u0000-LAB\t  ",
            ReadProcessorName = () => "Fake\r\nCPU",
        };

        var hardware = new HostHardwareCollector(source).Collect();

        Assert.NotNull(hardware);
        Assert.Equal("PC-LAB", hardware!.Hostname);
        Assert.StartsWith("FakeCPU", hardware.Cpu);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void NonPositiveMemoryReadingBecomesZero(long bytes)
    {
        var source = new FakeSource { ReadTotalPhysicalMemory = () => bytes };

        var hardware = new HostHardwareCollector(source).Collect();

        Assert.NotNull(hardware);
        Assert.Equal(0, hardware!.RamBytes);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void RealWindowsHostProducesABoundedProfile()
    {
        if (!OperatingSystem.IsWindows()) return;

        var hardware = new HostHardwareCollector(new WindowsHostHardwareSource()).Collect();

        Assert.NotNull(hardware);
        Assert.False(string.IsNullOrWhiteSpace(hardware!.Hostname));
        Assert.Equal(Environment.MachineName.Trim(), hardware.Hostname);
        Assert.True(hardware.Hostname.Length <= HostHardwareCollector.MaximumHostnameLength);
        Assert.True(hardware.Cpu.Length <= HostHardwareCollector.MaximumCpuLength);
        Assert.False(string.IsNullOrWhiteSpace(hardware.Cpu));
        Assert.True(hardware.Gpu.Length <= HostHardwareCollector.MaximumGpuLength);
        Assert.True(hardware.RamBytes > 0);
    }
}
