using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using System.Security.Cryptography;
namespace Controller.Tests;
public class EnrollmentAdmissionTests
{
    [Fact] public void NewIdentityRateAndPendingQuotaPersistWhileExistingEnrollmentRemainsIdempotent() {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
        using var key=ECDsa.Create();
        var request=new EnrollmentRequest("one",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2);
        var clock=new TestClock(); var policy=new EnrollmentPolicy(2,1);
        using(var db=new ControllerDatabase(path,policy,clock)) {
            db.EnrollWithTicket(request);
            Assert.Throws<EnrollmentCapacityException>(()=>db.EnrollWithTicket(request with {MachineId="two"}));
            Assert.Equal("pending",db.Enroll(request).ApprovalStatus);
            Assert.Single(db.List());
        }
        using(var db=new ControllerDatabase(path,policy,clock)) {
            Assert.Throws<EnrollmentCapacityException>(()=>db.EnrollWithTicket(request with {MachineId="two"}));
            clock.Now=clock.Now.AddSeconds(61);db.EnrollWithTicket(request with {MachineId="two"});
            clock.Now=clock.Now.AddSeconds(61);
            Assert.Throws<EnrollmentCapacityException>(()=>db.EnrollWithTicket(request with {MachineId="three"}));
            Assert.Equal(2,db.List().Length);
            db.Update("one",new("PC",100,1,2),1);db.Approve("one","admin");
            Assert.Equal("approved",db.Enroll(request).ApprovalStatus);
            Assert.Equal("pending",db.EnrollWithTicket(request with {MachineId="three"}).ApprovalStatus);
        }
        File.Delete(path);
    }
    private sealed class TestClock:TimeProvider { public DateTimeOffset Now=DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow()=>Now; }
}
