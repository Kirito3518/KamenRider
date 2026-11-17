using System.Runtime.Versioning;
using KamenRaider.Agent.Hardware;
using KamenRaider.Agent.Sunshine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agent.Tests;

public class SunshineCredentialHostCompositionTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void AgentHostRegistersHardwareCollectorAndSunshineCredentialWorker()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dataDirectory = Path.Combine(Path.GetTempPath(), "agent-host-sunshine-credential-" + Guid.NewGuid().ToString("N"));
        using var host = KamenRaider.Agent.AgentHost.Build([], builder => ConfigureTestAgentOptions(builder, dataDirectory));

        var hostedServices = host.Services.GetServices<IHostedService>().ToList();
        Assert.Single(hostedServices.OfType<SunshineCredentialWorker>());
        Assert.Single(hostedServices.OfType<KamenRaider.Agent.Sync.SyncWorker>());
        Assert.IsType<WindowsHostHardwareSource>(host.Services.GetRequiredService<IHardwareSource>());
        Assert.NotNull(host.Services.GetRequiredService<HostHardwareCollector>());
        Assert.NotNull(host.Services.GetRequiredService<SunshineCredentialSyncSignal>());
        Assert.NotNull(host.Services.GetRequiredService<SunshineAppliedCredentialStore>());
        Assert.NotNull(host.Services.GetRequiredService<WindowsSunshineCredentialStore>());
        Assert.IsType<LazySunshinePairingSession>(host.Services.GetRequiredService<ISunshinePairingSession>());
    }

    private static void ConfigureTestAgentOptions(HostApplicationBuilder builder, string dataDirectory)
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ControllerUrl"] = "https://controller.test",
            ["DataDirectory"] = dataDirectory,
            ["MoonlightPort"] = "47989",
            ["SunshinePort"] = "47984",
            ["NetworkAdapterId"] = "test-adapter"
        });
        var pipe = new KamenRaider.Agent.Sunshine.StreamGuardPipeClient(
            $"KamenRaider.Test.Missing.{Guid.NewGuid():N}", TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150));
        builder.Services.AddSingleton(pipe);
        builder.Services.AddSingleton<KamenRaider.Agent.Leases.IStreamGate>(pipe);
    }
}
