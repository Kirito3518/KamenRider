namespace KamenRaider.AgentMaintenance;

internal static class Program
{
    public static int Main(string[] args)
    {
        IAgentMaintenanceCommandHost host;
        if (OperatingSystem.IsWindows())
        {
            var security = new WindowsMaintenanceBackupSecurity();
            var inspector = new InstalledAgentSourceInspector(security.SourceRoot, security);
            var engine = new LocalUpdateEngine(new WindowsMaintenanceWorkStore());
            host = new WindowsAgentMaintenanceCommandHost(
                engine, inspector, MaintenanceApplyCommand.CreateFailClosed(engine));
        }
        else
        {
            host = new UnsupportedPlatformCommandHost();
        }
        return AgentMaintenanceCli.Run(args, host, Console.Out, Console.Error);
    }

    private sealed class UnsupportedPlatformCommandHost : IAgentMaintenanceCommandHost
    {
        public AgentMaintenanceCommandResult Execute(string command) =>
            new("unavailable", "windowsRequired", null, null, null, null, null, null);
    }
}
