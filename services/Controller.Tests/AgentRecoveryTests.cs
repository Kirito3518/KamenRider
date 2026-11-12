using KamenRaider.Contracts;
using System.Text;

namespace Controller.Tests;

public sealed class AgentRecoveryTests
{
    [Fact]
    public void RecoveryInspectionCanonicalPayloadBindsBackupSourceAndInstalledInventory()
    {
        var inspection = ValidInspection();
        var original = Convert.ToHexString(AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(inspection));

        var changedBackup = Convert.ToHexString(AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { OldBackupDigest = new string('F', 64) }));
        var changedInstalled = Convert.ToHexString(AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { ActualInstalledDigest = new string('E', 64) }));
        var changedSourceEvidence = Convert.ToHexString(AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { BoundSourceEvidenceDigest = new string('9', 64) }));

        Assert.NotEqual(original, changedBackup);
        Assert.NotEqual(original, changedInstalled);
        Assert.NotEqual(original, changedSourceEvidence);
        Assert.StartsWith("KamenRaider.AgentRecoveryInspection.v1\n", Encoding.UTF8.GetString(
            AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(inspection)));
    }

    [Fact]
    public void RecoveryInspectionCanonicalizationRejectsUnpairedSurrogates()
    {
        var replacement = ValidInspection() with { IntentId = "intent-\uFFFD" };
        var replacementBytes = AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(replacement);

        foreach (var unpairedSurrogate in new[] { '\uD800', '\uDC00' })
        {
            var malformed = ValidInspection() with { IntentId = $"intent-{unpairedSurrogate}" };
            Assert.ThrowsAny<ArgumentException>(() => AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(malformed));
        }

        Assert.Contains("intent-\uFFFD", Encoding.UTF8.GetString(replacementBytes));
        Assert.Equal(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(replacementBytes)), replacementBytes);

        var validUnicodeBytes = AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            ValidInspection() with { IntentId = "intent-λ-🚀" });
        var validUnicodeText = Encoding.UTF8.GetString(validUnicodeBytes);
        Assert.Contains("intent-λ-🚀", validUnicodeText);
        Assert.Equal(Encoding.UTF8.GetBytes(validUnicodeText), validUnicodeBytes);
    }

    [Fact]
    public void RecoveryInspectionMustMatchEnrolledKeyIntentHeadSourceTargetAndOriginalBackup()
    {
        var inspection = ValidInspection();
        var expected = ValidExpectation();

        Assert.True(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(inspection, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection with { KeyFingerprint = new string('F', 64) }, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection with { HeadOperationId = "stale-head" }, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection with { TargetPackageSha256 = new string('F', 64) }, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection with { BoundSourceVersion = "another-source" }, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection with { BoundSourceEvidenceDigest = new string('9', 64) }, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection with { OldBackupOwnerOperationId = "another-owner" }, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection with { OldBackupDigest = new string('F', 64) }, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection, expected with { ExpectedPriorNativeBootId = new string('B', 32) }));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection, expected with { ExpectedPriorNativeBootId = "boot-before" }));
    }

    [Fact]
    public void RecoveryInspectionRejectsUnknownOrUnqualifiedOldUpdaterAutoResumeEvidence()
    {
        var inspection = ValidInspection();

        Assert.Throws<ArgumentException>(() => AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { ExclusionProvenance = "unknown" }));
        Assert.Throws<ArgumentException>(() => AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { NativeBootIdAfter = inspection.NativeBootIdBefore }));
        Assert.Throws<ArgumentException>(() => AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { NativeBootIdBefore = "boot-before" }));
        Assert.Throws<ArgumentException>(() => AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { NativeBootIdBefore = "00000000000000000000000000000000" }));
        Assert.Throws<ArgumentException>(() => AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { OldUpdaterAutoResumeStatus = "unknown" }));
        Assert.Throws<ArgumentException>(() => AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { OldUpdaterAutoResumeStatus = "unqualified" }));
        Assert.Throws<ArgumentException>(() => AgentUpdateRecoveryInspectionCanonicalizer.CanonicalBytes(
            inspection with { ActualInstalledDigest = "missing" }));

        var expected = ValidExpectation();
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection, expected with { HelperBuildQualified = false }));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection, expected with { ExclusionProcedureQualified = false }));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection, expected with { QualifiedOldUpdaterAutoResumeStatus = "unknown" }));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection, expected with { QualifiedOldUpdaterAutoResumeStatus = "unqualified" }));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection, expected with { SourceEvidenceQualified = false }));
    }

    [Fact]
    public void LegacyRecoveryMayUseOnlyTheExplicitlyQualifiedOriginalBackupAuthority()
    {
        var inspection = ValidInspection() with
        {
            SourceAuthority = "verifiedOriginalBackup",
            BoundSourceVersion = null,
            BoundSourceEvidenceDigest = null,
        };
        var expected = ValidExpectation() with
        {
            SourceAuthority = "verifiedOriginalBackup",
            BoundSourceVersion = null,
            BoundSourceEvidenceDigest = null,
        };

        Assert.True(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(inspection, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection with { SourceAuthority = "unverifiedLegacyHint" }, expected));
        Assert.False(AgentUpdateRecoveryInspectionValidator.MatchesQualifiedScopeInputs(
            inspection, expected with { SourceAuthority = "unverifiedLegacyHint" }));
    }

    private static SignedRecoveryInspection ValidInspection() => new(
        "0123456789abcdef0123456789abcdef",
        new string('A', 64),
        "intent-1",
        "chain-1",
        "head-1",
        3,
        "inspection-request-1",
        "2.0.0",
        new string('B', 64),
        "1.0.0",
        "boundOperationSource",
        "0.9.0",
        new string('F', 64),
        "backup-owner-1",
        new string('C', 64),
        new string('D', 64),
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        "verifiedFullHostRestart",
        "qualifiedDisabled",
        new string('E', 64),
        "win-x64",
        1,
        "signature");

    private static AgentUpdateRecoveryInspectionExpectation ValidExpectation() => new(
        "0123456789abcdef0123456789abcdef",
        "intent-1",
        "chain-1",
        "head-1",
        3,
        "inspection-request-1",
        new string('A', 64),
        "2.0.0",
        new string('B', 64),
        "boundOperationSource",
        "0.9.0",
        new string('F', 64),
        "backup-owner-1",
        new string('C', 64),
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        new string('E', 64),
        "win-x64",
        1,
        true,
        true,
        "qualifiedDisabled",
        true);
}
