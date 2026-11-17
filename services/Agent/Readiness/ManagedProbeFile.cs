using Microsoft.Win32.SafeHandles;

namespace KamenRaider.Agent.Probe;

internal sealed record MachineSecurityContext(SafeAccessTokenHandle TenantToken, string UserSid);

// Implementations must own all validated directory, volume and duplicated tenant-token handles
// for this operation, revalidate them before use, and dispose them when the operation completes.
// Locations must never be serialized or cached between operations or boots.
internal abstract class ValidatedProbeLocation : IDisposable
{
    internal abstract string ParentPath { get; }
    internal abstract string VolumeId { get; }
    internal abstract string MachineId { get; }
    public abstract void Dispose();
}

internal interface IManagedProbeFile
{
    Task<ValidatedProbeLocation> ValidateAsync(
        string parent,
        string volumeId,
        string machineId,
        MachineSecurityContext tenant,
        CancellationToken ct);

    Task<ProbeFileRead> ProvisionAsync(
        ValidatedProbeLocation location,
        ReadOnlyMemory<byte> clean,
        CancellationToken ct);

    Task<ProbeFileRead> ReadAsync(ValidatedProbeLocation location, CancellationToken ct);

    Task<ProbeFileRead> WriteAndVerifyAsync(
        ValidatedProbeLocation location,
        ReadOnlyMemory<byte> expected,
        CancellationToken ct);
}
