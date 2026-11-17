using KamenRaider.Contracts;
using KamenRaider.Controller.Security;
using Microsoft.Data.Sqlite;

namespace KamenRaider.Controller.Data;

/// <summary>
/// Single fleet-wide Sunshine management credential. The password is stored only as an
/// AES-256-GCM ciphertext (nonce + tag in separate columns) and is delivered to an approved Agent
/// only through a signed request. It is never logged, echoed by admin metadata or persisted in
/// plaintext anywhere.
/// </summary>
public sealed partial class ControllerDatabase
{
    private const string SunshineCredentialPath = "/agent/sunshine-credential";
    private const int MaximumSunshineUserNameLength = 128;
    private const int MaximumSunshinePasswordLength = 256;
    private const int MaximumSunshineActorLength = 128;

    private sealed record StoredSunshineCredential(string UserName, long Revision, DateTimeOffset UpdatedAtUtc,
        byte[] Ciphertext, byte[] Nonce, byte[] Tag);

    /// <summary>Administrator-facing credential metadata; never carries the password or ciphertext.</summary>
    public SunshineCredentialState GetSunshineCredentialState()
    {
        lock (gate) return GetSunshineCredentialState(null);
    }

    private SunshineCredentialState GetSunshineCredentialState(SqliteTransaction? transaction)
    {
        using var command = Command("SELECT username,revision,updated_at_unix_ms FROM sunshine_management_credential WHERE singleton=1");
        command.Transaction = transaction;
        using var row = command.ExecuteReader();
        if (!row.Read()) return new(false, null, 0, null);
        return new(true, row.GetString(0), row.GetInt64(1), DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(2)));
    }

    /// <summary>
    /// Replaces the fleet-wide credential. Requires a configured encryption key; the returned
    /// state contains metadata only. Throws <see cref="InvalidOperationException"/> when credential
    /// storage is not configured.
    /// </summary>
    public SunshineCredentialState SetSunshineCredential(string userName, string password, string actorId)
    {
        var normalizedUserName = ValidateSunshineUserName(userName);
        if (password is null || password.Length is 0 or > MaximumSunshinePasswordLength)
            throw new ArgumentException("Invalid Sunshine credential password.", nameof(password));
        var normalizedActor = ValidateSunshineActor(actorId);
        lock (gate)
        {
            var protector = sunshineCredentialProtector
                ?? throw new InvalidOperationException("Sunshine credential storage is not configured");
            var protectedCredential = protector.Protect(password);
            using var transaction = connection.BeginTransaction();
            var updatedAtUnixMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
            using (var upsert = Command(
                "INSERT INTO sunshine_management_credential(singleton,username,password_ciphertext,password_nonce,password_tag,revision,updated_at_unix_ms,updated_by) " +
                "VALUES(1,$username,$ciphertext,$nonce,$tag,COALESCE((SELECT revision FROM sunshine_management_credential WHERE singleton=1),0)+1,$updated,$actor) " +
                "ON CONFLICT(singleton) DO UPDATE SET username=$username,password_ciphertext=$ciphertext,password_nonce=$nonce," +
                "password_tag=$tag,revision=sunshine_management_credential.revision+1,updated_at_unix_ms=$updated,updated_by=$actor",
                ("$username", normalizedUserName), ("$ciphertext", protectedCredential.Ciphertext),
                ("$nonce", protectedCredential.Nonce), ("$tag", protectedCredential.Tag),
                ("$updated", updatedAtUnixMs), ("$actor", normalizedActor)))
            {
                upsert.Transaction = transaction;
                if (upsert.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("The Sunshine credential could not be stored.");
            }
            var state = GetSunshineCredentialState(transaction);
            if (!state.Configured || state.Revision < 1 || !string.Equals(state.UserName, normalizedUserName, StringComparison.Ordinal))
                throw new InvalidOperationException("The stored Sunshine credential is malformed.");
            transaction.Commit();
            return state;
        }
    }

    /// <summary>
    /// Signed delivery of the current credential to one approved Agent. Returns null when no
    /// credential is configured or the Agent already applied the current revision. Unapproved,
    /// quarantined or otherwise ineligible Agents are rejected with an exception so the endpoint
    /// never downgrades the decision to "nothing to deliver".
    /// </summary>
    public SunshineCredentialDelivery? FetchSunshineCredential(VerifiedAgentRequest verified, SunshineCredentialFetchRequest request)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(request);
        if (request.KnownRevision < 0) throw new ArgumentException("Invalid Sunshine credential revision.");
        if (verified.Method != "POST" || verified.Path != SunshineCredentialPath || !IsWithinLegacyWindow(verified))
            throw new UnauthorizedAccessException("Signed Sunshine credential request is invalid or outside the allowed window.");
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            if (!ValidateVerifiedRequest(transaction, verified, SunshineCredentialPath))
                throw new UnauthorizedAccessException("Signed Sunshine credential request is invalid.");
            EnsureNotRestoreQuarantined(transaction);
            var machine = ReadSyncMachine(transaction, verified.MachineId);
            if (machine is null || machine.ApprovalStatus != "approved")
                throw new InvalidOperationException("Machine must be approved before it may fetch the Sunshine management credential.");
            if (!AcceptScopedNonce(transaction, verified))
                throw new UnauthorizedAccessException("Signed Sunshine credential request was replayed or invalidated.");
            var stored = ReadStoredSunshineCredential(transaction);
            if (stored is null || request.KnownRevision == stored.Revision)
            {
                transaction.Commit();
                return null;
            }
            var protector = sunshineCredentialProtector
                ?? throw new InvalidOperationException("Sunshine credential storage is not configured");
            var password = protector.Unprotect(stored.Ciphertext, stored.Nonce, stored.Tag);
            transaction.Commit();
            return new SunshineCredentialDelivery(stored.UserName, password, stored.Revision);
        }
    }

    /// <summary>Current fleet-wide credential revision, or 0 when no credential is configured.</summary>
    internal long ReadSunshineCredentialRevision(SqliteTransaction transaction)
    {
        using var command = Command("SELECT revision FROM sunshine_management_credential WHERE singleton=1");
        command.Transaction = transaction;
        return command.ExecuteScalar() is long revision ? revision : 0;
    }

    private StoredSunshineCredential? ReadStoredSunshineCredential(SqliteTransaction? transaction)
    {
        using var command = Command("SELECT username,revision,updated_at_unix_ms,password_ciphertext,password_nonce,password_tag FROM sunshine_management_credential WHERE singleton=1");
        command.Transaction = transaction;
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        return new(row.GetString(0), row.GetInt64(1), DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(2)),
            row.GetFieldValue<byte[]>(3), row.GetFieldValue<byte[]>(4), row.GetFieldValue<byte[]>(5));
    }

    private static string ValidateSunshineUserName(string userName)
    {
        var normalized = (userName ?? string.Empty).Trim();
        if (normalized.Length is 0 or > MaximumSunshineUserNameLength || normalized.Any(char.IsControl))
            throw new ArgumentException("Invalid Sunshine credential user name.", nameof(userName));
        return normalized;
    }

    private static string ValidateSunshineActor(string actorId)
    {
        var normalized = (actorId ?? string.Empty).Trim();
        if (normalized.Length is 0 or > MaximumSunshineActorLength || normalized.Any(char.IsControl))
            throw new ArgumentException("Authenticated actor required.", nameof(actorId));
        return normalized;
    }
}
