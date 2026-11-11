using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using KamenRaider.AgentMaintenance;
using Controller.Tests.Fixtures;

namespace Controller.Tests;

[SupportedOSPlatform("windows")]
public sealed class ElevatedWindowsWorkStoreFactAttribute : FactAttribute
{
    public ElevatedWindowsWorkStoreFactAttribute()
    {
        if (!OperatingSystem.IsWindows() ||
            !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Skip = "The production work-store ACL integration requires an elevated Windows test process.";
            return;
        }
        try { _ = WindowsMaintenanceFenceStore.ResolveAgentServiceSid(); }
        catch (Exception)
        {
            Skip = "The production Agent service SID is unavailable for the Windows work-store ACL integration.";
        }
    }
}

public sealed class AgentMaintenanceCliTests
{
    private sealed class WorkStore(MaintenanceCommandRequest request, byte[] archive) : IMaintenanceWorkStore
    {
        public int RequestReads;
        public int ArchiveOpens;
        public bool ExtractionCreated;
        public string ExtractionRoot { get; set; } = Path.Combine(Path.GetTempPath(), "kr-maintenance-extract-" + Guid.NewGuid().ToString("N"));

        public MaintenanceCommandRequest ReadRequest(string expectedCommand)
        {
            RequestReads++;
            Assert.Equal(expectedCommand, request.Command);
            return request;
        }

        public Stream OpenArchiveRead()
        {
            ArchiveOpens++;
            return new MemoryStream(archive, writable: false);
        }

        public void CreateExtractionRoot()
        {
            ExtractionCreated = true;
            Directory.CreateDirectory(ExtractionRoot);
        }
    }

    private sealed class FakeCommandHost : IAgentMaintenanceCommandHost
    {
        public string? Command;
        public AgentMaintenanceCommandResult Execute(string command)
        {
            Command = command;
            return new("ok", null, null, null, null, null, null, null);
        }
    }

    [Fact]
    public void ValidateArchiveReturnsBoundedInventoryOnlyAfterExpectedBytesAndRuntimeMatch()
    {
        var archive = AgentPackageCorpus.Valid();
        var hash = Convert.ToHexString(SHA256.HashData(archive));
        var work = new WorkStore(new(1, "validate-archive", "op-1", AgentPackageCorpus.Version,
            "win-x64", hash, archive.Length), archive);
        var engine = new LocalUpdateEngine(work);

        var inventory = engine.ValidateArchive();

        Assert.Equal(hash, inventory.ArchiveHash, ignoreCase: true);
        Assert.Equal("win-x64", inventory.TargetRuntime);
        Assert.Equal(AgentPackageCorpus.Version, inventory.Version);
        Assert.NotEmpty(inventory.InventoryDigest);
        Assert.InRange(inventory.Nodes.Count, 1, 5000);
        Assert.Equal(1, work.RequestReads);
        Assert.Equal(1, work.ArchiveOpens);
    }

    [Fact]
    public void ValidateArchiveRejectsACommandRequestForDifferentArchiveBytes()
    {
        var archive = AgentPackageCorpus.Valid();
        var work = new WorkStore(new(1, "validate-archive", "op-1", AgentPackageCorpus.Version,
            "win-x64", new string('0', 64), archive.Length), archive);

        Assert.Throws<InvalidOperationException>(() => new LocalUpdateEngine(work).ValidateArchive());
    }

    [Fact]
    public void ExtractRevalidatesPinnedArchiveAndCreatesOnlyTheFixedFreshTree()
    {
        var archive = AgentPackageCorpus.Valid();
        var hash = Convert.ToHexString(SHA256.HashData(archive));
        var work = new WorkStore(new(1, "extract", "op-1", AgentPackageCorpus.Version,
            "win-x64", hash, archive.Length), archive);
        try
        {
            var inventory = new LocalUpdateEngine(work).Extract();

            Assert.True(work.ExtractionCreated);
            Assert.True(File.Exists(Path.Combine(work.ExtractionRoot, "Agent", "Agent.dll")));
            Assert.Equal(hash, inventory.ArchiveHash, ignoreCase: true);
            Assert.Equal(1, work.RequestReads);
            Assert.Equal(1, work.ArchiveOpens);
        }
        finally
        {
            AgentPackageCorpus.DeleteOwnedTemp(work.ExtractionRoot, "kr-maintenance-extract-");
        }
    }

    [Fact]
    public void ExtractRejectsWrongNativeRuntimeBeforeCreatingItsTree()
    {
        var archive = AgentPackageCorpus.Valid();
        var hash = Convert.ToHexString(SHA256.HashData(archive));
        var work = new WorkStore(new(1, "extract", "op-1", AgentPackageCorpus.Version,
            "win-arm64", hash, archive.Length), archive);

        Assert.Throws<InvalidOperationException>(() => new LocalUpdateEngine(work).Extract());
        Assert.False(work.ExtractionCreated);
        Assert.False(Directory.Exists(work.ExtractionRoot));
    }

    [Fact]
    public void CommandLineRejectsCallerSuppliedPathsBeforeOpeningARequest()
    {
        var host = new FakeCommandHost();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = AgentMaintenanceCli.Run(["validate-archive", @"C:\\untrusted.zip"], host, output, error);

        Assert.NotEqual(0, exitCode);
        Assert.Null(host.Command);
        Assert.DoesNotContain("untrusted.zip", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandLineDispatchesOneFixedOperationAndEmitsOnlyTypedResultJson()
    {
        var host = new FakeCommandHost();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = AgentMaintenanceCli.Run(["validate-archive"], host, output, error);

        Assert.Equal(0, exitCode);
        Assert.Equal("validate-archive", host.Command);
        Assert.Contains("\"status\":\"ok\"", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void RequestParserAcceptsOnlyTheExactTypedSchema()
    {
        const string valid = """{"schemaVersion":1,"command":"validate-archive","operationId":"op-1","targetVersion":"2.3.4","targetRuntime":"win-x64","targetPackageSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","targetPackageLength":12}""";
        var parsed = MaintenanceCommandRequestParser.Parse(Encoding.UTF8.GetBytes(valid), "validate-archive");

        Assert.Equal("op-1", parsed.OperationId);
        Assert.Equal("2.3.4", parsed.TargetVersion);
        Assert.Equal(12, parsed.TargetPackageLength);
    }

    [Theory]
    [InlineData("\"unexpected\":true")]
    [InlineData("\"TargetRuntime\":\"win-arm64\"")]
    [InlineData("\"command\":\"extract\"")]
    public void RequestParserRejectsUnknownDuplicateOrMismatchedFields(string extraProperty)
    {
        const string prefix = """{"schemaVersion":1,"command":"validate-archive","operationId":"op-1","targetVersion":"2.3.4","targetRuntime":"win-x64","targetPackageSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","targetPackageLength":12,""";
        var json = prefix + extraProperty + "}";

        Assert.Throws<InvalidDataException>(() => MaintenanceCommandRequestParser.Parse(
            Encoding.UTF8.GetBytes(json), "validate-archive"));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ProductionArchiveHandleRemainsOwnedByItsReturnedStream()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The native file identity check requires Windows.");
        var root = Path.Combine(Path.GetTempPath(), "kr-maint-handle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "target.zip");
        var expected = new byte[] { 1, 3, 5, 7, 9 };
        File.WriteAllBytes(path, expected);
        try
        {
            using var stream = WindowsMaintenanceWorkStore.OpenArchiveStream(path);
            Assert.Equal(expected.Length, stream.Length);
            var actual = new byte[expected.Length];
            stream.ReadExactly(actual);
            Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WindowsFileInformationLayoutMatchesByHandleFileInformation()
    {
        Assert.Equal(52, Marshal.SizeOf<WindowsFileInformation>());
        Assert.Equal(4, Marshal.OffsetOf<WindowsFileInformation>(nameof(WindowsFileInformation.CreationTime)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<WindowsFileInformation>(nameof(WindowsFileInformation.VolumeSerialNumber)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<WindowsFileInformation>(nameof(WindowsFileInformation.NumberOfLinks)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<WindowsFileInformation>(nameof(WindowsFileInformation.FileIndexLow)).ToInt32());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ProductionArchiveHandleCheckAcceptsOneLinkAndRejectsHardlinks()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The native file identity check requires Windows.");
        var root = Path.Combine(Path.GetTempPath(), "kr-maint-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var original = Path.Combine(root, "target.zip");
        var linked = Path.Combine(root, "linked.zip");
        File.WriteAllBytes(original, [0x50, 0x4b, 0x03, 0x04]);
        try
        {
            using (var stream = WindowsMaintenanceWorkStore.OpenArchiveStream(original))
                Assert.Equal(4, stream.Length);
            Assert.True(CreateHardLinkW(linked, original, IntPtr.Zero), $"CreateHardLinkW failed with {Marshal.GetLastWin32Error()}.");
            Assert.Throws<UnauthorizedAccessException>(() => WindowsMaintenanceWorkStore.OpenArchiveStream(linked));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [ElevatedWindowsWorkStoreFact]
    [SupportedOSPlatform("windows")]
    public void ProductionWorkStoreReadsPinnedArchiveFromAProtectedTemporaryTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "kr-maint-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var protectedRoot = Path.Combine(root, "maintenance");
            new WindowsMaintenanceFenceStore(protectedRoot).EnsureLocationForJournal();
            var workRoot = Path.Combine(protectedRoot, "work");
            new DirectoryInfo(workRoot).Create(WindowsMaintenanceWorkStore.CreatePrivateDirectorySecurity());
            var archivePath = Path.Combine(workRoot, "target.zip");
            var expected = new byte[] { 2, 4, 6, 8 };
            File.WriteAllBytes(archivePath, expected);
            WindowsMaintenanceWorkStore.SetPrivateFileSecurity(archivePath);

            using var stream = new WindowsMaintenanceWorkStore(workRoot).OpenArchiveRead();
            var actual = new byte[expected.Length];
            stream.ReadExactly(actual);
            Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);
}
