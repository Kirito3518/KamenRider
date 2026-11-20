namespace KamenRaider.Contracts;

/// <summary>
/// Admin PC-control snapshot: one entry for every machine, plus the allocation-holding lease
/// that currently blocks or describes that machine (if any).
/// </summary>
public sealed record PcControlSnapshotDto(DateTimeOffset ServerTimeUtc, PcControlMachineDto[] Machines);

public sealed record PcControlMachineDto(MachineDto Machine, PcControlLeaseDto? Lease);

public sealed record PcControlLeaseDto(
    string RentalId,
    string UserId,
    string State,
    long Revision,
    long? PendingRevision,
    DateTimeOffset? StartsAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    long? RemainingSeconds);

public sealed record MachinePowerOffRequest(string RequestId);

public sealed record MachinePowerOffResponse(string CommandId, string Status, DateTimeOffset ExpiresAtUtc);

public sealed record AdminPairingOfferDto(
    string RentalId,
    long LeaseRevision,
    DateTimeOffset ValidUntilUtc,
    string[] PairingIds);

public sealed record AdminPairingRequestDto(string PairingId, string Pin, string IdempotencyKey);

public sealed record AdminPairingStatusDto(string CommandId, string Status);

public sealed record LeaseTimeAdjustRequestDto(int DeltaSeconds, long ExpectedRevision, string IdempotencyKey);

public sealed record LeaseCancelRequestDto(long ExpectedRevision, string IdempotencyKey, string RequestId);

/// <summary>
/// Machine-scoped Sunshine maintenance action requested by an administrator.
/// "restart" queues RestartSunshine; "unpair" queues UnpairSunshine (clears paired clients
/// and restarts Sunshine). Force must be set when an active rental would be interrupted.
/// </summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record AdminSunshineCommandRequestDto(string Action, bool Force, string? RequestId = null)
{
    public const string Restart = "restart";
    public const string Unpair = "unpair";
}

public sealed record AdminSunshineCommandStatusDto(string CommandId, string Action, string Status, DateTimeOffset ExpiresAtUtc);
