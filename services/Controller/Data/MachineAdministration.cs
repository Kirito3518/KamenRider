using KamenRaider.Contracts;
namespace KamenRaider.Controller.Data;
public sealed partial class ControllerDatabase {
 public MachineDto ApproveVerified(string id,string actor,string fingerprint){lock(gate){var m=List().SingleOrDefault(x=>x.MachineId==id)??throw new KeyNotFoundException();if(m.ApprovalStatus!="pending"||m.PublicKeyFingerprint!=fingerprint)throw new InvalidOperationException("Identity confirmation required");return Approve(id,actor);}}
 public object RejectPending(string id,string fingerprint){lock(gate){using var tx=connection.BeginTransaction();var m=List().SingleOrDefault(x=>x.MachineId==id)??throw new KeyNotFoundException();if(m.ApprovalStatus!="pending"||m.PublicKeyFingerprint!=fingerprint)throw new InvalidOperationException("Pending identity required");using var cmd=Command("DELETE FROM machine_heartbeats WHERE machine_id=$id; DELETE FROM machine_boots WHERE machine_id=$id; DELETE FROM boot_reconciliation_challenges WHERE machine_id=$id; DELETE FROM machines WHERE machine_id=$id AND approval_status='pending'",("$id",id));cmd.Transaction=tx;cmd.ExecuteNonQuery();tx.Commit();return new {status="rejected"};}}
}
