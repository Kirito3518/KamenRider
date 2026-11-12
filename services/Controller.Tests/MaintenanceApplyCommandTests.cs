using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using KamenRaider.AgentMaintenance;
using KamenRaider.AgentPackage;
using Controller.Tests.Fixtures;

namespace Controller.Tests;

public sealed class MaintenanceApplyCommandTests
{
    private sealed class WorkStore : IMaintenanceWorkStore
    {
        public int RequestReadCount { get; private set; }
        public int ArchiveOpenCount { get; private set; }
        public bool ExtractionCreated { get; private set; }
        public string ExtractionRoot { get; } = Path.Combine(Path.GetTempPath(), "kr-maintenance-command-" + Guid.NewGuid().ToString("N"));

        public MaintenanceCommandRequest ReadRequest(string expectedCommand)
        {
            RequestReadCount++;
            return new(1, "apply", "operation-1", "2.3.4", "win-x64", new string('a', 64), 1,
                "chain-1", "operation-1", 1);
        }

        public Stream OpenArchiveRead()
        {
            ArchiveOpenCount++;
            return new MemoryStream([0x50, 0x4b, 0x03, 0x04], writable: false);
        }

        public void CreateExtractionRoot()
        {
            ExtractionCreated = true;
            Directory.CreateDirectory(ExtractionRoot);
        }
    }

    [Fact]
    public void ApplyCommandReportsUnqualifiedLegacyProcessExclusionBeforeReadingArchiveBytes()
    {
        var workStore = new WorkStore();
        var engine = new LocalUpdateEngine(workStore);
        var host = new WindowsAgentMaintenanceCommandHost(engine,
            applyCommand: MaintenanceApplyCommand.CreateFailClosed(engine));

        var result = host.Execute("apply");

        Assert.Equal("unavailable", result.Status);
        Assert.Equal("legacyProcessExclusionUnqualified", result.Category);
        Assert.Equal(0, workStore.RequestReadCount);
        Assert.Equal(0, workStore.ArchiveOpenCount);
        Assert.False(workStore.ExtractionCreated);
    }
}

public sealed partial class InstalledAgentSourceInspectorTests
{
    private sealed class ApplyCommandWorkStore(
        MaintenanceCommandRequest request,
        byte[] archive,
        string extractionRoot,
        List<string> events) : IMaintenanceWorkStore, IMaintenanceApplyWorkStore
    {
        public int ArchiveOpenCount { get; private set; }
        public int ExtractionRootCreateCount { get; private set; }
        public int ApplyExtractionRootCreateCount { get; private set; }
        public int ApplyExtractionRootReuseCount { get; private set; }
        public string ExtractionRoot { get; } = extractionRoot;
        public string ApplyExtractionRoot { get; } = Path.Combine(Path.GetDirectoryName(extractionRoot)!,
            "apply-target-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
                request.ChainId, request.HeadOperationId,
                request.Generation!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), request.OperationId,
                request.TargetVersion, request.TargetRuntime, request.TargetPackageSha256.ToUpperInvariant())))));

        public MaintenanceCommandRequest ReadRequest(string expectedCommand)
        {
            Assert.Contains(expectedCommand, new[] { "apply", "extract" });
            events.Add("request-read");
            return request with
            {
                Command = expectedCommand,
                ChainId = expectedCommand == "apply" ? request.ChainId : null,
                HeadOperationId = expectedCommand == "apply" ? request.HeadOperationId : null,
                Generation = expectedCommand == "apply" ? request.Generation : null
            };
        }

        public Stream OpenArchiveRead()
        {
            ArchiveOpenCount++;
            events.Add("archive-open");
            return new MemoryStream(archive, writable: false);
        }

        public void CreateExtractionRoot()
        {
            Assert.False(Directory.Exists(ExtractionRoot));
            Directory.CreateDirectory(ExtractionRoot);
            ExtractionRootCreateCount++;
            events.Add("extraction-root-created");
        }

        public MaintenanceApplyExtractionRoot PrepareApplyExtractionRoot(
            MaintenanceCommandRequest applyRequest,
            PackageInventory expectedInventory)
        {
            Assert.Equal("apply", applyRequest.Command);
            Assert.Equal(request.OperationId, applyRequest.OperationId);
            if (Directory.Exists(ApplyExtractionRoot))
            {
                Assert.Equal(FileAttributes.Directory,
                    File.GetAttributes(ApplyExtractionRoot) & (FileAttributes.Directory | FileAttributes.ReparsePoint));
                try
                {
                    _ = PackageVerifier.ValidateExtracted(ApplyExtractionRoot, expectedInventory);
                    ApplyExtractionRootReuseCount++;
                    events.Add("apply-extraction-reused");
                    return new(ApplyExtractionRoot, IsVerifiedReuse: true);
                }
                catch (PackageValidationException)
                {
                    AssertSafeApplyExtractionTree(ApplyExtractionRoot);
                    Directory.Delete(ApplyExtractionRoot, recursive: true);
                }
            }
            else if (File.Exists(ApplyExtractionRoot))
            {
                throw new UnauthorizedAccessException("The operation-bound extraction path is not a directory.");
            }

            Directory.CreateDirectory(ApplyExtractionRoot);
            ApplyExtractionRootCreateCount++;
            events.Add("apply-extraction-root-created");
            return new(ApplyExtractionRoot, IsVerifiedReuse: false);
        }

        private static void AssertSafeApplyExtractionTree(string path)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                var attributes = File.GetAttributes(entry);
                Assert.Equal(0, (int)(attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)));
                if ((attributes & FileAttributes.Directory) != 0)
                    AssertSafeApplyExtractionTree(entry);
            }
        }
    }

    private sealed class QualifiedExclusionProvider : IMaintenanceApplyProcessExclusionProvider
    {
        private readonly List<string> events;

        public QualifiedExclusionProvider(List<string> events)
        {
            this.events = events;
            Lease = new QualifiedExclusionLease(events);
        }

        public QualifiedExclusionLease Lease { get; }

        public IMaintenanceApplyProcessExclusionLease? Acquire()
        {
            events.Add("exclusion-acquired");
            return Lease;
        }
    }

    private sealed class QualifiedExclusionLease(List<string> events) : IMaintenanceApplyProcessExclusionLease
    {
        public bool IsHeld { get; private set; } = true;

        public void VerifyContinuouslyHeld(string operationId)
        {
            Assert.True(IsHeld, "The qualified exclusion must remain held through apply.");
            Assert.Equal("operation-1", operationId);
            events.Add("exclusion-verified");
        }

        public void Dispose()
        {
            IsHeld = false;
            events.Add("exclusion-released");
        }
    }

    private sealed class ApplyCommandLeaseAuthorization(
        List<string> events,
        QualifiedExclusionLease exclusion,
        Func<bool> isQuiesced,
        bool acceptAnyRequestBinding = false) : IMaintenanceApplyLeaseAuthorization
    {
        public void VerifyRequestBinding(MaintenanceCommandRequest request)
        {
            if (!acceptAnyRequestBinding)
            {
                Assert.Equal("operation-1", request.OperationId);
                Assert.Equal("chain-1", request.ChainId);
                Assert.Equal("operation-1", request.HeadOperationId);
                Assert.Equal(1, request.Generation);
            }
            Assert.True(exclusion.IsHeld);
            events.Add("lease-binding-verified");
        }

        public void VerifyHeldAndQuiesced(string operationId)
        {
            Assert.Equal("operation-1", operationId);
            Assert.True(exclusion.IsHeld);
            Assert.True(isQuiesced(), "The maintenance lease must be held while services are quiesced.");
            events.Add("held-lease-quiesced-verified");
        }

        public void VerifyHeld(string operationId)
        {
            Assert.Equal("operation-1", operationId);
            Assert.True(exclusion.IsHeld);
            events.Add("held-lease-verified");
        }
    }

    private sealed class TrustedSunshinePathProvider(string path, List<string> events) : IMaintenanceTrustedSunshinePathProvider
    {
        public string GetTrustedSunshineExecutablePath(MaintenanceCommandRequest request)
        {
            Assert.Equal("operation-1", request.OperationId);
            events.Add("trusted-sunshine-resolved");
            return path;
        }
    }

    private sealed class ApplyCommandOperationFactory(
        string installDirectory,
        MaintenanceJournal journal,
        MaintenanceBackupOperation backupOperation,
        IMaintenanceApplyFileSystem fileSystem,
        IMaintenanceApplyServiceLifecycle services,
        string expectedSunshinePath,
        string expectedExtractedTargetDirectory,
        List<string> events) : IMaintenanceApplyOperationFactory
    {
        public MaintenanceApplyOperation Create(
            MaintenanceCommandRequest request,
            string extractedTargetDirectory,
            string trustedSunshineExecutablePath,
            IMaintenanceApplyAuthorization authorization)
        {
            Assert.Equal("operation-1", request.OperationId);
            Assert.Equal(Path.GetFullPath(expectedExtractedTargetDirectory), Path.GetFullPath(extractedTargetDirectory));
            Assert.Equal(Path.GetFullPath(expectedSunshinePath), trustedSunshineExecutablePath);
            events.Add("apply-operation-created");
            return new(installDirectory, extractedTargetDirectory, journal, backupOperation,
                authorization, fileSystem, services);
        }
    }

    [Fact]
    public async Task ApplyCommandRunsVerifiedArchiveThroughHeldExclusionAndNeverExecutesZipScripts()
    {
        var sentinel = Path.Combine(root, "zip-script-executed.txt");
        var nativeRuntime = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("The apply command test requires x64 or ARM64.")
        };
        var entries = AgentPackageCorpus.Entries(nativeRuntime);
        var scriptIndex = entries.FindIndex(entry => entry.Name == "Update-Agent.ps1");
        entries[scriptIndex] = ("Update-Agent.ps1", AgentPackageCorpus.Utf8(
            "Set-Content -LiteralPath '" + sentinel + "' -Value executed"));
        var archive = AgentPackageCorpus.Build(entries);
        var setup = await PrepareApplySetupAsync(archive);
        var events = new List<string>();
        var services = new ApplyServices(events);
        setup.BackupAuthorization.IsQuiesced = () => !services.IsRunning;
        var extractionRoot = Path.Combine(root, "apply-target-" + Guid.NewGuid().ToString("N"));
        var packageHash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var request = new MaintenanceCommandRequest(1, "apply", "operation-1", AgentPackageCorpus.Version,
            nativeRuntime, packageHash, archive.LongLength, "chain-1", "operation-1", 1);
        var workStore = new ApplyCommandWorkStore(request, archive, extractionRoot, events);
        var engine = new LocalUpdateEngine(workStore);
        var exclusionProvider = new QualifiedExclusionProvider(events);
        var leaseAuthorization = new ApplyCommandLeaseAuthorization(events, exclusionProvider.Lease, () => !services.IsRunning);
        var sunshinePath = Path.Combine(root, "Sunshine", "sunshine.exe");
        var operationFactory = new ApplyCommandOperationFactory(sourceRoot, setup.Journal,
            setup.BackupOperation, new ApplyFileSystem(events), services, sunshinePath,
            workStore.ApplyExtractionRoot, events);
        var command = new MaintenanceApplyCommand(engine, exclusionProvider, leaseAuthorization,
            new TrustedSunshinePathProvider(sunshinePath, events), operationFactory, setup.JournalStore);
        var host = new WindowsAgentMaintenanceCommandHost(engine, applyCommand: command);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = AgentMaintenanceCli.Run(["apply"], host, output, error);
        using var result = JsonDocument.Parse(output.ToString());

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Equal("ok", result.RootElement.GetProperty("status").GetString());
        Assert.Equal("applied", result.RootElement.GetProperty("applyOutcome").GetString());
        Assert.Equal(AgentPackageCorpus.Version, result.RootElement.GetProperty("version").GetString());
        Assert.Equal(1, workStore.ArchiveOpenCount);
        Assert.Equal(0, workStore.ExtractionRootCreateCount);
        Assert.Equal(1, workStore.ApplyExtractionRootCreateCount);
        Assert.True(events.IndexOf("exclusion-acquired") < events.IndexOf("request-read"));
        Assert.True(events.IndexOf("exclusion-acquired") < events.IndexOf("archive-open"));
        Assert.True(events.IndexOf("trusted-sunshine-resolved") < events.IndexOf("archive-open"));
        Assert.True(events.IndexOf("apply-operation-created") < events.IndexOf("exclusion-released"));
        Assert.Equal("servicesStarted", setup.Journal.Read()!.Operations.Single().Phase);
        Assert.True(services.IsRunning);
        Assert.False(File.Exists(sentinel));
        Assert.False(exclusionProvider.Lease.IsHeld);
    }

    [Fact]
    public async Task ApplyCommandReconcilesARealPriorExtractThroughTheCli()
    {
        var nativeRuntime = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("The apply command test requires x64 or ARM64.")
        };
        var archive = AgentPackageCorpus.Valid(nativeRuntime);
        var setup = await PrepareApplySetupAsync(archive);
        var events = new List<string>();
        var services = new ApplyServices(events);
        setup.BackupAuthorization.IsQuiesced = () => !services.IsRunning;
        var extractionRoot = Path.Combine(root, "apply-prior-extract-" + Guid.NewGuid().ToString("N"));
        var request = new MaintenanceCommandRequest(1, "apply", "operation-1", AgentPackageCorpus.Version,
            nativeRuntime, Convert.ToHexStringLower(SHA256.HashData(archive)), archive.LongLength,
            "chain-1", "operation-1", 1);
        var workStore = new ApplyCommandWorkStore(request, archive, extractionRoot, events);
        var engine = new LocalUpdateEngine(workStore);

        var priorExtract = engine.Extract();

        Assert.Equal(AgentPackageCorpus.Version, priorExtract.Version);
        Assert.True(Directory.Exists(extractionRoot));

        var exclusionProvider = new QualifiedExclusionProvider(events);
        var leaseAuthorization = new ApplyCommandLeaseAuthorization(events, exclusionProvider.Lease, () => !services.IsRunning);
        var sunshinePath = Path.Combine(root, "Sunshine", "sunshine.exe");
        var operationFactory = new ApplyCommandOperationFactory(sourceRoot, setup.Journal,
            setup.BackupOperation, new ApplyFileSystem(events), services, sunshinePath,
            workStore.ApplyExtractionRoot, events);
        var command = new MaintenanceApplyCommand(engine, exclusionProvider, leaseAuthorization,
            new TrustedSunshinePathProvider(sunshinePath, events), operationFactory, setup.JournalStore);
        var host = new WindowsAgentMaintenanceCommandHost(engine, applyCommand: command);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = AgentMaintenanceCli.Run(["apply"], host, output, error);
        using var result = JsonDocument.Parse(output.ToString());

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Equal("applied", result.RootElement.GetProperty("applyOutcome").GetString());
        Assert.Equal("servicesStarted", setup.Journal.Read()!.Operations.Single().Phase);
        Assert.True(Directory.Exists(extractionRoot), "The prior extract remains available for the extract command.");
        Assert.Equal(2, workStore.ArchiveOpenCount);
        Assert.Equal(1, workStore.ExtractionRootCreateCount);
        Assert.Equal(1, workStore.ApplyExtractionRootCreateCount);
    }

    [Fact]
    public async Task ApplyCliRetryReusesItsOperationTargetAndReconcilesAfterServiceStartInterruption()
    {
        var nativeRuntime = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("The apply command test requires x64 or ARM64.")
        };
        var archive = AgentPackageCorpus.Valid(nativeRuntime);
        var setup = await PrepareApplySetupAsync(archive);
        setup.JournalStore.FailOnPhase = "servicesStarted";
        var events = new List<string>();
        var services = new ApplyServices(events);
        setup.BackupAuthorization.IsQuiesced = () => !services.IsRunning;
        var extractionRoot = Path.Combine(root, "apply-interrupted-" + Guid.NewGuid().ToString("N"));
        var request = new MaintenanceCommandRequest(1, "apply", "operation-1", AgentPackageCorpus.Version,
            nativeRuntime, Convert.ToHexStringLower(SHA256.HashData(archive)), archive.LongLength,
            "chain-1", "operation-1", 1);
        var workStore = new ApplyCommandWorkStore(request, archive, extractionRoot, events);
        var engine = new LocalUpdateEngine(workStore);
        var sunshinePath = Path.Combine(root, "Sunshine", "sunshine.exe");

        var firstExclusion = new QualifiedExclusionProvider(events);
        var firstCommand = new MaintenanceApplyCommand(engine, firstExclusion,
            new ApplyCommandLeaseAuthorization(events, firstExclusion.Lease, () => !services.IsRunning),
            new TrustedSunshinePathProvider(sunshinePath, events),
            new ApplyCommandOperationFactory(sourceRoot, setup.Journal, setup.BackupOperation,
                new ApplyFileSystem(events), services, sunshinePath, workStore.ApplyExtractionRoot, events),
            setup.JournalStore);
        var firstHost = new WindowsAgentMaintenanceCommandHost(engine, applyCommand: firstCommand);
        using var firstOutput = new StringWriter();
        using var firstError = new StringWriter();

        var firstExitCode = AgentMaintenanceCli.Run(["apply"], firstHost, firstOutput, firstError);
        using var firstResult = JsonDocument.Parse(firstOutput.ToString());

        Assert.Equal(5, firstExitCode);
        Assert.Equal("localOperation", firstResult.RootElement.GetProperty("category").GetString());
        Assert.Equal("targetPlaced", setup.Journal.Read()!.Operations.Single().Phase);
        Assert.True(services.IsRunning, "The service start completed before the journal write was interrupted.");
        Assert.True(Directory.Exists(workStore.ApplyExtractionRoot));
        Assert.False(firstExclusion.Lease.IsHeld);

        var retryExclusion = new QualifiedExclusionProvider(events);
        var retryCommand = new MaintenanceApplyCommand(engine, retryExclusion,
            new ApplyCommandLeaseAuthorization(events, retryExclusion.Lease, () => !services.IsRunning),
            new TrustedSunshinePathProvider(sunshinePath, events),
            new ApplyCommandOperationFactory(sourceRoot, setup.Journal, setup.BackupOperation,
                new ApplyFileSystem(events), services, sunshinePath, workStore.ApplyExtractionRoot, events),
            setup.JournalStore);
        var retryHost = new WindowsAgentMaintenanceCommandHost(engine, applyCommand: retryCommand);
        using var retryOutput = new StringWriter();
        using var retryError = new StringWriter();

        var retryExitCode = AgentMaintenanceCli.Run(["apply"], retryHost, retryOutput, retryError);
        using var retryResult = JsonDocument.Parse(retryOutput.ToString());

        Assert.Equal(0, retryExitCode);
        Assert.Empty(retryError.ToString());
        Assert.Equal("applied", retryResult.RootElement.GetProperty("applyOutcome").GetString());
        Assert.Equal("servicesStarted", setup.Journal.Read()!.Operations.Single().Phase);
        Assert.Equal(1, services.StopChecks);
        Assert.True(services.IsRunning);
        Assert.Equal(2, workStore.ArchiveOpenCount);
        Assert.Equal(1, workStore.ApplyExtractionRootCreateCount);
        Assert.Equal(1, workStore.ApplyExtractionRootReuseCount);
        Assert.False(retryExclusion.Lease.IsHeld);
    }

    [Theory]
    [InlineData("chain")]
    [InlineData("head")]
    [InlineData("generation")]
    public async Task ApplyCliRejectsRequestJournalIdentityMismatchBeforeArchiveOrExtraction(string mismatch)
    {
        var nativeRuntime = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("The apply command test requires x64 or ARM64.")
        };
        var archive = AgentPackageCorpus.Valid(nativeRuntime);
        var setup = await PrepareApplySetupAsync(archive);
        var events = new List<string>();
        var services = new ApplyServices(events);
        setup.BackupAuthorization.IsQuiesced = () => !services.IsRunning;
        var request = new MaintenanceCommandRequest(1, "apply", "operation-1", AgentPackageCorpus.Version,
            nativeRuntime, Convert.ToHexStringLower(SHA256.HashData(archive)), archive.LongLength,
            "chain-1", "operation-1", 1);
        request = mismatch switch
        {
            "chain" => request with { ChainId = "chain-other" },
            "head" => request with { HeadOperationId = "operation-other" },
            "generation" => request with { Generation = 2 },
            _ => throw new InvalidOperationException("Unknown journal mismatch case.")
        };
        var workStore = new ApplyCommandWorkStore(request, archive,
            Path.Combine(root, "apply-mismatch-" + Guid.NewGuid().ToString("N")), events);
        var engine = new LocalUpdateEngine(workStore);
        var exclusionProvider = new QualifiedExclusionProvider(events);
        var sunshinePath = Path.Combine(root, "Sunshine", "sunshine.exe");
        var command = new MaintenanceApplyCommand(engine, exclusionProvider,
            new ApplyCommandLeaseAuthorization(events, exclusionProvider.Lease, () => !services.IsRunning,
                acceptAnyRequestBinding: true),
            new TrustedSunshinePathProvider(sunshinePath, events),
            new ApplyCommandOperationFactory(sourceRoot, setup.Journal, setup.BackupOperation,
                new ApplyFileSystem(events), services, sunshinePath, workStore.ApplyExtractionRoot, events),
            setup.JournalStore);
        var host = new WindowsAgentMaintenanceCommandHost(engine, applyCommand: command);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = AgentMaintenanceCli.Run(["apply"], host, output, error);
        using var result = JsonDocument.Parse(output.ToString());

        Assert.Equal(4, exitCode);
        Assert.Equal("rejected", result.RootElement.GetProperty("status").GetString());
        Assert.Equal(mismatch == "chain" ? "request" : "precondition",
            result.RootElement.GetProperty("category").GetString());
        Assert.Equal(0, workStore.ArchiveOpenCount);
        Assert.Equal(0, workStore.ExtractionRootCreateCount);
        Assert.Equal(0, workStore.ApplyExtractionRootCreateCount);
        Assert.DoesNotContain("apply-operation-created", events);
        Assert.Equal("sourceBound", setup.Journal.Read()!.Operations.Single().Phase);
        Assert.False(services.IsRunning);
    }

    [Fact]
    public async Task ApplyCliCompletedServicesStartedRetryDoesNotRequireServicesToBeQuiesced()
    {
        var nativeRuntime = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("The apply command test requires x64 or ARM64.")
        };
        var archive = AgentPackageCorpus.Valid(nativeRuntime);
        var setup = await PrepareApplySetupAsync(archive);
        var events = new List<string>();
        var services = new ApplyServices(events);
        setup.BackupAuthorization.IsQuiesced = () => !services.IsRunning;
        var request = new MaintenanceCommandRequest(1, "apply", "operation-1", AgentPackageCorpus.Version,
            nativeRuntime, Convert.ToHexStringLower(SHA256.HashData(archive)), archive.LongLength,
            "chain-1", "operation-1", 1);
        var workStore = new ApplyCommandWorkStore(request, archive,
            Path.Combine(root, "apply-completed-retry-" + Guid.NewGuid().ToString("N")), events);
        var engine = new LocalUpdateEngine(workStore);
        var sunshinePath = Path.Combine(root, "Sunshine", "sunshine.exe");

        var firstExclusion = new QualifiedExclusionProvider(events);
        var firstCommand = new MaintenanceApplyCommand(engine, firstExclusion,
            new ApplyCommandLeaseAuthorization(events, firstExclusion.Lease, () => !services.IsRunning),
            new TrustedSunshinePathProvider(sunshinePath, events),
            new ApplyCommandOperationFactory(sourceRoot, setup.Journal, setup.BackupOperation,
                new ApplyFileSystem(events), services, sunshinePath, workStore.ApplyExtractionRoot, events),
            setup.JournalStore);
        var firstHost = new WindowsAgentMaintenanceCommandHost(engine, applyCommand: firstCommand);
        using var firstOutput = new StringWriter();
        using var firstError = new StringWriter();

        var firstExitCode = AgentMaintenanceCli.Run(["apply"], firstHost, firstOutput, firstError);

        Assert.Equal(0, firstExitCode);
        Assert.Equal("servicesStarted", setup.Journal.Read()!.Operations.Single().Phase);
        Assert.True(services.IsRunning);
        Assert.Equal(0, services.StopChecks);

        var retryExclusion = new QualifiedExclusionProvider(events);
        var retryCommand = new MaintenanceApplyCommand(engine, retryExclusion,
            new ApplyCommandLeaseAuthorization(events, retryExclusion.Lease, () => !services.IsRunning),
            new TrustedSunshinePathProvider(sunshinePath, events),
            new ApplyCommandOperationFactory(sourceRoot, setup.Journal, setup.BackupOperation,
                new ApplyFileSystem(events), services, sunshinePath, workStore.ApplyExtractionRoot, events),
            setup.JournalStore);
        var retryHost = new WindowsAgentMaintenanceCommandHost(engine, applyCommand: retryCommand);
        using var retryOutput = new StringWriter();
        using var retryError = new StringWriter();

        var retryExitCode = AgentMaintenanceCli.Run(["apply"], retryHost, retryOutput, retryError);
        using var retryResult = JsonDocument.Parse(retryOutput.ToString());

        Assert.Equal(0, retryExitCode);
        Assert.Empty(retryError.ToString());
        Assert.Equal("applied", retryResult.RootElement.GetProperty("applyOutcome").GetString());
        Assert.Equal("servicesStarted", setup.Journal.Read()!.Operations.Single().Phase);
        Assert.True(services.IsRunning);
        Assert.Equal(0, services.StopChecks);
        Assert.Equal(2, workStore.ArchiveOpenCount);
        Assert.Equal(1, workStore.ApplyExtractionRootCreateCount);
        Assert.Equal(1, workStore.ApplyExtractionRootReuseCount);
        Assert.False(retryExclusion.Lease.IsHeld);
    }

    [Fact]
    public async Task ApplyCliRejectsSunshineResolverOutputWithTheWrongExecutableName()
    {
        var nativeRuntime = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("The apply command test requires x64 or ARM64.")
        };
        var archive = AgentPackageCorpus.Valid(nativeRuntime);
        var setup = await PrepareApplySetupAsync(archive);
        var events = new List<string>();
        var services = new ApplyServices(events);
        var request = new MaintenanceCommandRequest(1, "apply", "operation-1", AgentPackageCorpus.Version,
            nativeRuntime, Convert.ToHexStringLower(SHA256.HashData(archive)), archive.LongLength,
            "chain-1", "operation-1", 1);
        var workStore = new ApplyCommandWorkStore(request, archive,
            Path.Combine(root, "apply-untrusted-sunshine-" + Guid.NewGuid().ToString("N")), events);
        var engine = new LocalUpdateEngine(workStore);
        var exclusionProvider = new QualifiedExclusionProvider(events);
        var untrustedPath = Path.Combine(root, "untrusted.exe");
        var command = new MaintenanceApplyCommand(engine, exclusionProvider,
            new ApplyCommandLeaseAuthorization(events, exclusionProvider.Lease, () => !services.IsRunning),
            new TrustedSunshinePathProvider(untrustedPath, events),
            new ApplyCommandOperationFactory(sourceRoot, setup.Journal, setup.BackupOperation,
                new ApplyFileSystem(events), services, untrustedPath, workStore.ApplyExtractionRoot, events),
            setup.JournalStore);
        var host = new WindowsAgentMaintenanceCommandHost(engine, applyCommand: command);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = AgentMaintenanceCli.Run(["apply"], host, output, error);
        using var result = JsonDocument.Parse(output.ToString());

        Assert.Equal(4, exitCode);
        Assert.Equal("request", result.RootElement.GetProperty("category").GetString());
        Assert.Equal(0, workStore.ArchiveOpenCount);
        Assert.Equal(0, workStore.ApplyExtractionRootCreateCount);
        Assert.DoesNotContain("apply-operation-created", events);
        Assert.False(services.IsRunning);
        Assert.False(exclusionProvider.Lease.IsHeld);
    }
}
