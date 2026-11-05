using System.Security.Cryptography;
using KamenRaider.Contracts;
using KamenRaider.Controller.Data;

namespace Controller.Tests;

public class EnrollmentTicketTests
{
    [Fact] public void TicketExpiresAndCanEnrollOnlyOneNewIdentity()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
        var clock=new TestClock();
        using var db=new ControllerDatabase(path,clock:clock);
        var ticket=db.CreateEnrollmentTicket();
        Assert.Matches("^[A-Za-z0-9_-]{43}$",ticket.Ticket);
        Assert.Equal(clock.Now.AddHours(24),ticket.ExpiresAtUtc);
        using var firstKey=ECDsa.Create();using var secondKey=ECDsa.Create();
        var first=new EnrollmentRequest("first",Convert.ToBase64String(firstKey.ExportSubjectPublicKeyInfo()),"192.0.2.1","001122334455",1,2);
        var second=new EnrollmentRequest("second",Convert.ToBase64String(secondKey.ExportSubjectPublicKeyInfo()),"192.0.2.2","001122334466",3,4);
        Assert.Equal("pending",db.Enroll(first,ticket.Ticket).ApprovalStatus);
        Assert.Throws<UnauthorizedAccessException>(()=>db.Enroll(second,ticket.Ticket));
        Assert.Equal("pending",db.Enroll(first).ApprovalStatus);

        var expiring=db.CreateEnrollmentTicket();clock.Now=clock.Now.AddHours(24).AddSeconds(1);
        Assert.Throws<UnauthorizedAccessException>(()=>db.Enroll(second,expiring.Ticket));
        Assert.Single(db.List());
        db.Dispose();
        File.Delete(path);
    }

    private sealed class TestClock:TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.Parse("2026-11-05T00:00:00Z");
        public override DateTimeOffset GetUtcNow()=>Now;
    }
}
