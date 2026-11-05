using KamenRaider.Contracts;

namespace KamenRaider.Agent.Preparation;

// Implementations must be idempotent for command retries and return true only after
// the machine has completed the fenced preparation required before lease activation.
public interface IMachinePreparation
{
    Task<bool> PrepareAsync(CommandEnvelope command, CancellationToken cancellationToken);
}

public sealed class UnavailableMachinePreparation : IMachinePreparation
{
    public Task<bool> PrepareAsync(CommandEnvelope command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}
