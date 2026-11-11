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
    public bool AllocationHeld {get;init;}
    public ReadinessDiagnostic[]? ReadinessDiagnostics {get;init;}
    public string? AgentUpdateOperationId { get; init; }
    public string? AgentUpdateTargetVersion { get; init; }
    public string? AgentUpdateStatus { get; init; }
}

public record MachineIdentityConfirmation(string Fingerprint);
public record AgentReleasePublication(string Version, string Channel, string ReleaseNotes, string PackageSha256, string PackagePath, long PackageSizeBytes,
    string SourceCommit, bool SourceDirty, bool ReleaseBuild, string TargetRuntime = "unknown", int AgentUpdateChallengeProtocol = 1);
public record AgentReleaseDto(string Version, string Channel, string ReleaseNotes, string PackageSha256, long PackageSizeBytes, string SourceCommit, bool SourceDirty, DateTimeOffset PublishedAtUtc);
public record AgentUpdateReservationRequest(string ReleaseVersion, string? RequestId = null);
public record AgentUpdateTicketResponse(string OperationId, string MachineId, string Ticket, DateTimeOffset ExpiresAtUtc, string ReleaseVersion, string TargetVersion, string TargetPackageSha256);
public record AgentUpdateOperationDto(string OperationId, string MachineId, string ChainId, string CurrentHeadOperationId,
    long Generation, bool Held, string? ParentOperationId, string ReleaseVersion, string TargetVersion,
    string TargetPackageSha256, string? TargetRuntime, int? TargetProtocol, string? LegacyReportedPreviousVersion,
    string? SourceVersion, string? SourceEvidenceDigest, string? BackupOwnerOperationId, string? BackupDigest,
    string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public record AgentUpdateTicketRequest(string Ticket, string ReleaseVersion, string TargetVersion, string TargetPackageSha256, string? RequestId = null);
public record AgentUpdateStatusReportRequest(string Ticket, string ReleaseVersion, string TargetVersion, string TargetPackageSha256, string Status);
public record AgentUpdateSourceChallengeRequest(
    string Ticket,
    string ReleaseVersion,
    string TargetVersion,
    string TargetPackageSha256,
    string RequestId,
    string HelperBuildSha256,
    string HelperRuntime,
    int HelperProtocol);
public record AgentUpdateSourceChallengeResponse(string ChallengeId, string Challenge, string RuntimeEpoch, DateTimeOffset ExpiresAtUtc);
public record AgentUpdateSourceBindingRequest(
    string Ticket,
    string ReleaseVersion,
    string TargetVersion,
    string TargetPackageSha256,
    string ChallengeId,
    string Challenge,
    string ChallengeRequestId,
    string RequestId,
    string HelperBuildSha256,
    string HelperRuntime,
    int HelperProtocol,
    string RuntimeEpoch,
    string SourceVersion,
    string BackupOwnerOperationId,
    string BackupDigest,
    string InventoryDigest,
    string InstalledTreeDigest,
    string NativeBootId,
    string JournalPhase,
    string ChainId = "",
    string HeadOperationId = "",
    long Generation = 0,
    string EvidenceSignature = "");
public sealed record AgentUpdateSourceEvidence(
    string MachineId,
    string KeyFingerprint,
    string OperationId,
    string ChainId,
    string HeadOperationId,
    long Generation,
    string TargetVersion,
    string TargetPackageSha256,
    string ChallengeId,
    string ChallengeRequestId,
    string SourceRequestId,
    string HelperBuildSha256,
    string HelperRuntime,
    int HelperProtocol,
    string RuntimeEpoch,
    string SourceVersion,
    string BackupOwnerOperationId,
    string BackupDigest,
    string InventoryDigest,
    string InstalledTreeDigest,
    string NativeBootId,
    string JournalPhase);

public static class AgentUpdateSourceEvidenceCanonicalizer
{
    public static byte[] CanonicalBytes(AgentUpdateSourceEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var fields = new[]
        {
            evidence.MachineId, evidence.KeyFingerprint, evidence.OperationId, evidence.ChainId,
            evidence.HeadOperationId, evidence.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            evidence.TargetVersion, evidence.TargetPackageSha256, evidence.ChallengeId, evidence.ChallengeRequestId, evidence.SourceRequestId,
            evidence.HelperBuildSha256, evidence.HelperRuntime, evidence.HelperProtocol.ToString(System.Globalization.CultureInfo.InvariantCulture),
            evidence.RuntimeEpoch, evidence.SourceVersion, evidence.BackupOwnerOperationId, evidence.BackupDigest,
            evidence.InventoryDigest, evidence.InstalledTreeDigest, evidence.NativeBootId, evidence.JournalPhase,
        };
        if (fields.Any(field => string.IsNullOrWhiteSpace(field) || field.Any(char.IsControl)))
            throw new ArgumentException("Source evidence contains an empty or non-canonical field.", nameof(evidence));
        return System.Text.Encoding.UTF8.GetBytes(string.Join('\n', new[] { "KamenRaider.AgentUpdateSourceEvidence.v1" }.Concat(fields)));
    }
}
