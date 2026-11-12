using KamenRaider.AgentMaintenance;

namespace Controller.Tests;

public sealed class MaintenanceServiceQuiescerTests
{
    private sealed class FakePrerequisites(List<string> events) : IMaintenanceStopPrerequisites
    {
        public bool Allow = true;
        public Task VerifyBeforeStopAsync(CancellationToken cancellationToken)
        {
            events.Add("authorize");
            if (!Allow) throw new InvalidOperationException("The durable start/fence/lease proof is incomplete.");
            return Task.CompletedTask;
        }

        public Task VerifyStillHeldAsync(CancellationToken cancellationToken)
        {
            events.Add("held");
            if (!Allow) throw new InvalidOperationException("The maintenance hold was lost.");
            return Task.CompletedTask;
        }

        public Task MarkServicesQuiescedAsync(CancellationToken cancellationToken)
        {
            events.Add("journal:servicesQuiesced");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeServices(List<string> events) : IMaintenanceWindowsServiceApi
    {
        public Dictionary<string, MaintenanceWindowsServiceSnapshot> Services = new(StringComparer.OrdinalIgnoreCase);
        public bool IgnoreStops;

        public MaintenanceWindowsServiceSnapshot? Query(string serviceName)
        {
            events.Add("query:" + serviceName);
            return Services.GetValueOrDefault(serviceName);
        }

        public void Stop(string serviceName)
        {
            events.Add("stop:" + serviceName);
            if (!IgnoreStops && Services.TryGetValue(serviceName, out var snapshot))
                Services[serviceName] = snapshot with { State = MaintenanceWindowsServiceState.Stopped, ProcessId = 0 };
        }

        public void Start(string serviceName) => events.Add("start:" + serviceName);
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

        public string GetExecutablePath(int processId) => PathsByPid[processId];
    }

    private sealed class Fixture
    {
        public readonly List<string> Events = [];
        public readonly FakePrerequisites Prerequisites;
        public readonly FakeServices Services;
        public readonly FakeProcesses Processes;
        public readonly MaintenanceServiceQuiescer Quiescer;
        public readonly MaintenanceServiceLayout Layout = new(
            AgentExecutablePath: @"C:\Program Files\KamenRaider\Agent\Agent.exe",
            SunshineServiceExecutablePath: @"C:\Program Files\Sunshine\tools\sunshinesvc.exe",
            SunshineExecutablePath: @"C:\Program Files\Sunshine\sunshine.exe");

        public Fixture()
        {
            Prerequisites = new(Events);
            Services = new(Events);
            Processes = new(Events);
            Services.Services[MaintenanceServiceQuiescer.AgentServiceName] = Service(
                MaintenanceServiceQuiescer.AgentServiceName,
                '"' + Layout.AgentExecutablePath + '"' + " --contentRoot \"C:\\Program Files\\KamenRaider\\Agent\"",
                "NT SERVICE\\KamenRaider Agent",
                [MaintenanceServiceQuiescer.GuardServiceName],
                101);
            Services.Services[MaintenanceServiceQuiescer.GuardServiceName] = Service(
                MaintenanceServiceQuiescer.GuardServiceName,
                '"' + Layout.AgentExecutablePath + '"' + " --contentRoot \"C:\\Program Files\\KamenRaider\\Agent\" --stream-guard",
                "LocalSystem",
                [],
                102);
            Services.Services[MaintenanceServiceQuiescer.SunshineServiceName] = Service(
                MaintenanceServiceQuiescer.SunshineServiceName,
                $"\"{Layout.SunshineServiceExecutablePath}\" --service",
                "LocalSystem",
                ["VendorService", MaintenanceServiceQuiescer.GuardServiceName],
                103);
            Processes.PathsByPid[101] = Layout.AgentExecutablePath;
            Processes.PathsByPid[102] = Layout.AgentExecutablePath;
            Processes.PathsByPid[103] = Layout.SunshineServiceExecutablePath;
            Quiescer = new(Services, Processes, Prerequisites, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(1));
        }

        private static MaintenanceWindowsServiceSnapshot Service(
            string name,
            string imagePath,
            string account,
            IReadOnlyList<string> dependencies,
            uint pid) => new(name, imagePath, account, dependencies,
                MaintenanceWindowsServiceState.Running, pid, MaintenanceWindowsServiceTypes.Win32OwnProcess);
    }

    [Fact]
    public async Task MissingDurableStartFenceOrLeaseProofPreventsAnyServiceStop()
    {
        var f = new Fixture();
        f.Prerequisites.Allow = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Quiescer.QuiesceAsync(f.Layout, CancellationToken.None));

        Assert.DoesNotContain(f.Events, value => value.StartsWith("stop:", StringComparison.Ordinal));
        Assert.DoesNotContain("journal:servicesQuiesced", f.Events);
    }

    [Fact]
    public async Task QuiescesDependentsBeforeGuardAndJournalsOnlyAfterNoMatchingProcessRemains()
    {
        var f = new Fixture();

        await f.Quiescer.QuiesceAsync(f.Layout, CancellationToken.None);

        Assert.Equal(new[]
        {
            "stop:KamenRaider Agent",
            "stop:SunshineService",
            "stop:KamenRaider Stream Guard"
        }, f.Events.Where(value => value.StartsWith("stop:", StringComparison.Ordinal)));
        Assert.True(f.Events.IndexOf("processes") < f.Events.IndexOf("journal:servicesQuiesced"));
        Assert.Equal(MaintenanceWindowsServiceState.Stopped, f.Services.Services[MaintenanceServiceQuiescer.AgentServiceName].State);
    }

    [Fact]
    public async Task MismatchedServiceImageIsRejectedBeforeAnyStop()
    {
        var f = new Fixture();
        f.Services.Services[MaintenanceServiceQuiescer.AgentServiceName] =
            f.Services.Services[MaintenanceServiceQuiescer.AgentServiceName] with { ImagePath = @"C:\Temp\Agent.exe" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Quiescer.QuiesceAsync(f.Layout, CancellationToken.None));

        Assert.DoesNotContain(f.Events, value => value.StartsWith("stop:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResidualAgentOrSunshineProcessLeavesJournalUnadvanced()
    {
        var f = new Fixture();
        f.Processes.Remaining = [103];

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Quiescer.QuiesceAsync(f.Layout, CancellationToken.None));

        Assert.DoesNotContain("journal:servicesQuiesced", f.Events);
        Assert.Contains("held", f.Events);
    }

    [Fact]
    public async Task ServiceThatDoesNotReachStoppedLeavesJournalUnadvanced()
    {
        var f = new Fixture();
        f.Services.IgnoreStops = true;

        await Assert.ThrowsAsync<TimeoutException>(() => f.Quiescer.QuiesceAsync(f.Layout, CancellationToken.None));

        Assert.DoesNotContain("journal:servicesQuiesced", f.Events);
    }
}
