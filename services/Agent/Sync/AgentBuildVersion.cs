using System.Reflection;

namespace KamenRaider.Agent.Sync;

public static class AgentBuildVersion
{
    public static string Current { get; } =
        typeof(AgentBuildVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AgentBuildVersion).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
