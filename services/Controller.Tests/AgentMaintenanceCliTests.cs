using System.Security.Cryptography;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using KamenRaider.AgentMaintenance;
using KamenRaider.AgentPackage;
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

    private sealed class FakeApplyCommand : IMaintenanceApplyCommand
    {
        public int Calls;

        public AgentMaintenanceCommandResult Execute()
        {
            Calls++;
            return new("ok", null, null, "2.3.4", "win-x64", null, null, null, ApplyOutcome: "applied");
        }
    }

    private sealed class FakeReconcileCommand : IMaintenanceReconcileCommand
    {
        public int Calls;

        public AgentMaintenanceCommandResult Execute()
        {
            Calls++;
            return new("ok", null, null, "2.3.4", "win-x64", null, null, null, ApplyOutcome: "reconciled");
        }
    }

    [Fact]
    public void ReconcileUsesItsDedicatedFailClosedRecoveryCommand()
    {
        var archive = AgentPackageCorpus.Valid();
        var hash = Convert.ToHexString(SHA256.HashData(archive));
        var work = new WorkStore(new(1, "apply", "op-1", AgentPackageCorpus.Version,
            "win-x64", hash, archive.Length, "chain-1", "op-1", 1), archive);
        var apply = new FakeApplyCommand();
        var reconcile = new FakeReconcileCommand();
        var host = new WindowsAgentMaintenanceCommandHost(new LocalUpdateEngine(work), applyCommand: apply,
            reconcileCommand: reconcile);

        var result = host.Execute("reconcile");

        Assert.Equal("ok", result.Status);
        Assert.Equal("reconciled", result.ApplyOutcome);
        Assert.Equal(0, apply.Calls);
        Assert.Equal(1, reconcile.Calls);
        Assert.Equal(0, work.RequestReads);
        Assert.Equal(0, work.ArchiveOpens);
        Assert.False(work.ExtractionCreated);
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

    [ElevatedWindowsWorkStoreFact]
    [SupportedOSPlatform("windows")]
    public void ProductionApplyWorkStoreReusesOnlyVerifiedOperationTreeAndRebuildsPartialTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "kr-maint-apply-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var protectedRoot = Path.Combine(root, "maintenance");
            new WindowsMaintenanceFenceStore(protectedRoot).EnsureLocationForJournal();
            var workRoot = Path.Combine(protectedRoot, "work");
            new DirectoryInfo(workRoot).Create(WindowsMaintenanceWorkStore.CreatePrivateDirectorySecurity());
            var archive = AgentPackageCorpus.Valid(RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.Arm64 => "win-arm64",
                _ => throw new PlatformNotSupportedException("The apply work-store test requires x64 or ARM64.")
            });
            var expected = PackageVerifier.ValidateArchive(new MemoryStream(archive), AgentPackageCorpus.Version);
            var request = new MaintenanceCommandRequest(1, "apply", "operation-work-store", expected.Version,
                expected.TargetRuntime, expected.ArchiveHash, archive.LongLength, "chain-work-store",
                "operation-work-store", 1);
            var store = new WindowsMaintenanceWorkStore(workRoot);

            var created = store.PrepareApplyExtractionRoot(request, expected);

            Assert.False(created.IsVerifiedReuse);
            Assert.NotEqual(store.ExtractionRoot, created.Path);
            _ = PackageVerifier.Extract(new MemoryStream(archive), created.Path, expected);
            var reused = store.PrepareApplyExtractionRoot(request, expected);
            Assert.True(reused.IsVerifiedReuse);
            Assert.Equal(created.Path, reused.Path);

            File.Delete(Path.Combine(reused.Path, "Agent.exe"));
            var rebuilt = store.PrepareApplyExtractionRoot(request, expected);

            Assert.False(rebuilt.IsVerifiedReuse);
            Assert.Equal(created.Path, rebuilt.Path);
            Assert.Empty(Directory.EnumerateFileSystemEntries(rebuilt.Path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [ElevatedWindowsWorkStoreFact]
    [SupportedOSPlatform("windows")]
    public void ProductionApplyWorkStoreRejectsAnOperationRootReparsePoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "kr-maint-apply-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var linkPath = string.Empty;
        try
        {
            var protectedRoot = Path.Combine(root, "maintenance");
            new WindowsMaintenanceFenceStore(protectedRoot).EnsureLocationForJournal();
            var workRoot = Path.Combine(protectedRoot, "work");
            new DirectoryInfo(workRoot).Create(WindowsMaintenanceWorkStore.CreatePrivateDirectorySecurity());
            var archive = AgentPackageCorpus.Valid(RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.Arm64 => "win-arm64",
                _ => throw new PlatformNotSupportedException("The apply work-store test requires x64 or ARM64.")
            });
            var expected = PackageVerifier.ValidateArchive(new MemoryStream(archive), AgentPackageCorpus.Version);
            var request = new MaintenanceCommandRequest(1, "apply", "operation-link-test", expected.Version,
                expected.TargetRuntime, expected.ArchiveHash, archive.LongLength, "chain-link-test",
                "operation-link-test", 1);
            var store = new WindowsMaintenanceWorkStore(workRoot);
            var prepared = store.PrepareApplyExtractionRoot(request, expected);
            Directory.Delete(prepared.Path);
            var external = Path.Combine(root, "external");
            Directory.CreateDirectory(external);
            var marker = Path.Combine(external, "do-not-delete.txt");
            File.WriteAllText(marker, "outside the operation-bound work tree");
            linkPath = prepared.Path;
            Directory.CreateSymbolicLink(linkPath, external);

            Assert.Throws<UnauthorizedAccessException>(() =>
            {
                _ = store.PrepareApplyExtractionRoot(request, expected);
            });
            Assert.Equal("outside the operation-bound work tree", File.ReadAllText(marker));
        }
        finally
        {
            if (linkPath.Length > 0 && Directory.Exists(linkPath))
                Directory.Delete(linkPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ApplyExtractionCleanupDeletesPartialFilesWithoutSharingViolation()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The production apply cleanup requires Windows file handles.");
        var root = Path.Combine(Path.GetTempPath(), "kr-maint-apply-cleanup-" + Guid.NewGuid().ToString("N"));
        var workRoot = Path.Combine(root, "work");
        var partialTree = Path.Combine(workRoot, "apply-target-partial");
        Directory.CreateDirectory(Path.Combine(partialTree, "nested"));
        try
        {
            File.WriteAllText(Path.Combine(partialTree, "root.bin"), "partial root file");
            File.WriteAllText(Path.Combine(partialTree, "nested", "nested.bin"), "partial nested file");
            var store = new WindowsMaintenanceWorkStore(workRoot);
            var cleanup = typeof(WindowsMaintenanceWorkStore).GetMethod(
                "DeleteSafeApplyExtractionTree", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(cleanup);

            var error = Record.Exception(() => cleanup.Invoke(store, [partialTree]));

            Assert.Null(error);
            Assert.False(Directory.Exists(partialTree));
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
