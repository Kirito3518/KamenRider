using Microsoft.Extensions.Hosting;

namespace KamenRaider.Agent.Sunshine;

public sealed class StreamGuardStartupFence(StreamGateCoordinator gate) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => gate.BlockAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => gate.BlockAsync(CancellationToken.None);
}

public static class StreamGuardRecovery
{
    public static void Close()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Stream Guard recovery is available only on Windows.");

        var services = new WindowsServiceApi();
        var installation = new WindowsSunshineInstallationDiscovery(services).Discover();
        var gate = new StreamGateCoordinator(
            new WindowsStreamIngressPolicy(new WindowsFirewallComApi(), installation),
            new WindowsSunshineServiceControl(services, installation),
            TimeProvider.System,
            new KamenRaider.Agent.Leases.WindowsBootClock(),
            new WindowsStreamGrantHistoryStore(),
            new WindowsMaintenanceFenceReader());
        gate.BlockAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
}
