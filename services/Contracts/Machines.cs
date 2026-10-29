namespace KamenRaider.Contracts;
public record EnrollmentRequest(string MachineId, string PublicKey, string LocalIp, string MacAddress, int MoonlightPort, int SunshinePort);
public record EnrollmentResult(string MachineId, string ApprovalStatus);
public record MachineUpdate(string DisplayName, long HourlyPriceMinorUnits, int MoonlightPort, int SunshinePort);
public record MachineDto(string MachineId, string ApprovalStatus, string DisplayName, long HourlyPriceMinorUnits, string LocalIp, string MacAddress, int MoonlightPort, int SunshinePort, long ConfigRevision, DateTimeOffset? LastSeenAt, string? BootId, string Readiness, string? AgentVersion, string? ApprovedBy, DateTimeOffset? ApprovedAt) {
    public long? AppliedConfigRevision {get;init;}
    public string? PublicKeyFingerprint {get;init;}
    public bool CanRent => ApprovalStatus == "approved";
}

public record MachineIdentityConfirmation(string Fingerprint);
