using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using KamenRaider.AgentMaintenance;
using KamenRaider.Contracts;

namespace Controller.Tests;

public sealed class MaintenanceLifecycleCommandTests
{
    private sealed class IdentitySource : ITrustedIdentitySource
    {
        public const string MachineId = "0123456789abcdef0123456789abcdef";
        public readonly TrustedIdentityDocument Document;
        public readonly string Fingerprint;
        public int Reads;

        public IdentitySource()
        {
            using var key = ECDsa.Create();
            var publicKey = key.ExportSubjectPublicKeyInfo();
            Document = new(MachineId, Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
            Fingerprint = Convert.ToHexString(SHA256.HashData(publicKey));
        }

        public TrustedIdentityDocument ReadExisting()
        {
            Reads++;
            return Document;
        }
    }

    private sealed class LifecycleWorkStore(MaintenanceLifecycleRequest request) : IMaintenanceLifecycleWorkStore
    {
        public int Reads;
        public MaintenanceLifecycleRequest ReadLifecycleRequest(string expectedCommand)
        {
            Reads++;
            Assert.Equal(expectedCommand, request.Command);
            return request;
        }
    }

    private sealed class JournalStore : IMaintenanceJournalStore
    {
        public MaintenanceJournalSnapshot? Snapshot;
        public int Writes;
        public MaintenanceJournalSnapshot? Read(string chainId)
        {
            Assert.Equal("chain-1", chainId);
            return Snapshot;
        }

        public void WriteAtomic(string chainId, MaintenanceJournalSnapshot snapshot)
        {
            Assert.Equal("chain-1", chainId);
            Writes++;
            Snapshot = snapshot;
        }
    }

    private sealed class ExclusionProvider(IMaintenanceApplyProcessExclusionLease? lease)
        : IMaintenanceApplyProcessExclusionProvider
    {
        public int Acquires;
        public IMaintenanceApplyProcessExclusionLease? Acquire()
        {
            Acquires++;
            return lease;
        }
    }

    private sealed class ExclusionLease : IMaintenanceApplyProcessExclusionLease
    {
        public int Verifications;
        public bool Disposed;
        public void VerifyContinuouslyHeld(string operationId)
        {
            Verifications++;
            Assert.Equal("op-1", operationId);
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class StartClient(MaintenanceLifecycleRequest request, bool acknowledgeHeld = true) : IMaintenanceStartClient
    {
        public int Calls;
        public AgentUpdateTicketRequest? Sent;
        public int? TargetProtocolOverride { get; set; }
        public Task<AgentUpdateOperationDto> StartAsync(
            string controllerOrigin,
            string machineId,
            string operationId,
            AgentUpdateTicketRequest body,
            CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("https://controller.example/", controllerOrigin);
            Assert.Equal(IdentitySource.MachineId, machineId);
            Assert.Equal("op-1", operationId);
            cancellationToken.ThrowIfCancellationRequested();
            Sent = body;
            return Task.FromResult(Dto(request, "installing") with
            {
                Held = acknowledgeHeld,
                TargetProtocol = TargetProtocolOverride ?? 2
            });
        }
    }

    private sealed class ReportClient(MaintenanceLifecycleRequest responseRequest) : IMaintenanceReportClient
    {
        public int Calls;
        public AgentUpdateStatusReportRequest? Sent;
        public Task<AgentUpdateOperationDto> ReportAsync(
            string controllerOrigin,
            string machineId,
            string operationId,
            AgentUpdateStatusReportRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("https://controller.example/", controllerOrigin);
            Assert.Equal(IdentitySource.MachineId, machineId);
            Assert.Equal("op-1", operationId);
            Sent = request;
            return Task.FromResult(Dto(responseRequest, request.Status));
        }
    }

    private sealed class TerminalStateVerifier : IMaintenanceTerminalStateVerifier
    {
        public int Calls;
        public MaintenanceTerminalOutcome? VerifiedOutcome;
        public bool Reject;

        public Task VerifyAsync(MaintenanceOperationBinding binding, MaintenanceJournalOperation operation,
            MaintenanceTerminalOutcome expectedOutcome, KamenRaider.AgentPackage.PackageInventory? expectedTarget,
            Action verifyHeld, CancellationToken cancellationToken)
        {
            Calls++;
            verifyHeld();
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(binding, operation.Binding);
            Assert.Null(expectedTarget);
            VerifiedOutcome = expectedOutcome;
            if (Reject) throw new InvalidDataException("simulated terminal state mismatch");
            return Task.CompletedTask;
        }
    }

    private sealed class HelperIdentity : IMaintenanceHelperBuildIdentityProvider
    {
        public int Reads;
        public string ReadSha256()
        {
            Reads++;
            return new string('f', 64);
        }
    }

    private sealed class NeverCreateSourceBindingOperation : IMaintenanceSourceBindingOperationFactory
    {
        public int Calls;
        public MaintenanceSourceBindingOperation Create(
            MaintenanceLifecycleRequest request,
            MaintenanceJournal journal,
            TrustedIdentity identity,
            IMaintenanceApplyProcessExclusionLease exclusion,
            IMaintenanceSourceBindingAuthorization authorization)
        {
            Calls++;
            throw new InvalidOperationException("The source-binding operation must not be created without proof.");
        }
    }

    private sealed class SourceBindingAuthorization : IMaintenanceSourceBindingAuthorization
    {
        public void VerifyRequestBinding(MaintenanceLifecycleRequest request) =>
            throw new InvalidOperationException("The source-binding authorization must not run without proof.");
        public void VerifyHeldAndQuiesced(string operationId) =>
            throw new InvalidOperationException("The source-binding authorization must not run without proof.");
        public void VerifyHeld(string operationId) =>
            throw new InvalidOperationException("The source-binding authorization must not run without proof.");
    }

    private sealed class RecordingSourceBindingAuthorization : IMaintenanceSourceBindingAuthorization
    {
        public int RequestChecks;
        public int HeldAndQuiescedChecks;
        public int HeldChecks;
        public void VerifyRequestBinding(MaintenanceLifecycleRequest request) => RequestChecks++;
        public void VerifyHeldAndQuiesced(string operationId) => HeldAndQuiescedChecks++;
        public void VerifyHeld(string operationId) => HeldChecks++;
    }

    private static string NativeRuntime() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.Arm64 => "win-arm64",
        _ => "win-x64"
    };

    private static MaintenanceLifecycleRequest Request(string command, string fingerprint) => new(
        1, command, "op-1", "chain-1", "op-1", 1, "https://controller.example/",
        fingerprint, 1, "2.3.4", NativeRuntime(), new string('b', 64), "2.3.4", new string('T', 43));

    private static AgentUpdateOperationDto Dto(MaintenanceLifecycleRequest request, string status) => new(
        request.OperationId, IdentitySource.MachineId, request.ChainId, request.HeadOperationId,
        request.Generation, true, null, request.ReleaseVersion, request.TargetVersion,
        request.TargetPackageSha256, request.TargetRuntime, 2, null, null, null, null, null,
        status, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public void LifecycleParserAcceptsOnlyTheFixedTicketScopedSchema()
    {
        const string prefix = "{\"schemaVersion\":1,\"command\":\"start\",\"operationId\":\"op-1\",\"chainId\":\"chain-1\",\"headOperationId\":\"op-1\",\"generation\":1,\"controllerOrigin\":\"https://controller.example/\",\"enrollmentKeyFingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"helperProtocol\":1,\"targetVersion\":\"2.3.4\",\"targetRuntime\":\"win-x64\",\"targetPackageSha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"releaseVersion\":\"2.3.4\",\"ticket\":\"";
        var json = Encoding.UTF8.GetBytes(prefix + new string('T', 43) + "\"}");

        var parsed = MaintenanceLifecycleRequestParser.Parse(json, "start");

        Assert.Equal("op-1", parsed.OperationId);
        Assert.Equal("chain-1", parsed.ChainId);
        Assert.Equal(43, parsed.Ticket.Length);
        Assert.Throws<InvalidDataException>(() => MaintenanceLifecycleRequestParser.Parse(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(json).Replace("}", ",\"executablePath\":\"C:\\\\bad.exe\"}", StringComparison.Ordinal)),
            "start"));
    }

    [Fact]
    public void StartReturnsBeforeReadingOrWritingWhenExclusionIsUnqualified()
    {
        var identity = new IdentitySource();
        var workStore = new LifecycleWorkStore(Request("start", identity.Fingerprint));
        var journal = new JournalStore();
        var exclusion = new ExclusionProvider(null);
        var client = new StartClient(Request("start", identity.Fingerprint));
        var command = new MaintenanceStartCommand(workStore, identity, journal, client, exclusion);

        var result = command.Execute();

        Assert.Equal("unavailable", result.Status);
        Assert.Equal("legacyProcessExclusionUnqualified", result.Category);
        Assert.Equal(0, workStore.Reads);
        Assert.Equal(0, identity.Reads);
        Assert.Equal(0, journal.Writes);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public void StartUsesTheScopedTicketAndPersistsOnlyAValidatedControllerAcknowledgement()
    {
        var identity = new IdentitySource();
        var request = Request("start", identity.Fingerprint);
        var workStore = new LifecycleWorkStore(request);
        var journal = new JournalStore();
        var lease = new ExclusionLease();
        var client = new StartClient(request);
        var command = new MaintenanceStartCommand(workStore, identity, journal, client, new ExclusionProvider(lease));

        var result = command.Execute();

        Assert.Equal("ok", result.Status);
        Assert.Equal("started", result.ApplyOutcome);
        Assert.Equal(1, client.Calls);
        Assert.Equal(new string('T', 43), client.Sent!.Ticket);
        Assert.Equal("2.3.4", client.Sent.ReleaseVersion);
        Assert.Equal("2.3.4", client.Sent.TargetVersion);
        Assert.Equal(new string('b', 64), client.Sent.TargetPackageSha256);
        Assert.StartsWith("start-", client.Sent.RequestId, StringComparison.Ordinal);
        Assert.Equal("controllerStarted", journal.Snapshot!.Operations.Single().Phase);
        Assert.Equal(2, lease.Verifications);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void StartKeepsJournalAtInspectedWhenControllerDoesNotConfirmTheHeldHead()
    {
        var identity = new IdentitySource();
        var request = Request("start", identity.Fingerprint);
        var journal = new JournalStore();
        var lease = new ExclusionLease();
        var client = new StartClient(request, acknowledgeHeld: false);
        var command = new MaintenanceStartCommand(new LifecycleWorkStore(request), identity, journal,
            client, new ExclusionProvider(lease));

        Assert.Throws<InvalidOperationException>(() => command.Execute());

        Assert.Equal(1, client.Calls);
        Assert.Equal("inspected", journal.Snapshot!.Operations.Single().Phase);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void StartKeepsJournalAtInspectedWhenControllerAcknowledgesAnotherTargetProtocol()
    {
        var identity = new IdentitySource();
        var request = Request("start", identity.Fingerprint);
        var journal = new JournalStore();
        var lease = new ExclusionLease();
        var client = new StartClient(request) { TargetProtocolOverride = 1 };
        var command = new MaintenanceStartCommand(new LifecycleWorkStore(request), identity, journal,
            client, new ExclusionProvider(lease));

        Assert.Throws<InvalidOperationException>(() => command.Execute());

        Assert.Equal(1, client.Calls);
        Assert.Equal("inspected", journal.Snapshot!.Operations.Single().Phase);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void SourceBindingDoesNotReadIdentityOrBackupStateBeforeContinuousExclusion()
    {
        var identity = new IdentitySource();
        var workStore = new LifecycleWorkStore(Request("bind-source", identity.Fingerprint));
        var journal = new JournalStore();
        var helper = new HelperIdentity();
        var factory = new NeverCreateSourceBindingOperation();
        var command = new MaintenanceSourceBindingCommand(workStore, identity, journal,
            new ExclusionProvider(null), new SourceBindingAuthorization(), helper, factory);

        var result = command.Execute();

        Assert.Equal("unavailable", result.Status);
        Assert.Equal("legacyProcessExclusionUnqualified", result.Category);
        Assert.Equal(0, workStore.Reads);
        Assert.Equal(0, identity.Reads);
        Assert.Equal(0, helper.Reads);
        Assert.Equal(0, factory.Calls);
        Assert.Null(journal.Snapshot);
    }

    [Fact]
    public void SourceBindingCannotTreatControllerStartAcknowledgementAsTargetValidationOrQuiescence()
    {
        var identity = new IdentitySource();
        var startRequest = Request("start", identity.Fingerprint);
        var journal = new JournalStore();
        var start = new MaintenanceStartCommand(new LifecycleWorkStore(startRequest), identity, journal,
            new StartClient(startRequest), new ExclusionProvider(new ExclusionLease()));
        Assert.Equal("ok", start.Execute().Status);

        var bindingRequest = Request("bind-source", identity.Fingerprint) with
        {
            HelperBuildSha256 = new string('f', 64)
        };
        var authorization = new RecordingSourceBindingAuthorization();
        var operation = new NeverCreateSourceBindingOperation();
        var command = new MaintenanceSourceBindingCommand(new LifecycleWorkStore(bindingRequest), identity, journal,
            new ExclusionProvider(new ExclusionLease()), authorization, new HelperIdentity(), operation);

        var result = command.Execute();

        Assert.Equal("unavailable", result.Status);
        Assert.Equal("targetOrQuiescenceUnverified", result.Category);
        Assert.Equal("controllerStarted", journal.Snapshot!.Operations.Single().Phase);
        Assert.Equal(0, authorization.HeldAndQuiescedChecks);
        Assert.Equal(0, operation.Calls);
    }

    [Fact]
    public void ReportDoesNotContactControllerBeforeServicesReachAnOutcomePhase()
    {
        var identity = new IdentitySource();
        var startRequest = Request("start", identity.Fingerprint);
        var workStore = new LifecycleWorkStore(startRequest);
        var journal = new JournalStore();
        var startLease = new ExclusionLease();
        var startClient = new StartClient(startRequest);
        var start = new MaintenanceStartCommand(workStore, identity, journal, startClient,
            new ExclusionProvider(startLease));
        Assert.Equal("ok", start.Execute().Status);

        var reportRequest = startRequest with { Command = "report" };
        var reportClient = new ReportClient(reportRequest);
        var report = new MaintenanceReportCommand(new LifecycleWorkStore(reportRequest), identity, journal,
            reportClient, new ExclusionProvider(new ExclusionLease()));

        var result = report.Execute();

        Assert.Equal("unavailable", result.Status);
        Assert.Equal("outcomeNotReady", result.Category);
        Assert.Equal(0, reportClient.Calls);
        Assert.Equal("controllerStarted", journal.Snapshot!.Operations.Single().Phase);
    }

    [Fact]
    public void ReportSendsOnlyTheVerifiedOutcomeAndKeepsTheMaintenanceHoldOpen()
    {
        var identity = new IdentitySource();
        var request = Request("report", identity.Fingerprint);
        var binding = MaintenanceLifecycleValidation.CreateBinding(request);
        var journal = new JournalStore { Snapshot = MaintenanceReconcileCommandTests.CreateServicesStartedJournal(binding) };
        var client = new ReportClient(request);
        var lease = new ExclusionLease();
        var terminalVerifier = new TerminalStateVerifier();
        var command = new MaintenanceReportCommand(new LifecycleWorkStore(request), identity, journal,
            client, new ExclusionProvider(lease), terminalVerifier);

        var result = command.Execute();

        Assert.Equal("ok", result.Status);
        Assert.Equal("awaitingHeartbeat", result.ApplyOutcome);
        Assert.Equal(1, client.Calls);
        Assert.Equal("awaitingHeartbeat", client.Sent!.Status);
        Assert.Equal("2.3.4", client.Sent.TargetVersion);
        Assert.Equal(new string('T', 43), client.Sent.Ticket);
        Assert.Equal("outcomeReported", journal.Snapshot!.Operations.Single().Phase);
        Assert.Equal(1, terminalVerifier.Calls);
        Assert.Equal(MaintenanceTerminalOutcome.Applied, terminalVerifier.VerifiedOutcome);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void ReportFailsClosedBeforeControllerCallWhenTerminalStateVerifierIsMissing()
    {
        var identity = new IdentitySource();
        var request = Request("report", identity.Fingerprint);
        var binding = MaintenanceLifecycleValidation.CreateBinding(request);
        var journal = new JournalStore { Snapshot = MaintenanceReconcileCommandTests.CreateServicesStartedJournal(binding) };
        var client = new ReportClient(request);
        var lease = new ExclusionLease();
        var command = new MaintenanceReportCommand(new LifecycleWorkStore(request), identity, journal,
            client, new ExclusionProvider(lease));

        var result = command.Execute();

        Assert.Equal("unavailable", result.Status);
        Assert.Equal("terminalStateUnverifiable", result.Category);
        Assert.Equal(0, client.Calls);
        Assert.Equal("servicesStarted", journal.Snapshot!.Operations.Single().Phase);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void ReportDoesNotPostOrAdvanceWhenTerminalVerifierRejectsTheInstalledState()
    {
        var identity = new IdentitySource();
        var request = Request("report", identity.Fingerprint);
        var binding = MaintenanceLifecycleValidation.CreateBinding(request);
        var journal = new JournalStore { Snapshot = MaintenanceReconcileCommandTests.CreateServicesStartedJournal(binding) };
        var client = new ReportClient(request);
        var lease = new ExclusionLease();
        var terminalVerifier = new TerminalStateVerifier { Reject = true };
        var command = new MaintenanceReportCommand(new LifecycleWorkStore(request), identity, journal,
            client, new ExclusionProvider(lease), terminalVerifier);

        var result = command.Execute();

        Assert.Equal("unavailable", result.Status);
        Assert.Equal("terminalStateUnverifiable", result.Category);
        Assert.Equal(1, terminalVerifier.Calls);
        Assert.Equal(0, client.Calls);
        Assert.Equal("servicesStarted", journal.Snapshot!.Operations.Single().Phase);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void ReportUsesTheProductionTerminalVerifierFactoryWhenNoInstanceIsProvided()
    {
        var identity = new IdentitySource();
        var request = Request("report", identity.Fingerprint);
        var binding = MaintenanceLifecycleValidation.CreateBinding(request);
        var journal = new JournalStore { Snapshot = MaintenanceReconcileCommandTests.CreateServicesStartedJournal(binding) };
        var client = new ReportClient(request);
        var lease = new ExclusionLease();
        var factoryVerifier = new TerminalStateVerifier();
        string? factoryChainId = null;
        Action? factoryVerifyHeld = null;
        var command = new MaintenanceReportCommand(new LifecycleWorkStore(request), identity, journal,
            client, new ExclusionProvider(lease),
            terminalStateVerifierFactory: (chainId, verifyHeld) =>
            {
                factoryChainId = chainId;
                factoryVerifyHeld = verifyHeld;
                return factoryVerifier;
            });

        var result = command.Execute();

        Assert.Equal("ok", result.Status);
        Assert.Equal(request.ChainId, factoryChainId);
        Assert.NotNull(factoryVerifyHeld);
        Assert.Equal(1, factoryVerifier.Calls);
        Assert.Equal(1, client.Calls);
        Assert.Equal("outcomeReported", journal.Snapshot!.Operations.Single().Phase);
        Assert.True(lease.Disposed);
    }
}
