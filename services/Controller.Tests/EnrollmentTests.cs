using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using System.Security.Cryptography;
namespace Controller.Tests;
public class EnrollmentTests
{
    [Fact] public void PendingIdentityCannotRentAndDuplicateDoesNotReplaceAdminConfiguration()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".db");
        using var key = ECDsa.Create();
        var request = new EnrollmentRequest("machine", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "192.0.2.1", "001122334455", 47989, 47990);
        using (var db = new ControllerDatabase(path)) {
            Assert.Equal("pending", db.EnrollWithTicket(request).ApprovalStatus);
            Assert.False(db.List().Single().CanRent);
            Assert.Throws<InvalidOperationException>(() => db.Approve("machine", "admin"));
            db.Update("machine", new("Gaming PC", 2500, 5000, 5001), 1);
            var approved = db.Approve("machine", "admin");
            Assert.Equal("admin", approved.ApprovedBy);
            Assert.NotNull(approved.ApprovedAt);
            db.Enroll(request);
            Assert.Single(db.List());
            Assert.Equal(5000, db.List()[0].MoonlightPort);
            using var other = ECDsa.Create();
            Assert.Throws<UnauthorizedAccessException>(() => db.EnrollWithTicket(request with {PublicKey=Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())}));
            Assert.Throws<InvalidOperationException>(() => db.Update("machine", new("Stale", 200, 1, 2), 1));
        }
        using (var restarted = new ControllerDatabase(path)) {
            var machine = restarted.List().Single();
            Assert.False(machine.CanRent); Assert.Equal("approved", machine.ApprovalStatus); Assert.Equal("Gaming PC", machine.DisplayName); Assert.Equal(2500, machine.HourlyPriceMinorUnits);
        }
        File.Delete(path);
    }
    [Theory] [InlineData("", 1, 1, 1)] [InlineData("PC", 0, 1, 1)] [InlineData("PC", -1, 1, 1)] [InlineData("PC", 1, 0, 1)] [InlineData("PC", 1, 1, 65536)]
    public void InvalidMetadataRejected(string name, long price, int moonlight, int sunshine) {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
        using(var db=new ControllerDatabase(path)) { using var key=ECDsa.Create(); db.EnrollWithTicket(new("id",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2)); Assert.Throws<ArgumentException>(()=>db.Update("id",new(name,price,moonlight,sunshine),1)); }
        File.Delete(path);
    }
}
