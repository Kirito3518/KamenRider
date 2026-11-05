using System.Text.Json;
using KamenRaider.Contracts;
namespace KamenRaider.Agent.Enrollment;
public record EnrollmentReceipt(string MachineId,string ApprovalStatus,string PublicKeyFingerprint,DateTimeOffset EnrolledAtUtc);
public sealed class EnrollmentReceiptStore(string directory) {
 private string FilePath=>Path.Combine(directory,"enrollment-receipt.json");
 public async Task<EnrollmentReceipt?> LoadAsync(CancellationToken ct) {
  if(!File.Exists(FilePath))return null;
  await using var file=File.OpenRead(FilePath);
  return await JsonSerializer.DeserializeAsync<EnrollmentReceipt>(file,cancellationToken:ct)??throw new InvalidDataException("Invalid enrollment receipt");
 }
 public async Task SaveAsync(EnrollmentResult result,string fingerprint,CancellationToken ct) {
  if(string.IsNullOrWhiteSpace(result.MachineId)||result.ApprovalStatus is not ("pending" or "approved")||fingerprint.Length!=64||!fingerprint.All(Uri.IsHexDigit))throw new InvalidDataException("Invalid enrollment receipt");
  var previous=await LoadAsync(ct);
  if(previous is not null&&previous.MachineId!=result.MachineId)throw new InvalidDataException("Enrollment identity changed");
  var temp=FilePath+"."+Guid.NewGuid().ToString("N")+".tmp";
  try {
   await using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) {
    await JsonSerializer.SerializeAsync(file,new EnrollmentReceipt(result.MachineId,result.ApprovalStatus,fingerprint,DateTimeOffset.UtcNow),cancellationToken:ct);
    await file.FlushAsync(ct);file.Flush(true);
   }
   File.Move(temp,FilePath,true);
  } finally {if(File.Exists(temp))File.Delete(temp);}
 }
}
