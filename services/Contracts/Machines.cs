namespace KamenRaider.Contracts;
public record EnrollmentRequest(string MachineId, string PublicKey, string LocalIp, string MacAddress, int MoonlightPort, int SunshinePort);
public record EnrollmentResult(string MachineId, string ApprovalStatus);
public record EnrollmentTicketResponse(string Ticket, DateTimeOffset ExpiresAtUtc);
public record AdminVncTicketResponse(string Ticket, DateTimeOffset ExpiresAtUtc);
public record CreateAdminVncTicketRequest(string MachineId, string AllowedOrigin);
public record MachineUpdate(string DisplayName, long HourlyPriceMinorUnits, int MoonlightPort, int SunshinePort) {
    public string? VncLocalIp { get; init; }
}
public record MachineDto(string MachineId, string ApprovalStatus, string DisplayName, long HourlyPriceMinorUnits, string LocalIp, string MacAddress, int MoonlightPort, int SunshinePort, long ConfigRevision, DateTimeOffset? LastSeenAt, string? BootId, string Readiness, string? AgentVersion, string? ApprovedBy, DateTimeOffset? ApprovedAt) {
    public long? AppliedConfigRevision {get;init;}
    public string? PublicKeyFingerprint {get;init;}
    public string? VncLocalIp {get;init;}
    public int ActiveAdminVncSessions {get;init;}
    public bool CanRent {get;init;}
    public ReadinessDiagnostic[]? ReadinessDiagnostics {get;init;}
    public string? AgentUpdateOperationId { get; init; }
    public string? AgentUpdateTargetVersion { get; init; }
    public string? AgentUpdateStatus { get; init; }
}

public record MachineIdentityConfirmation(string Fingerprint);
public record AgentReleasePublication(string Version, string Channel, string ReleaseNotes, string PackageSha256, string PackagePath, long PackageSizeBytes, string SourceCommit, bool SourceDirty, bool ReleaseBuild);
public record AgentReleaseDto(string Version, string Channel, string ReleaseNotes, string PackageSha256, long PackageSizeBytes, string SourceCommit, bool SourceDirty, DateTimeOffset PublishedAtUtc);
public record AgentUpdateReservationRequest(string ReleaseVersion);
public record AgentUpdateTicketResponse(string OperationId, string MachineId, string Ticket, DateTimeOffset ExpiresAtUtc, string ReleaseVersion, string TargetVersion, string TargetPackageSha256);
public record AgentUpdateOperationDto(string OperationId, string MachineId, string ReleaseVersion, string TargetVersion, string TargetPackageSha256, string PreviousVersion, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public record AgentUpdateTicketRequest(string Ticket, string ReleaseVersion, string TargetVersion, string TargetPackageSha256);
public record AgentUpdateStatusReportRequest(string Ticket, string ReleaseVersion, string TargetVersion, string TargetPackageSha256, string Status);
