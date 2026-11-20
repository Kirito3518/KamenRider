using System.Runtime.Versioning;
using KamenRaider.Agent.Configuration;
using KamenRaider.Agent.Hardware;
using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Sunshine;
using KamenRaider.Agent.Sync;
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
        Directory.CreateDirectory(dataDirectory);
        try
        {
            using var host = KamenRaider.Agent.AgentHost.Build([], builder => ConfigureTestAgentOptions(builder, dataDirectory));

            var hostedServices = host.Services.GetServices<IHostedService>().ToList();
            Assert.Single(hostedServices.OfType<SunshineCredentialWorker>());
            Assert.Single(hostedServices.OfType<SyncWorker>());
            Assert.Single(hostedServices.OfType<SunshineLivenessMonitor>());
            Assert.Single(hostedServices.OfType<LeaseEnforcementWorker>());
            Assert.IsType<WindowsHostHardwareSource>(host.Services.GetRequiredService<IHardwareSource>());
            Assert.NotNull(host.Services.GetRequiredService<HostHardwareCollector>());
            Assert.NotNull(host.Services.GetRequiredService<SunshineCredentialSyncSignal>());
            Assert.NotNull(host.Services.GetRequiredService<SunshineAppliedCredentialStore>());
            Assert.NotNull(host.Services.GetRequiredService<WindowsSunshineCredentialStore>());
            Assert.IsType<SunshineService>(host.Services.GetRequiredService<ISunshinePairingSession>());
        }
        finally
        {
            try
            {
                if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
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
    }
}
