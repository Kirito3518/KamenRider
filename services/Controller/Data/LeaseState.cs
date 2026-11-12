using KamenRaider.Contracts;
using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace KamenRaider.Controller.Data;
public sealed partial class ControllerDatabase {
    private const string LeaseColumns="id,user_id,machine_id,state,revision,starts_at,expires_at,pending_revision,duration_seconds,preparation_deadline,pending_expires_at,boot_id";
    private record LeaseRow(LeaseDto Dto,int Duration,long PreparationDeadline,long? PendingExpiry,string? Boot);
    private static DateTimeOffset? Instant(SqliteDataReader r,int i) => r.IsDBNull(i)?null:DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(i));
    private static LeaseRow ReadLease(SqliteDataReader r) => new(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt64(4),Instant(r,5),Instant(r,6),r.IsDBNull(7)?null:r.GetInt64(7)),r.GetInt32(8),r.GetInt64(9),r.IsDBNull(10)?null:r.GetInt64(10),r.IsDBNull(11)?null:r.GetString(11));
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
        ExecuteLease(tx,"UPDATE leases SET state=CASE WHEN state='preparing' THEN 'failed' ELSE 'ending' END,pending_revision=NULL,pending_expires_at=NULL WHERE (state='preparing' AND preparation_deadline <= $now) OR (state IN ('preparing','active') AND expires_at <= $now)",("$now",now));
        ExecuteLease(tx,"UPDATE command_outbox SET status='expired' WHERE status='pending' AND (valid_until <= $now OR rental_id IN (SELECT id FROM leases WHERE state IN ('failed','ending')))",("$now",now));
    }
    public LeaseDto CreateTrialLease(string userId,string machineId,int durationSeconds,string idempotencyKey) {
        Identifier(userId); Identifier(machineId); Identifier(idempotencyKey); Duration(durationSeconds);
        lock(gate) {
            using var tx=connection.BeginTransaction(); EnsureNotRestoreQuarantined(tx); var nowUtc=clock.GetUtcNow();var now=nowUtc.ToUnixTimeMilliseconds(); ExpireLeases(tx,now);ExpireUnstartedAgentUpdates(tx,now);
            var scope="create:"+userId; var fp=JsonSerializer.Serialize(new {machineId,durationSeconds});
            var prior=PriorRequest(tx,scope,idempotencyKey,fp);
            if(prior is not null) {var existing=FindLease(tx,prior)!.Dto;tx.Commit();return existing;}
            string? currentBoot;
            using(var eligibility=LeaseCommand(tx,"SELECT m.approval_status,m.revision,h.last_seen,h.readiness,EXISTS(SELECT 1 FROM leases l WHERE l.machine_id=m.machine_id AND l.allocation_held=1),a.applied_revision,EXISTS(SELECT 1 FROM agent_maintenance_chains c WHERE c.machine_id=m.machine_id AND c.held=1),h.boot_id,p.last_proven_runtime_epoch,p.last_proven_tick FROM machines m LEFT JOIN machine_heartbeats h ON h.machine_id=m.machine_id LEFT JOIN machine_config_acks a ON a.machine_id=m.machine_id LEFT JOIN agent_sync_proof_state p ON p.machine_id=m.machine_id WHERE m.machine_id=$id",("$id",machineId))) {
                using var row=eligibility.ExecuteReader();
                if(!row.Read())throw new InvalidOperationException("Machine is not currently rentable");
                currentBoot=row.IsDBNull(7)?null:row.GetString(7);
                var currentProof=IsRecentProvenHeartbeat(row.IsDBNull(8)?null:row.GetString(8),row.IsDBNull(9)?null:row.GetInt64(9));
                var rentability=EvaluateRentability(row.GetString(0),row.GetInt64(1),nowUtc,row.IsDBNull(2)?null:DateTimeOffset.Parse(row.GetString(2)),row.IsDBNull(3)?null:row.GetString(3),row.GetInt64(4)!=0,row.GetInt64(6)!=0,row.IsDBNull(5)?null:row.GetInt64(5),currentProof,IsRestoreQuarantined(tx));
                if(!rentability.CanRent)throw new InvalidOperationException("Machine is not currently rentable");
            }
            var id=Guid.NewGuid().ToString("N");
            ExecuteLease(tx,"INSERT INTO leases(id,user_id,machine_id,state,pending_revision,duration_seconds,preparation_deadline) VALUES($id,$user,$machine,'preparing',1,$duration,$deadline)",("$id",id),("$user",userId),("$machine",machineId),("$duration",durationSeconds),("$deadline",now+300000));
            SaveRequest(tx,scope,idempotencyKey,fp,id);
            if(currentBoot is not null) PrepareCommand(tx,FindLease(tx,id)!,currentBoot);
            var result=FindLease(tx,id)!.Dto;tx.Commit();return result;
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
}
