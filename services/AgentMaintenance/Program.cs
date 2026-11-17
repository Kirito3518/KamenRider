using System.Runtime.Versioning;

namespace KamenRaider.AgentMaintenance;

internal static class Program
{
    public static int Main(string[] args) =>
        AgentMaintenanceCli.Run(args, OperatingSystem.IsWindows() ? CreateWindowsHost() : new UnsupportedPlatformCommandHost(),
            Console.Out, Console.Error);

    [SupportedOSPlatform("windows")]
    private static IAgentMaintenanceCommandHost CreateWindowsHost()
    {
        var security = new WindowsMaintenanceBackupSecurity();
        var inspector = new InstalledAgentSourceInspector(security.SourceRoot, security);
        var workStore = new WindowsMaintenanceWorkStore();
        var journalStore = new WindowsMaintenanceJournalStore();
        var identitySource = new WindowsTrustedIdentitySource();
        var engine = new LocalUpdateEngine(workStore);
        var lifecycleClient = HttpMaintenanceLifecycleClient.CreateDefault();

        // Production installation-wide lock: one new updater invocation at a time, held
        // through start/apply/report/reconcile. Busy means another updater is active.
        var exclusion = new WindowsMaintenanceInstallationLockProvider();

        // Local lease reconciliation and durable Controller start ACK evidence: a
        // corrupt/unknown or still-live lease blocks every service transition.
        var leaseAuthorization = new WindowsMaintenanceApplyLeaseAuthorization();
        var sourceBindingAuthorization = new WindowsMaintenanceSourceBindingAuthorization();

        // Trusted Sunshine resolution and the independent maintenance firewall fence are
        // resolved lazily so read-only commands (inspect/validate-archive/extract) keep
        // working on a host whose Sunshine installation is missing or being repaired.
        var trustedSunshine = new WindowsMaintenanceTrustedSunshinePathProvider();
        var sunshinePath = new Lazy<string>(trustedSunshine.ResolveInstalledExecutablePath,
            LazyThreadSafetyMode.ExecutionAndPublication);
        var fence = new WindowsMaintenanceFence(
            new WindowsMaintenanceFenceStore(), new WindowsPowerShellMaintenanceFirewallFence());
        var fenceVerifier = new LazyFenceVerifier(() =>
            new WindowsMaintenanceOperationFenceVerifier(fence, sunshinePath.Value));

        var sourceBindingFactory = new MaintenanceSourceBindingOperationFactory(
            security, security.SourceRoot, security.BackupRoot,
            HttpMaintenanceSourceBindingClient.CreateDefault(), new WindowsMaintenanceBootIdentityProvider());
        var preparation = new LazySourceBindingPreparation(() =>
        {
            var path = sunshinePath.Value;
            var layout = WindowsMaintenanceApplyOperationFactory.CreateServiceLayout(security.SourceRoot, path);
            return new MaintenanceSourceBindingPreparation(
                engine, fence, path, layout,
                new WindowsMaintenanceServiceApi(), new WindowsMaintenanceProcessInspector());
        });

        // Production apply composition: trusted Sunshine path, protected backup publisher,
        // real service/filesystem adapters and the terminal state verifier.
        var applyFactory = new WindowsMaintenanceApplyOperationFactory(security, inspector, journalStore, engine);
        var applyCommand = new MaintenanceApplyCommand(
            engine, exclusion, leaseAuthorization, trustedSunshine, applyFactory,
            journalStore: journalStore, applyMetadataStore: null, fenceVerifier: fenceVerifier);

        // Per-chain terminal verifier for the report command: the real backup/service/filesystem
        // adapters re-verify the immutable backup and installed tree under the same exclusion and
        // fence callback the report command already enforces.
        IMaintenanceTerminalStateVerifier CreateReportVerifier(string chainId, Action verifyHeld)
        {
            var path = sunshinePath.Value;
            var layout = WindowsMaintenanceApplyOperationFactory.CreateServiceLayout(security.SourceRoot, path);
            var reportJournal = new MaintenanceJournal(journalStore, chainId);
            var publisher = new MaintenanceBackupPublisher(
                security.SourceRoot, security.BackupRoot, security, new CallbackBackupAuthorization(verifyHeld));
            var backupOperation = new MaintenanceBackupOperation(
                security.SourceRoot, inspector, publisher, reportJournal);
            var fileSystem = new WindowsMaintenanceApplyFileSystem(security, workStore.ExtractionRoot);
            var services = new WindowsMaintenanceApplyServiceLifecycle(
                new WindowsMaintenanceServiceApi(),
                new WindowsMaintenanceProcessInspector(),
                new CallbackApplyAuthorization(verifyHeld),
                layout,
                _ => inspector.Inspect().Version);
            return new MaintenanceTerminalStateVerifier(
                security.SourceRoot, backupOperation, fileSystem, services, engine, engine.ApplyMetadataStore);
        }

        return new WindowsAgentMaintenanceCommandHost(
            engine, inspector, applyCommand,
            new MaintenanceReconcileCommand(engine, journalStore, exclusion,
                new MaintenanceApplyReconcileExecutor(applyCommand)),
            new MaintenanceStartCommand(workStore, identitySource, journalStore, lifecycleClient, exclusion),
            new MaintenanceSourceBindingCommand(workStore, identitySource, journalStore, exclusion,
                sourceBindingAuthorization, new WindowsMaintenanceHelperBuildIdentityProvider(),
                sourceBindingFactory, preparation, fenceVerifier),
            new MaintenanceReportCommand(workStore, identitySource, journalStore, lifecycleClient, exclusion,
                terminalStateVerifier: null, fenceVerifier: fenceVerifier,
                terminalStateVerifierFactory: CreateReportVerifier));
    }

    private sealed class UnsupportedPlatformCommandHost : IAgentMaintenanceCommandHost
    {
        public AgentMaintenanceCommandResult Execute(string command) =>
            new("unavailable", "windowsRequired", null, null, null, null, null, null);
    }

    /// <summary>
    /// Defers the fixed Sunshine/fence composition until a command actually needs it, so an
    /// unhealthy Sunshine installation only blocks start/bind-source/apply/report rather than
    /// read-only inspection of the installed Agent tree.
    /// </summary>
    private sealed class LazyFenceVerifier(Func<IMaintenanceOperationFenceVerifier> factory) : IMaintenanceOperationFenceVerifier
    {
        private readonly Lazy<IMaintenanceOperationFenceVerifier> verifier =
            new(factory ?? throw new ArgumentNullException(nameof(factory)), LazyThreadSafetyMode.ExecutionAndPublication);

        public void VerifyHeld(MaintenanceOperationBinding binding) => verifier.Value.VerifyHeld(binding);
    }

    private sealed class CallbackApplyAuthorization(Action verifyHeld) : IMaintenanceApplyAuthorization
    {
        private readonly Action verifyHeld = verifyHeld ?? throw new ArgumentNullException(nameof(verifyHeld));

        public void VerifyHeld(string operationId) => verifyHeld();

        public void VerifyHeldAndQuiesced(string operationId) => verifyHeld();
    }

    private sealed class CallbackBackupAuthorization(Action verifyHeld) : IMaintenanceBackupAuthorization
    {
        private readonly Action verifyHeld = verifyHeld ?? throw new ArgumentNullException(nameof(verifyHeld));

        public void VerifyHeld(string operationId) => verifyHeld();

        public void VerifyHeldAndQuiesced(string operationId) => verifyHeld();
    }

    private sealed class LazySourceBindingPreparation(Func<IMaintenanceSourceBindingPreparation> factory)
        : IMaintenanceSourceBindingPreparation
    {
        private readonly Lazy<IMaintenanceSourceBindingPreparation> preparation =
            new(factory ?? throw new ArgumentNullException(nameof(factory)), LazyThreadSafetyMode.ExecutionAndPublication);

        public void Prepare(
            MaintenanceLifecycleRequest request,
            MaintenanceJournal journal,
            IMaintenanceApplyProcessExclusionLease exclusion,
            IMaintenanceSourceBindingAuthorization authorization) =>
            preparation.Value.Prepare(request, journal, exclusion, authorization);
    }
}
