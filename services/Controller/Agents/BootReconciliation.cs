using KamenRaider.Contracts;
using System.Text.Json;
namespace KamenRaider.Controller.Data;
public sealed partial class ControllerDatabase {
 // Evidence must come from qualified Agent adapters; baseline enrollment alone is never proof.
 public ReconcileResponse Reconcile(string machineId,ReconcileRequest request) {
  Identifier(request.BootId);if(request.AppliedConfigRevision<0)throw new ArgumentException("Invalid configuration revision");
  lock(gate) {
   var machine=List().SingleOrDefault(m=>m.MachineId==machineId)??throw new KeyNotFoundException();
   using var tx=connection.BeginTransaction();var now=clock.GetUtcNow();ExpireLeases(tx,now.ToUnixTimeMilliseconds());
   using(var retired=LeaseCommand(tx,"SELECT retired FROM machine_boots WHERE machine_id=$id AND boot_id=$boot",("$id",machineId),("$boot",request.BootId)))
    if(retired.ExecuteScalar() is long value&&value==1)throw new ArgumentException("Retired boot");
   var config=new ConfigurationDto(machine.ConfigRevision,machine.MoonlightPort,machine.SunshinePort);
   LeaseRow? allocation=null;
   using(var find=LeaseCommand(tx,$"SELECT {LeaseColumns} FROM leases WHERE machine_id=$id AND allocation_held=1",("$id",machineId)))
   using(var row=find.ExecuteReader())if(row.Read())allocation=ReadLease(row);
   long lastCommand,pendingCommands;
   using(var commands=LeaseCommand(tx,"SELECT COALESCE(MAX(rowid),0),COALESCE(SUM(CASE WHEN status='pending' THEN 1 ELSE 0 END),0) FROM command_outbox WHERE machine_id=$id",("$id",machineId)))
   using(var row=commands.ExecuteReader()){row.Read();lastCommand=row.GetInt64(0);pendingCommands=row.GetInt64(1);}
   var fingerprint=JsonSerializer.Serialize(new{allocation,lastCommand,pendingCommands,config.Revision});
   if(request.Challenge is null) {
    var challenge=new ReconciliationChallenge(Guid.NewGuid().ToString("N"),request.BootId,allocation?.Dto.Id,allocation?.Dto.Revision,now.AddSeconds(30));
    ExecuteLease(tx,"INSERT INTO boot_reconciliation_challenges VALUES($id,$token,$boot,$allocation,$revision,$fingerprint,$until) ON CONFLICT(machine_id) DO UPDATE SET token=$token,boot_id=$boot,allocation_id=$allocation,lease_revision=$revision,fingerprint=$fingerprint,expires_at=$until",("$id",machineId),("$token",challenge.Token),("$boot",challenge.BootId),("$allocation",challenge.AllocationId),("$revision",challenge.LeaseRevision),("$fingerprint",fingerprint),("$until",challenge.ExpiresAtUtc.ToUnixTimeMilliseconds()));
    tx.Commit();return new(now,config,null,"Hold",challenge);
   }
   var proof=request.Challenge;
   bool valid;
   using(var check=LeaseCommand(tx,"SELECT COUNT(*) FROM boot_reconciliation_challenges WHERE machine_id=$id AND token=$token AND boot_id=$boot AND allocation_id IS $allocation AND lease_revision IS $revision AND fingerprint=$fingerprint AND expires_at=$until AND expires_at>$now",("$id",machineId),("$token",proof.Token),("$boot",request.BootId),("$allocation",proof.AllocationId),("$revision",proof.LeaseRevision),("$fingerprint",fingerprint),("$until",proof.ExpiresAtUtc.ToUnixTimeMilliseconds()),("$now",now.ToUnixTimeMilliseconds())))valid=(long)check.ExecuteScalar()! == 1;
   if(!valid||proof.BootId!=request.BootId||machine.ApprovalStatus!="approved"||request.AppliedConfigRevision!=config.Revision||!request.BaselineValidated||!request.RestorationVerified||!request.FencedCleanupVerified||!request.HealthVerified){tx.Commit();return new(now,config,null,"Hold");}
   // Consume with the state transition: replay cannot affect this or a replacement allocation.
   ExecuteLease(tx,"DELETE FROM boot_reconciliation_challenges WHERE machine_id=$id",("$id",machineId));
   // Only this transaction can preserve an active lease across a verified boot transition.
   RegisterBoot(tx,machineId,request.BootId,preserveActive:true);
   ExecuteLease(tx,"UPDATE command_outbox SET status='invalidated',payload=CASE WHEN type='PairPin' THEN '{}' ELSE payload END WHERE machine_id=$id AND status='pending'; DELETE FROM pairing_offers WHERE machine_id=$id",("$id",machineId));
   ExecuteLease(tx,"UPDATE leases SET state='failed',pending_revision=NULL,pending_expires_at=NULL WHERE machine_id=$id AND state='preparing'; UPDATE leases SET boot_id=$boot,pending_revision=NULL,pending_expires_at=NULL WHERE machine_id=$id AND state='active'; UPDATE leases SET state='completed',allocation_held=0 WHERE machine_id=$id AND state='ending'; UPDATE leases SET allocation_held=0 WHERE machine_id=$id AND state='failed'",("$id",machineId),("$boot",request.BootId));
   PurgePairingSecrets(tx,now.ToUnixTimeMilliseconds());
   ExecuteLease(tx,"INSERT INTO machine_heartbeats(machine_id,last_seen,boot_id,readiness) VALUES($id,$at,$boot,'notReady') ON CONFLICT(machine_id) DO UPDATE SET last_seen=$at,boot_id=$boot,readiness='notReady'",("$id",machineId),("$at",now.ToString("O")),("$boot",request.BootId));
   var lease=ConfirmLease(tx,machineId,request.BootId,"ready");tx.Commit();return new(now,config,lease,lease is null?"PrepareAvailable":"ResumeLease");
  }
 }
}
