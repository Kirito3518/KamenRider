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
public record AgentReleaseDto(string Version, string Channel, string ReleaseNotes, string PackageSha256, long PackageSizeBytes, string SourceCommit, bool SourceDirty, DateTimeOffset PublishedAtUtc,
    string TargetRuntime = "unknown", int AgentUpdateChallengeProtocol = 1);
public record AgentUpdateReservationRequest(string ReleaseVersion, string? RequestId = null);
public record AgentUpdateTicketResponse(string OperationId, string MachineId, string Ticket, DateTimeOffset ExpiresAtUtc, string ReleaseVersion, string TargetVersion, string TargetPackageSha256,
    string? ChainId = null, string? CurrentHeadOperationId = null, long? Generation = null,
    long? TargetPackageLength = null, string? KeyFingerprint = null);
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
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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
        return StrictUtf8.GetBytes(string.Join('\n', new[] { "KamenRaider.AgentUpdateSourceEvidence.v1" }.Concat(fields)));
    }
}

public sealed record SignedRecoveryInspection(
    string MachineId,
    string KeyFingerprint,
    string IntentId,
    string ChainId,
    string HeadOperationId,
    long Generation,
    string RequestId,
    string TargetVersion,
    string TargetPackageSha256,
    string ActualInstalledVersion,
    string SourceAuthority,
    string? BoundSourceVersion,
    string? BoundSourceEvidenceDigest,
    string OldBackupOwnerOperationId,
    string OldBackupDigest,
    string ActualInstalledDigest,
    string NativeBootIdBefore,
    string NativeBootIdAfter,
    string ExclusionProvenance,
    string OldUpdaterAutoResumeStatus,
    string HelperBuildSha256,
    string HelperRuntime,
    int HelperProtocol,
    string EvidenceSignature);

public sealed record AgentUpdateRecoveryInspectionChallengeRequest(SignedRecoveryInspection Inspection);

public sealed record AgentUpdateRecoveryInspectionChallengeResponse(
    string Challenge,
    DateTimeOffset ExpiresAtUtc);

public sealed record AgentUpdateRecoveryInspectionExpectation(
    string MachineId,
    string IntentId,
    string ChainId,
    string HeadOperationId,
    long Generation,
    string RequestId,
    string KeyFingerprint,
    string TargetVersion,
    string TargetPackageSha256,
    string SourceAuthority,
    string? BoundSourceVersion,
    string? BoundSourceEvidenceDigest,
    string OldBackupOwnerOperationId,
    string OldBackupDigest,
    string ExpectedPriorNativeBootId,
    string HelperBuildSha256,
    string HelperRuntime,
    int HelperProtocol,
    bool HelperBuildQualified,
    bool ExclusionProcedureQualified,
    string QualifiedOldUpdaterAutoResumeStatus,
    bool SourceEvidenceQualified);

public static class AgentUpdateRecoveryInspectionCanonicalizer
{
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static byte[] CanonicalBytes(SignedRecoveryInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ValidateInspection(inspection);
        var fields = new[]
        {
            inspection.MachineId,
            inspection.KeyFingerprint,
            inspection.IntentId,
            inspection.ChainId,
            inspection.HeadOperationId,
            inspection.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            inspection.RequestId,
            inspection.TargetVersion,
            inspection.TargetPackageSha256,
            inspection.ActualInstalledVersion,
            inspection.SourceAuthority,
            inspection.BoundSourceVersion ?? "",
            inspection.BoundSourceEvidenceDigest ?? "",
            inspection.OldBackupOwnerOperationId,
            inspection.OldBackupDigest,
            inspection.ActualInstalledDigest,
            inspection.NativeBootIdBefore,
            inspection.NativeBootIdAfter,
            inspection.ExclusionProvenance,
            inspection.OldUpdaterAutoResumeStatus,
            inspection.HelperBuildSha256,
            inspection.HelperRuntime,
            inspection.HelperProtocol.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return StrictUtf8.GetBytes(string.Join('\n', new[]
        {
            "KamenRaider.AgentRecoveryInspection.v1",
        }.Concat(fields)));
    }

    internal static void ValidateInspection(SignedRecoveryInspection inspection)
    {
        if (inspection.Generation <= 0 || inspection.HelperProtocol != 1)
            throw new ArgumentException("Recovery inspection has an invalid generation or helper protocol.", nameof(inspection));
        RequiredText(inspection.MachineId, 128, nameof(inspection.MachineId));
        RequiredText(inspection.KeyFingerprint, 64, nameof(inspection.KeyFingerprint));
        RequiredText(inspection.IntentId, 128, nameof(inspection.IntentId));
        RequiredText(inspection.ChainId, 128, nameof(inspection.ChainId));
        RequiredText(inspection.HeadOperationId, 128, nameof(inspection.HeadOperationId));
        RequiredText(inspection.RequestId, 128, nameof(inspection.RequestId));
        RequiredText(inspection.TargetVersion, 64, nameof(inspection.TargetVersion));
        RequiredText(inspection.ActualInstalledVersion, 64, nameof(inspection.ActualInstalledVersion));
        RequiredText(inspection.OldBackupOwnerOperationId, 128, nameof(inspection.OldBackupOwnerOperationId));
        RequiredText(inspection.SourceAuthority, 64, nameof(inspection.SourceAuthority));
        RequiredText(inspection.NativeBootIdBefore, 32, nameof(inspection.NativeBootIdBefore));
        RequiredText(inspection.NativeBootIdAfter, 32, nameof(inspection.NativeBootIdAfter));
        RequiredText(inspection.HelperRuntime, 32, nameof(inspection.HelperRuntime));
        RequiredText(inspection.OldUpdaterAutoResumeStatus, 64, nameof(inspection.OldUpdaterAutoResumeStatus));
        if (!IsSha256(inspection.KeyFingerprint) || !IsSha256(inspection.TargetPackageSha256)
            || !IsSha256(inspection.OldBackupDigest) || !IsSha256(inspection.ActualInstalledDigest)
            || !IsSha256(inspection.HelperBuildSha256)
            || inspection.HelperRuntime is not ("win-x64" or "win-arm64")
            || !IsNativeBootId(inspection.NativeBootIdBefore) || !IsNativeBootId(inspection.NativeBootIdAfter))
            throw new ArgumentException("Recovery inspection contains an invalid digest or helper identity.", nameof(inspection));

        if (inspection.SourceAuthority switch
            {
                "boundOperationSource" => string.IsNullOrWhiteSpace(inspection.BoundSourceVersion)
                    || inspection.BoundSourceVersion.Length > 64 || inspection.BoundSourceVersion.Any(char.IsControl)
                    || !IsSha256(inspection.BoundSourceEvidenceDigest),
                "verifiedOriginalBackup" => inspection.BoundSourceVersion is not null
                    || inspection.BoundSourceEvidenceDigest is not null,
                _ => true,
            })
            throw new ArgumentException("Recovery inspection has no qualified source authority.", nameof(inspection));

        var sameBoot = string.Equals(inspection.NativeBootIdBefore, inspection.NativeBootIdAfter, StringComparison.Ordinal);
        if (inspection.ExclusionProvenance switch
            {
                "continuousSharedLock" => !sameBoot,
                "verifiedFullHostRestart" => sameBoot,
                _ => true,
            }
            || inspection.OldUpdaterAutoResumeStatus switch
            {
                "qualifiedNotApplicable" => inspection.ExclusionProvenance != "continuousSharedLock",
                "qualifiedDisabled" => inspection.ExclusionProvenance != "verifiedFullHostRestart",
                _ => true,
            })
            throw new ArgumentException("Recovery inspection contains an unqualified exclusion or auto-resume claim.", nameof(inspection));
    }

    internal static bool IsNativeBootId(string value)
        => Guid.TryParseExact(value, "N", out var parsed)
            && parsed != Guid.Empty
            && string.Equals(parsed.ToString("N"), value, StringComparison.Ordinal);

    private static void RequiredText(string value, int maxLength, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
            throw new ArgumentException($"Recovery inspection field '{fieldName}' is invalid.", fieldName);
    }

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64) return false;
        try { return Convert.FromHexString(value).Length == 32; }
        catch (FormatException) { return false; }
    }
}

public static class AgentUpdateRecoveryInspectionValidator
{
    /// <summary>
    /// Checks declared values against independently supplied scope and qualification inputs. This does not
    /// authenticate EvidenceSignature or prove that a boot change, lock handoff, backup verification, or
    /// auto-resume mitigation actually occurred, and must not authorize a recovery transition by itself.
    /// </summary>
    public static bool MatchesQualifiedScopeInputs(SignedRecoveryInspection inspection,
        AgentUpdateRecoveryInspectionExpectation expected)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(expected);
        try { AgentUpdateRecoveryInspectionCanonicalizer.ValidateInspection(inspection); }
        catch (ArgumentException) { return false; }

        if (!expected.HelperBuildQualified || !expected.ExclusionProcedureQualified || !expected.SourceEvidenceQualified
            || !AgentUpdateRecoveryInspectionCanonicalizer.IsNativeBootId(expected.ExpectedPriorNativeBootId)
            || !IsSha256(expected.KeyFingerprint) || !IsSha256(expected.TargetPackageSha256)
            || !IsSha256(expected.OldBackupDigest) || !IsSha256(expected.HelperBuildSha256)
            || expected.HelperRuntime is not ("win-x64" or "win-arm64") || expected.HelperProtocol != 1
            || !IsQualifiedAutoResumeStatus(expected.QualifiedOldUpdaterAutoResumeStatus))
            return false;

        var fullRestart = string.Equals(inspection.ExclusionProvenance, "verifiedFullHostRestart", StringComparison.Ordinal);
        if (!string.Equals(inspection.NativeBootIdBefore, expected.ExpectedPriorNativeBootId, StringComparison.Ordinal)
            || !string.Equals(inspection.OldUpdaterAutoResumeStatus, expected.QualifiedOldUpdaterAutoResumeStatus, StringComparison.Ordinal)
            || (fullRestart && string.Equals(inspection.NativeBootIdAfter, expected.ExpectedPriorNativeBootId, StringComparison.Ordinal))
            || (!fullRestart && !string.Equals(inspection.NativeBootIdAfter, expected.ExpectedPriorNativeBootId, StringComparison.Ordinal)))
            return false;

        var sourceFieldsMatch = expected.SourceAuthority switch
        {
            "boundOperationSource" => !string.IsNullOrWhiteSpace(expected.BoundSourceVersion)
                && IsSha256(expected.BoundSourceEvidenceDigest)
                && string.Equals(inspection.BoundSourceVersion, expected.BoundSourceVersion, StringComparison.Ordinal)
                && string.Equals(inspection.BoundSourceEvidenceDigest, expected.BoundSourceEvidenceDigest, StringComparison.OrdinalIgnoreCase),
            "verifiedOriginalBackup" => expected.BoundSourceVersion is null
                && expected.BoundSourceEvidenceDigest is null
                && inspection.BoundSourceVersion is null
                && inspection.BoundSourceEvidenceDigest is null,
            _ => false,
        };

        return string.Equals(inspection.MachineId, expected.MachineId, StringComparison.Ordinal)
            && string.Equals(inspection.IntentId, expected.IntentId, StringComparison.Ordinal)
            && string.Equals(inspection.ChainId, expected.ChainId, StringComparison.Ordinal)
            && string.Equals(inspection.HeadOperationId, expected.HeadOperationId, StringComparison.Ordinal)
            && inspection.Generation == expected.Generation
            && string.Equals(inspection.RequestId, expected.RequestId, StringComparison.Ordinal)
            && string.Equals(inspection.KeyFingerprint, expected.KeyFingerprint, StringComparison.OrdinalIgnoreCase)
            && string.Equals(inspection.TargetVersion, expected.TargetVersion, StringComparison.Ordinal)
            && string.Equals(inspection.TargetPackageSha256, expected.TargetPackageSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(inspection.SourceAuthority, expected.SourceAuthority, StringComparison.Ordinal)
            && sourceFieldsMatch
            && string.Equals(inspection.OldBackupOwnerOperationId, expected.OldBackupOwnerOperationId, StringComparison.Ordinal)
            && string.Equals(inspection.OldBackupDigest, expected.OldBackupDigest, StringComparison.OrdinalIgnoreCase)
            && string.Equals(inspection.HelperBuildSha256, expected.HelperBuildSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(inspection.HelperRuntime, expected.HelperRuntime, StringComparison.Ordinal)
            && inspection.HelperProtocol == expected.HelperProtocol;
    }

    private static bool IsQualifiedAutoResumeStatus(string value)
        => value is "qualifiedNotApplicable" or "qualifiedDisabled";

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64) return false;
        try { return Convert.FromHexString(value).Length == 32; }
        catch (FormatException) { return false; }
    }
}
