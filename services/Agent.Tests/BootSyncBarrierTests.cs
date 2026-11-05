using KamenRaider.Agent.Startup;
namespace Agent.Tests;
public class BootSyncBarrierTests {
 [Theory][InlineData(Readiness.NotReady)][InlineData(Readiness.Fault)] public async Task HeartbeatRunsEvenWhenReconciliationIsBlocked(Readiness expected){var calls=0;var reported=Readiness.Ready;await BootSyncBarrier.RunAsync(()=>Task.FromResult(expected),readiness=>{calls++;reported=readiness;return Task.CompletedTask;});Assert.Equal(1,calls);Assert.Equal(expected,reported);}
 [Fact] public async Task SyncRunsAfterSuccessfulReconciliation(){var calls=0;await BootSyncBarrier.RunAsync(()=>Task.FromResult(Readiness.Ready),_=>{calls++;return Task.CompletedTask;});Assert.Equal(1,calls);}
}
