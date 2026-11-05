using KamenRaider.Agent.Startup;
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Services.GetRequiredService<KamenRaider.Agent.Leases.IStreamGate>().BlockAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Services.GetRequiredService<KamenRaider.Agent.Leases.IStreamGate>().AllowAsync(default));
        Assert.Null(host.Services.GetService<KamenRaider.Agent.Sunshine.SunshineClient>());
        Assert.Null(host.Services.GetService<KamenRaider.Agent.Sunshine.PairingSession>());
        Assert.IsType<LeaseEnforcerReconciledLeaseImporter>(host.Services.GetRequiredService<IReconciledLeaseImporter>());
        Assert.Single(host.Services.GetServices<IHostedService>().OfType<KamenRaider.Agent.Sync.SyncWorker>());
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
