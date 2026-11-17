namespace KamenRaider.Contracts;

/// <summary>
/// Bounded hardware identity reported by the Agent at enrollment and on sync. The values are
/// diagnostic/display data only; they never grant readiness or approval. Text is trimmed and
/// bounded by the Controller before it is persisted or returned to the web.
/// </summary>
public record HardwareInfo(string Hostname, string Cpu, string Gpu, long RamBytes);

/// <summary>Fleet-wide Sunshine management credential metadata. It never carries the password.</summary>
public record SunshineCredentialSetting(string UserName, long Revision, DateTimeOffset UpdatedAtUtc);

/// <summary>Admin/operator view of the fleet-wide Sunshine credential; never includes the password.</summary>
public record SunshineCredentialState(bool Configured, string? UserName, long Revision, DateTimeOffset? UpdatedAtUtc);

/// <summary>Administrator request that replaces the fleet-wide Sunshine management credential.</summary>
public record SunshineCredentialUpdate(string UserName, string Password);

/// <summary>Signed delivery of the current Sunshine management credential to one enrolled Agent.</summary>
public record SunshineCredentialDelivery(string UserName, string Password, long Revision);

/// <summary>Agent request for the fleet-wide credential when its applied revision is behind.</summary>
public record SunshineCredentialFetchRequest(long KnownRevision);
