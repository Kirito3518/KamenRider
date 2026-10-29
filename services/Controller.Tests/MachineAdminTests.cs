using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using System.Security.Cryptography;
namespace Controller.Tests;
public class MachineAdminTests {
 [Fact] public void FingerprintApprovalAndPendingRejectionAreBoundToIdentity(){
 var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
 try{using var db=new ControllerDatabase(path);using var key=ECDsa.Create();db.Enroll(new("pc",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2));db.Update("pc",new("PC",100,1,2),1);
 var fingerprint=Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
 Assert.Throws<InvalidOperationException>(()=>db.ApproveVerified("pc","admin","wrong"));
 Assert.Equal("pending",db.List().Single().ApprovalStatus);
 Assert.Equal(fingerprint,db.List().Single().PublicKeyFingerprint);
 Assert.Null(db.List().Single().AppliedConfigRevision);
 db.Sync("pc",new("boot","192.0.2.1","001122334455",1,"unknown",[]));
 Assert.Equal(1,db.List().Single().AppliedConfigRevision);
 db.ApproveVerified("pc","admin",fingerprint);
 Assert.Throws<InvalidOperationException>(()=>db.RejectPending("pc",fingerprint));
 using var second=ECDsa.Create();db.Enroll(new("pending",Convert.ToBase64String(second.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",3,4));
 db.Sync("pending",new("pending-boot","192.0.2.1","001122334455",1,"unknown",[]));
 Assert.Throws<InvalidOperationException>(()=>db.RejectPending("pending","wrong"));
 db.RejectPending("pending",Convert.ToHexString(SHA256.HashData(second.ExportSubjectPublicKeyInfo())));
 Assert.DoesNotContain(db.List(),m=>m.MachineId=="pending");
 }finally{File.Delete(path);}
 }
}
