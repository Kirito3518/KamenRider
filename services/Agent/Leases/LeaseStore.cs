using System.Text.Json;
namespace KamenRaider.Agent.Leases;
public record LeaseSnapshot(string RentalId,long Revision,DateTimeOffset ExpiresAtUtc,string BootId,string LastCommandId,long DeadlineUptimeMilliseconds);
public class LeaseStore(string directory) {
 private string FilePath=>Path.Combine(directory,"lease.json");
 public virtual async Task SaveAsync(LeaseSnapshot snapshot,CancellationToken ct) {
  Directory.CreateDirectory(directory);var temp=Path.Combine(directory,Guid.NewGuid()+".tmp");
  try {await using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){await JsonSerializer.SerializeAsync(stream,snapshot,cancellationToken:ct);await stream.FlushAsync(ct);stream.Flush(true);}File.Move(temp,FilePath,true);}finally{if(File.Exists(temp))File.Delete(temp);}
 }
 public async Task<LeaseSnapshot?> LoadAsync(CancellationToken ct) {if(!File.Exists(FilePath))return null;await using var stream=File.OpenRead(FilePath);return await JsonSerializer.DeserializeAsync<LeaseSnapshot>(stream,cancellationToken:ct)??throw new InvalidDataException("Invalid lease state");}
}
