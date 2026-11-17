using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using System.Security.Cryptography;

namespace Controller.Tests;

public class MachineProfileTests
{
    private static string NewDatabasePath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");

    private static EnrollmentRequest Enrollment(ECDsa key, string machineId = "pc") => new(
        machineId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "192.0.2.1", "001122334455", 47989, 47990)
    {
        Hardware = new HardwareInfo("DESKTOP-7F3K", "AMD Ryzen 9 7950X", "NVIDIA GeForce RTX 5080", 32L << 30),
    };

    [Fact]
    public void EnrollmentStoresHardwareAndUsesHostnameAsInitialDisplayName()
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path);
            using var key = ECDsa.Create();
            Assert.Equal("pending", db.EnrollWithTicket(Enrollment(key)).ApprovalStatus);

            var machine = Assert.Single(db.List());
            Assert.Equal("DESKTOP-7F3K", machine.Hostname);
            Assert.Equal("AMD Ryzen 9 7950X", machine.Cpu);
            Assert.Equal("NVIDIA GeForce RTX 5080", machine.Gpu);
            Assert.Equal(32L << 30, machine.RamBytes);
            Assert.Equal("DESKTOP-7F3K", machine.DisplayName);
            Assert.Equal(0, machine.AppliedSunshineCredentialRevision);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EnrollmentTrimsReportedTextAndAcceptsMissingOptionalFields()
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path);
            using var key = ECDsa.Create();
            var request = Enrollment(key) with
            {
                Hardware = new HardwareInfo("  trimmed-host  ", "  ", null!, 0),
            };
            db.EnrollWithTicket(request);

            var machine = Assert.Single(db.List());
            Assert.Equal("trimmed-host", machine.Hostname);
            Assert.Null(machine.Cpu);
            Assert.Null(machine.Gpu);
            Assert.Equal(0, machine.RamBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReenrollmentRefreshesHardwareButNeverRewritesAdministratorDisplayName()
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path);
            using var key = ECDsa.Create();
            db.EnrollWithTicket(Enrollment(key));
            db.Update("pc", new MachineUpdate("Gaming Rig", 2500, 47989, 47990), 1);

            var refreshed = Enrollment(key) with
            {
                Hardware = new HardwareInfo("DESKTOP-NEW", "AMD Ryzen 9 9950X", "NVIDIA GeForce RTX 5090", 64L << 30),
            };
            Assert.Equal("pending", db.Enroll(refreshed).ApprovalStatus);

            var machine = Assert.Single(db.List());
            Assert.Equal("Gaming Rig", machine.DisplayName);
            Assert.Equal(2500, machine.HourlyPriceMinorUnits);
            Assert.Equal("DESKTOP-NEW", machine.Hostname);
            Assert.Equal("AMD Ryzen 9 9950X", machine.Cpu);
            Assert.Equal("NVIDIA GeForce RTX 5090", machine.Gpu);
            Assert.Equal(64L << 30, machine.RamBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReenrollmentWithoutHardwareKeepsPreviouslyReportedValues()
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path);
            using var key = ECDsa.Create();
            db.EnrollWithTicket(Enrollment(key));
            db.Enroll(Enrollment(key) with { Hardware = null });

            var machine = Assert.Single(db.List());
            Assert.Equal("DESKTOP-7F3K", machine.Hostname);
            Assert.Equal("AMD Ryzen 9 7950X", machine.Cpu);
            Assert.Equal(32L << 30, machine.RamBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0, 3, 3, 1L)]
    [InlineData(65, 3, 3, 1L)]
    [InlineData(8, 129, 3, 1L)]
    [InlineData(8, 3, 257, 1L)]
    [InlineData(8, 3, 3, -1L)]
    [InlineData(8, 3, 3, 1099511627777L)]
    public void InvalidReportedHardwareIsRejectedWithoutCreatingTheMachine(int hostnameLength, int cpuLength, int gpuLength, long ramBytes)
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path);
            using var key = ECDsa.Create();
            var request = Enrollment(key) with
            {
                Hardware = new HardwareInfo(new string('h', hostnameLength), new string('c', cpuLength), new string('g', gpuLength), ramBytes),
            };
            Assert.Throws<ArgumentException>(() => db.EnrollWithTicket(request));
            Assert.Empty(db.List());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("bad\thostname")]
    public void BlankOrControlCharacterHostnameIsRejected(string hostname)
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path);
            using var key = ECDsa.Create();
            var request = Enrollment(key) with { Hardware = new HardwareInfo(hostname, "cpu", "gpu", 1) };
            Assert.Throws<ArgumentException>(() => db.EnrollWithTicket(request));
            Assert.Empty(db.List());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SyncRefreshesHardwareAndAppliedCredentialRevision()
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path);
            using var key = ECDsa.Create();
            db.EnrollWithTicket(Enrollment(key) with { Hardware = null });
            Assert.Null(Assert.Single(db.List()).Hostname);

            var response = db.Sync("pc", new SyncRequest("boot", "192.0.2.2", "001122334455", 1, "unknown", [])
            {
                Hardware = new HardwareInfo("SYNC-HOST", "Intel Core i9", "AMD Radeon RX 7900", 16L << 30),
                AppliedSunshineCredentialRevision = 3,
            });
            Assert.Equal(0, response.SunshineCredentialRevision);

            var machine = Assert.Single(db.List());
            Assert.Equal("SYNC-HOST", machine.Hostname);
            Assert.Equal("Intel Core i9", machine.Cpu);
            Assert.Equal("AMD Radeon RX 7900", machine.Gpu);
            Assert.Equal(16L << 30, machine.RamBytes);
            Assert.Equal(3, machine.AppliedSunshineCredentialRevision);

            // A sync without a profile (or applied revision) must not erase stored values.
            db.Sync("pc", new SyncRequest("boot", "192.0.2.2", "001122334455", 1, "unknown", []));
            machine = Assert.Single(db.List());
            Assert.Equal("SYNC-HOST", machine.Hostname);
            Assert.Equal(3, machine.AppliedSunshineCredentialRevision);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("host\tname")]
    public void SyncRejectsMalformedReportedHardwareWithoutTouchingState(string hostname)
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path);
            using var key = ECDsa.Create();
            db.EnrollWithTicket(Enrollment(key));
            var request = new SyncRequest("boot", "192.0.2.2", "001122334455", 1, "unknown", [])
            {
                Hardware = new HardwareInfo(hostname, "cpu", "gpu", 1),
            };
            Assert.Throws<ArgumentException>(() => db.Sync("pc", request));
            Assert.Equal("DESKTOP-7F3K", Assert.Single(db.List()).Hostname);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SyncRejectsANegativeAppliedCredentialRevision()
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path);
            using var key = ECDsa.Create();
            db.EnrollWithTicket(Enrollment(key));
            var request = new SyncRequest("boot", "192.0.2.2", "001122334455", 1, "unknown", [])
            {
                AppliedSunshineCredentialRevision = -1,
            };
            Assert.Throws<ArgumentException>(() => db.Sync("pc", request));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
