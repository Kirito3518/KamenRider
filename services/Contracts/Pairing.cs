namespace KamenRaider.Contracts;
public record PairingOffer(string RentalId,long Revision,string BootId,DateTimeOffset ValidUntilUtc,string[] PairingIds);
public record PairingRequest(string UserId,string PairingId,string Pin,string IdempotencyKey);
public record PairingCommandStatus(string CommandId,string Status);
