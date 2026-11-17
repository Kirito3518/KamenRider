using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using KamenRaider.AgentMaintenance;
using KamenRaider.AgentPackage;
using KamenRaider.Contracts;
using Controller.Tests.Fixtures;

namespace Controller.Tests;

public sealed partial class InstalledAgentSourceInspectorTests
{
    private sealed class OperationExclusionScope(List<string> events)
    {
        private bool operationHeld = true;
        public bool IsHeld => operationHeld;

        public IMaintenanceApplyProcessExclusionLease Acquire()
        {
            if (!operationHeld) throw new InvalidOperationException("The operation exclusion has been released.");
            events.Add("exclusion-acquired");
            return new Lease(this, events);
        }

        public void Verify(string operationId)
        {
            Assert.True(operationHeld, "The operation-wide exclusion must remain held through report.");
            Assert.Equal("operation-1", operationId);
            events.Add("exclusion-verified");
        }

        public void ReleaseOperation() => operationHeld = false;

        private sealed class Lease(OperationExclusionScope owner, List<string> events)
            : IMaintenanceApplyProcessExclusionLease
        {
            private bool disposed;
            public void VerifyContinuouslyHeld(string operationId)
            {
                Assert.False(disposed, "A command cannot reuse its disposed exclusion lease.");
                owner.Verify(operationId);
            }
            public void Dispose()
            {
                disposed = true;
                events.Add("command-exclusion-released");
            }
        }
    }

    private sealed class OperationExclusionProvider(OperationExclusionScope scope)
        : IMaintenanceApplyProcessExclusionProvider
    {
        public IMaintenanceApplyProcessExclusionLease? Acquire() => scope.Acquire();
    }

    private sealed class OperationFenceStore(List<string> events) : IMaintenanceFenceStore
    {
        private AgentMaintenanceFenceRecord? current;
        public AgentMaintenanceFenceRecord? Read() => current;
        public void Write(AgentMaintenanceFenceRecord record)
        {
            current = record;
            events.Add("fence-record:" + record.Phase);
        }
    }

    private sealed class OperationFirewallFence(List<string> events, string expectedPath)
        : IWindowsMaintenanceFirewallFence
    {
        public bool Blocked { get; private set; }
        public void EnsureBlocked(string sunshinePath)
        {
            Assert.Equal(Path.GetFullPath(expectedPath), Path.GetFullPath(sunshinePath));
            Blocked = true;
            events.Add("firewall-blocked");
        }
        public bool IsBlocked(string sunshinePath)
        {
            Assert.Equal(Path.GetFullPath(expectedPath), Path.GetFullPath(sunshinePath));
            return Blocked;
        }
        public void RemoveBlock(string sunshinePath)
        {
            Assert.Equal(Path.GetFullPath(expectedPath), Path.GetFullPath(sunshinePath));
            Blocked = false;
            events.Add("firewall-unblocked");
        }
    }

    private sealed class OperationServiceApi : IMaintenanceWindowsServiceApi
    {
        private readonly Dictionary<string, MaintenanceWindowsServiceSnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, string> processPaths = new();
        private readonly MaintenanceServiceLayout layout;
        private readonly List<string> events;
        private uint nextPid = 2000;
        private static string Quote(string path) => "\"" + path + "\"";

        public OperationServiceApi(MaintenanceServiceLayout layout, List<string> events)
        {
            this.layout = layout;
            this.events = events;
            var agentDirectory = Path.GetDirectoryName(Path.GetFullPath(layout.AgentExecutablePath))!;
            Add(MaintenanceServiceQuiescer.AgentServiceName,
                Quote(layout.AgentExecutablePath) + " --contentRoot " + Quote(agentDirectory),
                "NT SERVICE\\KamenRaider Agent", [MaintenanceServiceQuiescer.GuardServiceName]);
            Add(MaintenanceServiceQuiescer.GuardServiceName,
                Quote(layout.AgentExecutablePath) + " --contentRoot " + Quote(agentDirectory) + " --stream-guard",
                "LocalSystem", []);
            Add(MaintenanceServiceQuiescer.SunshineServiceName,
                Quote(layout.SunshineServiceExecutablePath) + " --service", "LocalSystem",
                [MaintenanceServiceQuiescer.GuardServiceName]);
        }

        public bool AllStopped => snapshots.Values.All(item => item.State == MaintenanceWindowsServiceState.Stopped && item.ProcessId == 0);
        public bool AnyRunning => snapshots.Values.Any(item => item.State == MaintenanceWindowsServiceState.Running);
        public bool AllRunning => snapshots.Values.All(item => item.State == MaintenanceWindowsServiceState.Running && item.ProcessId > 0);

        public MaintenanceWindowsServiceSnapshot? Query(string serviceName)
        {
            if (!snapshots.TryGetValue(serviceName, out var snapshot)) return null;
            events.Add("service-query:" + serviceName + ":" + snapshot.State);
            return snapshot;
        }

        public void Stop(string serviceName)
        {
            var current = snapshots[serviceName];
            if (current.State != MaintenanceWindowsServiceState.Running) return;
            snapshots[serviceName] = current with { State = MaintenanceWindowsServiceState.Stopped, ProcessId = 0 };
            events.Add("service-stop:" + serviceName);
        }

        public void Start(string serviceName)
        {
            var current = snapshots[serviceName];
            var pid = ++nextPid;
            snapshots[serviceName] = current with { State = MaintenanceWindowsServiceState.Running, ProcessId = pid };
            processPaths[pid] = CurrentImagePath(serviceName);
            events.Add("service-start:" + serviceName);
        }

        public string GetExecutablePath(int processId) =>
            processPaths.TryGetValue((uint)processId, out var path)
                ? path
                : throw new InvalidOperationException("The fake service process is unknown.");

        public IReadOnlyList<int> FindByExecutablePath(IReadOnlyCollection<string> exactPaths)
        {
            var paths = exactPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return snapshots.Values.Where(item => item.State == MaintenanceWindowsServiceState.Running)
                .Where(item => paths.Contains(Path.GetFullPath(CurrentImagePath(item.ServiceName))))
                .Select(item => (int)item.ProcessId).ToArray();
        }

        private void Add(string name, string imagePath, string account, IReadOnlyList<string> dependencies)
        {
            var pid = ++nextPid;
            snapshots[name] = new(name, imagePath, account, dependencies,
                MaintenanceWindowsServiceState.Running, pid, MaintenanceWindowsServiceTypes.Win32OwnProcess);
            processPaths[pid] = MaintenanceServiceQuiescer.ParseImageCommandLine(imagePath).Executable;
        }

        private string CurrentImagePath(string serviceName) => serviceName == MaintenanceServiceQuiescer.SunshineServiceName
            ? layout.SunshineServiceExecutablePath
            : layout.AgentExecutablePath;
    }

    private sealed class OperationProcessInspector(OperationServiceApi services) : IMaintenanceProcessInspector
    {
        public IReadOnlyList<int> FindByExecutablePath(IReadOnlyCollection<string> exactPaths) =>
            services.FindByExecutablePath(exactPaths);
        public string GetExecutablePath(int processId) => services.GetExecutablePath(processId);
    }

    private sealed class OrchestrationWorkStore(
        Dictionary<string, MaintenanceLifecycleRequest> lifecycleRequests,
        MaintenanceCommandRequest applyRequest,
        byte[] archive,
        string workDirectory,
        List<string> events) : IMaintenanceWorkStore, IMaintenanceApplyWorkStore,
            IMaintenanceLifecycleWorkStore, IMaintenanceApplyMetadataStore
    {
        private string currentRequest = "start";
        private MaintenanceApplyMetadata? applyMetadata;
        public int ApplyRequestReads { get; private set; }
        public int ApplyMetadataReads { get; private set; }
        public string ExtractionRoot => Path.Combine(workDirectory, "generic-extract");
        public string ApplyExtractionRoot => Path.Combine(workDirectory, "apply-target-operation-1");

        public MaintenanceLifecycleRequest ReadLifecycleRequest(string expectedCommand)
        {
            currentRequest = expectedCommand;
            events.Add("request-read:" + expectedCommand);
            return lifecycleRequests[expectedCommand];
        }

        public void SelectCommand(string command) => currentRequest = command;

        public MaintenanceCommandRequest ReadRequest(string expectedCommand)
        {
            if (currentRequest != "apply")
                throw new InvalidOperationException("request.json now contains a non-apply lifecycle request.");
            Assert.Equal("apply", expectedCommand);
            ApplyRequestReads++;
            events.Add("apply-request-read");
            return applyRequest;
        }

        public Stream OpenArchiveRead()
        {
            events.Add("archive-open:" + currentRequest);
            return new MemoryStream(archive, writable: false);
        }

        public void CreateExtractionRoot()
        {
            Directory.CreateDirectory(ExtractionRoot);
        }

        public MaintenanceApplyExtractionRoot PrepareApplyExtractionRoot(
            MaintenanceCommandRequest request, PackageInventory expectedInventory)
        {
            Assert.Equal("operation-1", request.OperationId);
            Directory.CreateDirectory(ApplyExtractionRoot);
            return new(ApplyExtractionRoot, IsVerifiedReuse: false);
        }

        public MaintenanceApplyMetadata GetOrCreate(MaintenanceCommandRequest request)
        {
            var candidate = MaintenanceApplyMetadata.FromRequest(request);
            if (applyMetadata is not null && applyMetadata != candidate)
                throw new InvalidOperationException("The operation-bound apply metadata cannot be changed.");
            applyMetadata = candidate;
            events.Add("apply-metadata-persisted");
            return candidate;
        }

        public MaintenanceApplyMetadata? StoredApplyMetadata => applyMetadata;

        public MaintenanceApplyMetadata Read(MaintenanceOperationBinding binding)
        {
            ApplyMetadataReads++;
            var metadata = applyMetadata ?? throw new InvalidOperationException("The apply metadata was not persisted.");
            Assert.Equal(binding.OperationId, metadata.OperationId);
            Assert.Equal(binding.ChainId, metadata.ChainId);
            Assert.Equal(binding.Generation, metadata.Generation);
            Assert.Equal(binding.TargetSha256, metadata.TargetPackageSha256);
            return metadata;
        }
    }

    private sealed class OrchestrationStartClient(
        string machineId,
        MaintenanceLifecycleRequest request,
        List<string> events) : IMaintenanceStartClient
    {
        public Task<AgentUpdateOperationDto> StartAsync(string controllerOrigin, string actualMachineId,
            string operationId, AgentUpdateTicketRequest body, CancellationToken cancellationToken)
        {
            Assert.Equal("https://controller.example/", controllerOrigin);
            Assert.Equal(machineId, actualMachineId);
            Assert.Equal(request.OperationId, operationId);
            Assert.Equal(request.Ticket, body.Ticket);
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("controller-start-acknowledged");
            return Task.FromResult(new AgentUpdateOperationDto(operationId, machineId, request.ChainId,
                request.HeadOperationId, request.Generation, true, null, request.ReleaseVersion,
                request.TargetVersion, request.TargetPackageSha256, request.TargetRuntime, 2, null,
                null, null, null, null, "installing", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
    }

    private sealed class OrchestrationSourceAuthorization(
        MaintenanceLifecycleRequest request,
        MaintenanceJournal journal,
        OperationExclusionScope exclusion,
        OperationServiceApi services,
        OperationFirewallFence firewall,
        WindowsMaintenanceFence fence,
        string sunshinePath,
        IMaintenanceOperationFenceVerifier fenceVerifier,
        List<string> events) : IMaintenanceSourceBindingAuthorization
    {
        public void VerifyRequestBinding(MaintenanceLifecycleRequest actual) => Assert.Equal(request, actual);
        public void VerifyHeld(string operationId)
        {
            exclusion.Verify(operationId);
            var phase = journal.Read()!.Operations.Single().Phase;
            if (phase == "targetValidated")
            {
                fence.VerifyHeld(new AgentMaintenanceFenceRecord(1, "held", request.OperationId,
                    request.ChainId, request.HeadOperationId, request.Generation, phase), sunshinePath);
            }
            else if (phase != "controllerStarted")
            {
                Assert.True(firewall.Blocked);
                fenceVerifier.VerifyHeld(journal.Read()!.Operations.Single().Binding);
            }
            events.Add("controller-hold-verified:" + phase);
        }
        public void VerifyHeldAndQuiesced(string operationId)
        {
            VerifyHeld(operationId);
            Assert.True(services.AllStopped, "The source backup can bind only after verified stopped services.");
            Assert.Contains(journal.Read()!.Operations.Single().Phase, new[] { "servicesQuiesced", "backupPublished", "sourceBound" });
            events.Add("services-quiesced-authorized");
        }
    }

    private sealed class OrchestrationApplyAuthorization(
        MaintenanceCommandRequest request,
        OperationExclusionScope exclusion,
        OperationServiceApi services,
        IMaintenanceOperationFenceVerifier fenceVerifier,
        List<string> events) : IMaintenanceApplyLeaseAuthorization
    {
        public void VerifyRequestBinding(MaintenanceCommandRequest actual) => Assert.Equal(request, actual);
        public void VerifyHeldAndQuiesced(string operationId)
        {
            exclusion.Verify(operationId);
            Assert.False(services.AnyRunning, "Apply transitions require the verified quiescence interval.");
            fenceVerifier.VerifyHeld(new MaintenanceOperationBinding(request.OperationId, request.ChainId!,
                request.HeadOperationId!, request.Generation!.Value, "https://controller.example/",
                new string('a', 64), 1, request.TargetRuntime, request.TargetVersion, request.TargetPackageSha256));
            events.Add("apply-held-quiesced-authorized");
        }
        public void VerifyHeld(string operationId)
        {
            exclusion.Verify(operationId);
            fenceVerifier.VerifyHeld(new MaintenanceOperationBinding(request.OperationId, request.ChainId!,
                request.HeadOperationId!, request.Generation!.Value, "https://controller.example/",
                new string('a', 64), 1, request.TargetRuntime, request.TargetVersion, request.TargetPackageSha256));
            events.Add("apply-held-authorized");
        }
    }

    private sealed class OrchestrationReportClient(
        string machineId,
        MaintenanceLifecycleRequest request,
        OperationExclusionScope exclusion,
        OperationFirewallFence firewall,
        OperationServiceApi services,
        List<string> events) : IMaintenanceReportClient
    {
        public Task<AgentUpdateOperationDto> ReportAsync(string controllerOrigin, string actualMachineId,
            string operationId, AgentUpdateStatusReportRequest body, CancellationToken cancellationToken)
        {
            Assert.Equal("https://controller.example/", controllerOrigin);
            Assert.Equal(machineId, actualMachineId);
            Assert.Equal(request.OperationId, operationId);
            Assert.Equal("awaitingHeartbeat", body.Status);
            exclusion.Verify(operationId);
            Assert.True(firewall.Blocked, "The independent firewall fence remains held while reporting.");
            Assert.True(services.AllRunning, "Agent, Guard, and Sunshine must be running before report.");
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("controller-report-acknowledged");
            return Task.FromResult(new AgentUpdateOperationDto(operationId, machineId, request.ChainId,
                request.HeadOperationId, request.Generation, true, null, request.ReleaseVersion,
                request.TargetVersion, request.TargetPackageSha256, request.TargetRuntime, 2, null,
                null, null, null, null, body.Status, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
    }

    [Fact]
    public async Task QualifiedFakeOrchestrationStartsValidatesFencesQuiescesBindsAppliesAndReports()
    {
        var runtime = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("The maintenance orchestration fixture requires x64 or ARM64.")
        };
        var archive = AgentPackageCorpus.Valid(runtime);
        var archiveHash = Convert.ToHexStringLower(SHA256.HashData(archive));
        const string operationId = "operation-1";
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identitySource = new IdentitySource("0123456789abcdef0123456789abcdef",
            Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
        using var identity = TrustedIdentity.ReadExisting(identitySource);
        var target = AgentPackageCorpus.Version;
        var events = new List<string>();
        var journalStore = new MemoryJournalStore();
        var journal = new MaintenanceJournal(journalStore, "chain-1");
        var startRequest = new MaintenanceLifecycleRequest(1, "start", operationId, "chain-1", operationId,
            1, "https://controller.example/", identity.KeyFingerprint.ToLowerInvariant(), 1, target,
            runtime, archiveHash, target, new string('T', 43));
        var bindRequest = startRequest with { Command = "bind-source", HelperBuildSha256 = new string('c', 64) };
        var reportRequest = startRequest with { Command = "report" };
        var applyRequest = new MaintenanceCommandRequest(1, "apply", operationId, target, runtime,
            archiveHash, archive.LongLength, "chain-1", operationId, 1);
        var workDirectory = Path.Combine(root, "orchestration-work");
        var workStore = new OrchestrationWorkStore(
            new Dictionary<string, MaintenanceLifecycleRequest>(StringComparer.Ordinal)
            {
                ["start"] = startRequest,
                ["bind-source"] = bindRequest,
                ["report"] = reportRequest
            }, applyRequest, archive, workDirectory, events);
        var engine = new LocalUpdateEngine(workStore);
        var exclusionScope = new OperationExclusionScope(events);
        var exclusion = new OperationExclusionProvider(exclusionScope);
        var sunshinePath = Path.Combine(root, "Sunshine", "sunshine.exe");
        var fenceStore = new OperationFenceStore(events);
        var firewall = new OperationFirewallFence(events, sunshinePath);
        var windowsFence = new WindowsMaintenanceFence(fenceStore, firewall);
        var fenceVerifier = new WindowsMaintenanceOperationFenceVerifier(windowsFence, sunshinePath);
        var layout = new MaintenanceServiceLayout(
            Path.Combine(sourceRoot, "Agent.exe"),
            Path.Combine(root, "Sunshine", "tools", "sunshinesvc.exe"), sunshinePath);
        var serviceApi = new OperationServiceApi(layout, events);
        var processInspector = new OperationProcessInspector(serviceApi);

        var startCommand = new MaintenanceStartCommand(workStore, identitySource, journalStore,
            new OrchestrationStartClient(identity.MachineId, startRequest, events), exclusion);
        var startResult = startCommand.Execute();
        Assert.Equal("started", startResult.ApplyOutcome);
        Assert.Equal("controllerStarted", journal.Read()!.Operations.Single().Phase);

        var sourceClient = new SourceBindingClient(TimeProvider.System);
        sourceClient.BeforeChallenge = () =>
        {
            Assert.Equal("backupPublished", journal.Read()!.Operations.Single().Phase);
            Assert.True(firewall.Blocked);
            Assert.True(serviceApi.AllStopped);
            exclusionScope.Verify(operationId);
            events.Add("qualified-source-challenge-after-backup");
        };
        sourceClient.BeforeBinding = _ =>
        {
            Assert.Equal("backupPublished", journal.Read()!.Operations.Single().Phase);
            Assert.True(firewall.Blocked);
            Assert.True(serviceApi.AllStopped);
        };
        var preparation = new MaintenanceSourceBindingPreparation(engine, windowsFence, sunshinePath,
            layout, serviceApi, processInspector);
        var sourceAuthorization = new OrchestrationSourceAuthorization(bindRequest, journal, exclusionScope,
            serviceApi, firewall, windowsFence, sunshinePath, fenceVerifier, events);
        var sourceFactory = new MaintenanceSourceBindingOperationFactory(NewSecurity(), sourceRoot, backupRoot,
            sourceClient, new BootIdentity("native-boot-1"));
        var sourceCommand = new MaintenanceSourceBindingCommand(workStore, identitySource, journalStore,
            exclusion, sourceAuthorization, new SourceCommandHelperIdentity(bindRequest.HelperBuildSha256!),
            sourceFactory, preparation, fenceVerifier);

        var sourceResult = sourceCommand.Execute();
        Assert.True(sourceResult.Status == "ok",
            $"Source binding failed with {sourceResult.Category}; events: {string.Join(" | ", events)}");
        Assert.Equal("sourceBound", journal.Read()!.Operations.Single().Phase);
        Assert.True(sourceClient.EvidenceSignatureVerified);
        Assert.True(firewall.Blocked);
        Assert.True(serviceApi.AllStopped);

        workStore.SelectCommand("apply");
        var applyEvents = events;
        var applyServices = new ApplyServices(applyEvents);
        applyServices.OnStart = _ =>
        {
            serviceApi.Start(MaintenanceServiceQuiescer.GuardServiceName);
            serviceApi.Start(MaintenanceServiceQuiescer.SunshineServiceName);
            serviceApi.Start(MaintenanceServiceQuiescer.AgentServiceName);
        };
        var applyAuthorization = new OrchestrationApplyAuthorization(applyRequest, exclusionScope,
            serviceApi, fenceVerifier, applyEvents);
        var applyBackupAuthorization = new Authorization { IsQuiesced = () => !applyServices.IsRunning };
        var applyPublisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, NewSecurity(), applyBackupAuthorization);
        var applyBackupOperation = new MaintenanceBackupOperation(sourceRoot,
            new InstalledAgentSourceInspector(sourceRoot, NewSecurity()), applyPublisher, journal);
        var applyFileSystem = new ApplyFileSystem(applyEvents);
        var sunshineProvider = new TrustedSunshinePathProvider(sunshinePath, applyEvents);
        var operationFactory = new ApplyCommandOperationFactory(sourceRoot, journal, applyBackupOperation,
            applyFileSystem, applyServices, sunshinePath, workStore.ApplyExtractionRoot, applyEvents);
        var applyCommand = new MaintenanceApplyCommand(engine, exclusion, applyAuthorization,
            sunshineProvider, operationFactory, journalStore, workStore, fenceVerifier);

        var applyResult = applyCommand.Execute();
        Assert.Equal("applied", applyResult.ApplyOutcome);
        Assert.Equal("servicesStarted", journal.Read()!.Operations.Single().Phase);
        Assert.True(applyServices.IsRunning);
        Assert.True(firewall.Blocked);
        Assert.Equal("operation-1", workStore.StoredApplyMetadata!.OperationId);

        workStore.ReadLifecycleRequest("report");
        var terminalVerifier = new MaintenanceTerminalStateVerifier(sourceRoot, applyBackupOperation,
            applyFileSystem, applyServices, engine, workStore);
        var reportClient = new OrchestrationReportClient(identity.MachineId, reportRequest, exclusionScope,
            firewall, serviceApi, events);
        var reportCommand = new MaintenanceReportCommand(workStore, identitySource, journalStore, reportClient,
            exclusion, terminalVerifier, fenceVerifier);

        var reportResult = reportCommand.Execute();

        Assert.Equal("awaitingHeartbeat", reportResult.ApplyOutcome);
        Assert.Equal("outcomeReported", journal.Read()!.Operations.Single().Phase);
        Assert.Equal(1, workStore.ApplyMetadataReads);
        Assert.True(firewall.Blocked, "The Controller still has not confirmed the target, so the fence stays held.");
        Assert.True(exclusionScope.IsHeld, "The operation-wide exclusion remains held through the complete orchestration.");
        Assert.Contains("qualified-source-challenge-after-backup", events);
        Assert.True(events.IndexOf("controller-start-acknowledged") < events.IndexOf("archive-open:bind-source"));
        Assert.True(events.IndexOf("firewall-blocked") < events.FindIndex(value => value.StartsWith("service-stop:", StringComparison.Ordinal)));
        Assert.True(events.IndexOf("apply-metadata-persisted") < events.FindIndex(value => value.StartsWith("archive-open:apply", StringComparison.Ordinal)));
        Assert.True(events.IndexOf("controller-report-acknowledged") > events.FindIndex(value => value.StartsWith("verify-running:", StringComparison.Ordinal)));
        Assert.DoesNotContain("ticket", JsonSerializer.Serialize(workStore.StoredApplyMetadata,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.OrdinalIgnoreCase);
    }
}
