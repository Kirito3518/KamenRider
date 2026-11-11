namespace KamenRaider.AgentMaintenance;

internal static class Program
{
    public static int Main(string[] args)
    {
        IAgentMaintenanceCommandHost host = OperatingSystem.IsWindows()
            ? new WindowsAgentMaintenanceCommandHost(new LocalUpdateEngine(new WindowsMaintenanceWorkStore()))
            : new UnsupportedPlatformCommandHost();
        return AgentMaintenanceCli.Run(args, host, Console.Out, Console.Error);
    }

    private sealed class UnsupportedPlatformCommandHost : IAgentMaintenanceCommandHost
    {
        public AgentMaintenanceCommandResult Execute(string command) =>
            new("unavailable", "windowsRequired", null, null, null, null, null, null);
    }
}
