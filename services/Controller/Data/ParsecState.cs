using KamenRaider.Contracts;
using KamenRaider.Controller.Security;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;

namespace KamenRaider.Controller.Data;

/// <summary>
/// Renter-supplied Parsec account login (0jay-style). The Agent decrypts the sealed secret in
/// memory; the Controller only stores ciphertext for one bounded attempt and purges it once the
/// command is no longer pending.
/// </summary>
public sealed partial class ControllerDatabase
{
    public const int ParsecAttemptWindowSeconds = 150;
    private const int ParsecAttemptsPerTenMinutes = 3;
    private const int ParsecMessageMaxLength = 200;

    private static bool ValidBase64Url(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maxLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string? SanitizeParsecMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var builder = new StringBuilder(Math.Min(value.Length, ParsecMessageMaxLength));
        foreach (var character in value)
        {
            if (builder.Length >= ParsecMessageMaxLength) break;
            if (char.IsControl(character) && character is not ('\r' or '\n' or '\t')) continue;
            builder.Append(character is '\r' or '\n' or '\t' ? ' ' : character);
        }
        return builder.ToString().Trim() is { Length: > 0 } text ? text : null;
    }

    private static ParsecAttemptDto ReadParsecAttempt(SqliteDataReader row) => new(
        row.GetString(0),
        row.GetString(1),
        row.IsDBNull(2) ? null : row.GetString(2),
        row.IsDBNull(3) ? null : row.GetString(3),
        DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(4)),
        DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(5)),
        DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(6)),
        row.IsDBNull(7) ? null : row.GetString(7));

    private const string ParsecAttemptColumns =
        "attempt_id,state,error_code,message,created_at_unix_ms,updated_at_unix_ms,expires_at_unix_ms,boot_key_id";

    /// <summary>Stores the capability advertised by the signed heartbeat or invalidates it.</summary>
    public void StoreParsecCapability(SqliteTransaction tx, string machineId, ParsecCapability? capability, long now)
    {
        if (capability is null || !capability.Available ||
            !ValidBase64Url(capability.BootKeyId, 64) || !ValidBase64Url(capability.PublicKey, 1024))
        {
            ExecuteLease(tx, "DELETE FROM machine_parsec_state WHERE machine_id=$id", ("$id", machineId));
            ExecuteLease(tx,
                "UPDATE parsec_attempts SET state='failed',error_code=$code,message=NULL,updated_at_unix_ms=$now WHERE machine_id=$id AND state IN ('queued','starting','waiting_email')",
                ("$id", machineId), ("$code", ParsecErrorCodes.AgentOffline), ("$now", now));
            ExecuteLease(tx,
                "UPDATE command_outbox SET status='invalidated',payload='{}' WHERE machine_id=$id AND type='ParsecConnect' AND status='pending'",
                ("$id", machineId));
            return;
        }

        ExecuteLease(tx,
            "INSERT INTO machine_parsec_state(machine_id,available,boot_key_id,public_key,updated_at_unix_ms) VALUES($id,1,$key,$public,$now) " +
            "ON CONFLICT(machine_id) DO UPDATE SET available=1,boot_key_id=$key,public_key=$public,updated_at_unix_ms=$now",
            ("$id", machineId), ("$key", capability.BootKeyId), ("$public", capability.PublicKey), ("$now", now));
        // A new boot key means old sealed secrets can no longer be opened by this Agent.
        ExecuteLease(tx,
            "UPDATE parsec_attempts SET state='failed',error_code=$code,message=NULL,updated_at_unix_ms=$now " +
            "WHERE machine_id=$id AND boot_key_id<>$key AND state IN ('queued','starting','waiting_email')",
            ("$id", machineId), ("$key", capability.BootKeyId), ("$code", ParsecErrorCodes.AgentRebooted), ("$now", now));
    }

    public MachineParsecCapability GetParsecCapability(string machineId)
    {
        Identifier(machineId);
        lock (gate)
        {
            using var command = Command(
                "SELECT available,boot_key_id,public_key FROM machine_parsec_state WHERE machine_id=$id",
                ("$id", machineId));
            using var row = command.ExecuteReader();
            if (!row.Read() || row.GetInt64(0) == 0) return MachineParsecCapability.Unavailable;
            return new(true, row.IsDBNull(1) ? null : row.GetString(1), row.IsDBNull(2) ? null : row.GetString(2));
        }
    }

    public ParsecLeaseState GetLeaseParsecState(string rentalId, string actorId)
    {
        Identifier(rentalId);
        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            var lease = FindLease(tx, rentalId) ?? throw new KeyNotFoundException();
            if (string.IsNullOrWhiteSpace(actorId) || lease.Dto.UserId != actorId)
                throw new UnauthorizedAccessException();
            // Reading must not depend on an Agent heartbeat: an offline Agent would otherwise
            // leave queued/starting/waiting_email attempts (and the renter's spinner) alive
            // forever, because expiry used to run only inside accepted Agent syncs.
            ExpireParsecAttempts(tx, clock.GetUtcNow().ToUnixTimeMilliseconds());
            var capability = ReadMachineParsecCapability(tx, lease.Dto.MachineId);
            ParsecAttemptDto? attempt = null;
            using (var command = LeaseCommand(tx,
                       $"SELECT {ParsecAttemptColumns} FROM parsec_attempts WHERE rental_id=$rental ORDER BY created_at_unix_ms DESC, rowid DESC LIMIT 1",
                       ("$rental", rentalId)))
            using (var row = command.ExecuteReader())
                if (row.Read()) attempt = ReadParsecAttempt(row);
            var available = lease.Dto.State == "active" && LeaseHasParsecEnabled(tx, lease.Dto.MachineId) && capability.Available;
            tx.Commit();
            return new(available, available ? capability.BootKeyId : null, available ? capability.PublicKey : null, attempt);
        }
    }

    public ParsecAttemptDto GetParsecAttempt(string rentalId, string attemptId, string actorId)
    {
        Identifier(rentalId);
        Identifier(attemptId);
        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            var lease = FindLease(tx, rentalId) ?? throw new KeyNotFoundException();
            if (string.IsNullOrWhiteSpace(actorId) || lease.Dto.UserId != actorId)
                throw new UnauthorizedAccessException();
            ExpireParsecAttempts(tx, clock.GetUtcNow().ToUnixTimeMilliseconds());
            using var command = LeaseCommand(tx,
                $"SELECT {ParsecAttemptColumns} FROM parsec_attempts WHERE attempt_id=$attempt AND rental_id=$rental",
                ("$attempt", attemptId), ("$rental", rentalId));
            using var row = command.ExecuteReader();
            if (!row.Read()) throw new KeyNotFoundException();
            var attempt = ReadParsecAttempt(row);
            tx.Commit();
            return attempt;
        }
    }

    public ParsecAttemptDto QueueParsecAttempt(string rentalId, string actorId, ParsecAttemptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Identifier(rentalId);
        Identifier(request.IdempotencyKey);
        if (string.IsNullOrWhiteSpace(actorId))
            throw new UnauthorizedAccessException();
        var secret = request.Secret ?? throw new ArgumentException("Missing sealed secret");
        if (!ValidBase64Url(secret.WrappedKey, 512) || !ValidBase64Url(secret.Iv, 64) || !ValidBase64Url(secret.Ciphertext, 8192))
            throw new ArgumentException("Invalid sealed secret");

        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            // A globally quarantined Controller must refuse new lease-mutating work; the
            // ParsecEndpoints.Execute wrapper maps this to 409 Conflict like the other lease paths.
            EnsureNotRestoreQuarantined(tx);
            var lease = FindLease(tx, rentalId) ?? throw new KeyNotFoundException();
            if (lease.Dto.UserId != actorId) throw new UnauthorizedAccessException();
            if (lease.Dto.State != "active" || string.IsNullOrWhiteSpace(lease.Boot))
                throw new InvalidOperationException("Machine lease is not active");

            using (var prior = LeaseCommand(tx, "SELECT attempt_id FROM parsec_attempts WHERE idempotency_key=$key",
                       ("$key", request.IdempotencyKey)))
            {
                if (prior.ExecuteScalar() is string existingId)
                {
                    using var existing = LeaseCommand(tx,
                        $"SELECT {ParsecAttemptColumns} FROM parsec_attempts WHERE attempt_id=$id AND rental_id=$rental",
                        ("$id", existingId), ("$rental", rentalId));
                    using var row = existing.ExecuteReader();
                    if (!row.Read()) throw new KeyNotFoundException();
                    var attempt = ReadParsecAttempt(row);
                    tx.Commit();
                    return attempt;
                }
            }

            if (!LeaseHasParsecEnabled(tx, lease.Dto.MachineId))
                throw new InvalidOperationException("Parsec is not enabled for this machine");
            var capability = ReadMachineParsecCapability(tx, lease.Dto.MachineId);
            if (!capability.Available || capability.BootKeyId is null)
                throw new InvalidOperationException("Agent has not advertised Parsec");

            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();

            // Round two of Parsec's e-mail/IP challenge. The renter proves which challenged
            // attempt they are continuing; the attempt must belong to this lease, renter and
            // current Agent boot key. Replacing it is the only way to queue a second login while
            // the first one is still waiting_email.
            var retryAttemptId = string.IsNullOrWhiteSpace(request.RetryAttemptId)
                ? null
                : request.RetryAttemptId;
            if (retryAttemptId is not null)
            {
                Identifier(retryAttemptId);
                string retryState;
                string? retryBootKeyId;
                using (var retry = LeaseCommand(tx,
                           "SELECT state,boot_key_id FROM parsec_attempts WHERE attempt_id=$attempt AND rental_id=$rental AND user_id=$user",
                           ("$attempt", retryAttemptId), ("$rental", rentalId), ("$user", actorId)))
                using (var row = retry.ExecuteReader())
                {
                    if (!row.Read())
                        throw new InvalidOperationException("Retry attempt is not available for this lease");
                    retryState = row.GetString(0);
                    retryBootKeyId = row.IsDBNull(1) ? null : row.GetString(1);
                }

                if (retryState != ParsecStates.WaitingEmail ||
                    !string.Equals(retryBootKeyId, capability.BootKeyId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Retry attempt is not awaiting e-mail verification");

                // Mark the challenged attempt failed and invalidate its pending ParsecConnect in the
                // same transaction. The unique partial index lease_parsec_pending allows only one
                // pending ParsecConnect per rental, so this must happen before the new insert.
                ExecuteLease(tx,
                    "UPDATE parsec_attempts SET state='failed', error_code=$code, message=NULL, updated_at_unix_ms=$now WHERE attempt_id=$id AND state='waiting_email'",
                    ("$code", ParsecErrorCodes.EmailVerified), ("$now", now), ("$id", retryAttemptId));
                ExecuteLease(tx,
                    "UPDATE command_outbox SET status='invalidated', payload='{}' WHERE type='ParsecConnect' AND rental_id=$rental AND status='pending'",
                    ("$rental", rentalId));
            }

            using (var running = LeaseCommand(tx,
                       "SELECT COUNT(*) FROM parsec_attempts WHERE rental_id=$rental AND state IN ('queued','starting','waiting_email')",
                       ("$rental", rentalId)))
                if ((long)running.ExecuteScalar()! != 0)
                    throw new InvalidOperationException("Parsec login is already running");

            using (var recent = LeaseCommand(tx,
                       "SELECT COUNT(*) FROM parsec_attempts WHERE machine_id=$machine AND created_at_unix_ms > $since",
                       ("$machine", lease.Dto.MachineId), ("$since", now - 10 * 60 * 1000)))
                if ((long)recent.ExecuteScalar()! >= ParsecAttemptsPerTenMinutes)
                    throw new InvalidOperationException("Too many Parsec attempts");

            var attemptId = Guid.NewGuid().ToString("N");
            var expiresAt = now + ParsecAttemptWindowSeconds * 1000L;
            ExecuteLease(tx,
                "INSERT INTO parsec_attempts(attempt_id,machine_id,rental_id,user_id,idempotency_key,state,created_at_unix_ms,updated_at_unix_ms,expires_at_unix_ms,boot_key_id) " +
                "VALUES($id,$machine,$rental,$user,$key,'queued',$now,$now,$expires,$boot)",
                ("$id", attemptId), ("$machine", lease.Dto.MachineId), ("$rental", rentalId),
                ("$user", actorId), ("$key", request.IdempotencyKey), ("$now", now), ("$expires", expiresAt),
                ("$boot", capability.BootKeyId));

            var commandId = Guid.NewGuid().ToString("N");
            var payload = JsonSerializer.Serialize(new
            {
                attemptId,
                deadlineAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(expiresAt).ToString("O"),
                secret = new { wrappedKey = secret.WrappedKey, iv = secret.Iv, ciphertext = secret.Ciphertext }
            });
            ExecuteLease(tx,
                "INSERT INTO command_outbox(command_id,type,rental_id,machine_id,lease_revision,boot_id,valid_until,payload) VALUES($command,'ParsecConnect',$rental,$machine,$revision,$boot,$until,$payload)",
                ("$command", commandId), ("$rental", rentalId), ("$machine", lease.Dto.MachineId),
                ("$revision", lease.Dto.Revision), ("$boot", lease.Boot), ("$until", expiresAt), ("$payload", payload));
            tx.Commit();
            return new ParsecAttemptDto(attemptId, ParsecStates.Queued, null, null,
                DateTimeOffset.FromUnixTimeMilliseconds(now), DateTimeOffset.FromUnixTimeMilliseconds(now),
                DateTimeOffset.FromUnixTimeMilliseconds(expiresAt), capability.BootKeyId);
        }
    }

    public void RecordParsecStatus(VerifiedAgentRequest verified, ParsecStatusReport report)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(report);
        Identifier(report.AttemptId);
        if (report.Status is not (ParsecStates.Queued or ParsecStates.Starting or ParsecStates.WaitingEmail or ParsecStates.Ready or ParsecStates.Failed))
            throw new ArgumentException("Invalid Parsec status");
        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            long now;
            using (var command = LeaseCommand(tx,
                       "SELECT machine_id,boot_key_id,state FROM parsec_attempts WHERE attempt_id=$id",
                       ("$id", report.AttemptId)))
            using (var row = command.ExecuteReader())
            {
                if (!row.Read()) throw new KeyNotFoundException();
                if (row.GetString(0) != verified.MachineId)
                    throw new UnauthorizedAccessException();
                if (row.IsDBNull(1) || !string.Equals(row.GetString(1), report.BootKeyId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Parsec attempt boot key is stale");
                if (row.GetString(2) is ParsecStates.Ready or ParsecStates.Failed)
                {
                    tx.Commit();
                    return;
                }
            }

            now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            ExecuteLease(tx,
                "UPDATE parsec_attempts SET state=$state,error_code=$error,message=$message,updated_at_unix_ms=$now WHERE attempt_id=$id AND machine_id=$machine",
                ("$state", report.Status), ("$error", report.ErrorCode), ("$message", SanitizeParsecMessage(report.Message)),
                ("$now", now), ("$id", report.AttemptId), ("$machine", verified.MachineId));
            ExecuteLease(tx, "UPDATE command_outbox SET payload='{}' WHERE type='ParsecConnect' AND status<>'pending' AND machine_id=$machine",
                ("$machine", verified.MachineId));
            tx.Commit();
        }
    }

    /// <summary>Marks timed-out attempts failed and purges sealed payloads that can no longer be delivered.</summary>
    public void ExpireParsecAttempts(SqliteTransaction tx, long now)
    {
        ExecuteLease(tx,
            "UPDATE parsec_attempts SET state='failed',error_code=$code,message=NULL,updated_at_unix_ms=$now " +
            "WHERE state IN ('queued','starting','waiting_email') AND expires_at_unix_ms<=$now",
            ("$code", ParsecErrorCodes.Timeout), ("$now", now));
        ExecuteLease(tx,
            "UPDATE command_outbox SET status='expired',payload='{}' WHERE type='ParsecConnect' AND status='pending' AND valid_until<=$now",
            ("$now", now));
        ExecuteLease(tx,
            "UPDATE command_outbox SET payload='{}' WHERE type='ParsecConnect' AND status<>'pending' AND payload<>'{}'");
    }

    /// <summary>Called when a rental ends: no Parsec login may outlive its lease.</summary>
    public void FailParsecAttemptsForLease(SqliteTransaction tx, string rentalId, long now)
    {
        ExecuteLease(tx,
            "UPDATE parsec_attempts SET state='failed',error_code=$code,message=NULL,updated_at_unix_ms=$now " +
            "WHERE rental_id=$rental AND state IN ('queued','starting','waiting_email')",
            ("$rental", rentalId), ("$code", ParsecErrorCodes.LeaseEnded), ("$now", now));
        ExecuteLease(tx,
            "UPDATE command_outbox SET status='invalidated',payload='{}' WHERE type='ParsecConnect' AND rental_id=$rental AND status='pending'",
            ("$rental", rentalId));
    }

    /// <summary>Records the Agent's acceptance of a ParsecConnect command.</summary>
    public void ApplyParsecConnectResult(SqliteTransaction tx, string commandId, string status, long now)
    {
        string? attemptId = null;
        using (var read = LeaseCommand(tx, "SELECT payload FROM command_outbox WHERE command_id=$id", ("$id", commandId)))
            if (read.ExecuteScalar() is string payload)
                try
                {
                    using var document = JsonDocument.Parse(payload);
                    if (document.RootElement.TryGetProperty("attemptId", out var value)) attemptId = value.GetString();
                }
                catch (JsonException) { }

        ExecuteLease(tx, "UPDATE command_outbox SET status=$status,payload='{}' WHERE command_id=$id",
            ("$status", status == "applied" ? "applied" : "failed"), ("$id", commandId));
        if (attemptId is null) return;
        if (status == "failed")
            ExecuteLease(tx,
                "UPDATE parsec_attempts SET state='failed',error_code=$code,updated_at_unix_ms=$now WHERE attempt_id=$id AND state IN ('queued','starting','waiting_email')",
                ("$code", ParsecErrorCodes.Internal), ("$now", now), ("$id", attemptId));
    }

    /// <summary>Admin opt-in for a machine; disabling it ends any queued attempt immediately.</summary>
    public MachineParsecCapability SetMachineParsecEnabled(string machineId, bool enabled, string actorId)
    {
        Identifier(machineId);
        if (string.IsNullOrWhiteSpace(actorId)) throw new UnauthorizedAccessException();
        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            using (var exists = LeaseCommand(tx, "SELECT COUNT(*) FROM machines WHERE machine_id=$id", ("$id", machineId)))
                if ((long)exists.ExecuteScalar()! == 0) throw new KeyNotFoundException();
            ExecuteLease(tx, "UPDATE machines SET parsec_enabled=$enabled WHERE machine_id=$id",
                ("$enabled", enabled ? 1 : 0), ("$id", machineId));
            if (!enabled)
            {
                var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
                ExecuteLease(tx,
                    "UPDATE parsec_attempts SET state='failed',error_code=$code,message=NULL,updated_at_unix_ms=$now WHERE machine_id=$id AND state IN ('queued','starting','waiting_email')",
                    ("$id", machineId), ("$code", ParsecErrorCodes.LeaseEnded), ("$now", now));
                ExecuteLease(tx,
                    "UPDATE command_outbox SET status='invalidated',payload='{}' WHERE machine_id=$id AND type='ParsecConnect' AND status='pending'",
                    ("$id", machineId));
            }
            var capability = ReadMachineParsecCapability(tx, machineId);
            tx.Commit();
            return capability;
        }
    }

    private MachineParsecCapability ReadMachineParsecCapability(SqliteTransaction tx, string machineId)
    {
        using var command = LeaseCommand(tx, "SELECT available,boot_key_id,public_key FROM machine_parsec_state WHERE machine_id=$id",
            ("$id", machineId));
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetInt64(0) == 0) return MachineParsecCapability.Unavailable;
        return new(true, row.IsDBNull(1) ? null : row.GetString(1), row.IsDBNull(2) ? null : row.GetString(2));
    }

    private bool LeaseHasParsecEnabled(SqliteTransaction tx, string machineId)
    {
        using var command = LeaseCommand(tx, "SELECT parsec_enabled FROM machines WHERE machine_id=$id", ("$id", machineId));
        return command.ExecuteScalar() is long enabled && enabled != 0;
    }
}
