using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.RemoteDesktop;
using KamenRaider.Controller;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Controller.Tests;

public class AdminVncHttpTests
{
    [Fact]
    public async Task TargetProbeUsesOnlyThePrivateIpAndDoesNotPersistOrCreateASession()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var probe = new RecordingVncTargetProbe();
        try
        {
            await using var app = ControllerHost.Build([], builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["InternalUrl"] = "http://127.0.0.1:5101",
                    ["AgentUrl"] = "https://127.0.0.1:5102",
                    ["DatabasePath"] = path,
                    ["ServiceCredential"] = "test-credential"
                });
                builder.WebHost.UseTestServer();
                builder.Services.AddTransient<IStartupFilter, LoopbackTestFilter>();
                builder.Services.AddSingleton<IAdminVncTargetProbe>(probe);
            });
            await app.StartAsync();
            using var client = app.GetTestClient();
            client.BaseAddress = new Uri("http://localhost");
            client.DefaultRequestHeaders.Add("Test-Local-Port", "5101");
            client.DefaultRequestHeaders.Add("X-Service-Credential", "test-credential");
            client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "admin-1");
            client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role", "admin");

            var database = app.Services.GetRequiredService<ControllerDatabase>();
            using var key = ECDsa.Create();
            database.EnrollWithTicket(new EnrollmentRequest("pc", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "10.0.0.4", "001122334455", 47989, 47990));
            database.Sync("pc", new SyncRequest("boot", "10.0.0.4", "001122334455", 1, "notReady", []));
            var before = database.List().Single();

            using var response = await client.PostAsync(
                "/internal/admin-vnc-target-probes",
                new StringContent(
                    "{\"machineId\":\"pc\",\"targetAddress\":\"10.0.0.18\",\"host\":\"vnc.example.com\",\"port\":22}",
                    Encoding.UTF8,
                    "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<Dictionary<string, bool>>();
            Assert.NotNull(result);
            Assert.True(result!["supported"]);
            Assert.Equal(IPAddress.Parse("10.0.0.18"), probe.Address);
            var after = database.List().Single();
            Assert.Equal(before.ConfigRevision, after.ConfigRevision);
            Assert.Equal(before.VncLocalIp, after.VncLocalIp);
            Assert.Equal(0, after.ActiveAdminVncSessions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TargetProbeEndpointRequiresAdminAndRejectsNonPrivateAddresses()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            await using var app = ControllerHost.Build([], builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["InternalUrl"] = "http://127.0.0.1:5101",
                    ["AgentUrl"] = "https://127.0.0.1:5102",
                    ["DatabasePath"] = path,
                    ["ServiceCredential"] = "test-credential"
                });
                builder.WebHost.UseTestServer();
                builder.Services.AddTransient<IStartupFilter, LoopbackTestFilter>();
            });
            await app.StartAsync();
            using var client = app.GetTestClient();
            client.BaseAddress = new Uri("http://localhost");
            client.DefaultRequestHeaders.Add("Test-Local-Port", "5101");
            var body = new StringContent(
                "{\"machineId\":\"pc\",\"targetAddress\":\"203.0.113.8\"}",
                Encoding.UTF8,
                "application/json");

            using (var response = await client.PostAsync("/internal/admin-vnc-target-probes", body))
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            client.DefaultRequestHeaders.Add("X-Service-Credential", "test-credential");
            using (var response = await client.PostAsync("/internal/admin-vnc-target-probes", body))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "admin-1");
            client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role", "admin");
            using (var response = await client.PostAsync("/internal/admin-vnc-target-probes", body))
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class RecordingVncTargetProbe : IAdminVncTargetProbe
    {
        public IPAddress? Address { get; private set; }

        public Task<bool> SupportsReadOnlyGatewayAsync(IPAddress address, CancellationToken cancellationToken)
        {
            Address = address;
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task TicketEndpointRequiresInternalCredentialAndAdminActor()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            await using var app = ControllerHost.Build([], builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["InternalUrl"] = "http://127.0.0.1:5101",
                    ["AgentUrl"] = "https://127.0.0.1:5102",
                    ["DatabasePath"] = path,
                    ["ServiceCredential"] = "test-credential"
                });
                builder.WebHost.UseTestServer();
                builder.Services.AddTransient<IStartupFilter, LoopbackTestFilter>();
            });
            await app.StartAsync();
            using var client = app.GetTestClient();
            client.BaseAddress = new Uri("http://localhost");
            client.DefaultRequestHeaders.Add("Test-Local-Port", "5101");
            var body = new CreateAdminVncTicketRequest("pc", "http://localhost:3000");
            var json = System.Text.Json.JsonSerializer.Serialize(body);

            using (var response = await client.PostAsync("/internal/admin-vnc-tickets", new StringContent(json, Encoding.UTF8, "application/json")))
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            client.DefaultRequestHeaders.Add("X-Service-Credential", "test-credential");
            using (var response = await client.PostAsync("/internal/admin-vnc-tickets", new StringContent(json, Encoding.UTF8, "application/json")))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "admin-1");
            client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role", "admin");
            using (var response = await client.PostAsync("/internal/admin-vnc-tickets", new StringContent(json, Encoding.UTF8, "application/json")))
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

            var database = app.Services.GetRequiredService<ControllerDatabase>();
            using var key = ECDsa.Create();
            database.EnrollWithTicket(new EnrollmentRequest("pc", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "10.0.0.4", "001122334455", 47989, 47990));
            database.Sync("pc", new SyncRequest("boot", "10.0.0.9", "001122334455", 1, "notReady", []));
            database.Update("pc", new MachineUpdate("PC", 100, 47989, 47990) { VncLocalIp = "10.0.0.8" }, 1);
            database.ApproveVerified("pc", "admin-1", database.List().Single().PublicKeyFingerprint!);

            using (var response = await client.PostAsync("/internal/admin-vnc-tickets", new StringContent(json, Encoding.UTF8, "application/json")))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var issued = await response.Content.ReadFromJsonAsync<AdminVncTicketResponse>();
                Assert.NotNull(issued);
                Assert.Matches("^[A-Za-z0-9_-]{43}$", issued!.Ticket);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
