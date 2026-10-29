namespace KamenRaider.Agent.Leases;
// Deliberately closed until Task 6 installs the real stream gate adapter.
public sealed class ClosedStreamGate:IStreamGate {public Task BlockAsync(CancellationToken ct)=>Task.CompletedTask;}
public sealed class LeaseEnforcementWorker(LeaseEnforcer enforcer,ILogger<LeaseEnforcementWorker> logger):BackgroundService {
 protected override async Task ExecuteAsync(CancellationToken stoppingToken){while(!stoppingToken.IsCancellationRequested){try{await enforcer.EnforceAsync(stoppingToken);if(enforcer.Fault is {} fault)logger.LogWarning("Lease enforcement fault: {Fault}",fault);}catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){return;}catch(Exception ex){logger.LogError(ex,"Lease enforcement failed; stream permission remains closed");}await Task.Delay(TimeSpan.FromMilliseconds(250),stoppingToken);}}
}
