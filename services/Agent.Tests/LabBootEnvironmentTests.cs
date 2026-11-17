using KamenRaider.Agent.Startup;

namespace KamenRaider.Agent.Tests;

public sealed class LabBootEnvironmentTests
{
    [Fact]
    public void LabBaselineEvidenceReportsPassed()
    {
        IBootBaselineEvidence evidence = new LabBootBaselineEvidence();
        Assert.Equal(BootEvidenceStatus.Passed, evidence.Status);
    }

    [Fact]
    public async Task LabEnvironmentReportsPassedForAllEvidenceChecks()
    {
        var environment = new LabBootEnvironment();

        Assert.Equal(BootEvidenceStatus.Passed, await environment.VerifyRestorationAsync(null!, CancellationToken.None));
        Assert.Equal(BootEvidenceStatus.Passed, await environment.FenceAndClearAsync(null!, CancellationToken.None));
        Assert.Equal(BootEvidenceStatus.Passed, await environment.VerifyHealthAsync(null!, CancellationToken.None));
        Assert.Equal(BootEvidenceStatus.Passed, await environment.VerifyRuntimeHealthAsync("boot", 1, CancellationToken.None));
    }
}
