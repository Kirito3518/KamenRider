using KamenRaider.Agent;
namespace Agent.Tests;
public class HostTests
{
    [Fact]
    public async Task Production_RejectsMissingConfiguration()
    {
        using var host = AgentHost.Build([], b => b.Environment.EnvironmentName = "Production");
        await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());
    }
}
