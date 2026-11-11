using KamenRaider.Agent.Sunshine;

namespace Agent.Tests;

public sealed class TestMaintenanceFence : IMaintenanceFenceReader
{
    private MaintenanceFenceSnapshot snapshot = new(
        IsKnown: true,
        IsHeld: false,
        OperationId: "test-operation",
        ChainId: "test-chain",
        HeadOperationId: "test-operation",
        Generation: 1,
        Phase: "controllerConfirmed");

    public MaintenanceFenceSnapshot Read() => Volatile.Read(ref snapshot);

    public void SetHeld() => Volatile.Write(ref snapshot, snapshot with
    {
        IsHeld = true,
        Phase = "controllerStarted"
    });

    public void SetUnknown() => Volatile.Write(ref snapshot, MaintenanceFenceSnapshot.UnknownHeld);
}
