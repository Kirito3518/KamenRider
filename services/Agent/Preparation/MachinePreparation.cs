using KamenRaider.Contracts;
using KamenRaider.Agent.Leases;

namespace KamenRaider.Agent.Preparation;

// Implementations must be idempotent for command retries and return true only after
// the machine has completed the fenced preparation required before lease activation.
public interface IMachinePreparation
{
    Task<bool> PrepareAsync(CommandEnvelope command, CancellationToken cancellationToken);
}

// Called only for commands in a sync cycle whose boot reconciliation and current
// runtime-health proof succeeded. Preparation confirms the machine remains unleased
// and the stream gate is closed before the Controller can proceed to lease activation.
public sealed class FencedMachinePreparation(LeaseEnforcer enforcer, IStreamGate gate) : IMachinePreparation
{
    public async Task<bool> PrepareAsync(CommandEnvelope command, CancellationToken cancellationToken)
    {
        if (!await enforcer.CanPrepareMachineAsync(command, cancellationToken)) return false;

        await gate.BlockAsync(cancellationToken);

        // Gate IPC can take time; revalidate command expiry and lease state before
        // acknowledging so an expired/stale command cannot reserve a machine.
        return await enforcer.CanPrepareMachineAsync(command, cancellationToken);
    }
}
