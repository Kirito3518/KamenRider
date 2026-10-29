namespace KamenRaider.Contracts;
public static class Protocol { public const int Version = 1; }
public sealed record HealthResponse(int ProtocolVersion, string Status);
