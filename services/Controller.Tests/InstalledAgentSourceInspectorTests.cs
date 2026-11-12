using System.Reflection;
using System.Runtime.InteropServices;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KamenRaider.Contracts;
using System.Text.Json.Nodes;
using KamenRaider.AgentMaintenance;
using KamenRaider.AgentPackage;
using Controller.Tests.Fixtures;

namespace Controller.Tests;

public sealed partial class InstalledAgentSourceInspectorTests : IDisposable
{
    private sealed class BackupSecurity(string sourceRoot, string backupRoot, bool rejectSource = false) : IMaintenanceBackupSecurity
    {
        public void EnsureSourceRoot(string path)
        {
            if (rejectSource)
                throw new UnauthorizedAccessException("simulated unsafe source ACL");
            Assert.Equal(Path.GetFullPath(sourceRoot), Path.GetFullPath(path));
            Assert.True(Directory.Exists(path));
        }

        public void EnsureSourceDirectory(string path) => Assert.True(Directory.Exists(path));

        public void EnsureBackupDirectory(string path, bool create)
        {
            Assert.Equal(Path.GetFullPath(backupRoot), Path.GetFullPath(path));
            if (create) Directory.CreateDirectory(path);
            Assert.True(Directory.Exists(path));
        }

        public void EnsurePrivateDirectory(string path, bool create)
        {
            if (create) Directory.CreateDirectory(path);
            Assert.True(Directory.Exists(path));
        }

        public Stream OpenSourceFile(string path) => File.OpenRead(path);
        public Stream OpenPrivateFile(string path) => File.OpenRead(path);
        public Stream CreateBackupFile(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        public void EnsurePrivateFile(string path) => Assert.True(File.Exists(path));
        public void FlushDirectory(string path) => Assert.True(Directory.Exists(path));
        public void PublishDirectory(string stagingDirectory, string publishedDirectory) => Directory.Move(stagingDirectory, publishedDirectory);
    }

    private sealed class Authorization : IMaintenanceBackupAuthorization
    {
        public int Checks { get; private set; }
        public Action<int>? OnCheck { get; set; }
        public Func<bool>? IsQuiesced { get; set; }

        public void VerifyHeldAndQuiesced(string operationId)
        {
            Assert.Equal("operation-1", operationId);
            Assert.True(IsQuiesced?.Invoke() ?? true, "Backup verification requires services to be stopped.");
            Checks++;
            OnCheck?.Invoke(Checks);
        }
    }

    private sealed class MemoryJournalStore : IMaintenanceJournalStore
    {
        public MaintenanceJournalSnapshot? Snapshot { get; private set; }
        public bool FailNextWrite { get; set; }
        public string? FailOnPhase { get; set; }
        public MaintenanceJournalSnapshot? Read(string chainId) => Snapshot;

        public void WriteAtomic(string chainId, MaintenanceJournalSnapshot snapshot)
        {
            if (snapshot.Operations.Single().Phase == FailOnPhase)
            {
                FailOnPhase = null;
                throw new IOException("simulated phase journal flush failure");
            }
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("simulated journal flush failure");
            }
            Snapshot = snapshot;
        }
    }

    private sealed class UnusedWorkStore : IMaintenanceWorkStore
    {
        public MaintenanceCommandRequest ReadRequest(string expectedCommand) =>
            throw new InvalidOperationException("The read-only inspect command must not read an archive request.");
        public Stream OpenArchiveRead() =>
            throw new InvalidOperationException("The read-only inspect command must not open an archive.");
        public string ExtractionRoot => throw new InvalidOperationException("The read-only inspect command has no extraction root.");
        public void CreateExtractionRoot() =>
            throw new InvalidOperationException("The read-only inspect command must not create an extraction tree.");
    }

    private sealed record IdentitySource(string MachineId, string PrivateKey) : ITrustedIdentitySource
    {
        public TrustedIdentityDocument ReadExisting() => new(MachineId, PrivateKey);
    }

    private sealed record BootIdentity(string BootId) : IMaintenanceBootIdentityProvider
    {
        public string ReadBootId() => BootId;
    }

    private sealed class StaticResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class SourceBindingClient(TimeProvider timeProvider) : IMaintenanceSourceBindingClient
    {
        public int ChallengeCalls { get; private set; }
        public int RenewalRequiredOnChallengeCall { get; set; }
        public int RenewalRequiredOnBindingCall { get; set; }
        public int ConflictOnBindingCall { get; set; }
        public List<AgentUpdateSourceBindingRequest> BindingRequests { get; } = [];
        public List<string> BindingBodyDigests { get; } = [];
        public List<string> ChallengeRequestIds { get; } = [];
        public bool EvidenceSignatureVerified { get; private set; }
        public AgentUpdateSourceChallengeRequest? ChallengeRequest { get; private set; }
        public AgentUpdateSourceChallengeResponse ChallengeResponse { get; set; } = new(
            Guid.NewGuid().ToString("N"), "opaque-source-challenge", "runtime-1", DateTimeOffset.UtcNow.AddMinutes(5));

        public Task<AgentUpdateSourceChallengeResponse> IssueChallengeAsync(string controllerOrigin, string machineId,
            string operationId, AgentUpdateSourceChallengeRequest request, TrustedIdentity identity,
            CancellationToken cancellationToken)
        {
            ChallengeCalls++;
            ChallengeRequest = request;
            ChallengeRequestIds.Add(request.RequestId);
            if (ChallengeCalls == RenewalRequiredOnChallengeCall)
                throw new MaintenanceSourceChallengeRenewalRequiredException();
            ChallengeResponse = new(Guid.NewGuid().ToString("N"), "opaque-source-challenge-" + ChallengeCalls,
                "runtime-" + ChallengeCalls, timeProvider.GetUtcNow().AddMinutes(5));
            return Task.FromResult(ChallengeResponse);
        }

        public Task<AgentUpdateOperationDto> BindSourceAsync(string controllerOrigin, string machineId,
            string operationId, AgentUpdateSourceBindingRequest request, TrustedIdentity identity,
            CancellationToken cancellationToken)
        {
            BindingRequests.Add(request);
            var body = JsonSerializer.SerializeToUtf8Bytes(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var bodyDigest = Convert.ToHexString(SHA256.HashData(body));
            BindingBodyDigests.Add(bodyDigest);
            var evidence = new AgentUpdateSourceEvidence(machineId, identity.KeyFingerprint, operationId,
                request.ChainId, request.HeadOperationId, request.Generation, request.TargetVersion,
                request.TargetPackageSha256, request.ChallengeId, request.ChallengeRequestId, request.RequestId,
                request.HelperBuildSha256, request.HelperRuntime, request.HelperProtocol, request.RuntimeEpoch,
                request.SourceVersion, request.BackupOwnerOperationId, request.BackupDigest,
                request.InventoryDigest, request.InstalledTreeDigest, request.NativeBootId, request.JournalPhase);
            using var publicKey = ECDsa.Create();
            publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(identity.PublicKey), out _);
            EvidenceSignatureVerified = publicKey.VerifyData(AgentUpdateSourceEvidenceCanonicalizer.CanonicalBytes(evidence),
                Convert.FromBase64String(request.EvidenceSignature), HashAlgorithmName.SHA256);
            if (BindingRequests.Count == RenewalRequiredOnBindingCall)
                throw new MaintenanceSourceChallengeRenewalRequiredException();
            if (BindingRequests.Count == ConflictOnBindingCall)
                throw new InvalidOperationException("simulated non-renewal conflict");
            return Task.FromResult(new AgentUpdateOperationDto(operationId, machineId, request.ChainId,
                request.HeadOperationId, request.Generation, true, null, request.ReleaseVersion,
                request.TargetVersion, request.TargetPackageSha256, request.HelperRuntime, 2, null,
                request.SourceVersion, bodyDigest, request.BackupOwnerOperationId, request.BackupDigest,
                "installing", timeProvider.GetUtcNow(), timeProvider.GetUtcNow()));
        }
    }

    private sealed class ApplyAuthorization(List<string> events, Func<bool> isQuiesced) : IMaintenanceApplyAuthorization
    {
        public void VerifyHeldAndQuiesced(string operationId)
        {
            Assert.True(isQuiesced(), "The apply path checked quiescence while a service was running.");
            events.Add("authorize-quiesced");
        }
        public void VerifyHeld(string operationId) => events.Add("authorize-held");
    }

    private sealed class ApplyServices(List<string> events) : IMaintenanceApplyServiceLifecycle
    {
        public string? FailedVersion { get; set; }
        public bool IsRunning { get; set; }
        public int FailStopOnCall { get; set; }
        public int StopChecks { get; private set; }

        public Task EnsureStoppedAndVerifyQuiescedAsync(string operationId, CancellationToken cancellationToken)
        {
            Assert.Equal("operation-1", operationId);
            cancellationToken.ThrowIfCancellationRequested();
            StopChecks++;
            events.Add("stop-quiesced");
            if (StopChecks == FailStopOnCall)
            {
                throw new IOException("simulated service stop failure");
            }
            IsRunning = false;
            return Task.CompletedTask;
        }

        public Task StartAndVerifyAsync(string operationId, string installDirectory, string expectedVersion, CancellationToken cancellationToken)
        {
            Assert.Equal("operation-1", operationId);
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("start:" + expectedVersion);
            IsRunning = true;
            if (expectedVersion == FailedVersion)
                throw new InvalidOperationException("simulated target service start failure");
            return Task.CompletedTask;
        }
    }

    private sealed class ApplyFileSystem(List<string> events) : IMaintenanceApplyFileSystem
    {
        public string? FailMoveSource { get; set; }
        public string? FailMoveDestination { get; set; }
        public Action<string, string>? AfterMove { get; set; }
        public bool FailTargetCopyOnce { get; set; }
        public bool FailBackupCopyOnce { get; set; }

        public void EnsureProtectedInstallParent(string installDirectory)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(installDirectory))!;
            Assert.True(Directory.Exists(parent));
            events.Add("parent-verified");
        }

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public void PrepareTargetInstallTree(
            string extractedTree,
            string destination,
            PackageInventory expected,
            MaintenanceBackupResult backup)
        {
            PackageVerifier.ValidateExtracted(extractedTree, expected);
            var config = backup.Nodes.Single(node => !node.IsDirectory && node.RelativePath == "appsettings.json");
            var installInventory = PackageVerifier.IncludePreservedAppSettings(
                PackageVerifier.CreateAgentInstallInventory(expected), config.Length, config.Sha256!);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.CreateDirectory(destination);
            foreach (var node in installInventory.Nodes.Where(node => node.IsDirectory)
                         .OrderBy(node => node.Path.Count(character => character == '/')))
                Directory.CreateDirectory(Local(destination, node.Path));
            foreach (var node in installInventory.Nodes.Where(node => !node.IsDirectory))
            {
                var source = node.Path == "appsettings.json"
                    ? Local(backup.DirectoryPath, node.Path)
                    : Local(extractedTree, "Agent/" + node.Path);
                File.Copy(source, Local(destination, node.Path));
                if (FailTargetCopyOnce)
                {
                    FailTargetCopyOnce = false;
                    throw new IOException("simulated target stage interruption after partial copy");
                }
            }
            events.Add("target-staged");
        }

        public void ValidateTargetInstallTree(string path, PackageInventory expected) =>
            PackageVerifier.ValidateExtracted(path, expected);

        public void EnsureOwnedInstallTree(string path)
        {
            Assert.True(Directory.Exists(path));
            Assert.Equal(FileAttributes.Directory, File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint));
        }

        public void VerifyBackupTree(string path, MaintenanceBackupResult backup)
        {
            var actual = Scan(path);
            Assert.Equal(backup.Nodes.OrderBy(node => node.RelativePath, StringComparer.Ordinal),
                actual.OrderBy(node => node.RelativePath, StringComparer.Ordinal));
        }

        public void PrepareBackupInstallTree(string backupPath, string destination, MaintenanceBackupResult backup)
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.CreateDirectory(destination);
            foreach (var node in backup.Nodes.Where(node => node.IsDirectory).OrderBy(node => node.RelativePath.Count(character => character == '/')))
                Directory.CreateDirectory(Local(destination, node.RelativePath));
            foreach (var node in backup.Nodes.Where(node => !node.IsDirectory))
            {
                File.Copy(Local(backupPath, node.RelativePath), Local(destination, node.RelativePath));
                if (FailBackupCopyOnce)
                {
                    FailBackupCopyOnce = false;
                    throw new IOException("simulated restore stage interruption after partial copy");
                }
            }
        }

        public void MoveDirectory(string source, string destination)
        {
            Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(source)), Path.GetDirectoryName(Path.GetFullPath(destination)), StringComparer.OrdinalIgnoreCase);
            if (string.Equals(Path.GetFullPath(source), FailMoveSource, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFullPath(destination), FailMoveDestination, StringComparison.OrdinalIgnoreCase))
                throw new IOException("simulated target placement failure");
            Directory.Move(source, destination);
            events.Add("move:" + Path.GetFileName(destination));
            AfterMove?.Invoke(source, destination);
        }

        public void FlushParent(string path) => events.Add("flush-parent");

        private static void CopyNodes(string source, string destination, IEnumerable<MaintenanceBackupNode> nodes)
        {
            Directory.CreateDirectory(destination);
            foreach (var node in nodes.Where(node => node.IsDirectory).OrderBy(node => node.RelativePath.Count(character => character == '/')))
                Directory.CreateDirectory(Local(destination, node.RelativePath));
            foreach (var node in nodes.Where(node => !node.IsDirectory))
                File.Copy(Local(source, node.RelativePath), Local(destination, node.RelativePath));
        }

        private static List<MaintenanceBackupNode> Scan(string root)
        {
            var result = new List<MaintenanceBackupNode>();
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                result.Add(new(Path.GetRelativePath(root, directory).Replace('\\', '/'), true, 0, null));
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                using var stream = File.OpenRead(file);
                result.Add(new(Path.GetRelativePath(root, file).Replace('\\', '/'), false, stream.Length,
                    Convert.ToHexStringLower(SHA256.HashData(stream))));
            }
            return result;
        }

        private static string Local(string root, string relativePath) =>
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task ApplyPublishesVerifiedTargetOnlyAfterSourceMoveAndKeepsOriginalBackup()
    {
        var setup = await PrepareApplySetupAsync();
        var originalAppSettings = File.ReadAllBytes(Path.Combine(sourceRoot, "appsettings.json"));
        var backup = setup.BackupOperation.VerifyPublishedBackup("operation-1");
        var config = backup.Nodes.Single(node => !node.IsDirectory && node.RelativePath == "appsettings.json");
        var events = new List<string>();
        var fileSystem = new ApplyFileSystem(events);
        var services = new ApplyServices(events);
        setup.BackupAuthorization.IsQuiesced = () => !services.IsRunning;
        var apply = new MaintenanceApplyOperation(sourceRoot, setup.ExtractedTarget, setup.Journal,
            setup.BackupOperation, new ApplyAuthorization(events, () => !services.IsRunning), fileSystem, services);

        var result = await apply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None);

        Assert.Equal("applied", result.Outcome);
        Assert.Equal("2.3.4", result.InstalledVersion);
        Assert.Equal("servicesStarted", setup.Journal.Read()!.Operations.Single().Phase);
        Assert.Equal(["swapIntent", "sourceMoved", "targetPlaced", "servicesStarted"],
            setup.Journal.Read()!.Operations.Single().PhaseHistory.TakeLast(4));
        var targetInventory = PackageVerifier.IncludePreservedAppSettings(
            PackageVerifier.CreateAgentInstallInventory(setup.ExpectedTarget), config.Length, config.Sha256!);
        PackageVerifier.ValidateExtracted(sourceRoot, targetInventory);
        Assert.True(File.Exists(Path.Combine(sourceRoot, "Agent.exe")));
        Assert.False(File.Exists(Path.Combine(sourceRoot, "Update-AgentPackage.ps1")));
        Assert.Equal(originalAppSettings, File.ReadAllBytes(Path.Combine(sourceRoot, "appsettings.json")));
        Assert.Equal(config.Sha256, Convert.ToHexStringLower(SHA256.HashData(originalAppSettings)));
        var unexpected = Path.Combine(sourceRoot, "unapproved-local-extra.txt");
        File.WriteAllText(unexpected, "must not be part of the target inventory");
        Assert.Throws<PackageValidationException>(() => PackageVerifier.ValidateExtracted(sourceRoot, targetInventory));
        File.Delete(unexpected);
        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(sourceRoot)!, ".Agent-prior-operation-1")));
        Assert.Equal(0, services.StopChecks);
        Assert.True(events.IndexOf("target-staged") < events.FindIndex(value => value.StartsWith("move:.Agent-prior-", StringComparison.Ordinal)));

        var repeated = await apply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None);

        Assert.Equal("applied", repeated.Outcome);
        Assert.Equal(0, services.StopChecks);
    }

    [Fact]
    public async Task ApplyRetryRebuildsOperationOwnedPartialTargetStageWithoutChangingSource()
    {
        var setup = await PrepareApplySetupAsync();
        var events = new List<string>();
        var services = new ApplyServices(events);
        var fileSystem = new ApplyFileSystem(events) { FailTargetCopyOnce = true };
        setup.BackupAuthorization.IsQuiesced = () => !services.IsRunning;
        var apply = new MaintenanceApplyOperation(sourceRoot, setup.ExtractedTarget, setup.Journal,
            setup.BackupOperation, new ApplyAuthorization(events, () => !services.IsRunning), fileSystem, services);
        var targetStage = Path.Combine(Path.GetDirectoryName(sourceRoot)!, ".Agent-stage-operation-1");

        await Assert.ThrowsAsync<IOException>(() => apply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None));

        Assert.True(Directory.Exists(targetStage));
        Assert.True(File.Exists(Path.Combine(targetStage, "Agent.deps.json")));
        Assert.False(File.Exists(Path.Combine(targetStage, "Agent.exe")));
        Assert.True(File.Exists(Path.Combine(sourceRoot, "Agent.exe")));
        Assert.Equal("sourceBound", setup.Journal.Read()!.Operations.Single().Phase);

        var result = await apply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None);

        Assert.Equal("applied", result.Outcome);
        Assert.Equal("servicesStarted", setup.Journal.Read()!.Operations.Single().Phase);
        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(sourceRoot)!, ".Agent-prior-operation-1")));
    }

    [Fact]
    public async Task ApplyKeepsMissingMachineConfigurationHeldBeforeSwap()
    {
        File.Delete(Path.Combine(sourceRoot, "appsettings.json"));
        var setup = await PrepareApplySetupAsync();
        var events = new List<string>();
        var services = new ApplyServices(events);
        setup.BackupAuthorization.IsQuiesced = () => !services.IsRunning;
        var apply = new MaintenanceApplyOperation(sourceRoot, setup.ExtractedTarget, setup.Journal,
            setup.BackupOperation, new ApplyAuthorization(events, () => !services.IsRunning),
            new ApplyFileSystem(events), services);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            apply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None));

        Assert.Equal("sourceBound", setup.Journal.Read()!.Operations.Single().Phase);
        Assert.True(File.Exists(Path.Combine(sourceRoot, "Agent.exe")));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(sourceRoot)!, ".Agent-stage-operation-1")));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(sourceRoot)!, ".Agent-prior-operation-1")));
    }

    [Fact]
    public async Task ApplyRetryRebuildsOperationOwnedPartialRestoreStageFromImmutableBackup()
    {
        var setup = await PrepareApplySetupAsync();
        var events = new List<string>();
        var services = new ApplyServices(events) { FailedVersion = setup.ExpectedTarget.Version };
        var fileSystem = new ApplyFileSystem(events) { FailBackupCopyOnce = true };
        setup.BackupAuthorization.IsQuiesced = () => !services.IsRunning;
        var backup = setup.BackupOperation.VerifyPublishedBackup("operation-1");
        var apply = new MaintenanceApplyOperation(sourceRoot, setup.ExtractedTarget, setup.Journal,
            setup.BackupOperation, new ApplyAuthorization(events, () => !services.IsRunning), fileSystem, services);
        var restoreStage = Path.Combine(Path.GetDirectoryName(sourceRoot)!, ".Agent-restore-operation-1");

        await Assert.ThrowsAsync<IOException>(() => apply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None));

        Assert.Equal("targetMovedToFailed", setup.Journal.Read()!.Operations.Single().Phase);
        Assert.True(Directory.Exists(restoreStage));
        Assert.False(Directory.Exists(sourceRoot));

        var result = await apply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None);

        Assert.Equal("rolledBack", result.Outcome);
        Assert.Equal("rollbackServicesStarted", setup.Journal.Read()!.Operations.Single().Phase);
        fileSystem.VerifyBackupTree(sourceRoot, backup);
        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(sourceRoot)!, ".Agent-failed-operation-1")));
    }

    [Fact]
    public async Task ApplyRetryRecoversWhenJournalFlushFailsAfterOriginalTreeMove()
    {
        var setup = await PrepareApplySetupAsync();
        var backup = setup.BackupOperation.VerifyPublishedBackup("operation-1");
        setup.JournalStore.FailOnPhase = "sourceMoved";
        var events = new List<string>();
        var services = new ApplyServices(events);
        setup.BackupAuthorization.IsQuiesced = () => !services.IsRunning;
        var apply = new MaintenanceApplyOperation(sourceRoot, setup.ExtractedTarget, setup.Journal,
            setup.BackupOperation, new ApplyAuthorization(events, () => !services.IsRunning), new ApplyFileSystem(events), services);
        var prior = Path.Combine(Path.GetDirectoryName(sourceRoot)!, ".Agent-prior-operation-1");

        await Assert.ThrowsAsync<IOException>(() => apply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None));

        Assert.Equal("swapIntent", setup.Journal.Read()!.Operations.Single().Phase);
        Assert.False(Directory.Exists(sourceRoot));
        Assert.True(Directory.Exists(prior));

        var result = await apply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None);

        Assert.Equal("applied", result.Outcome);
        Assert.Equal("servicesStarted", setup.Journal.Read()!.Operations.Single().Phase);
        var config = backup.Nodes.Single(node => !node.IsDirectory && node.RelativePath == "appsettings.json");
        PackageVerifier.ValidateExtracted(sourceRoot, PackageVerifier.IncludePreservedAppSettings(
            PackageVerifier.CreateAgentInstallInventory(setup.ExpectedTarget), config.Length, config.Sha256!));
        Assert.True(Directory.Exists(prior));
    }

    [Fact]
    public async Task ApplyRestoresImmutableBackupWhenTargetServiceCannotStart()
    {
        var setup = await PrepareApplySetupAsync();
        var events = new List<string>();
        var fileSystem = new ApplyFileSystem(events);
        var services = new ApplyServices(events) { FailedVersion = setup.ExpectedTarget.Version };
        setup.BackupAuthorization.IsQuiesced = () => !services.IsRunning;
        var backup = setup.BackupOperation.VerifyPublishedBackup("operation-1");
        var apply = new MaintenanceApplyOperation(sourceRoot, setup.ExtractedTarget, setup.Journal,
            setup.BackupOperation, new ApplyAuthorization(events, () => !services.IsRunning), fileSystem, services);

        var result = await apply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None);

        var recorded = setup.Journal.Read()!.Operations.Single();
        Assert.Equal("rolledBack", result.Outcome);
        Assert.Equal("1.2.3", result.InstalledVersion);
        Assert.Equal("targetStartFailed", result.RollbackReason);
        Assert.Equal("rollbackServicesStarted", recorded.Phase);
        Assert.Equal(["rollbackIntent", "targetMovedToFailed", "sourceRestored", "rollbackServicesStarted"],
            recorded.PhaseHistory.TakeLast(4));
        fileSystem.VerifyBackupTree(sourceRoot, backup);
        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(sourceRoot)!, ".Agent-failed-operation-1")));
        Assert.Equal(1, services.StopChecks);
    }

    [Fact]
    public async Task ApplyRetriesTargetStartAfterServiceWasLeftRunningByLostJournalWrite()
    {
        var setup = await PrepareApplySetupAsync();
        setup.JournalStore.FailOnPhase = "servicesStarted";
        var events = new List<string>();
        var firstServices = new ApplyServices(events);
        setup.BackupAuthorization.IsQuiesced = () => !firstServices.IsRunning;
        var firstApply = new MaintenanceApplyOperation(sourceRoot, setup.ExtractedTarget, setup.Journal,
            setup.BackupOperation, new ApplyAuthorization(events, () => !firstServices.IsRunning),
            new ApplyFileSystem(events), firstServices);

        await Assert.ThrowsAsync<IOException>(() => firstApply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None));

        Assert.Equal("targetPlaced", setup.Journal.Read()!.Operations.Single().Phase);
        Assert.True(firstServices.IsRunning);

        var recoveryServices = new ApplyServices(events) { IsRunning = true };
        setup.BackupAuthorization.IsQuiesced = () => !recoveryServices.IsRunning;
        var recoveryApply = new MaintenanceApplyOperation(sourceRoot, setup.ExtractedTarget, setup.Journal,
            setup.BackupOperation, new ApplyAuthorization(events, () => !recoveryServices.IsRunning),
            new ApplyFileSystem(events), recoveryServices);
        var result = await recoveryApply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None);

        Assert.Equal("applied", result.Outcome);
        Assert.Equal("servicesStarted", setup.Journal.Read()!.Operations.Single().Phase);
        Assert.Equal(1, recoveryServices.StopChecks);
        Assert.True(recoveryServices.IsRunning);
    }

    [Fact]
    public async Task ApplyRecoversPersistedRollbackIntentByStoppingPartiallyStartedTargetBeforeBackupRead()
    {
        var setup = await PrepareApplySetupAsync();
        var events = new List<string>();
        var firstServices = new ApplyServices(events)
        {
            FailedVersion = setup.ExpectedTarget.Version,
            FailStopOnCall = 1
        };
        setup.BackupAuthorization.IsQuiesced = () => !firstServices.IsRunning;
        var firstApply = new MaintenanceApplyOperation(sourceRoot, setup.ExtractedTarget, setup.Journal,
            setup.BackupOperation, new ApplyAuthorization(events, () => !firstServices.IsRunning),
            new ApplyFileSystem(events), firstServices);

        await Assert.ThrowsAsync<IOException>(() => firstApply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None));

        Assert.Equal("rollbackIntent", setup.Journal.Read()!.Operations.Single().Phase);
        Assert.True(firstServices.IsRunning);

        var recoveryServices = new ApplyServices(events) { IsRunning = true };
        setup.BackupAuthorization.IsQuiesced = () => !recoveryServices.IsRunning;
        var recoveryApply = new MaintenanceApplyOperation(sourceRoot, setup.ExtractedTarget, setup.Journal,
            setup.BackupOperation, new ApplyAuthorization(events, () => !recoveryServices.IsRunning),
            new ApplyFileSystem(events), recoveryServices);
        var result = await recoveryApply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None);

        Assert.Equal("rolledBack", result.Outcome);
        Assert.Equal("rollbackServicesStarted", setup.Journal.Read()!.Operations.Single().Phase);
        Assert.Equal("1.2.3", result.InstalledVersion);
        Assert.Equal(1, recoveryServices.StopChecks);
        Assert.True(recoveryServices.IsRunning);
    }

    [Fact]
    public async Task ApplyRestoresBackupIfVerifiedTargetStageDisappearsAfterSourceMove()
    {
        var setup = await PrepareApplySetupAsync();
        var events = new List<string>();
        var services = new ApplyServices(events);
        setup.BackupAuthorization.IsQuiesced = () => !services.IsRunning;
        var backup = setup.BackupOperation.VerifyPublishedBackup("operation-1");
        var fileSystem = new ApplyFileSystem(events)
        {
            AfterMove = (_, destination) =>
            {
                if (Path.GetFileName(destination) == ".Agent-prior-operation-1")
                    Directory.Delete(Path.Combine(Path.GetDirectoryName(sourceRoot)!, ".Agent-stage-operation-1"), recursive: true);
            }
        };
        var apply = new MaintenanceApplyOperation(sourceRoot, setup.ExtractedTarget, setup.Journal,
            setup.BackupOperation, new ApplyAuthorization(events, () => !services.IsRunning), fileSystem, services);

        var result = await apply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None);

        Assert.Equal("rolledBack", result.Outcome);
        Assert.Equal("applyFailed", result.RollbackReason);
        Assert.Equal("rollbackServicesStarted", setup.Journal.Read()!.Operations.Single().Phase);
        fileSystem.VerifyBackupTree(sourceRoot, backup);
    }

    [Fact]
    public async Task ApplyRestoresBackupIfVerifiedTargetPlacementFailsAfterSourceMove()
    {
        var setup = await PrepareApplySetupAsync();
        var events = new List<string>();
        var services = new ApplyServices(events);
        setup.BackupAuthorization.IsQuiesced = () => !services.IsRunning;
        var backup = setup.BackupOperation.VerifyPublishedBackup("operation-1");
        var fileSystem = new ApplyFileSystem(events)
        {
            FailMoveSource = Path.Combine(Path.GetDirectoryName(sourceRoot)!, ".Agent-stage-operation-1"),
            FailMoveDestination = sourceRoot
        };
        var apply = new MaintenanceApplyOperation(sourceRoot, setup.ExtractedTarget, setup.Journal,
            setup.BackupOperation, new ApplyAuthorization(events, () => !services.IsRunning), fileSystem, services);

        var result = await apply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None);

        Assert.Equal("rolledBack", result.Outcome);
        Assert.Equal("applyFailed", result.RollbackReason);
        Assert.Equal("rollbackServicesStarted", setup.Journal.Read()!.Operations.Single().Phase);
        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(sourceRoot)!, ".Agent-stage-operation-1")));
        fileSystem.VerifyBackupTree(sourceRoot, backup);
    }

    [Fact]
    public async Task ApplyRetryReconcilesQuarantinedInvalidTargetAfterJournalFlushFailure()
    {
        var setup = await PrepareApplySetupAsync();
        setup.JournalStore.FailOnPhase = "targetMovedToFailed";
        var events = new List<string>();
        var services = new ApplyServices(events);
        setup.BackupAuthorization.IsQuiesced = () => !services.IsRunning;
        var backup = setup.BackupOperation.VerifyPublishedBackup("operation-1");
        var fileSystem = new ApplyFileSystem(events)
        {
            AfterMove = (source, destination) =>
            {
                if (Path.GetFileName(source) == ".Agent-stage-operation-1" &&
                    string.Equals(Path.GetFullPath(destination), Path.GetFullPath(sourceRoot), StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(Path.Combine(destination, "tampered-after-placement.bin"), "invalid target contents");
            }
        };
        var apply = new MaintenanceApplyOperation(sourceRoot, setup.ExtractedTarget, setup.Journal,
            setup.BackupOperation, new ApplyAuthorization(events, () => !services.IsRunning), fileSystem, services);

        await Assert.ThrowsAsync<IOException>(() => apply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None));

        var held = setup.Journal.Read()!.Operations.Single();
        var failedTarget = Path.Combine(Path.GetDirectoryName(sourceRoot)!, ".Agent-failed-operation-1");
        Assert.Equal("rollbackIntent", held.Phase);
        Assert.False(Directory.Exists(sourceRoot));
        Assert.True(File.Exists(Path.Combine(failedTarget, "tampered-after-placement.bin")));

        var result = await apply.ApplyAsync("operation-1", setup.ExpectedTarget, CancellationToken.None);

        Assert.Equal("rolledBack", result.Outcome);
        Assert.Equal("rollbackServicesStarted", setup.Journal.Read()!.Operations.Single().Phase);
        fileSystem.VerifyBackupTree(sourceRoot, backup);
        Assert.True(File.Exists(Path.Combine(failedTarget, "tampered-after-placement.bin")));
    }

    private readonly string root = Path.Combine(Path.GetTempPath(), "kr-source-inspector-" + Guid.NewGuid().ToString("N"));
    private readonly string sourceRoot;
    private readonly string backupRoot;
    private readonly string fixtureOutputPath;

    public InstalledAgentSourceInspectorTests()
    {
        var metadata = typeof(InstalledAgentSourceInspectorTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "LegacyInstalledAgentOutputPath");
        fixtureOutputPath = Path.GetFullPath(metadata.Value!);
        sourceRoot = Path.Combine(root, "installed", "Agent");
        backupRoot = Path.Combine(root, "backups");
        CopyFixtureTree();
    }

    [Fact]
    public void ProtocolOneInstalledTreeUsesOneDepsEntryAndMatchingAgentAssemblyMetadata()
    {
        var snapshot = new InstalledAgentSourceInspector(sourceRoot, NewSecurity()).Inspect();

        Assert.Equal("1.2.3", snapshot.Version);
        Assert.Equal("1.2.3", snapshot.AssemblyInformationalVersion);
        Assert.Equal(1, snapshot.AgentUpdateChallengeProtocol);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(sourceRoot, "Agent.deps.json")))), snapshot.DepsSha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(sourceRoot, "Agent.dll")))), snapshot.AgentAssemblySha256);
    }

    [Fact]
    public void InspectCommandReturnsOnlyAclCheckedSourceMetadata()
    {
        var inspector = new InstalledAgentSourceInspector(sourceRoot, NewSecurity());
        var host = new WindowsAgentMaintenanceCommandHost(new LocalUpdateEngine(new UnusedWorkStore()), inspector);

        var result = host.Execute("inspect");

        Assert.Equal("ok", result.Status);
        Assert.Null(result.Category);
        Assert.Equal("1.2.3", result.Version);
        Assert.NotNull(result.Source);
        Assert.Equal("1.2.3", result.Source.Version);
        Assert.Equal(1, result.Source.AgentUpdateChallengeProtocol);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(sourceRoot, "Agent.deps.json")))), result.Source.DepsSha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(sourceRoot, "Agent.dll")))), result.Source.AgentAssemblySha256);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain(sourceRoot, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ticket", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectCommandRejectsAnUntrustedInstalledSourceWithoutRepairingIt()
    {
        var inspector = new InstalledAgentSourceInspector(sourceRoot, new BackupSecurity(sourceRoot, backupRoot, rejectSource: true));
        var host = new WindowsAgentMaintenanceCommandHost(new LocalUpdateEngine(new UnusedWorkStore()), inspector);

        var result = host.Execute("inspect");

        Assert.Equal("rejected", result.Status);
        Assert.Equal("sourceUntrusted", result.Category);
        Assert.Null(result.Source);
        Assert.True(File.Exists(Path.Combine(sourceRoot, "Agent.dll")));
    }

    [Fact]
    public void MultipleAgentProjectEntriesAreUnverifiable()
    {
        AddSecondAgentLibrary("Agent/9.9.9");

        Assert.Throws<InvalidDataException>(() => new InstalledAgentSourceInspector(sourceRoot, NewSecurity()).Inspect());
    }

    [Fact]
    public void DllInformationalVersionMustMatchTheUniqueDepsProjectVersion()
    {
        ReplaceDepsLibraryKey("Agent/1.2.3", "Agent/9.9.9");

        Assert.Throws<InvalidDataException>(() => new InstalledAgentSourceInspector(sourceRoot, NewSecurity()).Inspect());
    }

    [Fact]
    public void BackupRequiresQuiescedJournalAndAtomicallyRecordsPublishedBackupEvidence()
    {
        var journalStore = NewJournalAt("servicesQuiesced");
        var journal = new MaintenanceJournal(journalStore, "chain-1");
        var security = NewSecurity();
        var authorization = new Authorization();
        var publisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, security, authorization);
        var operation = new MaintenanceBackupOperation(sourceRoot, new InstalledAgentSourceInspector(sourceRoot, security), publisher, journal);

        var backup = operation.Publish("operation-1");
        var retry = operation.Publish("operation-1");

        var recorded = journal.Read()!.Operations.Single();
        Assert.Equal("1.2.3", backup.Source.Version);
        Assert.Equal("backupPublished", recorded.Phase);
        Assert.Equal("operation-1", recorded.BackupOwnerOperationId);
        Assert.Equal(backup.Backup.BackupDigest, recorded.BackupDigest);
        Assert.Equal(backup.Backup.BackupDigest, retry.Backup.BackupDigest);
        Assert.Contains(backup.Backup.Nodes, node => node.RelativePath == "Agent.deps.json");
        Assert.Equal(3, authorization.Checks);
    }

    [Fact]
    public void BackupRejectsSameSourceAndTargetVersionBeforeCreatingBackupRoot()
    {
        var journalStore = NewJournalAt("servicesQuiesced", targetVersion: "1.2.3");
        var security = NewSecurity();
        var publisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, security, new Authorization());
        var operation = new MaintenanceBackupOperation(sourceRoot,
            new InstalledAgentSourceInspector(sourceRoot, security), publisher,
            new MaintenanceJournal(journalStore, "chain-1"));

        Assert.Throws<InvalidOperationException>(() => { _ = operation.Publish("operation-1"); });
        Assert.False(Directory.Exists(backupRoot));
    }

    [Fact]
    public void BackupRetryRecoversIfJournalWriteFailsAfterAtomicPublication()
    {
        var journalStore = NewJournalAt("servicesQuiesced");
        journalStore.FailNextWrite = true;
        var security = NewSecurity();
        var authorization = new Authorization();
        var publisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, security, authorization);
        var operation = new MaintenanceBackupOperation(sourceRoot,
            new InstalledAgentSourceInspector(sourceRoot, security), publisher,
            new MaintenanceJournal(journalStore, "chain-1"));

        Assert.Throws<IOException>(() => { _ = operation.Publish("operation-1"); });
        Assert.True(Directory.Exists(Path.Combine(backupRoot, "operation-1")));
        Assert.Equal("servicesQuiesced", journalStore.Snapshot!.Operations.Single().Phase);

        var retry = operation.Publish("operation-1");
        Assert.Equal("backupPublished", journalStore.Snapshot.Operations.Single().Phase);
        Assert.Equal(retry.Backup.BackupDigest, journalStore.Snapshot.Operations.Single().BackupDigest);
    }

    [Fact]
    public void BackupFailsHeldWhenSourceMetadataChangesBetweenInspectionAndSnapshot()
    {
        var journalStore = NewJournalAt("servicesQuiesced");
        var security = NewSecurity();
        var authorization = new Authorization
        {
            OnCheck = check =>
            {
                if (check == 1)
                    File.AppendAllText(Path.Combine(sourceRoot, "Agent.dll"), "changed");
            }
        };
        var publisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, security, authorization);
        var operation = new MaintenanceBackupOperation(sourceRoot,
            new InstalledAgentSourceInspector(sourceRoot, security), publisher,
            new MaintenanceJournal(journalStore, "chain-1"));

        Assert.Throws<InvalidOperationException>(() => { _ = operation.Publish("operation-1"); });
        Assert.Equal("servicesQuiesced", journalStore.Snapshot!.Operations.Single().Phase);
        Assert.True(Directory.Exists(Path.Combine(backupRoot, "operation-1")));
    }

    [Fact]
    public void BackupRequiresTheJournalToBeQuiescedBeforeTouchingBackupStorage()
    {
        var journalStore = NewJournalAt("targetValidated");
        var security = NewSecurity();
        var publisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, security, new Authorization());
        var operation = new MaintenanceBackupOperation(sourceRoot,
            new InstalledAgentSourceInspector(sourceRoot, security), publisher,
            new MaintenanceJournal(journalStore, "chain-1"));

        Assert.Throws<InvalidOperationException>(() => { _ = operation.Publish("operation-1"); });
        Assert.False(Directory.Exists(backupRoot));
    }

    [Fact]
    public async Task SourceBindingPersistsExactSignedRequestBeforeSendingAndRecoversJournalWriteFailure()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var identity = TrustedIdentity.ReadExisting(new IdentitySource(
            "0123456789abcdef0123456789abcdef", Convert.ToBase64String(key.ExportPkcs8PrivateKey())));
        var journalStore = NewJournalAt("servicesQuiesced", keyFingerprint: identity.KeyFingerprint.ToLowerInvariant());
        var journal = new MaintenanceJournal(journalStore, "chain-1");
        var security = NewSecurity();
        var authorization = new Authorization();
        var publisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, security, authorization);
        var backupOperation = new MaintenanceBackupOperation(sourceRoot,
            new InstalledAgentSourceInspector(sourceRoot, security), publisher, journal);
        var client = new SourceBindingClient(TimeProvider.System);
        var operation = new MaintenanceSourceBindingOperation(backupOperation, journal, client, identity,
            new BootIdentity("native-boot-1"));
        var ticket = new string('A', 43);
        var parameters = new MaintenanceSourceBindingParameters(ticket, "2.3.4", new string('c', 64), "win-x64", 1);
        journalStore.FailOnPhase = "sourceBound";

        await Assert.ThrowsAsync<IOException>(() => operation.BindAsync(parameters, CancellationToken.None));

        var backup = publisher.VerifyPublished("operation-1");
        var held = journal.Read()!.Operations.Single();
        Assert.Equal("backupPublished", held.Phase);
        Assert.NotNull(held.SourceBindingIntent);
        Assert.DoesNotContain(ticket, JsonSerializer.Serialize(journal.Read()));
        Assert.Equal(backup.InventoryDigest, held.SourceBindingIntent!.Evidence.InventoryDigest);
        Assert.Equal(backup.BackupDigest, held.SourceBindingIntent.Evidence.BackupDigest);
        Assert.Equal("native-boot-1", held.SourceBindingIntent.Evidence.NativeBootId);
        Assert.True(client.EvidenceSignatureVerified);
        Assert.Equal(1, client.ChallengeCalls);
        var firstRequest = Assert.Single(client.BindingRequests);

        _ = await operation.BindAsync(parameters, CancellationToken.None);

        Assert.Equal("sourceBound", journal.Read()!.Operations.Single().Phase);
        Assert.Equal(1, client.ChallengeCalls);
        Assert.Equal(2, client.BindingRequests.Count);
        Assert.Equal(firstRequest, client.BindingRequests[1]);
        Assert.Equal(client.BindingBodyDigests[0], client.BindingBodyDigests[1]);
        Assert.Equal(held.SourceBindingIntent.BindingBodyDigest, client.BindingBodyDigests[0], ignoreCase: true);
        Assert.Equal("1.2.3", journal.Read()!.Operations.Single().SourceVersion);
    }

    [Fact]
    public async Task SourceBindingRejectsBeforeChallengeUnlessBackupWasJournaled()
    {
        var journalStore = NewJournalAt("targetValidated");
        var journal = new MaintenanceJournal(journalStore, "chain-1");
        var security = NewSecurity();
        var client = new SourceBindingClient(TimeProvider.System);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var identity = TrustedIdentity.ReadExisting(new IdentitySource(
            "0123456789abcdef0123456789abcdef", Convert.ToBase64String(key.ExportPkcs8PrivateKey())));
        var publisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, security, new Authorization());
        var backupOperation = new MaintenanceBackupOperation(sourceRoot,
            new InstalledAgentSourceInspector(sourceRoot, security), publisher, journal);
        var operation = new MaintenanceSourceBindingOperation(backupOperation, journal, client, identity,
            new BootIdentity("native-boot-1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.BindAsync(
            new MaintenanceSourceBindingParameters(new string('A', 43), "2.3.4", new string('c', 64), "win-x64", 1),
            CancellationToken.None));

        Assert.Equal(0, client.ChallengeCalls);
        Assert.False(Directory.Exists(backupRoot));
    }

    [Fact]
    public async Task SourceBindingRotatesChallengeAfterControllerRestartBeforeChallengeWasBound()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var identity = TrustedIdentity.ReadExisting(new IdentitySource(
            "0123456789abcdef0123456789abcdef", Convert.ToBase64String(key.ExportPkcs8PrivateKey())));
        var journalStore = NewJournalAt("servicesQuiesced", keyFingerprint: identity.KeyFingerprint.ToLowerInvariant());
        var journal = new MaintenanceJournal(journalStore, "chain-1");
        var security = NewSecurity();
        var publisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, security, new Authorization());
        var backupOperation = new MaintenanceBackupOperation(sourceRoot,
            new InstalledAgentSourceInspector(sourceRoot, security), publisher, journal);
        var client = new SourceBindingClient(TimeProvider.System) { RenewalRequiredOnChallengeCall = 1 };
        var operation = new MaintenanceSourceBindingOperation(backupOperation, journal, client, identity,
            new BootIdentity("native-boot-1"));

        _ = await operation.BindAsync(
            new MaintenanceSourceBindingParameters(new string('A', 43), "2.3.4", new string('c', 64), "win-x64", 1),
            CancellationToken.None);

        var recorded = journal.Read()!.Operations.Single();
        Assert.Equal("sourceBound", recorded.Phase);
        Assert.Equal(2, client.ChallengeCalls);
        Assert.Single(client.BindingRequests);
        Assert.NotEqual(client.ChallengeRequestIds[0], client.ChallengeRequestIds[1]);
        Assert.Equal(0, Assert.Single(recorded.RejectedSourceChallengeAttempts!).AttemptNumber);
        Assert.Equal(1, recorded.SourceBindingIntent!.AttemptNumber);
        Assert.Empty(recorded.RejectedSourceBindingIntents ?? []);
    }

    [Fact]
    public async Task SourceBindingRotatesExactIntentOnlyAfterExplicitStaleChallengeRejection()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var identity = TrustedIdentity.ReadExisting(new IdentitySource(
            "0123456789abcdef0123456789abcdef", Convert.ToBase64String(key.ExportPkcs8PrivateKey())));
        var journalStore = NewJournalAt("servicesQuiesced", keyFingerprint: identity.KeyFingerprint.ToLowerInvariant());
        var journal = new MaintenanceJournal(journalStore, "chain-1");
        var security = NewSecurity();
        var publisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, security, new Authorization());
        var backupOperation = new MaintenanceBackupOperation(sourceRoot,
            new InstalledAgentSourceInspector(sourceRoot, security), publisher, journal);
        var client = new SourceBindingClient(TimeProvider.System) { RenewalRequiredOnBindingCall = 1 };
        var operation = new MaintenanceSourceBindingOperation(backupOperation, journal, client, identity,
            new BootIdentity("native-boot-1"));

        _ = await operation.BindAsync(
            new MaintenanceSourceBindingParameters(new string('A', 43), "2.3.4", new string('c', 64), "win-x64", 1),
            CancellationToken.None);

        var recorded = journal.Read()!.Operations.Single();
        Assert.Equal("sourceBound", recorded.Phase);
        Assert.Equal(2, client.ChallengeCalls);
        Assert.Equal(2, client.BindingRequests.Count);
        Assert.NotEqual(client.BindingRequests[0].RequestId, client.BindingRequests[1].RequestId);
        Assert.Equal(client.BindingRequests[0].SourceVersion, client.BindingRequests[1].SourceVersion);
        Assert.Equal(client.BindingRequests[0].BackupDigest, client.BindingRequests[1].BackupDigest);
        Assert.Equal(client.BindingRequests[0].InventoryDigest, client.BindingRequests[1].InventoryDigest);
        Assert.Equal(0, Assert.Single(recorded.RejectedSourceBindingIntents!).AttemptNumber);
        Assert.Equal(1, recorded.SourceBindingIntent!.AttemptNumber);
        Assert.True(client.EvidenceSignatureVerified);
    }

    [Fact]
    public async Task HttpSourceClientOnlyClassifiesTheTypedRenewalConflictAsRotatable()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var identity = TrustedIdentity.ReadExisting(new IdentitySource(
            "0123456789abcdef0123456789abcdef", Convert.ToBase64String(key.ExportPkcs8PrivateKey())));
        var request = new AgentUpdateSourceChallengeRequest(new string('A', 43), "2.3.4", "2.3.4",
            new string('c', 64), "source-challenge-test", new string('d', 64), "win-x64", 1);

        using var renewalHttp = new HttpClient(new StaticResponseHandler(HttpStatusCode.Conflict,
            "{\"code\":\"sourceChallengeRenewalRequired\"}"));
        var renewalClient = new HttpMaintenanceSourceBindingClient(renewalHttp);
        await Assert.ThrowsAsync<MaintenanceSourceChallengeRenewalRequiredException>(() => renewalClient.IssueChallengeAsync(
            "https://controller.example/", identity.MachineId, "operation-1", request, identity, CancellationToken.None));

        using var otherConflictHttp = new HttpClient(new StaticResponseHandler(HttpStatusCode.Conflict,
            "{\"error\":\"conflict\"}"));
        var otherConflictClient = new HttpMaintenanceSourceBindingClient(otherConflictHttp);
        await Assert.ThrowsAsync<InvalidOperationException>(() => otherConflictClient.IssueChallengeAsync(
            "https://controller.example/", identity.MachineId, "operation-1", request, identity, CancellationToken.None));
    }

    [Fact]
    public async Task AlteredBindParametersCannotReplaceOrRotateThePersistedSignedBody()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var identity = TrustedIdentity.ReadExisting(new IdentitySource(
            "0123456789abcdef0123456789abcdef", Convert.ToBase64String(key.ExportPkcs8PrivateKey())));
        var journalStore = NewJournalAt("servicesQuiesced", keyFingerprint: identity.KeyFingerprint.ToLowerInvariant());
        var journal = new MaintenanceJournal(journalStore, "chain-1");
        var security = NewSecurity();
        var publisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, security, new Authorization());
        var backupOperation = new MaintenanceBackupOperation(sourceRoot,
            new InstalledAgentSourceInspector(sourceRoot, security), publisher, journal);
        var client = new SourceBindingClient(TimeProvider.System) { ConflictOnBindingCall = 1 };
        var operation = new MaintenanceSourceBindingOperation(backupOperation, journal, client, identity,
            new BootIdentity("native-boot-1"));
        var parameters = new MaintenanceSourceBindingParameters(new string('A', 43), "2.3.4", new string('c', 64), "win-x64", 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.BindAsync(parameters, CancellationToken.None));
        var intent = journal.Read()!.Operations.Single().SourceBindingIntent;
        Assert.NotNull(intent);

        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.BindAsync(
            parameters with { Ticket = new string('B', 43) }, CancellationToken.None));

        var after = journal.Read()!.Operations.Single();
        Assert.Equal(intent, after.SourceBindingIntent);
        Assert.Empty(after.RejectedSourceBindingIntents ?? []);
        Assert.Equal(1, client.ChallengeCalls);
        Assert.Single(client.BindingRequests);
    }

    private BackupSecurity NewSecurity() => new(sourceRoot, backupRoot);

    private sealed record ApplySetup(
        MemoryJournalStore JournalStore,
        MaintenanceJournal Journal,
        MaintenanceBackupOperation BackupOperation,
        Authorization BackupAuthorization,
        PackageInventory ExpectedTarget,
        string ExtractedTarget);

    private async Task<ApplySetup> PrepareApplySetupAsync(byte[]? targetArchive = null)
    {
        var nativeRuntime = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("The apply fixtures require x64 or ARM64.")
        };
        var archive = targetArchive ?? AgentPackageCorpus.Valid(nativeRuntime);
        var expectedTarget = PackageVerifier.ValidateArchive(new MemoryStream(archive), AgentPackageCorpus.Version);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var identity = TrustedIdentity.ReadExisting(new IdentitySource(
            "0123456789abcdef0123456789abcdef", Convert.ToBase64String(key.ExportPkcs8PrivateKey())));
        var journalStore = NewJournalAt("servicesQuiesced", expectedTarget.Version,
            identity.KeyFingerprint.ToLowerInvariant(), expectedTarget.ArchiveHash);
        var journal = new MaintenanceJournal(journalStore, "chain-1");
        var security = NewSecurity();
        var backupAuthorization = new Authorization();
        var publisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, security, backupAuthorization);
        var backupOperation = new MaintenanceBackupOperation(sourceRoot,
            new InstalledAgentSourceInspector(sourceRoot, security), publisher, journal);
        var binding = new MaintenanceSourceBindingOperation(backupOperation, journal,
            new SourceBindingClient(TimeProvider.System), identity, new BootIdentity("native-boot-1"));
        _ = await binding.BindAsync(new MaintenanceSourceBindingParameters(new string('A', 43),
            expectedTarget.Version, new string('c', 64), expectedTarget.TargetRuntime, 1), CancellationToken.None);

        var extractedTarget = Path.Combine(root, "target-extracted");
        Directory.CreateDirectory(extractedTarget);
        _ = PackageVerifier.Extract(new MemoryStream(archive), extractedTarget, expectedTarget);
        return new(journalStore, journal, backupOperation, backupAuthorization, expectedTarget, extractedTarget);
    }

    private MemoryJournalStore NewJournalAt(string phase, string targetVersion = "2.3.4", string? keyFingerprint = null,
        string? targetSha256 = null)
    {
        var phases = new[] { "inspected", "controllerStarted", "targetValidated", "servicesQuiesced" };
        var targetIndex = Array.IndexOf(phases, phase);
        Assert.True(targetIndex >= 0);
        var store = new MemoryJournalStore();
        var journal = new MaintenanceJournal(store, "chain-1");
        journal.Start(new MaintenanceOperationBinding("operation-1", "chain-1", "operation-1", 1,
            "https://controller.example/", keyFingerprint ?? new string('a', 64), 1, "win-x64", targetVersion,
            targetSha256 ?? new string('b', 64)));
        foreach (var next in phases.Skip(1).Take(targetIndex))
            journal.Advance("operation-1", next);
        return store;
    }

    private void CopyFixtureTree()
    {
        Assert.True(File.Exists(Path.Combine(fixtureOutputPath, "Agent.exe")), "The legacy installed fixture apphost was not built.");
        Assert.True(File.Exists(Path.Combine(fixtureOutputPath, "Agent.dll")), "The legacy installed fixture DLL was not built.");
        Assert.True(File.Exists(Path.Combine(fixtureOutputPath, "Agent.deps.json")), "The legacy installed fixture deps file was not built.");
        Assert.True(File.Exists(Path.Combine(fixtureOutputPath, "Agent.runtimeconfig.json")), "The legacy installed fixture runtime config was not built.");
        Directory.CreateDirectory(sourceRoot);
        foreach (var file in Directory.EnumerateFiles(fixtureOutputPath))
            File.Copy(file, Path.Combine(sourceRoot, Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(sourceRoot, "appsettings.json"), "{\"MachineConfiguration\":\"preserve\"}");
    }

    private void AddSecondAgentLibrary(string key)
    {
        var path = Path.Combine(sourceRoot, "Agent.deps.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document["libraries"]!.AsObject()[key] = new JsonObject { ["type"] = "project" };
        File.WriteAllText(path, document.ToJsonString());
    }

    private void ReplaceDepsLibraryKey(string current, string replacement)
    {
        var path = Path.Combine(sourceRoot, "Agent.deps.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var libraries = document["libraries"]!.AsObject();
        var value = libraries[current]!.DeepClone();
        libraries.Remove(current);
        libraries[replacement] = value;
        var targets = document["targets"]!.AsObject();
        foreach (var target in targets.Select(property => property.Value!.AsObject()))
        {
            if (target[current] is not { } entry) continue;
            target.Remove(current);
            target[replacement] = entry.DeepClone();
        }
        File.WriteAllText(path, document.ToJsonString());
    }

    public void Dispose()
    {
        if (Directory.Exists(root) && Path.GetFileName(root).StartsWith("kr-source-inspector-", StringComparison.Ordinal))
            Directory.Delete(root, recursive: true);
    }
}
