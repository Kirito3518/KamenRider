using System.Runtime.Versioning;
using KamenRaider.Agent.Configuration;
using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Parsec;
using KamenRaider.Agent.Preparation;
using KamenRaider.Agent.Startup;
using KamenRaider.Agent.Sunshine;
using KamenRaider.Agent.Sync;
using KamenRaider.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agent.Tests;

/// <summary>
/// Production composition for the 0jay-style Agent: local Sunshine REST control replaces the
/// retired Stream Guard process, so there is no gate/firewall/service registration to compose.
/// </summary>
public class AgentHostCompositionTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void AgentHostRegistersTheLocalSunshineComposition()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDataDirectory("agent-composition");
        using var host = KamenRaider.Agent.AgentHost.Build([], builder => ConfigureTestAgentOptions(builder, directory.Path));

        var hostedServices = host.Services.GetServices<IHostedService>().ToList();

        var liveness = Assert.IsType<SunshineLivenessMonitor>(host.Services.GetRequiredService<ISunshineLiveness>());
        Assert.Same(liveness, host.Services.GetRequiredService<ISunshineLiveness>());
        Assert.Contains(liveness, hostedServices);
        Assert.IsType<SunshineLivenessProbe>(host.Services.GetRequiredService<ISunshineLivenessProbe>());

        var pairing = Assert.IsType<SunshineService>(host.Services.GetRequiredService<ISunshinePairingSession>());
        Assert.IsType<AgentStreamCleanup>(host.Services.GetRequiredService<IStreamCleanup>());
        Assert.Same(pairing, host.Services.GetRequiredService<ISunshineAdmin>());

        Assert.IsType<AgentBootEnvironment>(host.Services.GetRequiredService<IBootEnvironment>());
        Assert.IsType<ParsecManager>(host.Services.GetRequiredService<ParsecManager>());
        Assert.IsType<ParsecStatusHttpReporter>(host.Services.GetRequiredService<IParsecStatusReporter>());
        var parsecCapability = host.Services.GetRequiredService<ParsecCapabilityProvider>();
        Assert.Contains(parsecCapability, hostedServices);
        Assert.Equal(BootEvidenceStatus.Unavailable, host.Services.GetRequiredService<IBootBaselineEvidence>().Status);
        Assert.IsType<LeaseEnforcerReconciledLeaseImporter>(host.Services.GetRequiredService<IReconciledLeaseImporter>());
        Assert.Equal("LeaseMachinePreparation", host.Services.GetRequiredService<IMachinePreparation>().GetType().Name);
        Assert.IsType<LeaseEnforcer>(host.Services.GetRequiredService<LeaseEnforcer>());
        Assert.IsType<LeaseCommandDispatcher>(host.Services.GetRequiredService<LeaseCommandDispatcher>());

        Assert.Single(hostedServices.OfType<SyncWorker>());
        Assert.Single(hostedServices.OfType<LeaseEnforcementWorker>());
        Assert.Single(hostedServices.OfType<KamenRaider.Agent.Enrollment.EnrollmentWorker>());
        Assert.Single(hostedServices.OfType<SunshineCredentialWorker>());
        Assert.DoesNotContain(hostedServices, service => service.GetType().Name.Contains("StreamGuard", StringComparison.Ordinal));

        Assert.Null(host.Services.GetService<SunshineClient>());
        Assert.Null(host.Services.GetService<PairingSession>());
    }

    [Fact]
    public void AgentHostHonorsExplicitBootProviderRegistrations()
    {
        using var directory = new TempDataDirectory("agent-boot-providers");
        using var host = KamenRaider.Agent.AgentHost.Build([], builder =>
        {
            ConfigureTestAgentOptions(builder, directory.Path);
            builder.Services.AddSingleton<IBootEnvironment, TestBootEnvironment>();
            builder.Services.AddSingleton<IBootBaselineEvidence, ValidatedBaselineEvidence>();
        });

        Assert.IsType<TestBootEnvironment>(host.Services.GetRequiredService<IBootEnvironment>());
        Assert.Equal(BootEvidenceStatus.Passed, host.Services.GetRequiredService<IBootBaselineEvidence>().Status);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AgentHostBuildsTheDefaultSunshinePairingSessionWithoutLoadingCredentials()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDataDirectory("agent-sunshine-credentials");
        using var host = KamenRaider.Agent.AgentHost.Build([], builder => ConfigureTestAgentOptions(builder, directory.Path));

        Assert.IsType<SunshineService>(host.Services.GetRequiredService<ISunshinePairingSession>());
        Assert.Null(host.Services.GetService<PairingSession>());
        Assert.NotNull(host.Services.GetRequiredService<AppliedConfiguration>());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task DefaultPreparationAcknowledgesOnlyTheCurrentUnleasedMachine()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDataDirectory("agent-preparation");
        var maintenanceFence = new TestMaintenanceFence();
        using var host = KamenRaider.Agent.AgentHost.Build([], builder =>
        {
            ConfigureTestAgentOptions(builder, directory.Path);
            builder.Services.AddSingleton<IMaintenanceFenceReader>(maintenanceFence);
        });

        var clock = host.Services.GetRequiredService<IBootClock>();
        var enforcer = host.Services.GetRequiredService<LeaseEnforcer>();
        var dispatcher = new LeaseCommandDispatcher(enforcer, machinePreparation: host.Services.GetRequiredService<IMachinePreparation>());
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
        Assert.False(enforcer.CanStream);
        Assert.Null(enforcer.Current);
    }

    private static void ConfigureTestAgentOptions(HostApplicationBuilder builder, string? dataDirectory = null)
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ControllerUrl"] = "https://controller.test",
            ["DataDirectory"] = dataDirectory ?? Path.Combine(Path.GetTempPath(), "agent-host-composition-tests"),
            ["MoonlightPort"] = "47989",
            ["SunshinePort"] = "47984",
            ["NetworkAdapterId"] = "test-adapter"
        });
    }

    private sealed class TempDataDirectory : IDisposable
    {
        public TempDataDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Path)) System.IO.Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class ValidatedBaselineEvidence : IBootBaselineEvidence
    {
        public BootEvidenceStatus Status => BootEvidenceStatus.Passed;
    }

    private sealed class TestBootEnvironment : IBootEnvironment
    {
        public Task<BootEvidenceStatus> VerifyRestorationAsync(ReconciliationChallenge challenge, CancellationToken ct) => Task.FromResult(BootEvidenceStatus.Blocked);
        public Task<BootEvidenceStatus> ClearStreamAccessAsync(ReconciliationChallenge challenge, CancellationToken ct) => Task.FromResult(BootEvidenceStatus.Blocked);
        public Task<BootEvidenceStatus> VerifyHealthAsync(ReconciliationChallenge challenge, CancellationToken ct) => Task.FromResult(BootEvidenceStatus.Blocked);
    }
}
