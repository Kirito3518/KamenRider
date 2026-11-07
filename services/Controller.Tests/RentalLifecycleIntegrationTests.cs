using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KamenRaider.Agent.Configuration;
using KamenRaider.Agent.Identity;
using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Power;
using KamenRaider.Agent.Preparation;
using KamenRaider.Agent.Startup;
using KamenRaider.Agent.Sunshine;
using KamenRaider.Agent.Sync;
using KamenRaider.Contracts;
using KamenRaider.Controller;
using KamenRaider.Controller.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Controller.Tests;

public sealed class RentalLifecycleIntegrationTests
{
    private const string ServiceCredential = "integration-service-credential";
    private const string LocalIp = "192.168.1.40";
    private const string MacAddress = "001122334455";
    private const string PairingId = PairingIds.FoundationSunshineSinglePendingSlotId;

    [Fact]
    public async Task SignedEnrollmentLeasePairingExpiryAndRebootReturnMachineToRentalPool()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), "kr-rental-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fullRoot = Path.GetFullPath(root);
        var tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        try
        {
            var clock = new IntegrationClock();
            var agentData = Path.Combine(root, "agent-data");
            Directory.CreateDirectory(agentData);

            await using (var app = ControllerHost.Build([], builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["InternalUrl"] = "http://127.0.0.1:5101",
                    ["AgentUrl"] = "https://127.0.0.1:5102",
                    ["DatabasePath"] = Path.Combine(root, "controller.db"),
                    ["ServiceCredential"] = ServiceCredential
                });
                builder.WebHost.UseTestServer();
                builder.Services.AddTransient<IStartupFilter, LoopbackTestFilter>();
                builder.Services.AddSingleton<TimeProvider>(clock);
            }))
            {
                await app.StartAsync();
                var database = app.Services.GetRequiredService<ControllerDatabase>();
                using var admin = app.GetTestClient();
                admin.DefaultRequestHeaders.Add("X-Service-Credential", ServiceCredential);
                admin.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "admin-1");

                using var ticketResponse = await admin.PostAsync("/internal/agent-enrollment-tickets", null);
                Assert.Equal(HttpStatusCode.OK, ticketResponse.StatusCode);
                var ticket = Assert.IsType<EnrollmentTicketResponse>(await ticketResponse.Content.ReadFromJsonAsync<EnrollmentTicketResponse>());

                using var identity = DeviceIdentity.LoadOrCreate(agentData);
                using var agentHttp = app.GetTestServer().CreateClient();
                agentHttp.BaseAddress = new Uri("https://localhost/");
                agentHttp.DefaultRequestHeaders.Add("Test-Local-Port", "5102");
                await EnrollAsync(agentHttp, identity, ticket.Ticket);

                var pending = Assert.Single((await admin.GetFromJsonAsync<MachineDto[]>("/internal/machines"))!);
                Assert.Equal("pending", pending.ApprovalStatus);
                Assert.False(pending.CanRent);

                using var update = await admin.PatchAsJsonAsync(
                    $"/internal/machines/{identity.MachineId}?expectedRevision=1",
                    new MachineUpdate("Integration PC", 500, 47989, 47990));
                Assert.Equal(HttpStatusCode.OK, update.StatusCode);

                var fingerprint = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(identity.PublicKey)));
                using var approve = await admin.PostAsJsonAsync(
                    $"/internal/machines/{identity.MachineId}/approve",
                    new MachineIdentityConfirmation(fingerprint));
                Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

                using var owner = app.GetTestClient();
                owner.DefaultRequestHeaders.Add("X-Service-Credential", ServiceCredential);
                owner.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "renter-1");

                var gate = new IntegrationGate(clock);
                var sunshine = new IntegrationSunshine();
                sunshine.Clients.Add("stale-client");
                var bootEnvironment = new IntegrationBootEnvironment(clock, gate, sunshine);
                var configuration = new AppliedConfiguration(agentData);
                var shutdown = new IntegrationShutdown();
                var enforcer = new LeaseEnforcer(new LeaseStore(agentData), clock, clock, gate, shutdown);
                var importer = new LeaseEnforcerReconciledLeaseImporter(enforcer);
                var controllerUri = new Uri("https://localhost/");
                var reconciliation = new ReconciliationClient(agentHttp, identity, controllerUri);

                var boot = new BootReconciler(clock, gate, configuration, reconciliation, bootEnvironment, importer, baselineValidated: true);
                Assert.Equal(Readiness.Ready, await boot.ReconcileAsync(default));
                Assert.Empty(sunshine.Clients);
                Assert.True(bootEnvironment.FencedCleanupCount > 0);

                var pairing = new PairingSession(sunshine, gate, () => enforcer.CurrentPairingScope, clock);
                var preparation = new IntegrationPreparation();
                var dispatcher = new LeaseCommandDispatcher(enforcer, pairing, preparation);
                var syncCycle = new AgentSyncCycle(new SyncClient(agentHttp, identity), controllerUri, clock, configuration, dispatcher);

                await syncCycle.RunAsync(clock.BootId, LocalIp, MacAddress, Readiness.Ready, default);
                var readyMachine = Assert.Single((await admin.GetFromJsonAsync<MachineDto[]>("/internal/machines"))!);
                Assert.True(readyMachine.CanRent);

                using var createLease = await owner.PostAsJsonAsync(
                    "/internal/trial-leases",
                    new TrialLeaseRequest("renter-1", identity.MachineId, 60, "create-round-1"));
                Assert.Equal(HttpStatusCode.OK, createLease.StatusCode);
                var firstLease = Assert.IsType<LeaseDto>(await createLease.Content.ReadFromJsonAsync<LeaseDto>());
                Assert.Equal("preparing", firstLease.State);

                await syncCycle.RunAsync(clock.BootId, LocalIp, MacAddress, Readiness.Ready, default);
                Assert.Equal(1, preparation.Calls);
                Assert.Equal("applied", Assert.Single(dispatcher.PendingResults).Status);

                await syncCycle.RunAsync(clock.BootId, LocalIp, MacAddress, Readiness.Ready, default);
                Assert.Equal("applied", Assert.Single(dispatcher.PendingResults).Status);
                await syncCycle.RunAsync(clock.BootId, LocalIp, MacAddress, Readiness.Ready, default);

                var activeLease = Assert.Single((await owner.GetFromJsonAsync<LeaseDto[]>("/internal/users/renter-1/leases"))!);
                Assert.Equal("active", activeLease.State);
                Assert.True(enforcer.CanStream);
                Assert.False(gate.IsBlocked);

                await syncCycle.RunAsync(clock.BootId, LocalIp, MacAddress, Readiness.Ready, default);
                var pairingOffer = await owner.GetFromJsonAsync<PairingOffer>($"/internal/leases/{activeLease.Id}/pair/pending");
                Assert.NotNull(pairingOffer);
                Assert.Contains(PairingId, pairingOffer.PairingIds);

                using var pair = await owner.PostAsJsonAsync(
                    $"/internal/leases/{activeLease.Id}/pair",
                    new PairingRequest("renter-1", PairingId, "1234", "pair-round-1"));
                Assert.Equal(HttpStatusCode.OK, pair.StatusCode);
                var queuedPair = Assert.IsType<PairingCommandStatus>(await pair.Content.ReadFromJsonAsync<PairingCommandStatus>());
                Assert.Equal("queued", queuedPair.Status);

                var pairCommand = Assert.Single(database.Sync(identity.MachineId, new SyncRequest(
                    clock.BootId, LocalIp, MacAddress, configuration.Current!.Revision, "ready", [], pairingOffer)).Commands);
                Assert.Equal("1234", pairCommand.Payload.GetProperty("pin").GetString());

                await syncCycle.RunAsync(clock.BootId, LocalIp, MacAddress, Readiness.Ready, default);
                var pairResult = Assert.Single(dispatcher.PendingResults);
                Assert.True(pairResult.Status == "applied", $"Pair command failed with {pairResult.ErrorCode}.");
                Assert.Equal("1234", sunshine.LastPin);
                await syncCycle.RunAsync(clock.BootId, LocalIp, MacAddress, Readiness.Ready, default);
                var pairingStatus = await owner.GetFromJsonAsync<PairingCommandStatus>($"/internal/leases/{activeLease.Id}/pair/{queuedPair.CommandId}");
                Assert.Equal("succeeded", pairingStatus?.Status);

                using var extend = await owner.PostAsJsonAsync(
                    $"/internal/leases/{activeLease.Id}/extend",
                    new ExtendLeaseRequest(60, 1, "extend-round-1"));
                Assert.Equal(HttpStatusCode.OK, extend.StatusCode);
                Assert.Equal(2, (await extend.Content.ReadFromJsonAsync<LeaseDto>())?.PendingRevision);

                await syncCycle.RunAsync(clock.BootId, LocalIp, MacAddress, Readiness.Ready, default);
                Assert.True(gate.IsBlocked);
                await syncCycle.RunAsync(clock.BootId, LocalIp, MacAddress, Readiness.Ready, default);
                activeLease = Assert.Single((await owner.GetFromJsonAsync<LeaseDto[]>("/internal/users/renter-1/leases"))!);
                Assert.Equal("active", activeLease.State);
                Assert.Equal(2, activeLease.Revision);
                Assert.True(enforcer.CanStream);

                clock.AdvanceSeconds(121);
                await enforcer.EnforceAsync(default);
                Assert.Equal(1, shutdown.Calls);
                Assert.True(gate.IsBlocked);

                await syncCycle.RunAsync(clock.BootId, LocalIp, MacAddress, Readiness.NotReady, default);
                var endingLease = Assert.Single((await owner.GetFromJsonAsync<LeaseDto[]>("/internal/users/renter-1/leases"))!);
                Assert.Equal("ending", endingLease.State);

                clock.Reboot("boot-2");
                var afterReboot = new BootReconciler(clock, gate, configuration, reconciliation, bootEnvironment, importer, baselineValidated: true);
                Assert.Equal(Readiness.Ready, await afterReboot.ReconcileAsync(default));
                Assert.Empty(sunshine.Clients);

                await syncCycle.RunAsync(clock.BootId, LocalIp, MacAddress, Readiness.Ready, default);
                var completedLease = Assert.Single((await owner.GetFromJsonAsync<LeaseDto[]>("/internal/users/renter-1/leases"))!);
                Assert.Equal("completed", completedLease.State);

                using var nextLeaseResponse = await owner.PostAsJsonAsync(
                    "/internal/trial-leases",
                    new TrialLeaseRequest("renter-1", identity.MachineId, 60, "create-round-2"));
                Assert.Equal(HttpStatusCode.OK, nextLeaseResponse.StatusCode);
                var nextLease = Assert.IsType<LeaseDto>(await nextLeaseResponse.Content.ReadFromJsonAsync<LeaseDto>());
                Assert.Equal("preparing", nextLease.State);

                await syncCycle.RunAsync(clock.BootId, LocalIp, MacAddress, Readiness.Ready, default);
                await syncCycle.RunAsync(clock.BootId, LocalIp, MacAddress, Readiness.Ready, default);
                await syncCycle.RunAsync(clock.BootId, LocalIp, MacAddress, Readiness.Ready, default);

                var allLeases = await owner.GetFromJsonAsync<LeaseDto[]>("/internal/users/renter-1/leases");
                var renewedRound = Assert.Single(allLeases!, lease => lease.State == "active");
                Assert.Equal(nextLease.Id, renewedRound.Id);
                Assert.True(enforcer.CanStream);
            }
        }
        finally
        {
            if (!fullRoot.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullRoot).StartsWith("kr-rental-lifecycle-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing unsafe integration-test cleanup.");
            if (Directory.Exists(fullRoot))
                Directory.Delete(fullRoot, recursive: true);
        }
    }

    private static async Task EnrollAsync(HttpClient client, DeviceIdentity identity, string ticket)
    {
        var requestBody = JsonSerializer.Serialize(
            new EnrollmentRequest(identity.MachineId, identity.PublicKey, LocalIp, MacAddress, 47989, 47990),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var timestamp = DateTimeOffset.UtcNow;
        var nonce = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent/enroll")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Enrollment-Ticket", ticket);
        request.Headers.Add("X-Agent-Timestamp", timestamp.ToUnixTimeSeconds().ToString());
        request.Headers.Add("X-Agent-Nonce", nonce);
        request.Headers.Add("X-Agent-Signature", identity.Sign("POST", "/agent/enroll", timestamp, nonce, requestBody));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<EnrollmentResult>();
        Assert.Equal("pending", result?.ApprovalStatus);
    }

    private sealed class IntegrationClock : TimeProvider, IBootClock
    {
        public string BootId { get; private set; } = "boot-1";
        public long UptimeMilliseconds { get; private set; } = 1000;
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;

        public void AdvanceSeconds(int seconds)
        {
            Now = Now.AddSeconds(seconds);
            UptimeMilliseconds = checked(UptimeMilliseconds + seconds * 1000L);
        }

        public void Reboot(string bootId)
        {
            BootId = bootId;
            UptimeMilliseconds = 1000;
        }
    }

    private sealed class IntegrationGate(IntegrationClock clock) : IStreamGate, IPairingFence
    {
        public bool IsBlocked { get; private set; } = true;
        public StreamLeaseGrant? Grant { get; private set; }

        public Task BlockAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsBlocked = true;
            Grant = null;
            return Task.CompletedTask;
        }

        public Task AllowAsync(StreamLeaseGrant grant, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (grant.BootId != clock.BootId || grant.DeadlineUptimeMilliseconds <= clock.UptimeMilliseconds || grant.ExpiresAtUtc <= clock.GetUtcNow())
                return Task.FromException(new InvalidOperationException("Expired or cross-boot stream grant."));
            Grant = grant;
            IsBlocked = false;
            return Task.CompletedTask;
        }

        public Task<bool> VerifyOpenForLeaseAsync(PairingScope scope, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var grant = Grant;
            return Task.FromResult(!IsBlocked && grant is not null && grant.RentalId == scope.RentalId &&
                grant.Revision == scope.Revision && grant.BootId == scope.BootId && grant.ExpiresAtUtc == scope.ExpiresAtUtc);
        }

        public async Task<bool> RunFencedCleanupAsync(Func<CancellationToken, Task> clear, CancellationToken cancellationToken)
        {
            await BlockAsync(cancellationToken);
            await clear(cancellationToken);
            return IsBlocked && Grant is null;
        }
    }

    private sealed class IntegrationSunshine : ISunshinePairingApi
    {
        public HashSet<string> Clients { get; } = [];
        public string? LastPin { get; private set; }

        public Task<string[]> PendingIdsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new[] { PairingId });
        }

        public Task<bool> SubmitAsync(string id, string pin, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (id != PairingId)
                return Task.FromResult(false);
            LastPin = pin;
            Clients.Add("paired-client");
            return Task.FromResult(true);
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Clients.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class IntegrationBootEnvironment(IntegrationClock clock, IntegrationGate gate, IntegrationSunshine sunshine) : IBootEnvironment
    {
        public int FencedCleanupCount { get; private set; }

        public Task<bool> VerifyRestorationAsync(ReconciliationChallenge challenge, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(challenge.BootId == clock.BootId);
        }

        public async Task<bool> FenceAndClearAsync(ReconciliationChallenge challenge, CancellationToken cancellationToken)
        {
            if (challenge.BootId != clock.BootId)
                return false;
            var cleared = await gate.RunFencedCleanupAsync(sunshine.ClearAsync, cancellationToken);
            if (cleared)
                FencedCleanupCount++;
            return cleared && sunshine.Clients.Count == 0;
        }

        public Task<bool> VerifyHealthAsync(ReconciliationChallenge challenge, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(challenge.BootId == clock.BootId);
        }

        public Task<bool> VerifyRuntimeHealthAsync(string bootId, long configurationRevision, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(bootId == clock.BootId && configurationRevision > 0);
        }
    }

    private sealed class IntegrationPreparation : IMachinePreparation
    {
        public int Calls { get; private set; }

        public Task<bool> PrepareAsync(CommandEnvelope command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(command.Type == "PrepareMachine");
        }
    }

    private sealed class IntegrationShutdown : IShutdown
    {
        public int Calls { get; private set; }

        public Task RequestImmediateShutdownAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.CompletedTask;
        }
    }
}
