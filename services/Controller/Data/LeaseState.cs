using KamenRaider.Contracts;
using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace KamenRaider.Controller.Data;
public sealed partial class ControllerDatabase {
    private const string LeaseColumns="id,user_id,machine_id,state,revision,starts_at,expires_at,pending_revision,duration_seconds,preparation_deadline,pending_expires_at,boot_id,ended_at,wake_requested_at_unix_ms";
    private const int CleanupGraceSeconds=30;
    private const int BootPreparationDeadlineSeconds=300;
    private record LeaseRow(LeaseDto Dto,int Duration,long PreparationDeadline,long? PendingExpiry,string? Boot,DateTimeOffset? EndedAt,DateTimeOffset? WakeRequested);
    private static DateTimeOffset? Instant(SqliteDataReader r,int i) => r.IsDBNull(i)?null:DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(i));
    private static LeaseRow ReadLease(SqliteDataReader r) {
        var wake=Instant(r,13);
        var dto=new LeaseDto(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt64(4),Instant(r,5),Instant(r,6),r.IsDBNull(7)?null:r.GetInt64(7),Instant(r,12)) {
            WakeRequestedAtUtc=wake,
            CanRequestPower=r.GetString(3)=="preparing"&&r.IsDBNull(11)&&wake is null
        };
        return new LeaseRow(dto,r.GetInt32(8),r.GetInt64(9),r.IsDBNull(10)?null:r.GetInt64(10),r.IsDBNull(11)?null:r.GetString(11),Instant(r,12),wake);
    }
    private SqliteCommand LeaseCommand(SqliteTransaction tx,string sql,params (string,object?)[] values) {
        var cmd=Command(sql,values); cmd.Transaction=tx; return cmd;
    }
    private void ExecuteLease(SqliteTransaction tx,string sql,params (string,object?)[] values) {
        using var cmd=LeaseCommand(tx,sql,values); cmd.ExecuteNonQuery();
    }
    private LeaseRow? FindLease(SqliteTransaction tx,string id) {
        using var cmd=LeaseCommand(tx,$"SELECT {LeaseColumns} FROM leases WHERE id=$id",("$id",id));
        using var r=cmd.ExecuteReader(); return r.Read()?ReadLease(r):null;
    }
    private static void Identifier(string value) {
        if(string.IsNullOrWhiteSpace(value)||value.Length>128) throw new ArgumentException("Invalid identifier");
    }
    private static void Duration(int seconds) {
        if(seconds is <1 or >86400) throw new ArgumentException("Duration must be 1 to 86400 seconds");
    }
    private string? PriorRequest(SqliteTransaction tx,string scope,string key,string fingerprint) {
        using var cmd=LeaseCommand(tx,"SELECT rental_id,fingerprint FROM lease_requests WHERE scope=$scope AND request_key=$key",("$scope",scope),("$key",key));
        using var r=cmd.ExecuteReader(); if(!r.Read()) return null;
        if(r.GetString(1)!=fingerprint) throw new InvalidOperationException("Idempotency conflict");
        return r.GetString(0);
    }
    private void SaveRequest(SqliteTransaction tx,string scope,string key,string fingerprint,string rentalId) =>
        ExecuteLease(tx,"INSERT INTO lease_requests(scope,request_key,fingerprint,rental_id) VALUES($scope,$key,$fp,$id)",("$scope",scope),("$key",key),("$fp",fingerprint),("$id",rentalId));

    // These deadlines are checked under the same immediate transaction as every state transition.
    private void ExpireLeases(SqliteTransaction tx,long now) {
        // The natural end of a rented window is its expiry instant; a preparation that never
        // activated (including one where the renter never pressed Power) ends at its deadline.
        ExecuteLease(tx,"UPDATE leases SET state=CASE WHEN state='preparing' THEN 'failed' ELSE 'ending' END,ended_at=COALESCE(ended_at,CASE WHEN state='preparing' THEN $now ELSE COALESCE(expires_at,$now) END),pending_revision=NULL,pending_expires_at=NULL,allocation_held=0 WHERE (state='preparing' AND preparation_deadline <= $now) OR (state IN ('preparing','active') AND expires_at <= $now)",("$now",now));
        ExecuteLease(tx,"UPDATE command_outbox SET status='expired' WHERE status='pending' AND (valid_until <= $now OR (type NOT IN ('EndLease','ShutdownMachine') AND rental_id IN (SELECT id FROM leases WHERE state IN ('failed','ending'))))",("$now",now));
        // A Parsec login may not outlive its lease. Expired/ending/completed rentals close any
        // queued attempt and drop the sealed payload that is still waiting in the outbox.
        ExecuteLease(tx,"UPDATE parsec_attempts SET state='failed',error_code=$code,message=NULL,updated_at_unix_ms=$now WHERE state IN ('queued','starting','waiting_email') AND rental_id IN (SELECT id FROM leases WHERE state IN ('failed','ending','completed'))",("$code",ParsecErrorCodes.LeaseEnded),("$now",now));
        ExecuteLease(tx,"UPDATE command_outbox SET status='invalidated',payload='{}' WHERE type='ParsecConnect' AND status='pending' AND rental_id IN (SELECT id FROM leases WHERE state IN ('failed','ending','completed'))");
        RefreshClearingWindows(tx);
    }

    private void RefreshClearingWindows(SqliteTransaction tx) {
        using var pending = LeaseCommand(tx,"SELECT machine_id,MAX(ended_at) FROM leases WHERE state IN ('failed','ending','completed') AND ended_at IS NOT NULL GROUP BY machine_id");
        var windows = new List<(string Machine,long Until)>();
        using (var row = pending.ExecuteReader())
            while (row.Read()) windows.Add((row.GetString(0),checked(row.GetInt64(1)+CleanupGraceSeconds*1000L)));
        foreach (var (machine,until) in windows)
            ExecuteLease(tx,"UPDATE machines SET clearing_until_unix_ms=CASE WHEN clearing_until_unix_ms IS NULL OR clearing_until_unix_ms<$until THEN $until ELSE clearing_until_unix_ms END WHERE machine_id=$id",("$until",until),("$id",machine));
    }

    private void ClearClearingWindow(SqliteTransaction tx,string machineId) =>
        ExecuteLease(tx,"UPDATE machines SET clearing_until_unix_ms=NULL WHERE machine_id=$id",("$id",machineId));

    public LeaseDto CreateLease(string userId,string machineId,int durationSeconds,string idempotencyKey) {
        Identifier(userId); Identifier(machineId); Identifier(idempotencyKey); Duration(durationSeconds);
        lock(gate) {
            using var tx=connection.BeginTransaction(); EnsureNotRestoreQuarantined(tx); var nowUtc=clock.GetUtcNow();var now=nowUtc.ToUnixTimeMilliseconds(); ExpireLeases(tx,now);ExpireUnstartedAgentUpdates(tx,now);
            var scope="create:"+userId; var fp=JsonSerializer.Serialize(new {machineId,durationSeconds});
            var prior=PriorRequest(tx,scope,idempotencyKey,fp);
            if(prior is not null) {var existing=FindLease(tx,prior)!.Dto;tx.Commit();return existing;}
            using(var eligibility=LeaseCommand(tx,"SELECT m.approval_status,m.availability,m.clearing_until_unix_ms,h.last_seen,m.local_ip,m.mac_address,EXISTS(SELECT 1 FROM leases l WHERE l.machine_id=m.machine_id AND l.allocation_held=1),EXISTS(SELECT 1 FROM agent_maintenance_chains c WHERE c.machine_id=m.machine_id AND c.held=1) FROM machines m LEFT JOIN machine_heartbeats h ON h.machine_id=m.machine_id WHERE m.machine_id=$id",("$id",machineId))) {
                using var row=eligibility.ExecuteReader();
                if(!row.Read())throw new InvalidOperationException("Machine is not currently rentable");
                if(row.GetString(1)!="rentable"||row.GetInt64(6)!=0||row.GetInt64(7)!=0)
                    throw new InvalidOperationException("Machine is not currently rentable");
                if(!row.IsDBNull(2)&&row.GetInt64(2)>now)
                    throw new InvalidOperationException("Machine is still clearing");
                if(row.IsDBNull(3)||nowUtc-DateTimeOffset.Parse(row.GetString(3))<=TimeSpan.FromSeconds(20))
                    throw new InvalidOperationException("Machine must be powered off before it can be rented");
                ValidateNetworkMetadata(row.IsDBNull(4)?null:row.GetString(4),row.IsDBNull(5)?null:row.GetString(5));
            }
            var id=Guid.NewGuid().ToString("N");
            ExecuteLease(tx,"INSERT INTO leases(id,user_id,machine_id,state,duration_seconds,preparation_deadline) VALUES($id,$user,$machine,'preparing',$duration,$deadline)",("$id",id),("$user",userId),("$machine",machineId),("$duration",durationSeconds),("$deadline",now+BootPreparationDeadlineSeconds*1000L));
            ClearClearingWindow(tx,machineId);
            SaveRequest(tx,scope,idempotencyKey,fp,id);
            var result=FindLease(tx,id)!.Dto;tx.Commit();return result;
        }
    }
    public sealed record LeasePowerTarget(string RentalId,string MachineId,string MacAddress,bool MachineOnline,LeaseDto Lease);

    /// <summary>Validates that this renter may still request Wake-on-LAN for a prepared lease.</summary>
    public LeasePowerTarget? GetLeasePowerTarget(string rentalId,string userId) {
        Identifier(rentalId);Identifier(userId);
        lock(gate) {
            using var tx=connection.BeginTransaction();var now=clock.GetUtcNow();ExpireLeases(tx,now.ToUnixTimeMilliseconds());
            var lease=FindLease(tx,rentalId);
            if(lease is null||lease.Dto.UserId!=userId){tx.Commit();return null;}
            if(lease.Dto.State!="preparing"||lease.Boot is not null){tx.Commit();return lease.Dto.CanRequestPower||lease.Dto.WakeRequestedAtUtc is not null?new(rentalId,lease.Dto.MachineId,"",false,lease.Dto):null;}
            if(lease.PreparationDeadline<=now.ToUnixTimeMilliseconds()){tx.Commit();return null;}
            using var machine=LeaseCommand(tx,"SELECT m.availability,m.clearing_until_unix_ms,m.mac_address,h.last_seen FROM machines m LEFT JOIN machine_heartbeats h ON h.machine_id=m.machine_id WHERE m.machine_id=$id",("$id",lease.Dto.MachineId));
            using var row=machine.ExecuteReader();
            if(!row.Read()||row.GetString(0)!="rentable"){tx.Commit();return null;}
            if(!row.IsDBNull(1)&&row.GetInt64(1)>now.ToUnixTimeMilliseconds()){tx.Commit();return null;}
            var mac=row.IsDBNull(2)?"":row.GetString(2);
            var online=!row.IsDBNull(3)&&now-DateTimeOffset.Parse(row.GetString(3))<=TimeSpan.FromSeconds(20);
            tx.Commit();
            return new(rentalId,lease.Dto.MachineId,mac,online,lease.Dto);
        }
    }

    public LeaseDto MarkLeasePowerRequested(string rentalId,string userId,string requestId) {
        Identifier(rentalId);Identifier(userId);Identifier(requestId);
        lock(gate) {
            using var tx=connection.BeginTransaction();EnsureNotRestoreQuarantined(tx);var now=clock.GetUtcNow();ExpireLeases(tx,now.ToUnixTimeMilliseconds());
            var lease=FindLease(tx,rentalId)??throw new KeyNotFoundException();
            if(lease.Dto.UserId!=userId)throw new UnauthorizedAccessException("Lease does not belong to this renter");
            if(lease.Dto.State!="preparing"||lease.Boot is not null)throw new InvalidOperationException("Lease is not waiting for power");
            if(lease.PreparationDeadline<=now.ToUnixTimeMilliseconds())throw new InvalidOperationException("Power request deadline has passed");
            if(lease.WakeRequested is null)
                ExecuteLease(tx,"UPDATE leases SET wake_requested_at_unix_ms=$at,wake_request_id=$request WHERE id=$id AND wake_requested_at_unix_ms IS NULL",("$at",now.ToUnixTimeMilliseconds()),("$request",requestId),("$id",rentalId));
            var result=FindLease(tx,rentalId)!.Dto;tx.Commit();return result;
        }
    }

    public LeaseDto CancelUserLease(string rentalId,string userId,long expectedRevision,string idempotencyKey) {
        Identifier(rentalId);Identifier(userId);Identifier(idempotencyKey);
        lock(gate) {
            using var tx=connection.BeginTransaction();EnsureNotRestoreQuarantined(tx);var now=clock.GetUtcNow();var nowMs=now.ToUnixTimeMilliseconds();ExpireLeases(tx,nowMs);
            var lease=FindLease(tx,rentalId)??throw new KeyNotFoundException();
            if(lease.Dto.UserId!=userId)throw new UnauthorizedAccessException("Lease does not belong to this renter");
            var scope="cancel-user:"+rentalId;var fp=Digest(Convert.ToString(expectedRevision,System.Globalization.CultureInfo.InvariantCulture));
            var prior=PriorRequest(tx,scope,idempotencyKey,fp);
            if(prior is not null){var existing=FindLease(tx,prior)!.Dto;tx.Commit();return existing;}
            if(lease.Dto.Revision!=expectedRevision||lease.Dto.PendingRevision is not null)
                throw new InvalidOperationException("Lease state or revision conflict");
            if(lease.Dto.State is not ("preparing" or "active"))
                throw new InvalidOperationException("Lease can no longer be cancelled");
            var terminal=lease.Boot is null?"failed":"ending";
            ExecuteLease(tx,"UPDATE leases SET state=$state,ended_at=COALESCE(ended_at,$now),pending_revision=NULL,pending_expires_at=NULL,allocation_held=0 WHERE id=$id",("$state",terminal),("$now",nowMs),("$id",rentalId));
            if(lease.Boot is not null && terminal=="ending")
                AddCommand(tx,lease with {Boot=lease.Boot},"EndLease",lease.Dto.Revision,lease.Boot,Math.Min(nowMs+PcControlCommandValidityMilliseconds,lease.Dto.ExpiresAtUtc?.ToUnixTimeMilliseconds()??nowMs+PcControlCommandValidityMilliseconds),new {});
            ExecuteLease(tx,"UPDATE command_outbox SET status='invalidated',payload='{}' WHERE rental_id=$rental AND status='pending' AND type<>'EndLease'",("$rental",rentalId));
            RefreshClearingWindows(tx);
            SaveRequest(tx,scope,idempotencyKey,fp,rentalId);
            var result=FindLease(tx,rentalId)!.Dto;tx.Commit();return result;
        }
    }

    public LeaseDto ExtendLease(string rentalId,int additionalSeconds,long expectedRevision,string idempotencyKey) {
        Identifier(rentalId); Identifier(idempotencyKey); Duration(additionalSeconds);
        lock(gate) {
            using var tx=connection.BeginTransaction(); EnsureNotRestoreQuarantined(tx); var now=clock.GetUtcNow().ToUnixTimeMilliseconds(); ExpireLeases(tx,now);
            var scope="extend:"+rentalId;var fp=JsonSerializer.Serialize(new {additionalSeconds,expectedRevision});
            var prior=PriorRequest(tx,scope,idempotencyKey,fp);
            if(prior is not null) {var existing=FindLease(tx,prior)!.Dto;tx.Commit();return existing;}
            var lease=FindLease(tx,rentalId)??throw new KeyNotFoundException();
            if(lease.Dto.State!="active"||lease.Dto.Revision!=expectedRevision||lease.Dto.PendingRevision is not null) throw new InvalidOperationException("Lease state or revision conflict");
            var revision=checked(expectedRevision+1);var expiry=lease.Dto.ExpiresAtUtc!.Value.AddSeconds(additionalSeconds).ToUnixTimeMilliseconds();
            ExecuteLease(tx,"UPDATE leases SET pending_revision=$revision,pending_expires_at=$expiry WHERE id=$id",("$revision",revision),("$expiry",expiry),("$id",rentalId));
            AddCommand(tx,lease,"ApplyLease",revision,lease.Boot!,lease.Dto.ExpiresAtUtc.Value.ToUnixTimeMilliseconds(),new { startsAtUtc=lease.Dto.StartsAtUtc, expiresAtUtc=DateTimeOffset.FromUnixTimeMilliseconds(expiry) });
            SaveRequest(tx,scope,idempotencyKey,fp,rentalId);
            var result=FindLease(tx,rentalId)!.Dto;tx.Commit();return result;
        }
    }
    public LeaseDto? GetLease(string rentalId) {
        lock(gate) {using var tx=connection.BeginTransaction();var restoreQuarantined=IsRestoreQuarantined(tx);if(!restoreQuarantined)ExpireLeases(tx,clock.GetUtcNow().ToUnixTimeMilliseconds());var result=restoreQuarantined?null:FindLease(tx,rentalId)?.Dto;tx.Commit();return result;}
    }
    public LeaseDto[] GetUserLeases(string userId) {
        Identifier(userId);
        lock(gate) {
            using var tx=connection.BeginTransaction();if(IsRestoreQuarantined(tx)){tx.Commit();return [];}ExpireLeases(tx,clock.GetUtcNow().ToUnixTimeMilliseconds());
            using var cmd=LeaseCommand(tx,$"SELECT {LeaseColumns} FROM leases WHERE user_id=$id ORDER BY rowid",("$id",userId));
            var leases=new List<LeaseDto>();using(var r=cmd.ExecuteReader())while(r.Read()) leases.Add(ReadLease(r).Dto);
            tx.Commit();return leases.ToArray();
        }
    }
    // Administrative history view: newest rows first so the most recent rentals lead the page.
    public LeaseDto[] GetAllLeases(int take) {
        if(take is <1 or >500) throw new ArgumentException("Take must be between 1 and 500",nameof(take));
        lock(gate) {
            using var tx=connection.BeginTransaction();if(IsRestoreQuarantined(tx)){tx.Commit();return [];}
            ExpireLeases(tx,clock.GetUtcNow().ToUnixTimeMilliseconds());
            using var cmd=LeaseCommand(tx,$"SELECT {LeaseColumns} FROM leases ORDER BY rowid DESC LIMIT $take",("$take",take));
            var leases=new List<LeaseDto>();using(var r=cmd.ExecuteReader())while(r.Read()) leases.Add(ReadLease(r).Dto);
            tx.Commit();return leases.ToArray();
        }
    }
}
