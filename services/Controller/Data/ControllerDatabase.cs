using Microsoft.Data.Sqlite;
using KamenRaider.Contracts;
using KamenRaider.Controller.RemoteDesktop;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using KamenRaider.Controller.Security;
namespace KamenRaider.Controller.Data;
public record EnrollmentPolicy(int MaxPending = 100, int NewPerMinute = 10);
public sealed class EnrollmentCapacityException : Exception { }
public sealed partial class ControllerDatabase : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate=new();
    private readonly EnrollmentPolicy policy;
    private readonly TimeProvider clock;
    private readonly string agentReleaseRoot;
    private readonly bool agentReleaseRemediationEnabled;
    private readonly string agentMaintenancePinnedHelperSha256;
    private readonly bool agentMaintenanceExclusionProcedureQualified;
    private readonly string agentMaintenanceQualifiedAutoResumeStatus;
    private readonly SunshineCredentialProtector? sunshineCredentialProtector;
    private readonly string databasePath;
    private readonly string syncProofRuntimeEpoch = Guid.NewGuid().ToString("N");
    private readonly byte[] syncProofHmacKey = RandomNumberGenerator.GetBytes(32);
    public ControllerDatabase(string path, EnrollmentPolicy? policy = null, TimeProvider? clock = null, bool agentReleaseRemediationEnabled = false,
        string agentMaintenancePinnedHelperSha256 = "", bool agentMaintenanceExclusionProcedureQualified = false,
        string agentMaintenanceQualifiedAutoResumeStatus = "", string sunshineCredentialKey = "")
        : this(path, policy, clock, agentReleaseRemediationEnabled, invalidateRuntimeProofsOnOpen: true,
            agentMaintenancePinnedHelperSha256, agentMaintenanceExclusionProcedureQualified, agentMaintenanceQualifiedAutoResumeStatus,
            sunshineCredentialKey) { }

    internal ControllerDatabase(string path, EnrollmentPolicy? policy, TimeProvider? clock, bool agentReleaseRemediationEnabled, bool invalidateRuntimeProofsOnOpen,
        string agentMaintenancePinnedHelperSha256 = "", bool agentMaintenanceExclusionProcedureQualified = false,
        string agentMaintenanceQualifiedAutoResumeStatus = "", string sunshineCredentialKey = "") {
        this.policy = policy ?? new(); this.clock = clock ?? TimeProvider.System;
        this.agentReleaseRemediationEnabled = agentReleaseRemediationEnabled;
        this.agentMaintenancePinnedHelperSha256 = agentMaintenancePinnedHelperSha256 ?? string.Empty;
        this.agentMaintenanceExclusionProcedureQualified = agentMaintenanceExclusionProcedureQualified;
        this.agentMaintenanceQualifiedAutoResumeStatus = agentMaintenanceQualifiedAutoResumeStatus ?? string.Empty;
        // A malformed key leaves the protector unconfigured; credential storage then fails closed.
        if (SunshineCredentialProtector.IsValidKey(sunshineCredentialKey))
            sunshineCredentialProtector = new SunshineCredentialProtector(sunshineCredentialKey);
        databasePath = Path.GetFullPath(path);
        var databaseExistedBeforeOpen = File.Exists(databasePath);
        agentReleaseRoot=Path.Combine(Path.GetDirectoryName(databasePath)!,"agent-releases");
        if (this.policy.MaxPending <= 0 || this.policy.NewPerMinute <= 0) throw new ArgumentException("Positive enrollment limits required");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        connection=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=databasePath, ForeignKeys=true, Pooling=false}.ToString()); connection.Open();
        try {
            var startingUserVersion = 0L;
            using (var version = connection.CreateCommand()) {
                version.CommandText = "PRAGMA user_version";
                startingUserVersion = Convert.ToInt64(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }
            ControllerDatabaseMaintenance.ValidateCanonicalSchema(connection, startingUserVersion);
            using var transaction=connection.BeginTransaction();
            foreach (var name in new[] { "001-machines.sql", "002-leases-commands.sql", "003-pairing.sql", "004-boot-challenges.sql", "005-config-acks.sql", "006-enrollment-tickets.sql", "007-admin-vnc-sessions.sql", "008-vnc-approved-target.sql", "009-agent-version.sql", "010-completed-leases.sql", "011-readiness-diagnostics.sql", "012-agent-releases.sql", "013-agent-update-downloads.sql", "014-agent-maintenance-chains.sql", "015-agent-sync-proof.sql", "016-agent-sync-challenge-rate-limit.sql", "017-agent-release-contract.sql", "018-agent-update-source-binding.sql", "019-controller-restore-quarantine.sql", "020-agent-update-recovery-intents.sql", "021-agent-update-recovery-successors.sql", "022-agent-update-confirmations.sql", "023-pc-control-commands.sql", "024-machine-profile-sunshine.sql", "025-lease-ended-at.sql" }) {
                if(name=="003-pairing.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=3)continue;}
                if(name=="004-boot-challenges.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=4)continue;}
                if(name=="010-completed-leases.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=10)continue;}
                if(name=="011-readiness-diagnostics.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=11)continue;}
                if(name=="012-agent-releases.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=12)continue;}
                if(name=="013-agent-update-downloads.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=13)continue;}
                if(name=="014-agent-maintenance-chains.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=14)continue;}
                if(name=="015-agent-sync-proof.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=15)continue;}
                if(name=="016-agent-sync-challenge-rate-limit.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=16)continue;}
                if(name=="017-agent-release-contract.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=17)continue;}
                if(name=="018-agent-update-source-binding.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=18)continue;}
                if(name=="019-controller-restore-quarantine.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=19)continue;}
                if(name=="020-agent-update-recovery-intents.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=20)continue;}
                if(name=="021-agent-update-recovery-successors.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=21)continue;}
                if(name=="022-agent-update-confirmations.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=22)continue;}
                if(name=="023-pc-control-commands.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=23)continue;}
                if(name=="024-machine-profile-sunshine.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=24)continue;}
                if(name=="025-lease-ended-at.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=25)continue;}
                if(name=="008-vnc-approved-target.sql") {
                    using var columns=connection.CreateCommand();columns.Transaction=transaction;columns.CommandText="PRAGMA table_info(machines)";using var rows=columns.ExecuteReader();
                    var hasVncLocalIp=false;while(rows.Read())if(rows.GetString(1)=="vnc_local_ip")hasVncLocalIp=true;
                    if(hasVncLocalIp)continue;
                }
                if(name=="009-agent-version.sql") {
                    using var columns=connection.CreateCommand();columns.Transaction=transaction;columns.CommandText="PRAGMA table_info(machine_heartbeats)";using var rows=columns.ExecuteReader();
                    var hasAgentVersion=false;while(rows.Read())if(rows.GetString(1)=="agent_version")hasAgentVersion=true;
                    if(hasAgentVersion)continue;
                }
                using var resource=typeof(ControllerDatabase).Assembly.GetManifestResourceStream("KamenRaider.Controller.Data.Migrations."+name)!;
                using var reader=new StreamReader(resource);
                using var cmd=connection.CreateCommand(); cmd.Transaction=transaction; cmd.CommandText=reader.ReadToEnd(); cmd.ExecuteNonQuery();
            }
            ControllerDatabaseMaintenance.ValidateCanonicalSchema(connection, 25, transaction);
            ControllerDatabaseMaintenance.ValidateMaintenanceChainSchema(connection,transaction);
            ControllerDatabaseMaintenance.ValidateSyncProofSchema(connection,transaction);
            ControllerDatabaseMaintenance.ValidateRestoreStateSchema(connection, transaction);
            ControllerDatabaseMaintenance.ValidateRecoverySchema(connection, transaction);
            if (databaseExistedBeforeOpen && startingUserVersion < 19)
                SetRestoreQuarantine(transaction, "legacy-upgrade-requires-independent-history", 0, this.clock.GetUtcNow().ToUnixTimeMilliseconds());
            if (invalidateRuntimeProofsOnOpen)
            {
                using var invalidateRuntimeProofs = connection.CreateCommand();
                invalidateRuntimeProofs.Transaction = transaction;
                invalidateRuntimeProofs.CommandText = "UPDATE agent_sync_proof_state SET token_sequence=NULL,token_digest=NULL,token_epoch=NULL,token_issued_tick=NULL,last_proven_runtime_epoch=NULL,last_proven_tick=NULL,challenge_window_tick=NULL,challenge_response_count=0";
                invalidateRuntimeProofs.ExecuteNonQuery();
            }
            transaction.Commit();
            ControllerDatabaseMaintenance.ValidateCanonicalSchema(connection, 25);
            ControllerDatabaseMaintenance.ValidateRestoreStateSchema(connection);
            ControllerDatabaseMaintenance.ValidateRecoverySchema(connection);
            ControllerDatabaseMaintenance.ValidateMachineProfileSchema(connection);
        } catch { connection.Dispose(); throw; }
    }
    private bool disposed;
    private void SetRestoreQuarantine(SqliteTransaction transaction, string reason, long restoreEpoch, long nowUnixMs)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE controller_restore_state SET quarantine_active=1,quarantine_reason=$reason,quarantine_epoch=MAX(quarantine_epoch,$epoch),quarantined_at_unix_ms=CASE WHEN quarantine_active=0 THEN $now ELSE quarantined_at_unix_ms END,updated_at_unix_ms=$now WHERE singleton=1";
        update.Parameters.AddWithValue("$reason", reason);
        update.Parameters.AddWithValue("$epoch", restoreEpoch);
        update.Parameters.AddWithValue("$now", nowUnixMs);
        if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("The Controller restore quarantine marker is missing or malformed.");
        if (!IsRestoreQuarantined(transaction))
            throw new InvalidDataException("Controller restore quarantine did not remain active after its update.");
    }

    internal void ActivateRestoreQuarantine(string reason, long restoreEpoch = 0)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 128) throw new ArgumentException("A restore quarantine reason is required.", nameof(reason));
        if (restoreEpoch < 0) throw new ArgumentOutOfRangeException(nameof(restoreEpoch));
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            SetRestoreQuarantine(transaction, reason, restoreEpoch, clock.GetUtcNow().ToUnixTimeMilliseconds());
            transaction.Commit();
            if (!IsRestoreQuarantined())
                throw new InvalidDataException("Controller restore quarantine was not durable after commit.");
        }
    }

    internal bool IsRestoreQuarantined(SqliteTransaction? transaction = null)
    {
        if (transaction is null)
        {
            lock (gate) return IsRestoreQuarantinedCore(null);
        }
        return IsRestoreQuarantinedCore(transaction);
    }

    private bool IsRestoreQuarantinedCore(SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT quarantine_active FROM controller_restore_state WHERE singleton=1";
        var active = command.ExecuteScalar();
        if (active is null or DBNull) throw new InvalidDataException("The Controller restore quarantine marker is missing or malformed.");
        return Convert.ToInt64(active, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    internal long GetPersistedRestoreEpoch(SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT database_epoch FROM controller_restore_state WHERE singleton=1";
        var value = command.ExecuteScalar();
        if (value is null or DBNull) throw new InvalidDataException("The Controller restore epoch binding is missing or malformed.");
        var epoch = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        if (epoch < 0) throw new InvalidDataException("The Controller restore epoch binding is invalid.");
        return epoch;
    }

    internal void ReconcileRestoreEpoch(long sidecarEpoch)
    {
        if (sidecarEpoch < 1) throw new InvalidDataException("The external Controller restore epoch is invalid.");
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            var databaseEpoch = GetPersistedRestoreEpoch(transaction);
            var reason = databaseEpoch == sidecarEpoch
                ? "existing-database-startup-requires-independent-history"
                : "external-restore-epoch-mismatch";
            // Epoch comparison detects rollback across completed admissions, but cannot prove
            // that the database was not replaced with a snapshot from the same running epoch.
            // Until complete independent history exists, every existing database starts held.
            SetRestoreQuarantine(transaction, reason, sidecarEpoch, clock.GetUtcNow().ToUnixTimeMilliseconds());
            transaction.Commit();
            if (!IsRestoreQuarantined())
                throw new InvalidDataException("The existing Controller database was not durably quarantined before admission.");
        }
    }

    internal void PersistRestoreEpoch(long epoch, bool requireUninitialized = false)
    {
        if (epoch < 1) throw new ArgumentOutOfRangeException(nameof(epoch));
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            if (requireUninitialized && (GetPersistedRestoreEpoch(transaction) != 0 || IsRestoreQuarantined(transaction)))
                throw new InvalidDataException("The new Controller database has unexpected restore history.");
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE controller_restore_state SET database_epoch=$epoch,updated_at_unix_ms=$now WHERE singleton=1";
            update.Parameters.AddWithValue("$epoch", epoch);
            update.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeMilliseconds());
            if (update.ExecuteNonQuery() != 1 || GetPersistedRestoreEpoch(transaction) != epoch)
                throw new InvalidDataException("The Controller restore epoch binding could not be persisted.");
            transaction.Commit();
            if (GetPersistedRestoreEpoch() != epoch)
                throw new InvalidDataException("The Controller restore epoch binding was not durable after commit.");
        }
    }

    internal void EnsureNotRestoreQuarantined(SqliteTransaction? transaction = null)
    {
        if (IsRestoreQuarantined(transaction))
            throw new InvalidOperationException("The Controller is globally quarantined after restore; independent complete recovery history is unavailable.");
    }

    public void Dispose(){lock(gate){if(disposed)return;disposed=true;foreach(var timer in pairingPinTimers.Values)timer.Dispose();pairingPinTimers.Clear();pairingPins.Clear();CryptographicOperations.ZeroMemory(syncProofHmacKey);connection.Dispose();}}
    private SqliteCommand Command(string sql, params (string,object?)[] values) { var cmd=connection.CreateCommand();cmd.CommandText=sql; foreach(var (name,value) in values) cmd.Parameters.AddWithValue(name,value??DBNull.Value); return cmd; }
    public string? PublicKey(string id) { lock(gate) {using var cmd=Command("SELECT public_key FROM machines WHERE machine_id=$id",("$id",id)); return cmd.ExecuteScalar() as string;} }
    public EnrollmentTicketResponse CreateEnrollmentTicket() { lock(gate) {
        var now=clock.GetUtcNow();var expires=now.AddHours(24);var nowMilliseconds=now.ToUnixTimeMilliseconds();
        var token=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
        var tokenHash=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token)));
        using var transaction=connection.BeginTransaction();
        using(var cleanup=Command("DELETE FROM agent_enrollment_tickets WHERE expires_at_unix_ms <= $now",("$now",nowMilliseconds))){cleanup.Transaction=transaction;cleanup.ExecuteNonQuery();}
        using(var insert=Command("INSERT INTO agent_enrollment_tickets(token_hash,expires_at_unix_ms) VALUES($hash,$expires)",("$hash",tokenHash),("$expires",expires.ToUnixTimeMilliseconds()))){insert.Transaction=transaction;insert.ExecuteNonQuery();}
        transaction.Commit();
        return new(token,expires);
    }}
    public EnrollmentResult Enroll(VerifiedAgentRequest verified,EnrollmentRequest request,string? enrollmentTicket=null) {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(request);
        lock(gate) {
            if (verified.Method != "POST" || verified.Path != "/agent/enroll" || verified.MachineId != request.MachineId
                || verified.KeyFingerprint.Length != 64 || !FixedHexEquals(PublicKeyFingerprint(request.PublicKey), verified.KeyFingerprint)
                || !IsWithinLegacyWindow(verified))
                throw new UnauthorizedAccessException("Signed enrollment request is invalid or outside the allowed window.");
            using var transaction = connection.BeginTransaction();
            if (!AcceptScopedNonce(transaction, verified)) throw new UnauthorizedAccessException("Signed enrollment request was replayed or invalidated.");
            var result = EnrollCore(transaction, request, enrollmentTicket);
            transaction.Commit();
            return result;
        }
    }
    private EnrollmentResult EnrollCore(SqliteTransaction transaction,EnrollmentRequest request,string? enrollmentTicket) {
        if(string.IsNullOrWhiteSpace(request.MachineId)||request.MachineId.Length>128) throw new ArgumentException("Invalid machine ID");
        using var key=ECDsa.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.PublicKey),out var bytes);
        if(bytes!=Convert.FromBase64String(request.PublicKey).Length) throw new ArgumentException("Invalid key");
        Ports(request.MoonlightPort,request.SunshinePort);
        ValidateNetworkMetadata(request.LocalIp, request.MacAddress);
        var hardware = NormalizeReportedHardware(request.Hardware);
        // Existing identities bypass admission budgets and never rewrite administrator data.
        string? existingApprovalStatus = null;
        using (var lookup = Command("SELECT public_key, approval_status FROM machines WHERE machine_id=$id", ("$id", request.MachineId)))
        {
            lookup.Transaction = transaction;
            using var row = lookup.ExecuteReader();
            if (row.Read()) {
                if (row.GetString(0) != request.PublicKey) throw new UnauthorizedAccessException("Identity conflict");
                existingApprovalStatus = row.GetString(1);
            }
        }
        if (existingApprovalStatus is not null) {
            // Re-enrollment keeps the administrator display name, price and approval; only the
            // Agent-reported hardware profile may be refreshed.
            if (hardware is not null) StoreReportedHardware(transaction, request.MachineId, hardware);
            return new(request.MachineId, existingApprovalStatus);
        }
        var ticketHash=GetEnrollmentTicketHash(enrollmentTicket);
        if(ticketHash is null)throw new UnauthorizedAccessException("A valid enrollment ticket is required");
        var nowMilliseconds=clock.GetUtcNow().ToUnixTimeMilliseconds();
        using(var consume=Command("DELETE FROM agent_enrollment_tickets WHERE token_hash=$hash AND expires_at_unix_ms > $now",("$hash",ticketHash),("$now",nowMilliseconds))){consume.Transaction=transaction;if(consume.ExecuteNonQuery()!=1)throw new UnauthorizedAccessException("Enrollment ticket is invalid, expired or already used");}
        // The immediate transaction serializes capacity checks and admission across connections.
        var cutoff = nowMilliseconds - (long)TimeSpan.FromMinutes(1).TotalMilliseconds;
        using (var cleanup = Command("DELETE FROM enrollment_admissions WHERE admitted_at <= $cutoff", ("$cutoff", cutoff))) {
            cleanup.Transaction = transaction; cleanup.ExecuteNonQuery();
        }
        using (var capacity = Command("SELECT (SELECT COUNT(*) FROM machines WHERE approval_status='pending'), (SELECT COUNT(*) FROM enrollment_admissions)")) {
            capacity.Transaction = transaction;
            using var counts = capacity.ExecuteReader(); counts.Read();
            if (counts.GetInt64(0) >= policy.MaxPending || counts.GetInt64(1) >= policy.NewPerMinute)
                throw new EnrollmentCapacityException();
        }
        using (var insert = Command("INSERT INTO machines(machine_id,public_key,local_ip,mac_address,moonlight_port,sunshine_port,hostname,cpu,gpu,ram_bytes,display_name) VALUES($id,$key,$ip,$mac,$moon,$sun,$hostname,$cpu,$gpu,$ram,$name); INSERT INTO enrollment_admissions(admitted_at) VALUES($at); INSERT INTO agent_sync_proof_state(machine_id,key_fingerprint) VALUES($id,$fingerprint)",
            ("$id",request.MachineId),("$key",request.PublicKey),("$ip",request.LocalIp),("$mac",request.MacAddress),("$moon",request.MoonlightPort),("$sun",request.SunshinePort),
            ("$hostname",hardware?.Hostname ?? ""),("$cpu",hardware?.Cpu ?? ""),("$gpu",hardware?.Gpu ?? ""),("$ram",hardware?.RamBytes ?? 0L),
            // A new machine's initial display name is the Agent-reported computer name only when one
            // was reported; afterwards the administrator owns display_name.
            ("$name",hardware?.Hostname ?? ""),
            ("$at",clock.GetUtcNow().ToUnixTimeMilliseconds()),("$fingerprint",PublicKeyFingerprint(request.PublicKey)))) {
            insert.Transaction = transaction; insert.ExecuteNonQuery();
        }
        return new(request.MachineId, "pending");
    }
    private static string? GetEnrollmentTicketHash(string? ticket) {
        if(ticket is null||!System.Text.RegularExpressions.Regex.IsMatch(ticket,@"\A[A-Za-z0-9_-]{43}\z",System.Text.RegularExpressions.RegexOptions.CultureInvariant))return null;
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(ticket)));
    }
    public AdminVncTicketResponse CreateAdminVncTicket(string machineId,string adminUserId,string allowedOrigin) {
        Identifier(machineId);Identifier(adminUserId);var origin=AdminVncOrigin(allowedOrigin);lock(gate) {
            var now=clock.GetUtcNow();var nowMs=now.ToUnixTimeMilliseconds();
            using var transaction=connection.BeginTransaction();
            using(var cleanup=Command("DELETE FROM admin_vnc_sessions WHERE (used_at_unix_ms IS NULL AND expires_at_unix_ms <= $now) OR (used_at_unix_ms IS NOT NULL AND ended_at_unix_ms IS NULL AND used_at_unix_ms <= $abandoned) OR ended_at_unix_ms <= $old",("$now",nowMs),("$abandoned",now.AddHours(-1).AddMinutes(-5).ToUnixTimeMilliseconds()),("$old",now.AddDays(-30).ToUnixTimeMilliseconds()))) {cleanup.Transaction=transaction;cleanup.ExecuteNonQuery();}
            string address;DateTimeOffset lastSeen;
            using(var machine=Command("SELECT m.vnc_local_ip,h.last_seen,m.approval_status FROM machines m JOIN machine_heartbeats h ON h.machine_id=m.machine_id WHERE m.machine_id=$id",("$id",machineId))) {
                machine.Transaction=transaction;using var row=machine.ExecuteReader();
                if(!row.Read()||row.IsDBNull(0)||row.GetString(2)!="approved")throw new UnauthorizedAccessException("Machine must be approved and have an administrator-confirmed VNC target");
                address=row.GetString(0);lastSeen=DateTimeOffset.Parse(row.GetString(1));
            }
            if(lastSeen>now||now-lastSeen>TimeSpan.FromSeconds(20)||!IsPrivateNetworkAddress(address))throw new UnauthorizedAccessException("Machine is not currently reachable on a private network");
            var token=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
            var hash=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token)));var sessionId=Guid.NewGuid().ToString("N");var expires=now.AddMinutes(1);
            using(var insert=Command("INSERT INTO admin_vnc_sessions(session_id,ticket_hash,machine_id,admin_user_id,allowed_origin,issued_at_unix_ms,expires_at_unix_ms) VALUES($session,$hash,$machine,$admin,$origin,$issued,$expires)",("$session",sessionId),("$hash",hash),("$machine",machineId),("$admin",adminUserId),("$origin",origin),("$issued",nowMs),("$expires",expires.ToUnixTimeMilliseconds()))) {insert.Transaction=transaction;insert.ExecuteNonQuery();}
            transaction.Commit();return new(token,expires);
        }
    }
    public IPAddress ValidateAdminVncTargetProbe(string machineId,string? targetAddress) {
        Identifier(machineId);var address=AdminVncTargetAddress.Parse(targetAddress);lock(gate) {
            var now=clock.GetUtcNow();
            using var machine=Command("SELECT h.last_seen FROM machines m JOIN machine_heartbeats h ON h.machine_id=m.machine_id WHERE m.machine_id=$id",("$id",machineId));
            using var row=machine.ExecuteReader();
            if(!row.Read())throw new UnauthorizedAccessException("Machine is not currently reachable");
            var lastSeen=DateTimeOffset.Parse(row.GetString(0));
            if(lastSeen>now||now-lastSeen>TimeSpan.FromSeconds(20))throw new UnauthorizedAccessException("Machine is not currently reachable");
            return address;
        }
    }
    public AdminVncTarget ConsumeAdminVncTicket(string ticket,string origin) {
        if(ticket is null||!System.Text.RegularExpressions.Regex.IsMatch(ticket,@"\A[A-Za-z0-9_-]{43}\z",System.Text.RegularExpressions.RegexOptions.CultureInvariant))throw new UnauthorizedAccessException("VNC session ticket is invalid");
        var hash=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(ticket)));var requestOrigin=AdminVncOrigin(origin);lock(gate) {
            var now=clock.GetUtcNow();var nowMs=now.ToUnixTimeMilliseconds();using var transaction=connection.BeginTransaction();
            string sessionId,machineId,adminUserId,allowedOrigin;
            using(var ticketRow=Command("SELECT session_id,machine_id,admin_user_id,allowed_origin FROM admin_vnc_sessions WHERE ticket_hash=$hash AND expires_at_unix_ms > $now AND used_at_unix_ms IS NULL",("$hash",hash),("$now",nowMs))) {
                ticketRow.Transaction=transaction;using var row=ticketRow.ExecuteReader();if(!row.Read())throw new UnauthorizedAccessException("VNC session ticket is expired or already used");
                sessionId=row.GetString(0);machineId=row.GetString(1);adminUserId=row.GetString(2);allowedOrigin=row.GetString(3);
            }
            if(!string.Equals(allowedOrigin,requestOrigin,StringComparison.Ordinal))throw new UnauthorizedAccessException("VNC session origin is invalid");
            string address;DateTimeOffset lastSeen;
            using(var machine=Command("SELECT m.vnc_local_ip,h.last_seen,m.approval_status FROM machines m JOIN machine_heartbeats h ON h.machine_id=m.machine_id WHERE m.machine_id=$id",("$id",machineId))) {
                machine.Transaction=transaction;using var row=machine.ExecuteReader();if(!row.Read()||row.IsDBNull(0)||row.GetString(2)!="approved")throw new UnauthorizedAccessException("Machine must be approved and have an administrator-confirmed VNC target");address=row.GetString(0);lastSeen=DateTimeOffset.Parse(row.GetString(1));
            }
            if(lastSeen>now||now-lastSeen>TimeSpan.FromSeconds(20)||!IsPrivateNetworkAddress(address))throw new UnauthorizedAccessException("Machine is not currently reachable on a private network");
            using(var consume=Command("UPDATE admin_vnc_sessions SET used_at_unix_ms=$now WHERE ticket_hash=$hash AND used_at_unix_ms IS NULL AND expires_at_unix_ms > $now",("$now",nowMs),("$hash",hash))) {consume.Transaction=transaction;if(consume.ExecuteNonQuery()!=1)throw new UnauthorizedAccessException("VNC session ticket is expired or already used");}
            transaction.Commit();return new(sessionId,machineId,adminUserId,System.Net.IPAddress.Parse(address),5900,allowedOrigin);
        }
    }
    public void FinishAdminVncSession(string sessionId) {if(string.IsNullOrWhiteSpace(sessionId)||sessionId.Length>64)return;lock(gate)using(var command=Command("UPDATE admin_vnc_sessions SET ended_at_unix_ms=$now WHERE session_id=$session AND used_at_unix_ms IS NOT NULL AND ended_at_unix_ms IS NULL",("$now",clock.GetUtcNow().ToUnixTimeMilliseconds()),("$session",sessionId)))command.ExecuteNonQuery();}
    private static string AdminVncOrigin(string value) {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var origin)||origin.UserInfo!=""||origin.Query!=""||origin.Fragment!=""||origin.AbsolutePath!="/")throw new ArgumentException("Invalid admin VNC origin");
        if(origin.Scheme=="http") {
            var host=origin.Host.Trim('[',']');
            if(!host.Equals("localhost",StringComparison.OrdinalIgnoreCase)&&(!System.Net.IPAddress.TryParse(host,out var address)||!System.Net.IPAddress.IsLoopback(address)))throw new ArgumentException("HTTP admin VNC origins must use loopback");
        } else if(origin.Scheme!="https")throw new ArgumentException("Invalid admin VNC origin");
        return origin.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
    private static string? VncTargetAddress(string? value) {
        return AdminVncTargetAddress.Normalize(value);
    }
    private static bool IsPrivateNetworkAddress(string value) => AdminVncTargetAddress.TryParse(value,out _);
    private static void ValidateNetworkMetadata(string? ip, string? mac) {
        if (string.IsNullOrWhiteSpace(ip) || ip.Length > 45 || !System.Net.IPAddress.TryParse(ip, out var address)
            || address.ToString().Contains('%') || ip != address.ToString())
            throw new ArgumentException("Invalid local IP address");
        // Agent reports six bytes without separators; common colon/dash inventory formats are accepted too.
        if (mac is null || mac.Length is not (12 or 17)
            || !System.Text.RegularExpressions.Regex.IsMatch(mac, @"\A(?:[0-9a-fA-F]{12}|(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}|(?:[0-9a-fA-F]{2}-){5}[0-9a-fA-F]{2})\z"))
            throw new ArgumentException("Invalid MAC address");
    }
    private static void Ports(int moon,int sun) {if(moon is <1 or >65535 || sun is <1 or >65535)throw new ArgumentException("Invalid port");}
    private static (string Readiness,bool CanRent) EvaluateRentability(string approvalStatus,long revision,DateTimeOffset now,DateTimeOffset? lastSeen,string? reportedReadiness,bool allocationHeld,bool maintenanceLocked,long? appliedRevision,bool currentProof,bool restoreQuarantined) {
        var readiness=lastSeen is null?"unknown":now-lastSeen.Value>TimeSpan.FromSeconds(20)?"unreachable":reportedReadiness??"unknown";
        var canRent=approvalStatus=="approved" && readiness=="ready" && !allocationHeld && !maintenanceLocked && appliedRevision==revision && currentProof && !restoreQuarantined;
        return (readiness,canRent);
    }
    public MachineDto[] List() => List(null);
    private MachineDto[] List(SqliteTransaction? existingTransaction) {lock(gate) {
        // Read heartbeat, configuration acknowledgment and allocation from the same SQLite snapshot.
        var now=clock.GetUtcNow();
        var restoreQuarantined = IsRestoreQuarantined(existingTransaction);
        if(!restoreQuarantined)
        {
            if(existingTransaction is null) {using var tx=connection.BeginTransaction();ExpireUnstartedAgentUpdates(tx,now.ToUnixTimeMilliseconds());tx.Commit();}
            else ExpireUnstartedAgentUpdates(existingTransaction,now.ToUnixTimeMilliseconds());
        }
        using var cmd=Command("SELECT m.machine_id,m.approval_status,m.display_name,m.hourly_price,m.local_ip,m.mac_address,m.moonlight_port,m.sunshine_port,m.revision,m.approved_by,m.approved_at,h.last_seen,h.boot_id,h.readiness,EXISTS(SELECT 1 FROM leases l WHERE l.machine_id=m.machine_id AND l.allocation_held=1),a.applied_revision,m.vnc_local_ip,p.last_proven_version,(SELECT COUNT(*) FROM admin_vnc_sessions v WHERE v.machine_id=m.machine_id AND v.used_at_unix_ms IS NOT NULL AND v.ended_at_unix_ms IS NULL AND v.used_at_unix_ms > $activeVncSince),d.diagnostics_json,COALESCE(c.held,0),o.operation_id,o.target_version,o.status,p.last_proven_runtime_epoch,p.last_proven_tick,m.hostname,m.cpu,m.gpu,m.ram_bytes,m.sunshine_credential_revision FROM machines m LEFT JOIN machine_heartbeats h ON h.machine_id=m.machine_id LEFT JOIN machine_config_acks a ON a.machine_id=m.machine_id LEFT JOIN machine_readiness_diagnostics d ON d.machine_id=m.machine_id LEFT JOIN agent_sync_proof_state p ON p.machine_id=m.machine_id LEFT JOIN agent_maintenance_chains c ON c.chain_id=(SELECT c1.chain_id FROM agent_maintenance_chains c1 WHERE c1.machine_id=m.machine_id ORDER BY c1.held DESC,c1.rowid DESC LIMIT 1) LEFT JOIN agent_update_operations o ON o.operation_id=c.current_head_operation_id ORDER BY m.machine_id",("$activeVncSince",now.AddHours(-1).ToUnixTimeMilliseconds()));
        cmd.Transaction=existingTransaction;
        using var r=cmd.ExecuteReader(); var rows=new List<MachineDto>();
        while(r.Read()) {
            DateTimeOffset? seen=r.IsDBNull(11)?null:DateTimeOffset.Parse(r.GetString(11));
            long? appliedRevision=r.IsDBNull(15)?null:r.GetInt64(15);
            var currentProof=IsRecentProvenHeartbeat(r.IsDBNull(24)?null:r.GetString(24),r.IsDBNull(25)?null:r.GetInt64(25));
            var rentability=EvaluateRentability(r.GetString(1),r.GetInt64(8),now,seen,r.IsDBNull(13)?null:r.GetString(13),r.GetInt64(14)!=0,r.GetInt64(20)!=0,appliedRevision,currentProof,restoreQuarantined);
            rows.Add(new MachineDto(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetString(4),r.GetString(5),r.GetInt32(6),r.GetInt32(7),r.GetInt64(8),seen,r.IsDBNull(12)?null:r.GetString(12),rentability.Readiness,r.IsDBNull(17)?null:r.GetString(17),r.IsDBNull(9)?null:r.GetString(9),r.IsDBNull(10)?null:DateTimeOffset.Parse(r.GetString(10))) {AppliedConfigRevision=appliedRevision,VncLocalIp=r.IsDBNull(16)?null:r.GetString(16),ActiveAdminVncSessions=r.GetInt32(18),CanRent=rentability.CanRent,AllocationHeld=r.GetInt64(14)!=0,ReadinessDiagnostics=r.IsDBNull(19)?null:JsonSerializer.Deserialize<ReadinessDiagnostic[]>(r.GetString(19)),AgentUpdateOperationId=r.IsDBNull(21)?null:r.GetString(21),AgentUpdateTargetVersion=r.IsDBNull(22)?null:r.GetString(22),AgentUpdateStatus=r.IsDBNull(23)?null:r.GetString(23),Hostname=OptionalReportedProfileText(r,26),Cpu=OptionalReportedProfileText(r,27),Gpu=OptionalReportedProfileText(r,28),RamBytes=r.GetInt64(29),AppliedSunshineCredentialRevision=r.GetInt64(30)});
        }
        r.Close();
        for(var i=0;i<rows.Count;i++){var row=rows[i];rows[i]=row with {PublicKeyFingerprint=Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(PublicKey(row.MachineId)!)))};}
        return rows.ToArray();
    }}
    public MachineDto Update(string id, MachineUpdate update,long expectedRevision) {lock(gate) {
        Ports(update.MoonlightPort,update.SunshinePort); if(string.IsNullOrWhiteSpace(update.DisplayName)||update.HourlyPriceMinorUnits<=0||update.HourlyPriceMinorUnits%100!=0)throw new ArgumentException("Name and a positive whole-baht price required");
        var vncLocalIp=VncTargetAddress(update.VncLocalIp);
        using var cmd=Command("UPDATE machines SET display_name=$name,hourly_price=$price,moonlight_port=$moon,sunshine_port=$sun,vnc_local_ip=$vnc,revision=revision+1 WHERE machine_id=$id AND revision=$revision",("$name",update.DisplayName.Trim()),("$price",update.HourlyPriceMinorUnits),("$moon",update.MoonlightPort),("$sun",update.SunshinePort),("$vnc",vncLocalIp),("$id",id),("$revision",expectedRevision));
        if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Revision conflict or missing machine"); return List().Single(x=>x.MachineId==id);
    }}
    public MachineDto Approve(string id,string actor) {lock(gate) {
        if(string.IsNullOrWhiteSpace(actor))throw new ArgumentException("Authenticated actor required");
        var machine=List().SingleOrDefault(x=>x.MachineId==id)??throw new KeyNotFoundException();
        if(string.IsNullOrWhiteSpace(machine.DisplayName)||machine.HourlyPriceMinorUnits<=0)throw new InvalidOperationException("Name and positive price required before approval");
        using var tx=connection.BeginTransaction(); var at=DateTimeOffset.UtcNow.ToString("O");
        using var cmd=Command("UPDATE machines SET approval_status='approved',approved_by=$actor,approved_at=$at,revision=revision+1 WHERE machine_id=$id; INSERT INTO machine_approval_audit(machine_id,actor_id,approved_at) VALUES($id,$actor,$at)",("$actor",actor),("$at",at),("$id",id)); cmd.Transaction=tx;cmd.ExecuteNonQuery();tx.Commit();return List().Single(x=>x.MachineId==id);
    }}
}
