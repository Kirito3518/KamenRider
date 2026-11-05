using System.Text.Json;
namespace KamenRaider.Agent.Leases;
public record LeaseSnapshot(string RentalId,long Revision,DateTimeOffset ExpiresAtUtc,string BootId,string LastCommandId,long DeadlineUptimeMilliseconds,long? RecoveryDeadlineUptimeMilliseconds=null);
public class LeaseStore(string directory) {
 private string FilePath=>Path.Combine(directory,"lease.json");
 public virtual Task SaveAsync(LeaseSnapshot snapshot,CancellationToken ct)=>SaveCoreAsync(snapshot,publish=>{publish();return true;},ct);
 public virtual Task<bool> SaveIfAsync(LeaseSnapshot snapshot,Func<Action,bool> commit,CancellationToken ct)=>SaveCoreAsync(snapshot,commit,ct);
 private async Task<bool> SaveCoreAsync(LeaseSnapshot snapshot,Func<Action,bool> commit,CancellationToken ct) {
  Directory.CreateDirectory(directory);var temp=Path.Combine(directory,Guid.NewGuid()+".tmp");var published=false;
  try {
   await using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){await JsonSerializer.SerializeAsync(stream,snapshot,cancellationToken:ct);await stream.FlushAsync(ct);stream.Flush(true);}
   var accepted=commit(()=>{if(published)throw new InvalidOperationException("Lease snapshot was already published.");File.Move(temp,FilePath,true);published=true;});
   if(accepted!=published)throw new InvalidOperationException("Lease snapshot commit callback returned an inconsistent result.");
   return accepted;
  }finally{if(File.Exists(temp))File.Delete(temp);}
 }
 public async Task<LeaseSnapshot?> LoadAsync(CancellationToken ct) {if(!File.Exists(FilePath))return null;await using var stream=File.OpenRead(FilePath);return await JsonSerializer.DeserializeAsync<LeaseSnapshot>(stream,cancellationToken:ct)??throw new InvalidDataException("Invalid lease state");}
}
