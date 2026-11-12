using System.Runtime.Versioning;
using KamenRaider.AgentMaintenance;

namespace Controller.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsMaintenanceApplyServiceLifecycleTests
{
    private sealed class FakeAuthorization(List<string> events) : IMaintenanceApplyAuthorization
    {
        public bool Held = true;

        public void VerifyHeldAndQuiesced(string operationId)
        {
            events.Add("authorize:held-and-quiesced");
            if (!Held) throw new InvalidOperationException("The maintenance hold was lost.");
        }

        public void VerifyHeld(string operationId)
        {
            events.Add("authorize:held");
            if (!Held) throw new InvalidOperationException("The maintenance hold was lost.");
        }
    }

    private sealed class FakeServices(List<string> events, FakeAuthorization authorization) : IMaintenanceWindowsServiceApi
    {
        public Dictionary<string, MaintenanceWindowsServiceSnapshot> Services = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> NeverStop = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> NeverStart = new(StringComparer.OrdinalIgnoreCase);
        public string? FailStartFor;
        public string? FailStopFor;
        public string? LoseHoldAfterStopFor;
        public string? LoseHoldAfterStartFor;
        private readonly Dictionary<string, int> startPids = new(StringComparer.OrdinalIgnoreCase)
        {
            [MaintenanceServiceQuiescer.AgentServiceName] = 201,
            [MaintenanceServiceQuiescer.GuardServiceName] = 202,
            [MaintenanceServiceQuiescer.SunshineServiceName] = 203
        };

        public MaintenanceWindowsServiceSnapshot? Query(string serviceName)
        {
            events.Add("query:" + serviceName);
            return Services.GetValueOrDefault(serviceName);
        }

        public void Stop(string serviceName)
        {
            events.Add("stop:" + serviceName);
            if (serviceName == FailStopFor)
                throw new InvalidOperationException("The service could not be stopped.");
            if (!NeverStop.Contains(serviceName) && Services.TryGetValue(serviceName, out var snapshot))
                Services[serviceName] = snapshot with { State = MaintenanceWindowsServiceState.Stopped, ProcessId = 0 };
            if (serviceName == LoseHoldAfterStopFor)
                authorization.Held = false;
        }

        public void Start(string serviceName)
        {
            events.Add("start:" + serviceName);
            if (serviceName == FailStartFor)
                throw new InvalidOperationException("The service could not be started.");
            if (!Services.TryGetValue(serviceName, out var snapshot))
                throw new InvalidOperationException("The service is missing.");
            if (NeverStart.Contains(serviceName))
                Services[serviceName] = snapshot with { State = MaintenanceWindowsServiceState.StartPending, ProcessId = 0 };
            else
                Services[serviceName] = snapshot with { State = MaintenanceWindowsServiceState.Running, ProcessId = (uint)startPids[serviceName] };
            if (serviceName == LoseHoldAfterStartFor)
                authorization.Held = false;
        }
    }

    private sealed class FakeProcesses(List<string> events) : IMaintenanceProcessInspector
    {
        public IReadOnlyList<int> Remaining = [];
        public Dictionary<int, string> PathsByPid { get; } = [];

        public IReadOnlyList<int> FindByExecutablePath(IReadOnlyCollection<string> exactPaths)
        {
            events.Add("processes");
            return Remaining;
        }

        public string GetExecutablePath(int processId)
        {
            events.Add("image:" + processId);
            return PathsByPid[processId];
        }
    }

    private sealed class Fixture
    {
        public const string OperationId = "operation-1";
        public const string Version = "1.2.3";
        public readonly List<string> Events = [];
        public readonly FakeAuthorization Authorization;
        public readonly FakeServices Services;
        public readonly FakeProcesses Processes;
        public readonly MaintenanceServiceLayout Layout = new(
            AgentExecutablePath: @"C:\Program Files\KamenRaider\Agent\Agent.exe",
            SunshineServiceExecutablePath: @"C:\Program Files\Sunshine\tools\sunshinesvc.exe",
            SunshineExecutablePath: @"C:\Program Files\Sunshine\sunshine.exe");
        public readonly WindowsMaintenanceApplyServiceLifecycle Lifecycle;

        public Fixture(TimeSpan? timeout = null)
        {
            Authorization = new(Events);
            Services = new(Events, Authorization);
            Processes = new(Events);
            Services.Services[MaintenanceServiceQuiescer.AgentServiceName] = Service(
                MaintenanceServiceQuiescer.AgentServiceName,
                '"' + Layout.AgentExecutablePath + '"' + " --contentRoot \"C:\\Program Files\\KamenRaider\\Agent\"",
                "NT SERVICE\\KamenRaider Agent",
                [MaintenanceServiceQuiescer.GuardServiceName], 101);
            Services.Services[MaintenanceServiceQuiescer.GuardServiceName] = Service(
                MaintenanceServiceQuiescer.GuardServiceName,
                '"' + Layout.AgentExecutablePath + '"' + " --contentRoot \"C:\\Program Files\\KamenRaider\\Agent\" --stream-guard",
                "LocalSystem", [], 102);
            Services.Services[MaintenanceServiceQuiescer.SunshineServiceName] = Service(
                MaintenanceServiceQuiescer.SunshineServiceName,
                $"\"{Layout.SunshineServiceExecutablePath}\" --service",
                "LocalSystem", ["VendorService", MaintenanceServiceQuiescer.GuardServiceName], 103);
            Processes.PathsByPid[101] = Layout.AgentExecutablePath;
            Processes.PathsByPid[102] = Layout.AgentExecutablePath;
            Processes.PathsByPid[103] = Layout.SunshineServiceExecutablePath;
            Processes.PathsByPid[201] = Layout.AgentExecutablePath;
            Processes.PathsByPid[202] = Layout.AgentExecutablePath;
            Processes.PathsByPid[203] = Layout.SunshineServiceExecutablePath;
            Lifecycle = new(Services, Processes, Authorization, Layout,
                _ => Version,
                operationTimeout: timeout ?? TimeSpan.FromMilliseconds(100),
                pollInterval: TimeSpan.FromMilliseconds(1));
        }

        public Task StopAllAsync() => Lifecycle.EnsureStoppedAndVerifyQuiescedAsync(OperationId, CancellationToken.None);

        private static MaintenanceWindowsServiceSnapshot Service(
            string name,
            string imagePath,
            string account,
            IReadOnlyList<string> dependencies,
            uint pid) => new(name, imagePath, account, dependencies, MaintenanceWindowsServiceState.Running, pid,
                MaintenanceWindowsServiceTypes.Win32OwnProcess);
    }

    [Theory]
    [InlineData("KamenRaider Agent", "extra")]
    [InlineData("KamenRaider Stream Guard", "extra")]
    public async Task AgentAndGuardDependenciesMustMatchTheirInstalledTopology(string serviceName, string extraDependency)
    {
        var f = new Fixture();
        f.Services.Services[serviceName] = f.Services.Services[serviceName] with
        {
            Dependencies = serviceName == MaintenanceServiceQuiescer.AgentServiceName
                ? [MaintenanceServiceQuiescer.GuardServiceName, extraDependency]
                : [extraDependency]
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.StopAllAsync());

        Assert.DoesNotContain(f.Events, value => value.StartsWith("stop:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SunshineDependencyIsValidatedSeparatelyFromAgentAndGuard()
    {
        var f = new Fixture();
        f.Services.Services[MaintenanceServiceQuiescer.SunshineServiceName] =
            f.Services.Services[MaintenanceServiceQuiescer.SunshineServiceName] with
            {
                Dependencies = ["VendorService", "AdditionalVendorService", MaintenanceServiceQuiescer.GuardServiceName]
            };

        await f.StopAllAsync();

        Assert.Equal(new[]
        {
            "stop:KamenRaider Agent",
            "stop:SunshineService",
            "stop:KamenRaider Stream Guard"
        }, f.Events.Where(value => value.StartsWith("stop:", StringComparison.Ordinal)));
        Assert.Contains("processes", f.Events);
    }

    [Fact]
    public async Task SunshineMustKeepTheGuardDependencyInstalledByTheStreamGate()
    {
        var f = new Fixture();
        f.Services.Services[MaintenanceServiceQuiescer.SunshineServiceName] =
            f.Services.Services[MaintenanceServiceQuiescer.SunshineServiceName] with { Dependencies = ["VendorService"] };

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.StopAllAsync());

        Assert.DoesNotContain(f.Events, value => value.StartsWith("stop:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SunshineAcceptsTheSupportedNoArgumentInstallForm()
    {
        var f = new Fixture();
        f.Services.Services[MaintenanceServiceQuiescer.SunshineServiceName] =
            f.Services.Services[MaintenanceServiceQuiescer.SunshineServiceName] with { ImagePath = '"' + f.Layout.SunshineServiceExecutablePath + '"' };

        await f.StopAllAsync();

        Assert.Contains("stop:SunshineService", f.Events);
    }

    [Fact]
    public async Task SunshineRejectsUnrecognizedServiceArguments()
    {
        var f = new Fixture();
        f.Services.Services[MaintenanceServiceQuiescer.SunshineServiceName] =
            f.Services.Services[MaintenanceServiceQuiescer.SunshineServiceName] with
            {
                ImagePath = $"\"{f.Layout.SunshineServiceExecutablePath}\" --unrecognized"
            };

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.StopAllAsync());

        Assert.DoesNotContain(f.Events, value => value.StartsWith("stop:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentContentRootMustMatchTheQuotedInstalledDirectory()
    {
        var f = new Fixture();
        f.Services.Services[MaintenanceServiceQuiescer.AgentServiceName] =
            f.Services.Services[MaintenanceServiceQuiescer.AgentServiceName] with
            {
                ImagePath = '"' + f.Layout.AgentExecutablePath + '"' + " --contentRoot \"C:\\Program Files\\Other Agent\""
            };

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.StopAllAsync());

        Assert.DoesNotContain(f.Events, value => value.StartsWith("stop:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GuardContentRootMustMatchTheQuotedInstalledDirectory()
    {
        var f = new Fixture();
        f.Services.Services[MaintenanceServiceQuiescer.GuardServiceName] =
            f.Services.Services[MaintenanceServiceQuiescer.GuardServiceName] with
            {
                ImagePath = '"' + f.Layout.AgentExecutablePath + '"' + " --contentRoot \"C:\\Program Files\\Other Agent\" --stream-guard"
            };

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.StopAllAsync());

        Assert.DoesNotContain(f.Events, value => value.StartsWith("stop:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QuiescenceStopsDependentsBeforeGuardAndChecksResidualProcesses()
    {
        var f = new Fixture();

        await f.StopAllAsync();

        Assert.Equal(new[]
        {
            "stop:KamenRaider Agent",
            "stop:SunshineService",
            "stop:KamenRaider Stream Guard"
        }, f.Events.Where(value => value.StartsWith("stop:", StringComparison.Ordinal)));
        Assert.True(f.Events.IndexOf("processes") < f.Events.IndexOf("authorize:held-and-quiesced"));
        Assert.All(f.Services.Services.Values, value => Assert.Equal(MaintenanceWindowsServiceState.Stopped, value.State));
    }

    [Fact]
    public async Task ResidualProcessBlocksQuiescenceAndLeavesHoldUnadvanced()
    {
        var f = new Fixture();
        f.Processes.Remaining = [900];

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.StopAllAsync());

        Assert.DoesNotContain("authorize:held-and-quiesced", f.Events);
    }

    [Fact]
    public async Task StopFailureDoesNotContinueToLaterServices()
    {
        var f = new Fixture();
        f.Services.FailStopFor = MaintenanceServiceQuiescer.AgentServiceName;

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.StopAllAsync());

        Assert.Equal(new[] { "stop:KamenRaider Agent" }, f.Events.Where(value => value.StartsWith("stop:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task StopTimeoutLeavesTheServiceHeld()
    {
        var f = new Fixture(TimeSpan.FromMilliseconds(8));
        f.Services.NeverStop.Add(MaintenanceServiceQuiescer.AgentServiceName);

        await Assert.ThrowsAsync<TimeoutException>(() => f.StopAllAsync());

        Assert.DoesNotContain("stop:SunshineService", f.Events);
        Assert.DoesNotContain("authorize:held-and-quiesced", f.Events);
    }

    [Fact]
    public async Task LosingHoldAfterFirstStopPreventsFurtherStops()
    {
        var f = new Fixture();
        f.Services.LoseHoldAfterStopFor = MaintenanceServiceQuiescer.AgentServiceName;

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.StopAllAsync());

        Assert.Equal(new[] { "stop:KamenRaider Agent" }, f.Events.Where(value => value.StartsWith("stop:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task StartupUsesGuardThenSeparateSunshineThenAgentAndVerifiesImages()
    {
        var f = new Fixture();
        await f.StopAllAsync();
        f.Events.Clear();

        await f.Lifecycle.StartAndVerifyAsync(Fixture.OperationId,
            @"C:\Program Files\KamenRaider\Agent", Fixture.Version, CancellationToken.None);

        Assert.Equal(new[]
        {
            "start:KamenRaider Stream Guard",
            "start:SunshineService",
            "start:KamenRaider Agent"
        }, f.Events.Where(value => value.StartsWith("start:", StringComparison.Ordinal)));
        Assert.Contains("image:202", f.Events);
        Assert.Contains("image:203", f.Events);
        Assert.Contains("image:201", f.Events);
        Assert.All(f.Services.Services.Values, value => Assert.Equal(MaintenanceWindowsServiceState.Running, value.State));
    }

    [Fact]
    public async Task StartFailureDoesNotStartDependentsOrLaterServices()
    {
        var f = new Fixture();
        await f.StopAllAsync();
        f.Events.Clear();
        f.Services.FailStartFor = MaintenanceServiceQuiescer.SunshineServiceName;

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Lifecycle.StartAndVerifyAsync(
            Fixture.OperationId, @"C:\Program Files\KamenRaider\Agent", Fixture.Version, CancellationToken.None));

        Assert.Equal(new[] { "start:KamenRaider Stream Guard", "start:SunshineService" },
            f.Events.Where(value => value.StartsWith("start:", StringComparison.Ordinal)));
        Assert.DoesNotContain("start:KamenRaider Agent", f.Events);
    }

    [Fact]
    public async Task StartTimeoutLeavesLaterServicesStopped()
    {
        var f = new Fixture(TimeSpan.FromMilliseconds(8));
        await f.StopAllAsync();
        f.Events.Clear();
        f.Services.NeverStart.Add(MaintenanceServiceQuiescer.GuardServiceName);

        await Assert.ThrowsAsync<TimeoutException>(() => f.Lifecycle.StartAndVerifyAsync(
            Fixture.OperationId, @"C:\Program Files\KamenRaider\Agent", Fixture.Version, CancellationToken.None));

        Assert.Equal(new[] { "start:KamenRaider Stream Guard" },
            f.Events.Where(value => value.StartsWith("start:", StringComparison.Ordinal)));
        Assert.DoesNotContain("start:SunshineService", f.Events);
    }

    [Fact]
    public async Task LosingHoldDuringGuardStartupPreventsSunshineAndAgentStart()
    {
        var f = new Fixture();
        await f.StopAllAsync();
        f.Events.Clear();
        f.Services.LoseHoldAfterStartFor = MaintenanceServiceQuiescer.GuardServiceName;

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Lifecycle.StartAndVerifyAsync(
            Fixture.OperationId, @"C:\Program Files\KamenRaider\Agent", Fixture.Version, CancellationToken.None));

        Assert.Equal(new[] { "start:KamenRaider Stream Guard" },
            f.Events.Where(value => value.StartsWith("start:", StringComparison.Ordinal)));
        Assert.DoesNotContain("start:SunshineService", f.Events);
        Assert.DoesNotContain("start:KamenRaider Agent", f.Events);
    }

    [Fact]
    public async Task RunningServicePidMustResolveToItsConfiguredImageBeforeAnyStop()
    {
        var f = new Fixture();
        f.Processes.PathsByPid[101] = @"C:\Temp\Agent.exe";

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.StopAllAsync());

        Assert.DoesNotContain(f.Events, value => value.StartsWith("stop:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x20)]
    [InlineData(0x110)]
    public async Task SharedOrUnexpectedServiceTypeIsRejectedBeforeAnyStop(int serviceType)
    {
        var f = new Fixture();
        f.Services.Services[MaintenanceServiceQuiescer.AgentServiceName] =
            f.Services.Services[MaintenanceServiceQuiescer.AgentServiceName] with { ServiceType = (uint)serviceType };

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.StopAllAsync());

        Assert.DoesNotContain(f.Events, value => value.StartsWith("stop:", StringComparison.Ordinal));
    }
}
