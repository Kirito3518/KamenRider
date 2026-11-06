using System.Security.Cryptography;
using KamenRaider.Contracts;
using KamenRaider.Controller.Data;

namespace Controller.Tests;

public class AdminVncSessionTests
{
    [Fact]
    public void TicketIsShortLivedSingleUseOriginBoundAndTargetsFreshMachinePresence()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var clock = new TestClock();
        try
        {
            using var database = new ControllerDatabase(path, clock: clock);
            using var key = ECDsa.Create();
            database.EnrollWithTicket(new EnrollmentRequest("pc", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "10.0.0.4", "001122334455", 47989, 47990));
            database.Sync("pc", new SyncRequest("boot", "10.0.0.9", "001122334455", 1, "notReady", []));

            Assert.Throws<UnauthorizedAccessException>(() => database.CreateAdminVncTicket("pc", "admin-1", "http://localhost:3000"));
            database.Update("pc", new MachineUpdate("PC", 100, 47989, 47990), 1);
            database.ApproveVerified("pc", "admin-1", database.List().Single().PublicKeyFingerprint!);
            Assert.Throws<UnauthorizedAccessException>(() => database.CreateAdminVncTicket("pc", "admin-1", "http://localhost:3000"));
            var machine = database.List().Single();
            database.Update("pc", new MachineUpdate("PC", 100, 47989, 47990) { VncLocalIp = "10.0.0.8" }, machine.ConfigRevision);
            var issued = database.CreateAdminVncTicket("pc", "admin-1", "http://localhost:3000");
            Assert.Matches("^[A-Za-z0-9_-]{43}$", issued.Ticket);
            Assert.Equal(clock.Now.AddMinutes(1), issued.ExpiresAtUtc);

            Assert.Throws<UnauthorizedAccessException>(() => database.ConsumeAdminVncTicket(issued.Ticket, "https://evil.example"));
            var target = database.ConsumeAdminVncTicket(issued.Ticket, "http://localhost:3000");
            Assert.Equal("pc", target.MachineId);
            Assert.Equal("admin-1", target.AdminUserId);
            Assert.Equal("10.0.0.8", target.Address.ToString());
            Assert.Equal(5900, target.Port);
            Assert.Equal(1, database.List().Single().ActiveAdminVncSessions);
            Assert.Throws<UnauthorizedAccessException>(() => database.ConsumeAdminVncTicket(issued.Ticket, "http://localhost:3000"));

            database.FinishAdminVncSession(target.SessionId);
            Assert.Equal(0, database.List().Single().ActiveAdminVncSessions);
            var stale = database.CreateAdminVncTicket("pc", "admin-1", "http://localhost:3000");
            var staleTarget = database.ConsumeAdminVncTicket(stale.Ticket, "http://localhost:3000");
            clock.Now = clock.Now.AddHours(1);
            Assert.Equal(0, database.List().Single().ActiveAdminVncSessions);
            database.FinishAdminVncSession(staleTarget.SessionId);
            clock.Now = clock.Now.AddSeconds(21);
            Assert.Throws<UnauthorizedAccessException>(() => database.CreateAdminVncTicket("pc", "admin-1", "http://localhost:3000"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AbandonedVncSessionsArePrunedButFinishedSessionsRemainForAudit()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var clock = new TestClock();
        try
        {
            using var database = new ControllerDatabase(path, clock: clock);
            using var key = ECDsa.Create();
            database.EnrollWithTicket(new EnrollmentRequest("pc", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "10.0.0.4", "001122334455", 47989, 47990));
            database.Sync("pc", new SyncRequest("boot", "10.0.0.4", "001122334455", 1, "notReady", []));
            database.Update("pc", new MachineUpdate("PC", 100, 47989, 47990) { VncLocalIp = "10.0.0.8" }, 1);
            database.ApproveVerified("pc", "admin-1", database.List().Single().PublicKeyFingerprint!);

            var finished = OpenSession();
            database.FinishAdminVncSession(finished.SessionId);
            var abandoned = OpenSession();

            clock.Now = clock.Now.AddHours(1).AddMinutes(5);
            RefreshPresence();
            database.CreateAdminVncTicket("pc", "admin-1", "http://localhost:3000");
            Assert.Equal(1, CountSession(finished.SessionId));
            Assert.Equal(0, CountSession(abandoned.SessionId));

            clock.Now = clock.Now.AddDays(30).AddSeconds(1);
            RefreshPresence();
            database.CreateAdminVncTicket("pc", "admin-1", "http://localhost:3000");
            Assert.Equal(0, CountSession(finished.SessionId));

            AdminVncTarget OpenSession()
            {
                var ticket = database.CreateAdminVncTicket("pc", "admin-1", "http://localhost:3000");
                return database.ConsumeAdminVncTicket(ticket.Ticket, "http://localhost:3000");
            }

            void RefreshPresence() => database.Sync("pc", new SyncRequest("boot", "10.0.0.4", "001122334455", 1, "notReady", []));

            long CountSession(string sessionId)
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path + ";Pooling=False");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM admin_vnc_sessions WHERE session_id=$session";
                command.Parameters.AddWithValue("$session", sessionId);
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MigrationCanOpenTheSameDatabaseMoreThanOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using (new ControllerDatabase(path)) { }
            using (var database = new ControllerDatabase(path))
                Assert.Empty(database.List());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.8")]
    [InlineData("203.0.113.4")]
    [InlineData("127.0.0.1")]
    [InlineData("fe80::1")]
    public void VncTargetMustBeAdminConfirmedPrivateAddress(string address)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var database = new ControllerDatabase(path);
            using var key = ECDsa.Create();
            database.EnrollWithTicket(new EnrollmentRequest("pc", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), address, "001122334455", 47989, 47990));
            database.Sync("pc", new SyncRequest("boot", address, "001122334455", 1, "notReady", []));
            Assert.Throws<ArgumentException>(() => database.Update("pc", new MachineUpdate("PC", 100, 47989, 47990) { VncLocalIp = address }, 1));
            database.Update("pc", new MachineUpdate("PC", 100, 47989, 47990), 1);
            database.ApproveVerified("pc", "admin-1", database.List().Single().PublicKeyFingerprint!);

            Assert.Throws<UnauthorizedAccessException>(() => database.CreateAdminVncTicket("pc", "admin-1", "http://localhost:3000"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AdministratorCanConfirmAnIpv6UniqueLocalTarget()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var database = new ControllerDatabase(path);
            using var key = ECDsa.Create();
            database.EnrollWithTicket(new EnrollmentRequest("pc", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "fd00::4", "001122334455", 47989, 47990));
            database.Sync("pc", new SyncRequest("boot", "fd00::4", "001122334455", 1, "notReady", []));
            database.Update("pc", new MachineUpdate("PC", 100, 47989, 47990) { VncLocalIp = "fd00::8" }, 1);
            database.ApproveVerified("pc", "admin-1", database.List().Single().PublicKeyFingerprint!);

            var ticket = database.CreateAdminVncTicket("pc", "admin-1", "https://shop.example");
            var target = database.ConsumeAdminVncTicket(ticket.Ticket, "https://shop.example");
            Assert.Equal("fd00::8", target.Address.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
