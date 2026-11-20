using Microsoft.Extensions.DependencyInjection.Extensions;
using KamenRaider.Agent.Configuration;
using KamenRaider.Agent.Hardware;
using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Power;
using KamenRaider.Agent.Preparation;
using KamenRaider.Agent.Startup;
using KamenRaider.Agent.Sunshine;
using KamenRaider.Agent.Sync;

namespace KamenRaider.Agent;
public sealed class AgentOptions
{
    public string ControllerUrl { get; set; } = "";
    public string DataDirectory { get; set; } = "";
    public int MoonlightPort { get; set; }
    public int SunshinePort { get; set; }
    public string NetworkAdapterId { get; set; } = "";
    public bool IsValid() => Uri.TryCreate(ControllerUrl, UriKind.Absolute, out var url) && url.Scheme == "https"
        && Path.IsPathFullyQualified(DataDirectory) && MoonlightPort is > 0 and <= 65535
        && SunshinePort is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(NetworkAdapterId);
}
public static class AgentHost
{
    public static IHost Build(string[] args, Action<HostApplicationBuilder>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        configure?.Invoke(builder);
        builder.Services.AddWindowsService(options => options.ServiceName = "KamenRaider Agent");
        builder.Services.AddOptions<AgentOptions>().Bind(builder.Configuration)
            .Validate(o => o.IsValid(), "HTTPS ControllerUrl, absolute DataDirectory, valid MoonlightPort/SunshinePort and NetworkAdapterId are required.").ValidateOnStart();
        builder.Services.AddHostedService<KamenRaider.Agent.Enrollment.EnrollmentWorker>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<KamenRaider.Agent.Hardware.IHardwareSource, KamenRaider.Agent.Hardware.WindowsHostHardwareSource>();
        builder.Services.TryAddSingleton<KamenRaider.Agent.Hardware.HostHardwareCollector>();
        builder.Services.TryAddSingleton<KamenRaider.Agent.Sunshine.SunshineCredentialSyncSignal>();
        builder.Services.TryAddSingleton(sp =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Sunshine credential storage requires Windows ACLs.");
            return new KamenRaider.Agent.Sunshine.SunshineAppliedCredentialStore(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value.DataDirectory,
                KamenRaider.Agent.Sunshine.WindowsSunshineCredentialStore.ResolveAgentServiceSid());
        });
        builder.Services.TryAddSingleton(typeof(KamenRaider.Agent.Sunshine.WindowsSunshineCredentialStore), sp =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Sunshine credential storage requires Windows ACLs.");
            return new KamenRaider.Agent.Sunshine.WindowsSunshineCredentialStore(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value.DataDirectory,
                KamenRaider.Agent.Sunshine.WindowsSunshineCredentialStore.ResolveAgentServiceSid(),
                new KamenRaider.Agent.Sunshine.WindowsDpapiMachineSecretProtector());
        });
        builder.Services.TryAddSingleton<KamenRaider.Agent.Sunshine.IMaintenanceFenceReader, KamenRaider.Agent.Sunshine.WindowsMaintenanceFenceReader>();
        builder.Services.AddSingleton<KamenRaider.Agent.Leases.IBootClock, KamenRaider.Agent.Leases.WindowsBootClock>();

        // 0jay-style Sunshine control: local REST only. Liveness of the two Sunshine endpoints
        // decides readiness; pairing/unpairing is the only stream gate.
        builder.Services.AddSingleton<KamenRaider.Agent.Sunshine.ISunshineLivenessProbe, KamenRaider.Agent.Sunshine.SunshineLivenessProbe>();
        builder.Services.AddSingleton<KamenRaider.Agent.Sunshine.SunshineLivenessMonitor>();
        builder.Services.AddSingleton<KamenRaider.Agent.Sunshine.ISunshineLiveness>(sp =>
            sp.GetRequiredService<KamenRaider.Agent.Sunshine.SunshineLivenessMonitor>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<KamenRaider.Agent.Sunshine.SunshineLivenessMonitor>());

        // Parsec renter-account integration: boot key + parsecd capability, desktop UI automation,
        // signed status reporting, and one-attempt-at-a-time manager.
        builder.Services.AddSingleton<KamenRaider.Agent.Parsec.ParsecCapabilityProvider>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<KamenRaider.Agent.Parsec.ParsecCapabilityProvider>());
        builder.Services.AddSingleton<KamenRaider.Agent.Parsec.IParsecDesktopAutomation, KamenRaider.Agent.Parsec.ParsecInteractiveRunner>();
        builder.Services.AddSingleton<KamenRaider.Agent.Identity.IAgentRequestSigner>(sp =>
            KamenRaider.Agent.Identity.DeviceIdentity.LoadOrCreate(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value.DataDirectory));
        builder.Services.AddSingleton<KamenRaider.Agent.Parsec.IParsecStatusReporter>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
            return new KamenRaider.Agent.Parsec.ParsecStatusHttpReporter(
                new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
                new Uri(options.ControllerUrl),
                sp.GetRequiredService<KamenRaider.Agent.Identity.IAgentRequestSigner>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<KamenRaider.Agent.Parsec.ParsecStatusHttpReporter>>());
        });
        builder.Services.AddSingleton(sp =>
        {
            var capability = sp.GetRequiredService<KamenRaider.Agent.Parsec.ParsecCapabilityProvider>();
            return new KamenRaider.Agent.Parsec.ParsecManager(
                capability,
                sp.GetRequiredService<KamenRaider.Agent.Parsec.IParsecStatusReporter>(),
                sp.GetRequiredService<KamenRaider.Agent.Parsec.IParsecDesktopAutomation>(),
                sp.GetRequiredService<ILogger<KamenRaider.Agent.Parsec.ParsecManager>>(),
                sp.GetRequiredService<TimeProvider>(),
                () => capability.ExecutablePath);
        });
        builder.Services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
            var configuration = sp.GetRequiredService<KamenRaider.Agent.Configuration.AppliedConfiguration>();
            var appliedCredentialStore = sp.GetRequiredService<KamenRaider.Agent.Sunshine.SunshineAppliedCredentialStore>();
            var credentialStore = sp.GetRequiredService<KamenRaider.Agent.Sunshine.WindowsSunshineCredentialStore>();
            var liveness = sp.GetRequiredService<KamenRaider.Agent.Sunshine.ISunshineLiveness>();
            return new KamenRaider.Agent.Sunshine.SunshineService(
                () => configuration.Current,
                () =>
                {
                    try { return appliedCredentialStore.Read(); }
                    catch (Exception) { return 0; }
                },
                () => credentialStore.Load(),
                () => liveness.Current.Ready,
                () => sp.GetRequiredService<KamenRaider.Agent.Leases.LeaseEnforcer>().CurrentPairingScope,
                liveness,
                Microsoft.Extensions.Options.Options.Create(options),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<KamenRaider.Agent.Sunshine.SunshineService>>());
        });
        builder.Services.AddSingleton<KamenRaider.Agent.Sunshine.ISunshinePairingSession>(sp =>
            sp.GetRequiredService<KamenRaider.Agent.Sunshine.SunshineService>());
        builder.Services.AddSingleton<KamenRaider.Agent.Sunshine.IStreamCleanup>(sp =>
            new KamenRaider.Agent.Leases.AgentStreamCleanup(
                sp.GetRequiredService<KamenRaider.Agent.Sunshine.ISunshinePairingSession>(),
                sp.GetRequiredService<KamenRaider.Agent.Parsec.ParsecManager>(),
                sp.GetRequiredService<ILogger<KamenRaider.Agent.Leases.AgentStreamCleanup>>()));
        builder.Services.AddSingleton<KamenRaider.Agent.Sunshine.ISunshineAdmin>(sp =>
            sp.GetRequiredService<KamenRaider.Agent.Sunshine.SunshineService>());

        // TEST ONLY: KAMENRAIDER_AGENT_LAB_READY=1 lets a lab machine report ready without the
        // hardware-qualified boot/restoration/desktop evidence providers. It must never be
        // set on a machine that is actually rented out. Sunshine liveness is still required.
        if (string.Equals(Environment.GetEnvironmentVariable("KAMENRAIDER_AGENT_LAB_READY"), "1", StringComparison.Ordinal))
        {
            builder.Services.TryAddSingleton<KamenRaider.Agent.Startup.IBootEnvironment, KamenRaider.Agent.Startup.LabBootEnvironment>();
            builder.Services.TryAddSingleton<KamenRaider.Agent.Startup.IBootBaselineEvidence, KamenRaider.Agent.Startup.LabBootBaselineEvidence>();
        }
        builder.Services.TryAddSingleton<KamenRaider.Agent.Startup.IBootEnvironment>(sp =>
            new KamenRaider.Agent.Startup.AgentBootEnvironment(
                () => sp.GetRequiredService<KamenRaider.Agent.Sunshine.ISunshinePairingSession>(),
                () => sp.GetService<KamenRaider.Agent.Parsec.ParsecManager>(),
                sp.GetService<ILogger<KamenRaider.Agent.Startup.AgentBootEnvironment>>()));
        builder.Services.TryAddSingleton<KamenRaider.Agent.Startup.IBootBaselineEvidence, KamenRaider.Agent.Startup.UnvalidatedBootBaselineEvidence>();
        builder.Services.AddSingleton<KamenRaider.Agent.Power.WindowsShutdown>();
        builder.Services.AddSingleton<KamenRaider.Agent.Power.IShutdown>(sp => new KamenRaider.Agent.Power.BoundedShutdown(sp.GetRequiredService<KamenRaider.Agent.Power.WindowsShutdown>(), TimeSpan.FromSeconds(15)));
        builder.Services.AddSingleton(sp => new KamenRaider.Agent.Configuration.AppliedConfiguration(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value.DataDirectory));
        builder.Services.AddSingleton(sp => new KamenRaider.Agent.Leases.LeaseStore(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value.DataDirectory));
        builder.Services.AddSingleton<KamenRaider.Agent.Leases.LeaseEnforcer>();
        builder.Services.TryAddSingleton<KamenRaider.Agent.Preparation.IMachinePreparation, KamenRaider.Agent.Preparation.LeaseMachinePreparation>();
        builder.Services.TryAddSingleton<KamenRaider.Agent.Startup.IReconciledLeaseImporter, KamenRaider.Agent.Startup.LeaseEnforcerReconciledLeaseImporter>();
        builder.Services.AddSingleton<KamenRaider.Agent.Sync.LeaseCommandDispatcher>();
        builder.Services.AddHostedService<KamenRaider.Agent.Leases.LeaseEnforcementWorker>();
        builder.Services.AddHostedService<KamenRaider.Agent.Sync.SyncWorker>();
        // Applies the fleet-wide Sunshine management credential delivered over signed HTTPS.
        // This is Agent-only: it never runs Sunshine.exe --creds or edits Sunshine's own configuration.
        builder.Services.AddHostedService<KamenRaider.Agent.Sunshine.SunshineCredentialWorker>();
        return builder.Build();
    }
}

public class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--provision-sunshine-credentials", StringComparison.OrdinalIgnoreCase))
            return KamenRaider.Agent.Sunshine.SunshineCredentialProvisionCommand.Run(args);

        AgentHost.Build(args).Run();
        return 0;
    }
}
