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
        builder.Services.AddSingleton<KamenRaider.Agent.Leases.IStreamGate,KamenRaider.Agent.Leases.ClosedStreamGate>();
        builder.Services.TryAddSingleton<KamenRaider.Agent.Startup.IBootEnvironment,KamenRaider.Agent.Startup.UnavailableBootEnvironment>();
        builder.Services.TryAddSingleton<KamenRaider.Agent.Startup.IBootBaselineEvidence,KamenRaider.Agent.Startup.UnvalidatedBootBaselineEvidence>();
        builder.Services.AddSingleton<KamenRaider.Agent.Power.WindowsShutdown>();
        builder.Services.AddSingleton<KamenRaider.Agent.Power.IShutdown>(sp=>new KamenRaider.Agent.Power.BoundedShutdown(sp.GetRequiredService<KamenRaider.Agent.Power.WindowsShutdown>(),TimeSpan.FromSeconds(15)));
        builder.Services.AddSingleton(sp=>new KamenRaider.Agent.Leases.LeaseStore(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value.DataDirectory));
        builder.Services.AddSingleton<KamenRaider.Agent.Leases.LeaseEnforcer>();
        builder.Services.TryAddSingleton<KamenRaider.Agent.Preparation.IMachinePreparation,KamenRaider.Agent.Preparation.UnavailableMachinePreparation>();
        builder.Services.TryAddSingleton<KamenRaider.Agent.Startup.IReconciledLeaseImporter,KamenRaider.Agent.Startup.LeaseEnforcerReconciledLeaseImporter>();
        builder.Services.AddSingleton<KamenRaider.Agent.Sync.LeaseCommandDispatcher>();
        builder.Services.AddHostedService<KamenRaider.Agent.Leases.LeaseEnforcementWorker>();
        builder.Services.AddHostedService<KamenRaider.Agent.Sync.SyncWorker>();
        return builder.Build();
    }
}
public class Program { public static void Main(string[] args) => AgentHost.Build(args).Run(); }
