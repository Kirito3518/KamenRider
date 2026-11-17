using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Controller.Tests;

public class SunshineCredentialTests
{
    private const string FetchPath = "/agent/sunshine-credential";

    private static string NewDatabasePath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");

    internal static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static void Enroll(ControllerDatabase db, string machineId = "pc")
    {
        using var key = ECDsa.Create();
        db.EnrollWithTicket(new EnrollmentRequest(machineId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            "192.0.2.1", "001122334455", 47989, 47990)
        {
            Hardware = new HardwareInfo("HOST-" + machineId, "CPU", "GPU", 8L << 30),
        });
    }

    private static void EnrollApproved(ControllerDatabase db, string machineId = "pc")
    {
        Enroll(db, machineId);
        db.Update(machineId, new MachineUpdate("Gaming PC", 2500, 47989, 47990), 1);
        db.Approve(machineId, "admin");
    }

    private static SunshineCredentialDelivery? Fetch(ControllerDatabase db, string machineId, long knownRevision)
    {
        var request = new SunshineCredentialFetchRequest(knownRevision);
        var verified = VerifiedAgentRequestFixture.Create(db, machineId, FetchPath, request);
        return db.FetchSunshineCredential(verified, request);
    }

    [Fact]
    public void VaultRoundtripEncryptsPasswordAndAdvancesRevision()
    {
        var path = NewDatabasePath();
        var key = NewKey();
        try
        {
            using (var db = new ControllerDatabase(path, sunshineCredentialKey: key))
            {
                EnrollApproved(db);
                var initial = db.GetSunshineCredentialState();
                Assert.False(initial.Configured);
                Assert.Equal(0, initial.Revision);
                Assert.Null(initial.UserName);
                Assert.Null(initial.UpdatedAtUtc);

                var first = db.SetSunshineCredential(" sunshine-admin ", "Passw0rd!rotate", "admin-user");
                Assert.True(first.Configured);
                Assert.Equal("sunshine-admin", first.UserName);
                Assert.Equal(1, first.Revision);
                Assert.NotNull(first.UpdatedAtUtc);

                var delivery = Fetch(db, "pc", 0);
                Assert.NotNull(delivery);
                Assert.Equal("sunshine-admin", delivery!.UserName);
                Assert.Equal("Passw0rd!rotate", delivery.Password);
                Assert.Equal(1, delivery.Revision);

                var second = db.SetSunshineCredential("sunshine-admin", "N3w-Fleet-Secret!", "admin-user");
                Assert.Equal(2, second.Revision);
                Assert.Equal(2, db.GetSunshineCredentialState().Revision);

                var rotated = Fetch(db, "pc", 1);
                Assert.NotNull(rotated);
                Assert.Equal("N3w-Fleet-Secret!", rotated!.Password);
                Assert.Equal(2, rotated.Revision);
            }

            foreach (var file in DatabaseFiles(path))
            {
                var store = File.ReadAllBytes(file);
                Assert.False(Contains(store, Encoding.UTF8.GetBytes("Passw0rd!rotate")), "A stale plaintext password was persisted.");
                Assert.False(Contains(store, Encoding.UTF8.GetBytes("N3w-Fleet-Secret!")), "The current plaintext password was persisted.");
                Assert.False(Contains(store, Encoding.UTF8.GetBytes(key)), "The encryption key must never be persisted.");
            }
        }
        finally
        {
            foreach (var file in DatabaseFiles(path)) File.Delete(file);
        }
    }

    [Fact]
    public void KnownRevisionReturnsNothingWhileAStaleRevisionReceivesTheDelivery()
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path, sunshineCredentialKey: NewKey());
            EnrollApproved(db);
            db.SetSunshineCredential("sunshine-admin", "Fleet-Secret!", "admin-user");

            Assert.Null(Fetch(db, "pc", 1));
            var delivery = Fetch(db, "pc", 0);
            Assert.NotNull(delivery);
            Assert.Equal(1, delivery!.Revision);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnconfiguredCredentialIsReportedAsNothingToDeliver()
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path, sunshineCredentialKey: NewKey());
            EnrollApproved(db);
            Assert.Null(Fetch(db, "pc", 0));
            var state = db.GetSunshineCredentialState();
            Assert.False(state.Configured);
            Assert.Equal(0, state.Revision);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AdminMetadataNeverExposesThePassword()
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path, sunshineCredentialKey: NewKey());
            EnrollApproved(db);
            db.SetSunshineCredential("sunshine-admin", "Top-Secret-Pass!", "admin-user");

            var json = JsonSerializer.Serialize(db.GetSunshineCredentialState(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.DoesNotContain("Top-Secret-Pass!", json, StringComparison.Ordinal);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ciphertext", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SettingCredentialWithoutAConfiguredKeyFailsClosed()
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path);
            var error = Assert.Throws<InvalidOperationException>(() => db.SetSunshineCredential("sunshine-admin", "Fleet-Secret!", "admin-user"));
            Assert.Contains("not configured", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(db.GetSunshineCredentialState().Configured);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MalformedConfiguredKeyFailsClosed()
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path, sunshineCredentialKey: "not-a-valid-key");
            Assert.Throws<InvalidOperationException>(() => db.SetSunshineCredential("sunshine-admin", "Fleet-Secret!", "admin-user"));
            Assert.False(db.GetSunshineCredentialState().Configured);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WrongEncryptionKeyFailsClosedWithoutReturningPlaintext()
    {
        var path = NewDatabasePath();
        try
        {
            using (var db = new ControllerDatabase(path, sunshineCredentialKey: NewKey()))
            {
                EnrollApproved(db);
                db.SetSunshineCredential("sunshine-admin", "Wrong-Key-Secret!", "admin-user");
            }

            var wrongKey = NewKey();
            using (var db = new ControllerDatabase(path, sunshineCredentialKey: wrongKey))
            {
                var error = Assert.Throws<InvalidOperationException>(() => Fetch(db, "pc", 0));
                Assert.DoesNotContain("Wrong-Key-Secret!", error.Message, StringComparison.Ordinal);
            }
            using (var db = new ControllerDatabase(path))
            {
                Assert.Throws<InvalidOperationException>(() => Fetch(db, "pc", 0));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnapprovedMachineCannotFetchTheFleetCredential()
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path, sunshineCredentialKey: NewKey());
            Enroll(db, "pending-pc");
            db.SetSunshineCredential("sunshine-admin", "Fleet-Secret!", "admin-user");
            Assert.Throws<InvalidOperationException>(() => Fetch(db, "pending-pc", 0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RestoreQuarantineBlocksCredentialDelivery()
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path, sunshineCredentialKey: NewKey());
            EnrollApproved(db);
            db.SetSunshineCredential("sunshine-admin", "Fleet-Secret!", "admin-user");
            db.ActivateRestoreQuarantine("test-quarantine");
            Assert.Throws<InvalidOperationException>(() => Fetch(db, "pc", 0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SignedRequestMustUseTheExactCredentialPathMethodAndNonce()
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path, sunshineCredentialKey: NewKey());
            EnrollApproved(db);
            db.SetSunshineCredential("sunshine-admin", "Fleet-Secret!", "admin-user");
            var request = new SunshineCredentialFetchRequest(0);
            var publicKey = db.PublicKey("pc")!;
            var body = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var wrongPath = VerifiedAgentRequest.CreateForTest("pc", publicKey, "POST", "/agent/sync",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Guid.NewGuid().ToString("N"), body);
            Assert.Throws<UnauthorizedAccessException>(() => db.FetchSunshineCredential(wrongPath, request));

            var wrongMethod = VerifiedAgentRequest.CreateForTest("pc", publicKey, "GET", FetchPath,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Guid.NewGuid().ToString("N"), body);
            Assert.Throws<UnauthorizedAccessException>(() => db.FetchSunshineCredential(wrongMethod, request));

            var nonce = Guid.NewGuid().ToString("N");
            var replayable = VerifiedAgentRequestFixture.CreateWithNonce(db, "pc", FetchPath, request, nonce);
            Assert.NotNull(db.FetchSunshineCredential(replayable, request));
            Assert.Throws<UnauthorizedAccessException>(() => db.FetchSunshineCredential(replayable, request));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SyncResponseReportsTheCurrentFleetCredentialRevision()
    {
        var path = NewDatabasePath();
        try
        {
            using var db = new ControllerDatabase(path, sunshineCredentialKey: NewKey());
            Enroll(db);
            db.SetSunshineCredential("sunshine-admin", "Fleet-Secret!", "admin-user");
            db.SetSunshineCredential("sunshine-admin", "Fleet-Secret-2!", "admin-user");

            var response = db.Sync("pc", new SyncRequest("boot", "192.0.2.2", "001122334455", 1, "unknown", [])
            {
                AppliedSunshineCredentialRevision = 1,
            });
            Assert.Equal(2, response.SunshineCredentialRevision);
            Assert.Equal(1, Assert.Single(db.List()).AppliedSunshineCredentialRevision);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string[] DatabaseFiles(string path) => new[] { path, path + "-wal", path + "-shm", path + "-journal" }
        .Where(File.Exists).ToArray();

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            var match = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (haystack[index + offset] != needle[offset]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }
}
