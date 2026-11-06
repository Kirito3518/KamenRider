namespace KamenRaider.Agent.Sunshine;
// Reports lack of proof; a no-op cannot close a running Sunshine server.
public sealed class UnverifiedStreamGate:IPairingFence {
 public Task<bool> VerifyOpenForLeaseAsync(PairingScope scope,CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(false);}
 public Task<bool> RunFencedCleanupAsync(Func<CancellationToken,Task> clear,CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(false);}
}
