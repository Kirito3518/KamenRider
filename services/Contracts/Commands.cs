using System.Text.Json;
namespace KamenRaider.Contracts;
public record CommandEnvelope(string CommandId,string Type,string RentalId,long LeaseRevision,string BootId,DateTimeOffset ValidUntilUtc,JsonElement Payload);
public record CommandResult(string CommandId,string Status,long AppliedRevision=0,string? ErrorCode=null);
