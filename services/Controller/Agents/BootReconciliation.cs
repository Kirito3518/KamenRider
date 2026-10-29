using KamenRaider.Contracts;
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
   if(machine.ApprovalStatus!="approved"||request.AppliedConfigRevision!=config.Revision||!request.BaselineValidated||!request.RestorationVerified||!request.FencedCleanupVerified||!request.HealthVerified){tx.Commit();return new(now,config,null,"Hold");}
   // Only this transaction can preserve an active lease across a verified boot transition.
   RegisterBoot(tx,machineId,request.BootId,preserveActive:true);
   ExecuteLease(tx,"UPDATE command_outbox SET status='invalidated',payload=CASE WHEN type='PairPin' THEN '{}' ELSE payload END WHERE machine_id=$id AND status='pending'; DELETE FROM pairing_offers WHERE machine_id=$id",("$id",machineId));
   ExecuteLease(tx,"UPDATE leases SET state='failed',pending_revision=NULL,pending_expires_at=NULL WHERE machine_id=$id AND state='preparing'; UPDATE leases SET boot_id=$boot,pending_revision=NULL,pending_expires_at=NULL WHERE machine_id=$id AND state='active'; UPDATE leases SET allocation_held=0 WHERE machine_id=$id AND state IN ('failed','ending')",("$id",machineId),("$boot",request.BootId));
   PurgePairingSecrets(tx,now.ToUnixTimeMilliseconds());
   ExecuteLease(tx,"INSERT INTO machine_heartbeats(machine_id,last_seen,boot_id,readiness) VALUES($id,$at,$boot,'notReady') ON CONFLICT(machine_id) DO UPDATE SET last_seen=$at,boot_id=$boot,readiness='notReady'",("$id",machineId),("$at",now.ToString("O")),("$boot",request.BootId));
   var lease=ConfirmLease(tx,machineId,request.BootId,"ready");tx.Commit();return new(now,config,lease,lease is null?"PrepareAvailable":"ResumeLease");
  }
 }
}
