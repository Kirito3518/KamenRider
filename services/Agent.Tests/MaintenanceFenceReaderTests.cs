using KamenRaider.Agent.Sunshine;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Agent.Tests;

[SupportedOSPlatform("windows")]
public sealed class MaintenanceFenceReaderTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    [Fact]
    public void MissingFenceIsUnknownAndHeld()
    {
        var root = Path.Combine(Path.GetTempPath(), "kr-fence-" + Guid.NewGuid().ToString("N"));
        var reader = new WindowsMaintenanceFenceReader(Path.Combine(root, "maintenance-fence.json"));

        var snapshot = reader.Read();

        Assert.False(snapshot.IsKnown);
        Assert.True(snapshot.IsHeld);
    }

    [Fact]
    public void CorruptFenceIsUnknownAndHeld()
    {
        var root = Path.Combine(Path.GetTempPath(), "kr-fence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "maintenance-fence.json");
            File.WriteAllText(path, "{\"schemaVersion\":1,\"state\":\"clear\"");

            var snapshot = new WindowsMaintenanceFenceReader(path).Read();

            Assert.False(snapshot.IsKnown);
            Assert.True(snapshot.IsHeld);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void HeldFenceReturnsItsOperationAndGeneration()
    {
        var root = Path.Combine(Path.GetTempPath(), "kr-fence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "maintenance-fence.json");
            File.WriteAllText(path, """
                {"schemaVersion":1,"state":"held","operationId":"op-1","chainId":"chain-1","headOperationId":"op-1","generation":4,"phase":"sourceBound"}
                """);

            var snapshot = new WindowsMaintenanceFenceReader(path).Read();

            Assert.True(snapshot.IsKnown);
            Assert.True(snapshot.IsHeld);
            Assert.Equal("op-1", snapshot.OperationId);
            Assert.Equal("chain-1", snapshot.ChainId);
            Assert.Equal(4, snapshot.Generation);
            Assert.Equal("sourceBound", snapshot.Phase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FenceCanClearOnlyAfterControllerConfirmation()
    {
        var root = Path.Combine(Path.GetTempPath(), "kr-fence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "maintenance-fence.json");
            File.WriteAllText(path, """
                {"schemaVersion":1,"state":"clear","operationId":"op-1","chainId":"chain-1","headOperationId":"op-1","generation":4,"phase":"servicesStarted"}
                """);
            var reader = new WindowsMaintenanceFenceReader(path);

            var prematureClear = reader.Read();
            Assert.False(prematureClear.IsKnown);
            Assert.True(prematureClear.IsHeld);

            File.WriteAllText(path, """
                {"schemaVersion":1,"state":"clear","operationId":"op-1","chainId":"chain-1","headOperationId":"op-1","generation":4,"phase":"controllerConfirmed"}
                """);
            var confirmedClear = reader.Read();

            Assert.True(confirmedClear.IsKnown);
            Assert.False(confirmedClear.IsHeld);
            Assert.Equal("controllerConfirmed", confirmedClear.Phase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FreshInstallerMayPublishOnlyTheExplicitFreshInstallClearPhase()
    {
        var root = Path.Combine(Path.GetTempPath(), "kr-fence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "maintenance-fence.json");
            File.WriteAllText(path, """
                {"schemaVersion":1,"state":"clear","operationId":"fresh-0123456789abcdef0123456789abcdef","chainId":"fresh-0123456789abcdef0123456789abcdef","headOperationId":"fresh-0123456789abcdef0123456789abcdef","generation":1,"phase":"freshInstall"}
                """);

            var freshClear = new WindowsMaintenanceFenceReader(path).Read();

            Assert.True(freshClear.IsKnown);
            Assert.False(freshClear.IsHeld);
            Assert.Equal("freshInstall", freshClear.Phase);
            File.WriteAllText(path, """
                {"schemaVersion":1,"state":"clear","operationId":"fresh-0123456789abcdef0123456789abcdef","chainId":"fresh-0123456789abcdef0123456789abcdef","headOperationId":"fresh-0123456789abcdef0123456789abcdef","generation":1,"phase":"legacyImported"}
                """);
            var unrelatedClear = new WindowsMaintenanceFenceReader(path).Read();
            Assert.False(unrelatedClear.IsKnown);
            Assert.True(unrelatedClear.IsHeld);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void HardLinkedFenceIsUnknownAndHeld()
    {
        var root = Path.Combine(Path.GetTempPath(), "kr-fence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "maintenance-fence.json");
            var alias = Path.Combine(root, "other-name.json");
            File.WriteAllText(path, """
                {"schemaVersion":1,"state":"clear","operationId":"op-1","chainId":"chain-1","headOperationId":"op-1","generation":4,"phase":"controllerConfirmed"}
                """);
            if (!CreateHardLink(alias, path, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The test filesystem could not create a hard link.");

            var snapshot = new WindowsMaintenanceFenceReader(path).Read();

            Assert.False(snapshot.IsKnown);
            Assert.True(snapshot.IsHeld);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
