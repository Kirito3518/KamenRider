using Microsoft.Extensions.DependencyInjection.Extensions;
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
        builder.Services.AddSingleton<KamenRaider.Agent.Leases.IBootClock,KamenRaider.Agent.Leases.WindowsBootClock>();
        builder.Services.TryAddSingleton<KamenRaider.Agent.Sunshine.StreamGuardPipeClient>();
        builder.Services.TryAddSingleton<KamenRaider.Agent.Leases.IStreamGate>(sp => sp.GetRequiredService<KamenRaider.Agent.Sunshine.StreamGuardPipeClient>());
        builder.Services.TryAddSingleton<KamenRaider.Agent.Sunshine.IPairingFence>(sp => sp.GetRequiredService<KamenRaider.Agent.Sunshine.StreamGuardPipeClient>());
        builder.Services.TryAddSingleton<KamenRaider.Agent.Startup.IBootEnvironment>(sp =>
            new KamenRaider.Agent.Startup.StreamGuardBootEnvironment(
                () => sp.GetRequiredService<KamenRaider.Agent.Sunshine.ISunshinePairingSession>()));
        builder.Services.TryAddSingleton<KamenRaider.Agent.Startup.IBootBaselineEvidence,KamenRaider.Agent.Startup.UnvalidatedBootBaselineEvidence>();
        builder.Services.AddSingleton<KamenRaider.Agent.Power.WindowsShutdown>();
        builder.Services.AddSingleton<KamenRaider.Agent.Power.IShutdown>(sp=>new KamenRaider.Agent.Power.BoundedShutdown(sp.GetRequiredService<KamenRaider.Agent.Power.WindowsShutdown>(),TimeSpan.FromSeconds(15)));
        builder.Services.AddSingleton(sp => new KamenRaider.Agent.Configuration.AppliedConfiguration(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value.DataDirectory));
        builder.Services.AddSingleton(sp=>new KamenRaider.Agent.Leases.LeaseStore(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value.DataDirectory));
        builder.Services.AddSingleton<KamenRaider.Agent.Leases.LeaseEnforcer>();
        builder.Services.TryAddSingleton<KamenRaider.Agent.Preparation.IMachinePreparation,KamenRaider.Agent.Preparation.FencedMachinePreparation>();
        builder.Services.TryAddSingleton<KamenRaider.Agent.Startup.IReconciledLeaseImporter,KamenRaider.Agent.Startup.LeaseEnforcerReconciledLeaseImporter>();
        builder.Services.TryAddSingleton<KamenRaider.Agent.Sunshine.ISunshinePairingSession>(sp =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Sunshine pairing credential storage requires Windows.");
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
            var configuration = sp.GetRequiredService<KamenRaider.Agent.Configuration.AppliedConfiguration>();
            var credentialStore = new KamenRaider.Agent.Sunshine.WindowsSunshineCredentialStore(
                options.DataDirectory,
                KamenRaider.Agent.Sunshine.WindowsSunshineCredentialStore.ResolveAgentServiceSid(),
                new KamenRaider.Agent.Sunshine.WindowsDpapiMachineSecretProtector());
            return new KamenRaider.Agent.Sunshine.LazySunshinePairingSession(
                credentialStore,
                () => configuration.Current?.SunshinePort ?? options.SunshinePort,
                sp.GetRequiredService<KamenRaider.Agent.Sunshine.IPairingFence>(),
                () => sp.GetRequiredService<KamenRaider.Agent.Leases.LeaseEnforcer>().CurrentPairingScope,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<KamenRaider.Agent.Sunshine.LazySunshinePairingSession>>());
        });
        builder.Services.AddSingleton<KamenRaider.Agent.Sync.LeaseCommandDispatcher>();
        builder.Services.AddHostedService<KamenRaider.Agent.Leases.LeaseEnforcementWorker>();
        builder.Services.AddHostedService<KamenRaider.Agent.Sync.SyncWorker>();
        return builder.Build();
    }
}

public static class StreamGuardHost
{
    public static IHost Build(string[] args, Action<HostApplicationBuilder>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        configure?.Invoke(builder);
        builder.Services.AddWindowsService(options => options.ServiceName = "KamenRaider Stream Guard");
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<KamenRaider.Agent.Leases.IBootClock,KamenRaider.Agent.Leases.WindowsBootClock>();
        builder.Services.TryAddSingleton<KamenRaider.Agent.Sunshine.IWindowsServiceApi>(_ =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("The Stream Guard service requires Windows.");
            return new KamenRaider.Agent.Sunshine.WindowsServiceApi();
        });
        builder.Services.TryAddSingleton<KamenRaider.Agent.Sunshine.SunshineServiceInstallation>(sp =>
            new KamenRaider.Agent.Sunshine.WindowsSunshineInstallationDiscovery(
                sp.GetRequiredService<KamenRaider.Agent.Sunshine.IWindowsServiceApi>()).Discover());
        builder.Services.TryAddSingleton<KamenRaider.Agent.Sunshine.IWindowsFirewallApi>(_ =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("The Stream Guard firewall provider requires Windows.");
            return new KamenRaider.Agent.Sunshine.WindowsFirewallComApi();
        });
        builder.Services.TryAddSingleton<KamenRaider.Agent.Sunshine.IStreamGrantHistoryStore>(_ =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("The Stream Guard grant history store requires Windows.");
            return new KamenRaider.Agent.Sunshine.WindowsStreamGrantHistoryStore();
        });
        builder.Services.TryAddSingleton<KamenRaider.Agent.Sunshine.IStreamIngressPolicy,KamenRaider.Agent.Sunshine.WindowsStreamIngressPolicy>();
        builder.Services.TryAddSingleton<KamenRaider.Agent.Sunshine.ISunshineServiceControl,KamenRaider.Agent.Sunshine.WindowsSunshineServiceControl>();
        builder.Services.AddSingleton<KamenRaider.Agent.Sunshine.StreamGateCoordinator>();
        builder.Services.AddSingleton<KamenRaider.Agent.Sunshine.IStreamGuardControl>(sp => sp.GetRequiredService<KamenRaider.Agent.Sunshine.StreamGateCoordinator>());
        builder.Services.AddSingleton<KamenRaider.Agent.Leases.IStreamGate>(sp => sp.GetRequiredService<KamenRaider.Agent.Sunshine.StreamGateCoordinator>());
        builder.Services.AddHostedService<KamenRaider.Agent.Sunshine.StreamGuardStartupFence>();
        builder.Services.TryAddSingleton<KamenRaider.Agent.Sunshine.IStreamGuardPipeServerFactory>(_ =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("The Stream Guard service can run only on Windows.");
            return new KamenRaider.Agent.Sunshine.WindowsStreamGuardPipeServerFactory();
        });
        builder.Services.AddSingleton<KamenRaider.Agent.Sunshine.StreamGuardPipeRequestHandler>();
        builder.Services.AddHostedService<KamenRaider.Agent.Sunshine.StreamGuardPipeServer>();
        builder.Services.AddHostedService<KamenRaider.Agent.Sunshine.StreamGuardDeadlineWorker>();
        return builder.Build();
    }

}

public class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--provision-sunshine-credentials", StringComparison.OrdinalIgnoreCase))
            return KamenRaider.Agent.Sunshine.SunshineCredentialProvisionCommand.Run(args);

        if (args.Any(argument => string.Equals(argument, "--stream-guard-recover", StringComparison.OrdinalIgnoreCase)))
        {
            KamenRaider.Agent.Sunshine.StreamGuardRecovery.Close();
            return 0;
        }

        if (args.Any(argument => string.Equals(argument, "--stream-guard", StringComparison.OrdinalIgnoreCase)))
        {
            StreamGuardHost.Build(args.Where(argument => !string.Equals(argument, "--stream-guard", StringComparison.OrdinalIgnoreCase)).ToArray()).Run();
            return 0;
        }

        AgentHost.Build(args).Run();
        return 0;
    }
}
