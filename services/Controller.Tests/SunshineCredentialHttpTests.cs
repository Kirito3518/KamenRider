using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KamenRaider.Contracts;
using KamenRaider.Controller;
using KamenRaider.Controller.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Controller.Tests;

public class SunshineCredentialHttpTests
{
    private const string FetchPath = "/agent/sunshine-credential";
    private const int InternalPort = 5201;
    private const int AgentPort = 5202;
    private const string ServiceCredential = "secret";

    private static string NewDatabasePath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static WebApplication Build(string path, string? credentialKey) => ControllerHost.Build([], builder =>
    {
        var settings = new Dictionary<string, string?>
        {
            ["InternalUrl"] = "http://127.0.0.1:" + InternalPort,
            ["AgentUrl"] = "https://127.0.0.1:" + AgentPort,
            ["DatabasePath"] = path,
            ["ServiceCredential"] = ServiceCredential,
        };
        if (credentialKey is not null) settings["SunshineCredentialKey"] = credentialKey;
        builder.Configuration.AddInMemoryCollection(settings);
        builder.WebHost.UseTestServer();
        builder.Services.AddTransient<IStartupFilter, LoopbackTestFilter>();
    });

    private static HttpClient InternalClient(WebApplication app, bool withActor = true)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Local-Port", InternalPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        client.DefaultRequestHeaders.Add("X-Service-Credential", ServiceCredential);
        if (withActor) client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "admin-user");
        return client;
    }

    private static HttpClient AgentClient(WebApplication app)
    {
        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");
        client.DefaultRequestHeaders.Add("Test-Local-Port", AgentPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return client;
    }

    private static void Enroll(ControllerDatabase database, ECDsa key, string machineId)
    {
        database.EnrollWithTicket(new EnrollmentRequest(machineId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            "192.0.2.1", "001122334455", 47989, 47990)
        {
            Hardware = new HardwareInfo("HOST-" + machineId, "CPU", "GPU", 8L << 30),
        });
    }

    private static void EnrollApproved(ControllerDatabase database, ECDsa key, string machineId = "pc")
    {
        Enroll(database, key, machineId);
        database.Update(machineId, new MachineUpdate("Gaming PC", 2500, 47989, 47990), 1);
        database.Approve(machineId, "admin");
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static Task<HttpResponseMessage> SendFetch(HttpClient client, ECDsa key, string machineId, long knownRevision)
        => SendFetch(client, key, machineId, JsonSerializer.Serialize(new SunshineCredentialFetchRequest(knownRevision), WebJson));

    private static async Task<HttpResponseMessage> SendFetch(HttpClient client, ECDsa key, string machineId, string body,
        string? signatureOverride = null)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Guid.NewGuid().ToString("N");
        var canonical = Encoding.UTF8.GetBytes($"{machineId}\nPOST\n{FetchPath}\n{timestamp}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}");
        var signature = signatureOverride ?? Convert.ToBase64String(key.SignData(canonical, HashAlgorithmName.SHA256));
        using var request = new HttpRequestMessage(HttpMethod.Post, FetchPath)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Agent-Id", machineId);
        request.Headers.Add("X-Agent-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Agent-Nonce", nonce);
        request.Headers.Add("X-Agent-Signature", signature);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task AdminGetAndPutManageTheFleetCredentialWithoutExposingThePassword()
    {
        var path = NewDatabasePath();
        try
        {
            await using var app = Build(path, NewKey());
            await app.StartAsync();

            using var client = InternalClient(app, withActor: false);
            using (var get = await client.GetAsync("/internal/sunshine-credential"))
            {
                Assert.Equal(HttpStatusCode.OK, get.StatusCode);
                var state = await get.Content.ReadFromJsonAsync<SunshineCredentialState>();
                Assert.False(state!.Configured);
                Assert.Equal(0, state.Revision);
            }

            using (var anonymousWrite = await client.PutAsJsonAsync("/internal/sunshine-credential",
                new SunshineCredentialUpdate("sunshine-admin", "Fleet-Secret!")))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymousWrite.StatusCode);

            client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "admin-user");
            using (var put = await client.PutAsJsonAsync("/internal/sunshine-credential",
                new SunshineCredentialUpdate("  sunshine-admin  ", "Fleet-Secret!")))
            {
                Assert.Equal(HttpStatusCode.OK, put.StatusCode);
                var state = await put.Content.ReadFromJsonAsync<SunshineCredentialState>();
                Assert.True(state!.Configured);
                Assert.Equal("sunshine-admin", state.UserName);
                Assert.Equal(1, state.Revision);
                Assert.NotNull(state.UpdatedAtUtc);
            }

            using (var rotated = await client.PutAsJsonAsync("/internal/sunshine-credential",
                new SunshineCredentialUpdate("sunshine-admin", "Rotated-Fleet-Secret!")))
            {
                Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
                Assert.Equal(2, (await rotated.Content.ReadFromJsonAsync<SunshineCredentialState>())!.Revision);
            }

            using (var get = await client.GetAsync("/internal/sunshine-credential"))
            {
                Assert.Equal(HttpStatusCode.OK, get.StatusCode);
                Assert.True(get.Headers.CacheControl?.NoStore);
                var body = await get.Content.ReadAsStringAsync();
                Assert.DoesNotContain("Fleet-Secret!", body, StringComparison.Ordinal);
                Assert.DoesNotContain("Rotated-Fleet-Secret!", body, StringComparison.Ordinal);
                Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
                var state = JsonSerializer.Deserialize<SunshineCredentialState>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Assert.Equal("sunshine-admin", state!.UserName);
                Assert.Equal(2, state.Revision);
            }

            // The internal listener requires the service credential before routing anything.
            using var unauthenticated = app.GetTestClient();
            unauthenticated.DefaultRequestHeaders.Add("Test-Local-Port", InternalPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using (var denied = await unauthenticated.GetAsync("/internal/sunshine-credential"))
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

            await app.StopAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AdminPutFailsClosedWhenCredentialStorageIsNotConfigured()
    {
        var path = NewDatabasePath();
        try
        {
            await using var app = Build(path, credentialKey: null);
            await app.StartAsync();
            using var client = InternalClient(app);
            using var put = await client.PutAsJsonAsync("/internal/sunshine-credential",
                new SunshineCredentialUpdate("sunshine-admin", "Fleet-Secret!"));
            Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
            using var get = await client.GetAsync("/internal/sunshine-credential");
            Assert.False((await get.Content.ReadFromJsonAsync<SunshineCredentialState>())!.Configured);
            await app.StopAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SignedAgentFetchDeliversOnlyWhenBehindAndOnlyToApprovedMachines()
    {
        var path = NewDatabasePath();
        try
        {
            await using var app = Build(path, NewKey());
            await app.StartAsync();
            var database = app.Services.GetRequiredService<ControllerDatabase>();
            using var key = ECDsa.Create();
            EnrollApproved(database, key);
            using var agent = AgentClient(app);

            // No fleet credential configured yet.
            using (var unconfigured = await SendFetch(agent, key, "pc", 0))
                Assert.Equal(HttpStatusCode.NotFound, unconfigured.StatusCode);

            using var admin = InternalClient(app);
            using (var put = await admin.PutAsJsonAsync("/internal/sunshine-credential",
                new SunshineCredentialUpdate("sunshine-admin", "Fleet-Secret!")))
                Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            using (var delivered = await SendFetch(agent, key, "pc", 0))
            {
                Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
                Assert.True(delivered.Headers.CacheControl?.NoStore);
                var delivery = await delivered.Content.ReadFromJsonAsync<SunshineCredentialDelivery>();
                Assert.Equal("sunshine-admin", delivery!.UserName);
                Assert.Equal("Fleet-Secret!", delivery.Password);
                Assert.Equal(1, delivery.Revision);
            }

            using (var current = await SendFetch(agent, key, "pc", 1))
                Assert.Equal(HttpStatusCode.NoContent, current.StatusCode);

            using (var badSignature = await SendFetch(agent, key, "pc",
                JsonSerializer.Serialize(new SunshineCredentialFetchRequest(0), WebJson),
                signatureOverride: Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))))
                Assert.Equal(HttpStatusCode.Unauthorized, badSignature.StatusCode);

            using var pendingKey = ECDsa.Create();
            Enroll(database, pendingKey, "pending-pc");
            using (var unapproved = await SendFetch(agent, pendingKey, "pending-pc", 0))
            {
                Assert.Equal(HttpStatusCode.Conflict, unapproved.StatusCode);
                Assert.DoesNotContain("Fleet-Secret!", await unapproved.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var malformed = await SendFetch(agent, key, "pc", body: "{"))
                Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

            await app.StopAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
