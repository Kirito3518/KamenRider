namespace KamenRaider.Agent.Sunshine;
// Reports lack of proof; a no-op cannot close a running Sunshine server.
public sealed class UnverifiedStreamGate:IPairingFence {
 public Task<bool> BlockAndVerifyAsync(CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(false);}
}
