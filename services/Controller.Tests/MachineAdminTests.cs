using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
namespace Controller.Tests;
public class MachineAdminTests {
 [Fact] public void FingerprintApprovalAndPendingRejectionAreBoundToIdentity(){
 var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
 try{using var db=new ControllerDatabase(path);using var key=ECDsa.Create();db.EnrollWithTicket(new("pc",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2));db.Update("pc",new("PC",100,1,2),1);
 var fingerprint=Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
 Assert.Throws<InvalidOperationException>(()=>db.ApproveVerified("pc","admin","wrong"));
 Assert.Equal("pending",db.List().Single().ApprovalStatus);
 Assert.Equal(fingerprint,db.List().Single().PublicKeyFingerprint);
 Assert.Null(db.List().Single().AppliedConfigRevision);
 db.Sync("pc",new("boot","192.0.2.1","001122334455",1,"unknown",[]));
 Assert.Equal(1,db.List().Single().AppliedConfigRevision);
 db.ApproveVerified("pc","admin",fingerprint);
 Assert.Throws<InvalidOperationException>(()=>db.RejectPending("pc",fingerprint));
 using var second=ECDsa.Create();db.EnrollWithTicket(new("pending",Convert.ToBase64String(second.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",3,4));
 db.Sync("pending",new("pending-boot","192.0.2.1","001122334455",1,"unknown",[]));
 Assert.Throws<InvalidOperationException>(()=>db.RejectPending("pending","wrong"));
 db.RejectPending("pending",Convert.ToHexString(SHA256.HashData(second.ExportSubjectPublicKeyInfo())));
 Assert.DoesNotContain(db.List(),m=>m.MachineId=="pending");
 }finally{File.Delete(path);}
 }
 [Fact] public void HourlyPriceMustBeAWholeBahtNumberForUpdateAndApproval(){
 var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
 try{
 using var db=new ControllerDatabase(path);using var key=ECDsa.Create();
 db.EnrollWithTicket(new("pc",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2));
 Assert.Throws<ArgumentException>(()=>db.Update("pc",new MachineUpdate("PC",250,47989,47990),1));
 Assert.Equal(0L,db.List().Single().HourlyPriceMinorUnits);
 db.Update("pc",new MachineUpdate("PC",100,47989,47990),1);
 // A restored legacy row can still carry a fractional-baht price; approval must refuse it.
 using(var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString())){
  connection.Open();
  using var rewrite=connection.CreateCommand();
  rewrite.CommandText="UPDATE machines SET hourly_price=250 WHERE machine_id='pc'";
  Assert.Equal(1,rewrite.ExecuteNonQuery());
 }
 var fingerprint=Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
 Assert.Throws<ArgumentException>(()=>db.ApproveVerified("pc","admin",fingerprint));
 Assert.Equal("pending",db.List().Single().ApprovalStatus);
 db.Update("pc",new MachineUpdate("PC",300,47989,47990),db.List().Single().ConfigRevision);
 Assert.Equal("approved",db.ApproveVerified("pc","admin",fingerprint).ApprovalStatus);
 Assert.Equal(300L,db.List().Single().HourlyPriceMinorUnits);
 }finally{File.Delete(path);}
 }
}
