using KamenRaider.Contracts;
using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace KamenRaider.Controller.Data;
public sealed partial class ControllerDatabase {
 // PINs never enter SQLite, including WAL/backups. Restart fails queued pairing rather than recovering the secret.
 private readonly Dictionary<string,(string Pin,long Until)> pairingPins=[];
 private readonly Dictionary<string,ITimer> pairingPinTimers=[];
 private void RemovePairingSecret(string id){pairingPins.Remove(id);if(pairingPinTimers.Remove(id,out var timer))timer.Dispose();}
 private void ExpirePairingSecret(string id){lock(gate){
  if(disposed||!pairingPins.ContainsKey(id))return;
  // Erase the reference before any SQLite operation: database contention must not prolong secret retention.
  RemovePairingSecret(id);
  try{using var cmd=Command("UPDATE command_outbox SET payload='{}',status=CASE WHEN status='pending' THEN 'expired' ELSE status END WHERE command_id=$id",("$id",id));cmd.ExecuteNonQuery();}
  catch(SqliteException){/* A later database operation reconciles status; the secret is already removed. */}
 }}
 private static bool PairId(string? id)=>PairingIds.IsValid(id);
 private void ObservePairingOffer(SqliteTransaction tx,string machine,SyncRequest request,long now){
  ExecuteLease(tx,"DELETE FROM pairing_offers WHERE machine_id=$machine",("$machine",machine));if(IsRestoreQuarantined(tx))return;var offer=request.PendingPairing;
  if(offer is null)return;
  var lease=FindLease(tx,offer.RentalId);
  if(offer.PairingIds is null||offer.PairingIds.Length>100||offer.PairingIds.Any(id=>!PairId(id))||offer.PairingIds.Distinct().Count()!=offer.PairingIds.Length||offer.BootId!=request.BootId||offer.ValidUntilUtc.ToUnixTimeMilliseconds()<=now||lease is null||lease.Dto.MachineId!=machine||lease.Dto.State!="active"||lease.Dto.Revision!=offer.Revision||lease.Boot!=offer.BootId)return;
  var until=Math.Min(Math.Min(offer.ValidUntilUtc.ToUnixTimeMilliseconds(),now+30000),lease.Dto.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds());
  ExecuteLease(tx,"INSERT INTO pairing_offers VALUES($machine,$rental,$revision,$boot,$until,$ids)",("$machine",machine),("$rental",offer.RentalId),("$revision",offer.Revision),("$boot",offer.BootId),("$until",until),("$ids",JsonSerializer.Serialize(offer.PairingIds)));
 }
 private void PurgePairingSecrets(SqliteTransaction tx,long now){
  using(var cmd=LeaseCommand(tx,"SELECT command_id,status,valid_until FROM command_outbox WHERE type='PairPin'")){
   using var rows=cmd.ExecuteReader();var invalid=new List<string>();while(rows.Read())if(rows.GetString(1)!="pending"||rows.GetInt64(2)<=now||!pairingPins.ContainsKey(rows.GetString(0)))invalid.Add(rows.GetString(0));
   rows.Close();foreach(var id in invalid){RemovePairingSecret(id);ExecuteLease(tx,"UPDATE command_outbox SET payload='{}',status=CASE WHEN status='pending' THEN 'expired' ELSE status END WHERE command_id=$id",("$id",id));}
  }
 }
 private LeaseRow PairOwner(SqliteTransaction tx,string id,string actor){var lease=FindLease(tx,id)??throw new KeyNotFoundException();if(string.IsNullOrWhiteSpace(actor)||lease.Dto.UserId!=actor)throw new UnauthorizedAccessException();return lease;}
 public PairingOffer? GetPairingOffer(string id,string actor){lock(gate){using var tx=connection.BeginTransaction();EnsureNotRestoreQuarantined(tx);var now=clock.GetUtcNow().ToUnixTimeMilliseconds();ExpireLeases(tx,now);var lease=PairOwner(tx,id,actor);PairingOffer? result=null;if(lease.Dto.State=="active"&&lease.Dto.PendingRevision is null){using var cmd=LeaseCommand(tx,"SELECT valid_until,ids FROM pairing_offers WHERE rental_id=$id AND boot_id=$boot AND revision=$revision AND valid_until>$now",("$id",id),("$boot",lease.Boot),("$revision",lease.Dto.Revision),("$now",now));using var row=cmd.ExecuteReader();if(row.Read())result=new(id,lease.Dto.Revision,lease.Boot!,DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(0)),JsonSerializer.Deserialize<string[]>(row.GetString(1))??[]);}tx.Commit();return result;}}
 public PairingCommandStatus QueuePairing(string id,string actor,PairingRequest request){
  Identifier(request.IdempotencyKey);if(!PairId(request.PairingId)||request.Pin is not {Length:4}||request.Pin.Any(c=>c<'0'||c>'9'))throw new ArgumentException("Invalid pairing request");
  lock(gate){using var tx=connection.BeginTransaction();EnsureNotRestoreQuarantined(tx);var now=clock.GetUtcNow().ToUnixTimeMilliseconds();ExpireLeases(tx,now);PurgePairingSecrets(tx,now);var lease=PairOwner(tx,id,actor);if(request.UserId!=actor)throw new UnauthorizedAccessException();
   using(var prior=LeaseCommand(tx,"SELECT command_id,pairing_id FROM pairing_requests WHERE rental_id=$rental AND request_key=$key",("$rental",id),("$key",request.IdempotencyKey))){using var row=prior.ExecuteReader();if(row.Read()){var command=row.GetString(0);if(row.GetString(1)!=request.PairingId)throw new InvalidOperationException("Idempotency conflict");row.Close();var result=ReadPairStatus(tx,id,command);tx.Commit();return result;}}
   if(lease.Dto.State!="active"||lease.Dto.PendingRevision is not null)throw new InvalidOperationException("Lease not active");
   using var offer=LeaseCommand(tx,"SELECT valid_until,ids FROM pairing_offers WHERE rental_id=$rental AND revision=$revision AND boot_id=$boot AND valid_until>$now",("$rental",id),("$revision",lease.Dto.Revision),("$boot",lease.Boot),("$now",now));long until;using(var row=offer.ExecuteReader()){if(!row.Read())throw new InvalidOperationException("No current pairing offer");until=row.GetInt64(0);if(!(JsonSerializer.Deserialize<string[]>(row.GetString(1))??[]).Contains(request.PairingId))throw new InvalidOperationException("Selection not offered");}
   using(var pending=LeaseCommand(tx,"SELECT COUNT(*) FROM command_outbox WHERE rental_id=$rental AND type='PairPin' AND status='pending'",("$rental",id)))if((long)pending.ExecuteScalar()!>0)throw new InvalidOperationException("Pairing already pending");
   var commandId=Guid.NewGuid().ToString("N");ExecuteLease(tx,"INSERT INTO command_outbox(command_id,type,rental_id,machine_id,lease_revision,boot_id,valid_until,payload) VALUES($command,'PairPin',$rental,$machine,$revision,$boot,$until,$payload)",("$command",commandId),("$rental",id),("$machine",lease.Dto.MachineId),("$revision",lease.Dto.Revision),("$boot",lease.Boot),("$until",until),("$payload",JsonSerializer.Serialize(new{pairingId=request.PairingId})));
   ExecuteLease(tx,"INSERT INTO pairing_requests VALUES($rental,$key,$pairing,$command)",("$rental",id),("$key",request.IdempotencyKey),("$pairing",request.PairingId),("$command",commandId));
   tx.Commit();pairingPins[commandId]=(request.Pin,until);
   try{pairingPinTimers[commandId]=clock.CreateTimer(_=>ExpirePairingSecret(commandId),null,TimeSpan.FromMilliseconds(Math.Max(0,until-clock.GetUtcNow().ToUnixTimeMilliseconds())),Timeout.InfiniteTimeSpan);}
   catch{RemovePairingSecret(commandId);throw;}
   return new(commandId,"queued");
  }
 }
 private PairingCommandStatus ReadPairStatus(SqliteTransaction tx,string id,string commandId){using var cmd=LeaseCommand(tx,"SELECT status FROM command_outbox WHERE rental_id=$rental AND command_id=$command AND type='PairPin'",("$rental",id),("$command",commandId));return new(commandId,(cmd.ExecuteScalar() as string) switch{"pending"=>"queued","applied"=>"succeeded","failed"=>"failed","expired" or "invalidated"=>"expired",_=>throw new KeyNotFoundException()});}
 public PairingCommandStatus PairingStatus(string id,string commandId,string actor){lock(gate){using var tx=connection.BeginTransaction();EnsureNotRestoreQuarantined(tx);ExpireLeases(tx,clock.GetUtcNow().ToUnixTimeMilliseconds());PurgePairingSecrets(tx,clock.GetUtcNow().ToUnixTimeMilliseconds());PairOwner(tx,id,actor);var result=ReadPairStatus(tx,id,commandId);tx.Commit();return result;}}
}

