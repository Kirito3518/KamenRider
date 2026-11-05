using KamenRaider.Agent.Startup;
using KamenRaider.Agent.Preparation;
using KamenRaider.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agent.Tests;

public class AgentHostCompositionTests
{
    [Fact]
    public async Task AgentHostRegistersFailClosedBootEnvironmentByDefault()
    {
        using var host = KamenRaider.Agent.AgentHost.Build([], ConfigureTestAgentOptions);

        Assert.IsType<UnavailableBootEnvironment>(host.Services.GetRequiredService<IBootEnvironment>());
        Assert.False(host.Services.GetRequiredService<IBootBaselineEvidence>().HasValidatedBaseline);
        Assert.IsType<KamenRaider.Agent.Sunshine.StreamGuardPipeClient>(host.Services.GetRequiredService<KamenRaider.Agent.Leases.IStreamGate>());
        Assert.Empty(host.Services.GetServices<IHostedService>().OfType<KamenRaider.Agent.Sunshine.StreamGuardPipeServer>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Services.GetRequiredService<KamenRaider.Agent.Leases.IStreamGate>().BlockAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Services.GetRequiredService<KamenRaider.Agent.Leases.IStreamGate>().AllowAsync(new("rental", 1, "boot", DateTimeOffset.UtcNow.AddMinutes(1), 60000), default));
        Assert.Null(host.Services.GetService<KamenRaider.Agent.Sunshine.SunshineClient>());
        Assert.Null(host.Services.GetService<KamenRaider.Agent.Sunshine.PairingSession>());
        Assert.IsType<LeaseEnforcerReconciledLeaseImporter>(host.Services.GetRequiredService<IReconciledLeaseImporter>());
        Assert.IsType<UnavailableMachinePreparation>(host.Services.GetRequiredService<IMachinePreparation>());
        Assert.Single(host.Services.GetServices<IHostedService>().OfType<KamenRaider.Agent.Sync.SyncWorker>());
    }

    [Fact]
    public async Task StreamGuardHostOwnsCoordinatorAndFailsClosedWithoutWindowsProviders()
    {
        using var host = KamenRaider.Agent.StreamGuardHost.Build([]);
        var coordinator = host.Services.GetRequiredService<KamenRaider.Agent.Sunshine.StreamGateCoordinator>();

        Assert.Same(coordinator, host.Services.GetRequiredService<KamenRaider.Agent.Leases.IStreamGate>());
        await Assert.ThrowsAsync<AggregateException>(() => coordinator.BlockAsync(default));
    }

    [Fact]
    public void AgentHostHonorsExplicitBootProviderRegistrations()
    {
        using var host = KamenRaider.Agent.AgentHost.Build([], builder =>
        {
            ConfigureTestAgentOptions(builder);
            builder.Services.AddSingleton<IBootEnvironment, TestBootEnvironment>();
            builder.Services.AddSingleton<IBootBaselineEvidence, ValidatedBaselineEvidence>();
        });

        Assert.IsType<TestBootEnvironment>(host.Services.GetRequiredService<IBootEnvironment>());
        Assert.True(host.Services.GetRequiredService<IBootBaselineEvidence>().HasValidatedBaseline);
    }

    private static void ConfigureTestAgentOptions(HostApplicationBuilder builder)
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ControllerUrl"] = "https://controller.test",
            ["DataDirectory"] = Path.Combine(Path.GetTempPath(), "agent-host-composition-tests"),
            ["MoonlightPort"] = "47989",
            ["SunshinePort"] = "47984",
            ["NetworkAdapterId"] = "test-adapter"
        });
        builder.Services.AddSingleton<KamenRaider.Agent.Leases.IStreamGate>(_ => new KamenRaider.Agent.Sunshine.StreamGuardPipeClient(
            $"KamenRaider.Test.Missing.{Guid.NewGuid():N}", TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150)));
    }

    private sealed class ValidatedBaselineEvidence : IBootBaselineEvidence
    {
        public bool HasValidatedBaseline => true;
    }

    private sealed class TestBootEnvironment : IBootEnvironment
    {
        public Task<bool> VerifyRestorationAsync(ReconciliationChallenge challenge, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> FenceAndClearAsync(ReconciliationChallenge challenge, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> VerifyHealthAsync(ReconciliationChallenge challenge, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> VerifyRuntimeHealthAsync(string bootId, long configurationRevision, CancellationToken ct) => Task.FromResult(false);
    }
}
