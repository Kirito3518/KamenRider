using KamenRaider.Contracts;
using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace KamenRaider.Controller.Data;
public sealed partial class ControllerDatabase {
    private string AddCommand(SqliteTransaction tx,LeaseRow lease,string type,long revision,string boot,long validUntil,object payload) {
        var commandId=Guid.NewGuid().ToString("N");
        ExecuteLease(tx,"INSERT INTO command_outbox(command_id,type,rental_id,machine_id,lease_revision,boot_id,valid_until,payload) VALUES($id,$type,$rental,$machine,$revision,$boot,$until,$payload)",
            ("$id",commandId),("$type",type),("$rental",lease.Dto.Id),("$machine",lease.Dto.MachineId),("$revision",revision),("$boot",boot),("$until",validUntil),("$payload",JsonSerializer.Serialize(payload)));
        return commandId;
    }
    private void PrepareCommand(SqliteTransaction tx,LeaseRow lease,string boot) {
        ExecuteLease(tx,"UPDATE leases SET boot_id=$boot WHERE id=$id",("$boot",boot),("$id",lease.Dto.Id));
        AddCommand(tx,lease,"PrepareMachine",1,boot,lease.PreparationDeadline,new {});
    }
    private CommandEnvelope[] PendingCommands(SqliteTransaction tx,string machineId,string boot) {
        if (IsRestoreQuarantined(tx)) return [];
        using var cmd=LeaseCommand(tx,"SELECT command_id,type,rental_id,lease_revision,boot_id,valid_until,payload FROM command_outbox WHERE machine_id=$id AND boot_id=$boot AND status='pending' ORDER BY rowid",("$id",machineId),("$boot",boot));
        using var r=cmd.ExecuteReader();var commands=new List<CommandEnvelope>();
        while(r.Read()) commands.Add(new(r.GetString(0),r.GetString(1),r.IsDBNull(2)?"":r.GetString(2),r.GetInt64(3),r.GetString(4),DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)),JsonSerializer.Deserialize<JsonElement>(r.GetString(6))));
                return commands.Select(c=>c.Type=="PairPin"&&pairingPins.TryGetValue(c.CommandId,out var secret)?c with{Payload=JsonSerializer.SerializeToElement(new{pairingId=c.Payload.GetProperty("pairingId").GetString(),pin=secret.Pin})}:c).ToArray();
    }

    private CommandEnvelope[] PendingMachineScopedCommands(SqliteTransaction tx,string machineId,string boot) {
        if (IsRestoreQuarantined(tx)) return [];
        using var cmd=LeaseCommand(tx,"SELECT command_id,type,rental_id,lease_revision,boot_id,valid_until,payload FROM command_outbox WHERE machine_id=$id AND boot_id=$boot AND type IN ('ShutdownMachine','RestartSunshine','UnpairSunshine') AND status='pending' ORDER BY rowid",("$id",machineId),("$boot",boot));
        using var r=cmd.ExecuteReader();var commands=new List<CommandEnvelope>();
        while(r.Read()) commands.Add(new(r.GetString(0),r.GetString(1),r.IsDBNull(2)?"":r.GetString(2),r.GetInt64(3),r.GetString(4),DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)),JsonSerializer.Deserialize<JsonElement>(r.GetString(6))));
        return commands.ToArray();
    }
    private static bool IsMachineScopedCommand(string type) => type is "ShutdownMachine" or "RestartSunshine" or "UnpairSunshine";
    private void ApplyMachineScopedCommandResult(SqliteTransaction tx,string commandId,string status) {
        if(status=="applied") ExecuteLease(tx,"UPDATE command_outbox SET status='applied',error_code=NULL WHERE command_id=$id",("$id",commandId));
        else if(status=="failed") ExecuteLease(tx,"UPDATE command_outbox SET status='failed',error_code='AgentFailure' WHERE command_id=$id",("$id",commandId));
    }
    // Machine-scoped repair commands (shutdown, Sunshine restart/unpair) are valid while the
    // machine reports notReady: a broken Sunshine is exactly when these must still be delivered.
    private void ApplyMachineScopedResults(SqliteTransaction tx,string machineId,SyncRequest request,long now) {
        if (IsRestoreQuarantined(tx)) return;
        foreach(var result in request.CommandResults) {
            var command=PendingMachineScopedCommands(tx,machineId,request.BootId).SingleOrDefault(c=>c.CommandId==result.CommandId);
            if(command is null||command.ValidUntilUtc.ToUnixTimeMilliseconds()<=now||result.AppliedRevision!=command.LeaseRevision) continue;
            ApplyMachineScopedCommandResult(tx,command.CommandId,result.Status);
        }
    }
    private void RejectRetiredBoot(SqliteTransaction tx,string machineId,string boot) {
        using(var check=LeaseCommand(tx,"SELECT retired FROM machine_boots WHERE machine_id=$id AND boot_id=$boot",("$id",machineId),("$boot",boot)))
            if(check.ExecuteScalar() is long retired && retired==1) throw new ArgumentException("Retired boot");
    }
    private void RegisterBoot(SqliteTransaction tx,string machineId,string boot,long now,bool preserveActive=false) {
        RejectRetiredBoot(tx,machineId,boot);
        // Boot history is durable: a signed old heartbeat cannot resurrect a retired process after a restart.
        ExecuteLease(tx,"UPDATE machine_boots SET retired=1 WHERE machine_id=$id AND boot_id<>$boot; INSERT OR IGNORE INTO machine_boots(machine_id,boot_id) VALUES($id,$boot)",("$id",machineId),("$boot",boot));
        if(!preserveActive) ExecuteLease(tx,"UPDATE leases SET state=CASE WHEN state='active' THEN 'ending' ELSE 'failed' END,ended_at=COALESCE(ended_at,$now),pending_revision=NULL,pending_expires_at=NULL WHERE machine_id=$id AND boot_id IS NOT NULL AND boot_id<>$boot AND state IN ('preparing','active')",("$id",machineId),("$boot",boot),("$now",now));
        ExecuteLease(tx,"UPDATE command_outbox SET status='invalidated' WHERE machine_id=$id AND boot_id<>$boot AND status='pending'",("$id",machineId),("$boot",boot));
    }
    private void ApplyResults(SqliteTransaction tx,string machineId,SyncRequest request,long now) {
        if (IsRestoreQuarantined(tx)) return;
        foreach(var result in request.CommandResults) {
            // A result can affect only its persisted pending command, current boot and exact revision.
            var command=PendingCommands(tx,machineId,request.BootId).SingleOrDefault(c=>c.CommandId==result.CommandId);
            if(command is null||command.ValidUntilUtc.ToUnixTimeMilliseconds()<=now||result.AppliedRevision!=command.LeaseRevision) continue;
            if(IsMachineScopedCommand(command.Type)){
                ApplyMachineScopedCommandResult(tx,command.CommandId,result.Status);
                continue;
            }
            if(command.Type=="ParsecConnect"){
                // The Agent accepts the attempt asynchronously; per-attempt progress arrives on the
                // signed /agent/parsec-status callback, not in this command result.
                if(result.Status is "applied" or "failed")
                    ApplyParsecConnectResult(tx,command.CommandId,result.Status,now);
                continue;
            }
            if(command.Type=="EndLease"){
                if(result.Status=="failed"){
                    ExecuteLease(tx,"UPDATE command_outbox SET status='failed',error_code='AgentFailure' WHERE command_id=$id",("$id",command.CommandId));
                    continue;
                }
                if(result.Status!="applied") continue;
                var ending=FindLease(tx,command.RentalId);
                if(ending is not null&&ending.Dto.Revision==command.LeaseRevision){
                    ExecuteLease(tx,"UPDATE leases SET state='completed',allocation_held=0,ended_at=COALESCE(ended_at,$now),pending_revision=NULL,pending_expires_at=NULL WHERE id=$id AND revision=$revision",("$id",ending.Dto.Id),("$revision",command.LeaseRevision),("$now",now));
                }
                FailParsecAttemptsForLease(tx,command.RentalId,now);
                ExecuteLease(tx,"UPDATE command_outbox SET status='applied' WHERE command_id=$id",("$id",command.CommandId));
                continue;
            }
            var lease=FindLease(tx,command.RentalId)!;
                        if(command.Type=="PairPin"){
                if(lease.Boot==request.BootId&&lease.Dto.State=="active"&&lease.Dto.Revision==command.LeaseRevision&&result.Status is "applied" or "failed"){
                    ExecuteLease(tx,"UPDATE command_outbox SET status=$status,payload='{}' WHERE command_id=$id",("$status",result.Status),("$id",command.CommandId));RemovePairingSecret(command.CommandId);
                }continue;
            }
            if(lease.Boot!=request.BootId||lease.Dto.PendingRevision!=command.LeaseRevision||lease.Dto.State is not ("preparing" or "active")) continue;
            if(result.Status=="failed") {
                // Do not persist or echo arbitrary agent text (it can contain secrets).
                ExecuteLease(tx,"UPDATE command_outbox SET status='failed',error_code='AgentFailure' WHERE command_id=$id",("$id",command.CommandId));
                ExecuteLease(tx,"UPDATE leases SET state=CASE WHEN state='active' THEN 'ending' ELSE 'failed' END,ended_at=COALESCE(ended_at,$now),pending_revision=NULL,pending_expires_at=NULL WHERE id=$id",("$id",command.RentalId),("$now",now));
                continue;
            }
            if(result.Status!="applied"||request.Readiness!="ready") continue;
            if(command.Type=="PrepareMachine" && lease.Dto.State=="preparing" && lease.Dto.StartsAtUtc is null) {
                var expiry=checked(now+lease.Duration*1000L);
                ExecuteLease(tx,"UPDATE leases SET starts_at=$now,expires_at=$expiry,pending_expires_at=$expiry WHERE id=$id",("$now",now),("$expiry",expiry),("$id",lease.Dto.Id));
                AddCommand(tx,lease,"ApplyLease",1,request.BootId,Math.Min(expiry,lease.PreparationDeadline),new {startsAtUtc=DateTimeOffset.FromUnixTimeMilliseconds(now),expiresAtUtc=DateTimeOffset.FromUnixTimeMilliseconds(expiry)});
            }
            else if(command.Type=="ApplyLease") {
                ExecuteLease(tx,"UPDATE leases SET state='active',revision=$revision,expires_at=pending_expires_at,pending_revision=NULL,pending_expires_at=NULL WHERE id=$id",("$revision",command.LeaseRevision),("$id",lease.Dto.Id));
            }
            else continue;
            ExecuteLease(tx,"UPDATE command_outbox SET status='applied' WHERE command_id=$id",("$id",command.CommandId));
        }
        RefreshClearingWindows(tx);
    }
    private ActiveLeaseConfirmation? ConfirmLease(SqliteTransaction tx,string machineId,string boot,string readiness) {
        if(readiness!="ready" || IsRestoreQuarantined(tx)) return null;
        using var cmd=LeaseCommand(tx,"SELECT id,revision,expires_at FROM leases WHERE machine_id=$id AND boot_id=$boot AND state='active'",("$id",machineId),("$boot",boot));
        using var r=cmd.ExecuteReader();return r.Read()?new(r.GetString(0),r.GetInt64(1),boot,DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2))):null;
    }
}
