using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Controller.Tests.Fixtures;
using KamenRaider.Contracts;
using KamenRaider.Controller;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Controller.Tests;

// Real Controller HTTP, signatures, SQLite, ZIP validation and artifact IO. The updater
// peer supplies simulated service outcomes; no PowerShell, SCM, TLS socket or hardware runs here.
public sealed class AgentReleaseAcceptanceTests
{
    [Theory]
    [InlineData("awaitingHeartbeat", "2.3.4")]
    [InlineData("rollingBack", "1.2.3")]
    public async Task SignedHeartbeatPreparedBeforeOutcomeCannotConfirmIt(string outcome, string version)
    {
        await using var fixture = await AcceptanceFixture.CreateAsync();
        var ticket = await fixture.PublishAndReserveAsync();
        await fixture.StartAndDownloadAsync(ticket);
        using var delayed = await fixture.PrepareHeartbeatAsync(version);
        await fixture.ReportAsync(ticket, outcome);

        using var response = await fixture.SendPreparedHeartbeatAsync(delayed);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await fixture.AssertLockedAsync(ticket, outcome);
    }
    [Theory]
    [InlineData("awaitingHeartbeat", "2.3.4", "completed")]
    [InlineData("rollingBack", "1.2.3", "rolledBack")]
    public async Task PublishedReleaseRemainsLockedUntilPostReportHeartbeatAndIndependentRentalGatesPass(
        string updaterOutcome, string confirmedVersion, string terminalStatus)
    {
        await using var fixture = await AcceptanceFixture.CreateAsync();
        var ticket = await fixture.PublishAndReserveAsync();
        await fixture.StartAndDownloadAsync(ticket);

        // An Agent can heartbeat while service startup/rollback is still in progress.
        await fixture.HeartbeatAsync("2.3.4");
        await fixture.AssertLockedAsync(ticket, "installing");
        await fixture.HeartbeatAsync("1.2.3");
        await fixture.AssertLockedAsync(ticket, "installing");

        var acknowledgedRevision = (await fixture.MachineAsync()).ConfigRevision;
        await fixture.ConfigureAsync();
        await fixture.ReportAsync(ticket, updaterOutcome);
        await fixture.AssertLockedAsync(ticket, updaterOutcome);

        if (updaterOutcome == "rollingBack")
        {
            await fixture.HeartbeatAsync("2.3.4");
            await fixture.AssertLockedAsync(ticket, "rollingBack");
            await fixture.HeartbeatAsync("0.9.0");
            await fixture.AssertLockedAsync(ticket, "rollingBack");
        }

        await fixture.HeartbeatAsync(confirmedVersion, "notReady", acknowledgedRevision);
        var confirmed = await fixture.MachineAsync();
        Assert.Equal(terminalStatus, confirmed.AgentUpdateStatus);
        Assert.Equal(ticket.OperationId, confirmed.AgentUpdateOperationId);
        Assert.Equal(confirmedVersion, confirmed.AgentVersion);
        Assert.Equal("approved", confirmed.ApprovalStatus);
        Assert.Equal("acceptance-admin", confirmed.ApprovedBy);
        Assert.Equal(acknowledgedRevision, confirmed.AppliedConfigRevision);
        await fixture.AssertRentalRejectedAsync();

        // Clearing maintenance is not a readiness/configuration acknowledgement.
        await fixture.HeartbeatAsync(confirmedVersion, "ready", acknowledgedRevision);
        Assert.Equal("ready", (await fixture.MachineAsync()).Readiness);
        await fixture.AssertRentalRejectedAsync();
        await fixture.HeartbeatAsync(confirmedVersion, "notReady");
        var unready = await fixture.MachineAsync();
        Assert.Equal(unready.ConfigRevision, unready.AppliedConfigRevision);
        await fixture.AssertRentalRejectedAsync();

        await fixture.HeartbeatAsync(confirmedVersion);
        Assert.True((await fixture.MachineAsync()).CanRent);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(21);
        await fixture.AssertRentalRejectedAsync();
        await fixture.HeartbeatAsync(confirmedVersion);
        await fixture.AssertRentalCreatedAsync();
    }

    [Fact]
    public async Task UncertainRollbackStaysLockedAcrossVersionsAndExpiryUntilSameOperationRecoveryIsConfirmed()
    {
        await using var fixture = await AcceptanceFixture.CreateAsync();
        var ticket = await fixture.PublishAndReserveAsync();
        await fixture.StartAndDownloadAsync(ticket);

        // Models an uncertain stop/restore/restart: the updater must report failed,
        // not claim restoration. PowerShell transaction suites exercise those OS boundaries.
        await fixture.ReportAsync(ticket, "failed");
        foreach (var version in new[] { "1.2.3", "2.3.4" })
        {
            await fixture.HeartbeatAsync(version);
            await fixture.AssertLockedAsync(ticket, "failed");
        }
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(11);
        await fixture.HeartbeatAsync("1.2.3");
        await fixture.AssertLockedAsync(ticket, "failed");
        using var unverified = await fixture.SendReportAsync(ticket, "rollingBack");
        Assert.Equal(HttpStatusCode.Conflict, unverified.StatusCode);
        using var secondReservation = await fixture.Admin.PostAsJsonAsync(
            "/internal/machines/acceptance-pc/agent-updates", new AgentUpdateReservationRequest("2.3.4"));
        Assert.Equal(HttpStatusCode.Conflict, secondReservation.StatusCode);

        using var recoveryResponse = await fixture.Admin.PostAsync(
            $"/internal/machines/acceptance-pc/agent-updates/{ticket.OperationId}/recovery-ticket", null);
        Assert.Equal(HttpStatusCode.OK, recoveryResponse.StatusCode);
        var recovery = Assert.IsType<AgentUpdateTicketResponse>(
            await recoveryResponse.Content.ReadFromJsonAsync<AgentUpdateTicketResponse>());
        Assert.Equal(ticket.OperationId, recovery.OperationId);
        Assert.Equal(ticket.MachineId, recovery.MachineId);
        Assert.Equal(ticket.ReleaseVersion, recovery.ReleaseVersion);
        Assert.Equal(ticket.TargetVersion, recovery.TargetVersion);
        Assert.Equal(ticket.TargetPackageSha256, recovery.TargetPackageSha256);
        Assert.NotEqual(ticket.Ticket, recovery.Ticket);
        await fixture.AssertLockedAsync(recovery, "failed");
        using var oldReport = await fixture.SendReportAsync(ticket, "failed");
        Assert.Equal(HttpStatusCode.Unauthorized, oldReport.StatusCode);

        await fixture.StartAndDownloadAsync(recovery);
        await fixture.HeartbeatAsync("1.2.3");
        await fixture.AssertLockedAsync(recovery, "installing");
        await fixture.ReportAsync(recovery, "rollingBack");
        await fixture.AssertLockedAsync(recovery, "rollingBack");
        await fixture.HeartbeatAsync("2.3.4");
        await fixture.AssertLockedAsync(recovery, "rollingBack");
        await fixture.HeartbeatAsync("1.2.3", "notReady");
        Assert.Equal("rolledBack", (await fixture.MachineAsync()).AgentUpdateStatus);
        await fixture.AssertRentalRejectedAsync();
        await fixture.HeartbeatAsync("1.2.3");
        await fixture.AssertRentalCreatedAsync();
    }

    private sealed class AcceptanceClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AcceptanceFixture : IAsyncDisposable
    {
        private const string MachineId = "acceptance-pc";
        private readonly string root;
        private readonly ECDsa key = ECDsa.Create();
        private readonly WebApplication app;
        private readonly HttpClient agent;
        private readonly HttpClient renter;
        private readonly byte[] package = CreatePackage();
        private int rentalAttempt;
        public AcceptanceClock Clock { get; }
        public HttpClient Admin { get; }

        private AcceptanceFixture(string root, AcceptanceClock clock, WebApplication app)
        {
            this.root = root;
            Clock = clock;
            this.app = app;
            Admin = app.GetTestClient();
            Admin.DefaultRequestHeaders.Add("X-Service-Credential", "acceptance-fixture-only");
            Admin.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "acceptance-admin");
            Admin.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role", "admin");
            renter = app.GetTestClient();
            renter.DefaultRequestHeaders.Add("X-Service-Credential", "acceptance-fixture-only");
            renter.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "acceptance-renter");
            agent = app.GetTestClient();
            agent.BaseAddress = new Uri("https://localhost/");
            agent.DefaultRequestHeaders.Add("Test-Local-Port", "5102");
        }

        public static async Task<AcceptanceFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "kr-release-acceptance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var clock = new AcceptanceClock();
            var app = ControllerHost.Build([], builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["InternalUrl"] = "http://127.0.0.1:5101",
                    ["AgentUrl"] = "https://127.0.0.1:5102",
                    ["DatabasePath"] = Path.Combine(root, "controller.db"),
                    ["ServiceCredential"] = "acceptance-fixture-only",
                    ["PublicIpEndpoint"] = "",
                });
                builder.WebHost.UseTestServer();
                builder.Services.AddTransient<IStartupFilter, LoopbackTestFilter>();
                builder.Services.AddSingleton<TimeProvider>(clock);
            });
            await app.StartAsync();
            return new AcceptanceFixture(root, clock, app);
        }

        public async Task<AgentUpdateTicketResponse> PublishAndReserveAsync()
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("2.3.4"), "version");
            form.Add(new StringContent("stable"), "channel");
            form.Add(new StringContent("Software acceptance fixture"), "releaseNotes");
            var content = new ByteArrayContent(package);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(content, "package", "AgentPackage.zip");
            using var published = await Admin.PostAsync("/internal/agent-releases", form);
            Assert.Equal(HttpStatusCode.OK, published.StatusCode);
            var release = Assert.Single((await Admin.GetFromJsonAsync<AgentReleaseDto[]>("/internal/agent-releases"))!);
            Assert.Equal("2.3.4", release.Version);
            Assert.Equal("stable", release.Channel);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(package)), release.PackageSha256);
            Assert.Equal(package.LongLength, release.PackageSizeBytes);

            using var enrollmentResponse = await Admin.PostAsync("/internal/agent-enrollment-tickets", null);
            Assert.Equal(HttpStatusCode.OK, enrollmentResponse.StatusCode);
            var enrollment = Assert.IsType<EnrollmentTicketResponse>(
                await enrollmentResponse.Content.ReadFromJsonAsync<EnrollmentTicketResponse>());
            using var enrolled = await SendSignedAsync("/agent/enroll", new EnrollmentRequest(MachineId,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "192.0.2.40", "001122334455", 47989, 47990), enrollment.Ticket);
            Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
            await HeartbeatAsync("1.2.3");
            Assert.Equal("pending", (await MachineAsync()).ApprovalStatus);
            await AssertRentalRejectedAsync();
            using var pendingUpdate = await Admin.PostAsJsonAsync($"/internal/machines/{MachineId}/agent-updates", new AgentUpdateReservationRequest(release.Version));
            Assert.Equal(HttpStatusCode.OK, pendingUpdate.StatusCode);
            Assert.Equal("pending", (await MachineAsync()).ApprovalStatus);
            await AssertRentalRejectedAsync();
            Clock.Now = Clock.Now.AddMinutes(10);
            await HeartbeatAsync("1.2.3");
            Assert.Equal("expired", (await MachineAsync()).AgentUpdateStatus);

            await ConfigureAsync();
            using var approved = await Admin.PostAsJsonAsync($"/internal/machines/{MachineId}/approve",
                new MachineIdentityConfirmation(Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))));
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            // Approval increments configuration; even ready on the old ACK cannot rent.
            await AssertRentalRejectedAsync();
            await HeartbeatAsync("1.2.3");
            var ready = await MachineAsync();
            Assert.True(ready.CanRent);
            Assert.False(ready.AllocationHeld);

            using var reserved = await Admin.PostAsJsonAsync($"/internal/machines/{MachineId}/agent-updates", new AgentUpdateReservationRequest(release.Version));
            Assert.Equal(HttpStatusCode.OK, reserved.StatusCode);
            var ticket = Assert.IsType<AgentUpdateTicketResponse>(await reserved.Content.ReadFromJsonAsync<AgentUpdateTicketResponse>());
            Assert.Equal(MachineId, ticket.MachineId);
            Assert.Equal("2.3.4", ticket.ReleaseVersion);
            Assert.Equal("2.3.4", ticket.TargetVersion);
            Assert.Equal(release.PackageSha256, ticket.TargetPackageSha256);
            await AssertLockedAsync(ticket, "awaitingInstaller");
            return ticket;
        }

        public async Task StartAndDownloadAsync(AgentUpdateTicketResponse ticket)
        {
            using var started = await agent.PostAsJsonAsync(UpdatePath(ticket, "start"),
                new AgentUpdateTicketRequest(ticket.Ticket, ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256));
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
            var operation = Assert.IsType<AgentUpdateOperationDto>(await started.Content.ReadFromJsonAsync<AgentUpdateOperationDto>());
            Assert.Equal("1.2.3", operation.PreviousVersion);
            await AssertLockedAsync(ticket, "installing");
            using var request = new HttpRequestMessage(HttpMethod.Get, UpdatePath(ticket, "package"));
            request.Headers.Add("X-Agent-Update-Ticket", ticket.Ticket);
            request.Headers.Add("X-Agent-Update-Release-Version", ticket.ReleaseVersion);
            request.Headers.Add("X-Agent-Update-Target-Version", ticket.TargetVersion);
            request.Headers.Add("X-Agent-Update-Package-SHA256", ticket.TargetPackageSha256);
            using var download = await agent.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("no-store", download.Headers.CacheControl?.ToString());
            Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
            Assert.Equal(package, await download.Content.ReadAsByteArrayAsync());
        }

        public async Task ConfigureAsync()
        {
            var machine = await MachineAsync();
            using var response = await Admin.PatchAsJsonAsync($"/internal/machines/{MachineId}?expectedRevision={machine.ConfigRevision}",
                new MachineUpdate("Acceptance PC", 500, 47989, 47990));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task HeartbeatAsync(string version, string readiness = "ready", long? appliedRevision = null)
        {
            var request = new SyncRequest("acceptance-boot", "192.0.2.40", "001122334455",
                appliedRevision ?? (await MachineAsync()).ConfigRevision, readiness, []) { AgentVersion = version };
            using var response = await SendSignedAsync("/agent/sync", request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<SyncResponse>();
            if (result?.AgentUpdateChallenge is { } challenge)
            {
                using var confirmation = await SendSignedAsync("/agent/sync", request with { AgentUpdateChallenge = challenge });
                Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
            }
        }

        public async Task<HttpRequestMessage> PrepareHeartbeatAsync(string version)
        {
            var body = new SyncRequest("acceptance-boot", "192.0.2.40", "001122334455", (await MachineAsync()).ConfigRevision, "ready", []) { AgentVersion = version };
            return PrepareSigned("/agent/sync", body);
        }

        public Task<HttpResponseMessage> SendPreparedHeartbeatAsync(HttpRequestMessage request) => agent.SendAsync(request);

        private async Task<HttpResponseMessage> SendSignedAsync<T>(string path, T body, string? enrollmentTicket = null)
        {
            using var request = PrepareSigned(path, body, enrollmentTicket);
            return await agent.SendAsync(request);
        }

        private HttpRequestMessage PrepareSigned<T>(string path, T body, string? enrollmentTicket = null)
        {
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var nonce = Guid.NewGuid().ToString("N");
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Agent-Id", MachineId);
            request.Headers.Add("X-Agent-Timestamp", timestamp.ToString());
            request.Headers.Add("X-Agent-Nonce", nonce);
            request.Headers.Add("X-Agent-Signature", Convert.ToBase64String(key.SignData(
                Encoding.UTF8.GetBytes($"{MachineId}\nPOST\n{path}\n{timestamp}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))}"), HashAlgorithmName.SHA256)));
            if (enrollmentTicket is not null) request.Headers.Add("X-Enrollment-Ticket", enrollmentTicket);
            return request;
        }

        public async Task ReportAsync(AgentUpdateTicketResponse ticket, string status)
        {
            using var response = await SendReportAsync(ticket, status);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public Task<HttpResponseMessage> SendReportAsync(AgentUpdateTicketResponse ticket, string status) =>
            agent.PostAsJsonAsync(UpdatePath(ticket, "status"), new AgentUpdateStatusReportRequest(
                ticket.Ticket, ticket.ReleaseVersion, ticket.TargetVersion, ticket.TargetPackageSha256, status));

        public async Task<MachineDto> MachineAsync() => Assert.Single((await Admin.GetFromJsonAsync<MachineDto[]>("/internal/machines"))!);

        public async Task AssertLockedAsync(AgentUpdateTicketResponse ticket, string status)
        {
            var machine = await MachineAsync();
            Assert.Equal(ticket.OperationId, machine.AgentUpdateOperationId);
            Assert.Equal(status, machine.AgentUpdateStatus);
            Assert.False(machine.AllocationHeld);
            await AssertRentalRejectedAsync();
        }

        private Task<HttpResponseMessage> CreateRentalAsync() => renter.PostAsJsonAsync("/internal/trial-leases",
            new TrialLeaseRequest("acceptance-renter", MachineId, 60, "acceptance-rental-" + ++rentalAttempt));

        public async Task AssertRentalRejectedAsync()
        {
            Assert.False((await MachineAsync()).CanRent);
            using var rejected = await CreateRentalAsync();
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Empty((await renter.GetFromJsonAsync<LeaseDto[]>("/internal/users/acceptance-renter/leases"))!);
        }

        public async Task AssertRentalCreatedAsync()
        {
            Assert.True((await MachineAsync()).CanRent);
            using var created = await CreateRentalAsync();
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            Assert.Equal("preparing", (await created.Content.ReadFromJsonAsync<LeaseDto>())?.State);
            Assert.True((await MachineAsync()).AllocationHeld);
            Assert.False((await MachineAsync()).CanRent);
        }

        private static string UpdatePath(AgentUpdateTicketResponse ticket, string action) =>
            $"/agent/machines/{MachineId}/agent-updates/{ticket.OperationId}/{action}";

        private static byte[] CreatePackage()
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                // Validation fixture only: versioned managed metadata, inert exe/scripts.
                Add("Agent/Agent.exe", "inert acceptance fixture");
                var dll = zip.CreateEntry("Agent/Agent.dll");
                using (var destination = dll.Open())
                using (var source = File.OpenRead(typeof(AgentMetadataFixture).Assembly.Location)) source.CopyTo(destination);
                Add("Agent/Agent.deps.json", "{\"libraries\":{\"Agent/2.3.4\":{\"type\":\"project\"}}}");
                foreach (var file in new[] { "Install-AgentPackage.ps1", "Install-Service.ps1", "Install-TightVncServer.ps1", "Prepare-SunshineStreamGate.ps1", "PrivateInstallFiles.ps1", "ServiceInstallTransaction.ps1", "Update-AgentPackage.ps1", "Update-Agent.ps1" })
                    Add(file, "# inert acceptance fixture; never executed");
                Add("AgentPackage.manifest.json", "{\"packageFormatVersion\":1,\"agentInformationalVersion\":\"2.3.4\",\"sourceCommit\":\"0123456789ab\",\"sourceDirty\":false,\"releaseBuild\":true,\"agentUpdateChallengeProtocol\":1}");
                void Add(string name, string text)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                    writer.Write(text);
                }
            }
            return output.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
            renter.Dispose();
            agent.Dispose();
            key.Dispose();
            await app.DisposeAsync();
            var resolved = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("kr-release-acceptance-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe acceptance fixture cleanup path");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
