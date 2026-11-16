using System.Runtime.Versioning;
using KamenRaider.Agent.Startup;
using KamenRaider.Agent.Preparation;
using KamenRaider.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Agent.Tests;

public class AgentHostCompositionTests
{
    [Fact]
    public async Task AgentHostRegistersFailClosedBootEnvironmentByDefault()
    {
        using var host = KamenRaider.Agent.AgentHost.Build([], ConfigureTestAgentOptions);

        Assert.IsType<KamenRaider.Agent.Startup.StreamGuardBootEnvironment>(host.Services.GetRequiredService<IBootEnvironment>());
        Assert.Equal(BootEvidenceStatus.Unavailable,host.Services.GetRequiredService<IBootBaselineEvidence>().Status);
        Assert.IsType<KamenRaider.Agent.Sunshine.StreamGuardPipeClient>(host.Services.GetRequiredService<KamenRaider.Agent.Leases.IStreamGate>());
        Assert.Same(host.Services.GetRequiredService<KamenRaider.Agent.Leases.IStreamGate>(), host.Services.GetRequiredService<KamenRaider.Agent.Sunshine.IPairingFence>());
        Assert.Empty(host.Services.GetServices<IHostedService>().OfType<KamenRaider.Agent.Sunshine.StreamGuardPipeServer>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Services.GetRequiredService<KamenRaider.Agent.Leases.IStreamGate>().BlockAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Services.GetRequiredService<KamenRaider.Agent.Leases.IStreamGate>().AllowAsync(new("rental", 1, "boot", DateTimeOffset.UtcNow.AddMinutes(1), 60000), default));
        Assert.Null(host.Services.GetService<KamenRaider.Agent.Sunshine.SunshineClient>());
        Assert.Null(host.Services.GetService<KamenRaider.Agent.Sunshine.PairingSession>());
        Assert.IsType<LeaseEnforcerReconciledLeaseImporter>(host.Services.GetRequiredService<IReconciledLeaseImporter>());
        Assert.Equal("FencedMachinePreparation", host.Services.GetRequiredService<IMachinePreparation>().GetType().Name);
        Assert.Single(host.Services.GetServices<IHostedService>().OfType<KamenRaider.Agent.Sync.SyncWorker>());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task StreamGuardHostOwnsCoordinatorAndHonorsInjectedProviders()
    {
        if (!OperatingSystem.IsWindows()) return;

        var ingress = new TestIngressPolicy();
        var sunshine = new TestSunshineServiceControl();
        using var host = KamenRaider.Agent.StreamGuardHost.Build([], builder =>
        {
            builder.Services.AddSingleton<KamenRaider.Agent.Sunshine.IStreamIngressPolicy>(ingress);
            builder.Services.AddSingleton<KamenRaider.Agent.Sunshine.ISunshineServiceControl>(sunshine);
        });
        var coordinator = host.Services.GetRequiredService<KamenRaider.Agent.Sunshine.StreamGateCoordinator>();
        var hostedServices = host.Services.GetServices<IHostedService>().ToList();
        var startupFence = Assert.Single(hostedServices.OfType<KamenRaider.Agent.Sunshine.StreamGuardStartupFence>());
        var serverIndex = hostedServices.FindIndex(service => service is KamenRaider.Agent.Sunshine.StreamGuardPipeServer);
        var deadlineIndex = hostedServices.FindIndex(service => service is KamenRaider.Agent.Sunshine.StreamGuardDeadlineWorker);

        Assert.Same(coordinator, host.Services.GetRequiredService<KamenRaider.Agent.Leases.IStreamGate>());
        Assert.IsType<KamenRaider.Agent.Sunshine.WindowsStreamGrantHistoryStore>(host.Services.GetRequiredService<KamenRaider.Agent.Sunshine.IStreamGrantHistoryStore>());
        Assert.True(serverIndex > hostedServices.IndexOf(startupFence));
        Assert.True(deadlineIndex > hostedServices.IndexOf(startupFence));
        await startupFence.StartAsync(default);
        Assert.True(ingress.Blocked);
        Assert.False(sunshine.Running);
        Assert.Equal(1, ingress.BlockCalls);
        Assert.Equal(1, sunshine.StopCalls);
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
        Assert.Equal(BootEvidenceStatus.Passed,host.Services.GetRequiredService<IBootBaselineEvidence>().Status);
    }

    [Fact]
    public async Task DefaultBootProviderKeepsRestorationDesktopAndRuntimeHealthUnavailableDespiteSuppliedBootAndRevisionIdentifiers()
    {
        var pairing = new RecordingPairingSession(clearResult: true);
        using var host = KamenRaider.Agent.AgentHost.Build([], builder =>
        {
            ConfigureTestAgentOptions(builder);
            builder.Services.AddSingleton<KamenRaider.Agent.Sunshine.ISunshinePairingSession>(pairing);
        });
        var environment = host.Services.GetRequiredService<IBootEnvironment>();
        var bootId = "test-os-boot-id";
        var configurationRevision = 41L;
        var challenge = new ReconciliationChallenge("challenge", bootId, null, null, DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.IsType<KamenRaider.Agent.Startup.StreamGuardBootEnvironment>(environment);
        Assert.Equal(BootEvidenceStatus.Unavailable, host.Services.GetRequiredService<IBootBaselineEvidence>().Status);
        Assert.Equal(BootEvidenceStatus.Unavailable, await environment.VerifyRestorationAsync(challenge, default));
        Assert.Equal(BootEvidenceStatus.Passed, await environment.FenceAndClearAsync(challenge, default));
        Assert.Equal(1, pairing.ClearCalls);
        Assert.Equal(BootEvidenceStatus.Unavailable, await environment.VerifyHealthAsync(challenge, default));
        Assert.Equal(BootEvidenceStatus.Unavailable, await environment.VerifyRuntimeHealthAsync(bootId, configurationRevision, default));
    }

    [Fact]
    public async Task AgentHostDoesNotPassBootFenceEvidenceWhenPairingCleanupFails()
    {
        var pairing = new RecordingPairingSession(clearResult: false);
        using var host = KamenRaider.Agent.AgentHost.Build([], builder =>
        {
            ConfigureTestAgentOptions(builder);
            builder.Services.AddSingleton<KamenRaider.Agent.Sunshine.ISunshinePairingSession>(pairing);
        });
        var environment = host.Services.GetRequiredService<IBootEnvironment>();
        var challenge = new ReconciliationChallenge("challenge", "boot", null, null, DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal(BootEvidenceStatus.Blocked, await environment.FenceAndClearAsync(challenge, default));
        Assert.Equal(1, pairing.ClearCalls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AgentHostRegistersLazySunshinePairingWithoutLoadingLocalCredentials()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = KamenRaider.Agent.AgentHost.Build([], ConfigureTestAgentOptions);

        Assert.IsType<KamenRaider.Agent.Sunshine.LazySunshinePairingSession>(
            host.Services.GetRequiredService<KamenRaider.Agent.Sunshine.ISunshinePairingSession>());
        Assert.Null(host.Services.GetService<KamenRaider.Agent.Sunshine.PairingSession>());
        Assert.NotNull(host.Services.GetRequiredService<KamenRaider.Agent.Configuration.AppliedConfiguration>());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task DefaultPreparationAcknowledgesOnlyAfterTheCurrentUnleasedMachineIsFenced()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dataDirectory = Path.Combine(Path.GetTempPath(), "agent-preparation-" + Guid.NewGuid().ToString("N"));
        var gate = new RecordingStreamGate();
        var maintenanceFence = new TestMaintenanceFence();
        try
        {
            using var host = KamenRaider.Agent.AgentHost.Build([], builder =>
            {
                ConfigureTestAgentOptions(builder);
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataDirectory"] = dataDirectory
                });
                builder.Services.Replace(ServiceDescriptor.Singleton<KamenRaider.Agent.Leases.IStreamGate>(gate));
                builder.Services.AddSingleton<KamenRaider.Agent.Sunshine.IMaintenanceFenceReader>(maintenanceFence);
            });

            var clock = host.Services.GetRequiredService<KamenRaider.Agent.Leases.IBootClock>();
            var dispatcher = host.Services.GetRequiredService<KamenRaider.Agent.Sync.LeaseCommandDispatcher>();
            var command = new CommandEnvelope(
                "prepare",
                "PrepareMachine",
                "rental",
                1,
                clock.BootId,
                DateTimeOffset.UtcNow.AddMinutes(3),
                System.Text.Json.JsonSerializer.SerializeToElement(new { }));

            await dispatcher.AcceptAsync(
                new(DateTimeOffset.UtcNow, new(1, 5000, 6000), [command]),
                clock.UptimeMilliseconds,
                default);

            Assert.Equal(new CommandResult("prepare", "applied", 1), Assert.Single(dispatcher.PendingResults));
            Assert.True(gate.Blocked);
            Assert.True(gate.BlockCalls >= 2);
            Assert.False(host.Services.GetRequiredService<KamenRaider.Agent.Leases.LeaseEnforcer>().CanStream);
        }
        finally
        {
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
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
        var pipe = new KamenRaider.Agent.Sunshine.StreamGuardPipeClient(
            $"KamenRaider.Test.Missing.{Guid.NewGuid():N}", TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150));
        builder.Services.AddSingleton(pipe);
        builder.Services.AddSingleton<KamenRaider.Agent.Leases.IStreamGate>(pipe);
    }

    private sealed class ValidatedBaselineEvidence : IBootBaselineEvidence
    {
        public BootEvidenceStatus Status => BootEvidenceStatus.Passed;
    }

    private sealed class TestBootEnvironment : IBootEnvironment
    {
        public Task<BootEvidenceStatus> VerifyRestorationAsync(ReconciliationChallenge challenge, CancellationToken ct) => Task.FromResult(BootEvidenceStatus.Blocked);
        public Task<BootEvidenceStatus> FenceAndClearAsync(ReconciliationChallenge challenge, CancellationToken ct) => Task.FromResult(BootEvidenceStatus.Blocked);
        public Task<BootEvidenceStatus> VerifyHealthAsync(ReconciliationChallenge challenge, CancellationToken ct) => Task.FromResult(BootEvidenceStatus.Blocked);
        public Task<BootEvidenceStatus> VerifyRuntimeHealthAsync(string bootId, long configurationRevision, CancellationToken ct) => Task.FromResult(BootEvidenceStatus.Blocked);
    }

    private sealed class TestIngressPolicy : KamenRaider.Agent.Sunshine.IStreamIngressPolicy
    {
        public bool Blocked { get; private set; }
        public int BlockCalls { get; private set; }

        public Task SetBlockedAsync(bool blocked, CancellationToken cancellationToken)
        {
            BlockCalls++;
            Blocked = blocked;
            return Task.CompletedTask;
        }

        public Task<bool> IsBlockedAsync(CancellationToken cancellationToken) => Task.FromResult(Blocked);
    }

    private sealed class TestSunshineServiceControl : KamenRaider.Agent.Sunshine.ISunshineServiceControl
    {
        public bool Running { get; private set; } = true;
        public int StopCalls { get; private set; }

        public Task StartAndWaitAsync(CancellationToken cancellationToken)
        {
            Running = true;
            return Task.CompletedTask;
        }

        public Task StopAndWaitAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            Running = false;
            return Task.CompletedTask;
        }

        public Task<bool> IsRunningAsync(CancellationToken cancellationToken) => Task.FromResult(Running);
    }

    private sealed class RecordingStreamGate : KamenRaider.Agent.Leases.IStreamGate
    {
        public bool Blocked { get; private set; }
        public int BlockCalls { get; private set; }

        public Task BlockAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            BlockCalls++;
            Blocked = true;
            return Task.CompletedTask;
        }

        public Task AllowAsync(KamenRaider.Agent.Leases.StreamLeaseGrant grant, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Blocked = false;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPairingSession(bool clearResult) : KamenRaider.Agent.Sunshine.ISunshinePairingSession
    {
        public int ClearCalls { get; private set; }

        public Task<KamenRaider.Contracts.PairingOffer?> CreateOfferAsync(CancellationToken ct) => Task.FromResult<KamenRaider.Contracts.PairingOffer?>(null);
        public Task<string[]> ListAsync(CancellationToken ct) => Task.FromResult(Array.Empty<string>());
        public Task<KamenRaider.Contracts.CommandResult> ExecuteAsync(KamenRaider.Contracts.CommandEnvelope command, CancellationToken ct) =>
            Task.FromResult(new KamenRaider.Contracts.CommandResult(command.CommandId, "failed", command.LeaseRevision, "PairingUnavailable"));

        public Task<bool> ClearAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ClearCalls++;
            return Task.FromResult(clearResult);
        }
    }
}
