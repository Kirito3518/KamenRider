using KamenRaider.Agent.Enrollment;
using KamenRaider.Contracts;
namespace Agent.Tests;
public class EnrollmentReceiptTests {
 [Fact] public async Task ReceiptIsWrittenOnlyForAnAcknowledgedMachine() {
  var directory=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(directory);
  try {
   var store=new EnrollmentReceiptStore(directory);
   var fingerprint=new string('A',64);
   await store.SaveAsync(new EnrollmentResult("machine-a","pending"),fingerprint,CancellationToken.None);
   var receipt=await store.LoadAsync(CancellationToken.None);
   Assert.NotNull(receipt);
   Assert.Equal("machine-a",receipt.MachineId);
   Assert.Equal("pending",receipt.ApprovalStatus);
   Assert.Equal(fingerprint,receipt.PublicKeyFingerprint);
   await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(new EnrollmentResult("machine-b","pending"),fingerprint,CancellationToken.None));
   Assert.Equal("machine-a",(await store.LoadAsync(CancellationToken.None))!.MachineId);
  } finally {Directory.Delete(directory,true);}
 }
}
