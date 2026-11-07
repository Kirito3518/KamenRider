namespace KamenRaider.Contracts;
public static class PairingIds
{
    public const string FoundationSunshineSinglePendingSlotId = "foundation-sunshine-single-pending-slot";

    public static bool IsValid(string? id) =>
        id == FoundationSunshineSinglePendingSlotId ||
        id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public record PairingOffer(string RentalId,long Revision,string BootId,DateTimeOffset ValidUntilUtc,string[] PairingIds);
public record PairingRequest(string UserId,string PairingId,string Pin,string IdempotencyKey);
public record PairingCommandStatus(string CommandId,string Status);
