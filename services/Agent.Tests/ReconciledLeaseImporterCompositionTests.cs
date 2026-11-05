using KamenRaider.Agent;
using KamenRaider.Agent.Sync;

namespace Agent.Tests;

public class ReconciledLeaseImporterCompositionTests
{
    [Fact]
    public void ProductionSyncWorkerMustReceiveAReconciledLeaseImporter()
    {
        var importerType = typeof(AgentHost).Assembly.GetType(
            "KamenRaider.Agent.Startup.IReconciledLeaseImporter");

        Assert.NotNull(importerType);
        var constructor = typeof(SyncWorker).GetConstructors().Single();
        Assert.Contains(constructor.GetParameters(), parameter => parameter.ParameterType == importerType);
    }
}
