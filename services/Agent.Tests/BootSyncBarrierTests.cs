using KamenRaider.Agent.Startup;
namespace Agent.Tests;
public class BootSyncBarrierTests {
 [Theory][InlineData(Readiness.NotReady)][InlineData(Readiness.Fault)] public async Task SyncDoesNotRunWithoutReconciliation(Readiness state){var calls=0;await BootSyncBarrier.RunAsync(()=>Task.FromResult(state),()=>{calls++;return Task.CompletedTask;});Assert.Equal(0,calls);}
 [Fact] public async Task SyncRunsAfterSuccessfulReconciliation(){var calls=0;await BootSyncBarrier.RunAsync(()=>Task.FromResult(Readiness.Ready),()=>{calls++;return Task.CompletedTask;});Assert.Equal(1,calls);}
}
