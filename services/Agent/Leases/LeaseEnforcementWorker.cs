namespace KamenRaider.Agent.Leases;
// A missing gate cannot claim that Sunshine ingress or active streams are blocked.
public sealed class ClosedStreamGate:IStreamGate {private static Task Unavailable(CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromException(new InvalidOperationException("No qualified Sunshine stream gate is configured."));}public Task BlockAsync(CancellationToken ct)=>Unavailable(ct);public Task AllowAsync(CancellationToken ct)=>Unavailable(ct);}
public sealed class LeaseEnforcementWorker(LeaseEnforcer enforcer,ILogger<LeaseEnforcementWorker> logger):BackgroundService {
 protected override async Task ExecuteAsync(CancellationToken stoppingToken){while(!stoppingToken.IsCancellationRequested){try{await enforcer.EnforceAsync(stoppingToken);if(enforcer.Fault is {} fault)logger.LogWarning("Lease enforcement fault: {Fault}",fault);}catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){return;}catch(Exception ex){logger.LogError(ex,"Lease enforcement failed; stream permission remains closed");}await Task.Delay(TimeSpan.FromMilliseconds(250),stoppingToken);}}
}
