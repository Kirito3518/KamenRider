using System.Net;
using System.Text;
using System.Text.Json;
using KamenRaider.Agent.Enrollment;
using KamenRaider.Agent.Identity;
using KamenRaider.Contracts;
using KamenRaider.Controller.Security;

namespace Agent.Tests;

public class EnrollmentHardwareTests
{
    [Fact]
    public async Task EnrollmentRequestCarriesHardwareProfileAndRemainsSigned()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kr-enrollment-hardware-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (!OperatingSystem.IsWindows() || !WindowsAclProbe.CanSetProtectedAcl) return;
            using var identity = DeviceIdentity.LoadOrCreate(directory);
            var hardware = new HardwareInfo("PC-LAB-01", "Fake CPU 9000 (16 logical processors)", "Fake GPU; Second GPU", 17179869184);
            using var handler = new RecordingHandler(identity);
            using var http = new HttpClient(handler);
            var client = new EnrollmentClient(http, identity);

            var result = await client.EnrollAsync(
                new Uri("https://controller:5102"), "192.0.2.1", "001122334455", 47989, 47990,
                new string('A', 43), hardware, CancellationToken.None);

            Assert.True(handler.SignatureVerified);
            Assert.Equal(identity.MachineId, result.MachineId);
            var body = Assert.IsType<JsonElement>(handler.RequestBody);
            var reported = body.GetProperty("hardware");
            Assert.Equal("PC-LAB-01", reported.GetProperty("hostname").GetString());
            Assert.Equal("Fake CPU 9000 (16 logical processors)", reported.GetProperty("cpu").GetString());
            Assert.Equal("Fake GPU; Second GPU", reported.GetProperty("gpu").GetString());
            Assert.Equal(17179869184, reported.GetProperty("ramBytes").GetInt64());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EnrollmentWithoutCollectedHardwareOmitsTheProfile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kr-enrollment-nohardware-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (!OperatingSystem.IsWindows() || !WindowsAclProbe.CanSetProtectedAcl) return;
            using var identity = DeviceIdentity.LoadOrCreate(directory);
            using var handler = new RecordingHandler(identity);
            using var http = new HttpClient(handler);
            var client = new EnrollmentClient(http, identity);

            await client.EnrollAsync(
                new Uri("https://controller:5102"), "192.0.2.1", "001122334455", 47989, 47990,
                null, null, CancellationToken.None);

            var body = Assert.IsType<JsonElement>(handler.RequestBody);
            Assert.False(body.TryGetProperty("hardware", out var hardware) && hardware.ValueKind != JsonValueKind.Null);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingHandler(DeviceIdentity identity) : HttpMessageHandler
    {
        public bool SignatureVerified { get; private set; }
        public JsonElement? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var timestamp = request.Headers.GetValues("X-Agent-Timestamp").Single();
            var nonce = request.Headers.GetValues("X-Agent-Nonce").Single();
            SignatureVerified = new AgentAuthentication().VerifySignature(
                identity.MachineId, identity.PublicKey, "POST", "/agent/enroll", timestamp, nonce,
                Encoding.UTF8.GetBytes(body), request.Headers.GetValues("X-Agent-Signature").Single()) is not null;
            using var json = JsonDocument.Parse(body);
            RequestBody = json.RootElement.Clone();
            return new HttpResponseMessage(SignatureVerified ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(JsonSerializer.Serialize(new EnrollmentResult(identity.MachineId, "pending")))
            };
        }
    }
}
