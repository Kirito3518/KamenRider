using System.Text.Json;
using KamenRaider.Agent.Leases;
using KamenRaider.Agent.Power;
using KamenRaider.Agent.Sunshine;
using KamenRaider.Agent.Sync;
using KamenRaider.Contracts;

namespace Agent.Tests;

/// <summary>
/// Admin Sunshine maintenance (RestartSunshine / UnpairSunshine) is machine scoped: the current
/// boot id and command expiry are checked here, and the admin result is mapped to a command result.
/// </summary>
public sealed class LeaseCommandDispatcherSunshineMaintenanceTests
{
    [Fact]
    public async Task RestartWithoutAnAdminProviderFailsInsteadOfBeingIgnored()
    {
        using var fixture = new Fixture();
        var dispatcher = new LeaseCommandDispatcher(fixture.Enforcer);

        await dispatcher.AcceptAsync(fixture.Response(fixture.Command("RestartSunshine")), default);

        Assert.Equal(new CommandResult("sunshine", "failed", 0, "SunshineMaintenanceUnavailable"),
            Assert.Single(dispatcher.PendingResults));
        Assert.Equal(0, fixture.Admin.Calls);
    }

    [Theory]
    [InlineData("other-boot")]
    [InlineData("")]
    public async Task MaintenanceForAnotherBootIsRejectedWithoutCallingTheAdmin(string bootId)
    {
        using var fixture = new Fixture();
        var dispatcher = fixture.NewDispatcher();

        await dispatcher.AcceptAsync(fixture.Response(fixture.Command("RestartSunshine", bootId: bootId)), default);

        Assert.Equal(new CommandResult("sunshine", "failed", 0, "SunshineMaintenanceUnavailable"),
            Assert.Single(dispatcher.PendingResults));
        Assert.Equal(0, fixture.Admin.Calls);
    }

    [Fact]
    public async Task ExpiredMaintenanceCommandIsRejectedWithoutCallingTheAdmin()
    {
        using var fixture = new Fixture();
        var dispatcher = fixture.NewDispatcher();

        await dispatcher.AcceptAsync(fixture.Response(fixture.Command("RestartSunshine", validFor: TimeSpan.FromSeconds(-1))), default);

        Assert.Equal(new CommandResult("sunshine", "failed", 0, "CommandExpired"),
            Assert.Single(dispatcher.PendingResults));
        Assert.Equal(0, fixture.Admin.Calls);
    }

    [Fact]
    public async Task RestartSuccessIsAppliedWithTheAdminDiagnosticCode()
    {
        using var fixture = new Fixture();
        fixture.Admin.Result = new(true, "NOT_BACK_YET");
        var dispatcher = fixture.NewDispatcher();

        await dispatcher.AcceptAsync(fixture.Response(fixture.Command("RestartSunshine")), default);

        Assert.Equal(new CommandResult("sunshine", "applied", 0, "NOT_BACK_YET"),
            Assert.Single(dispatcher.PendingResults));
        Assert.Equal(1, fixture.Admin.RestartCalls);
        Assert.False(fixture.Admin.LastForce);
    }

    [Fact]
    public async Task UnpairSuccessIsApplied()
    {
        using var fixture = new Fixture();
        var dispatcher = fixture.NewDispatcher();

        await dispatcher.AcceptAsync(fixture.Response(fixture.Command("UnpairSunshine")), default);

        Assert.Equal(new CommandResult("sunshine", "applied", 0),
            Assert.Single(dispatcher.PendingResults));
        Assert.Equal(1, fixture.Admin.UnpairCalls);
        Assert.Equal(0, fixture.Admin.RestartCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForceFlagIsReadFromThePayloadOnlyWhenItIsABoolean(bool force)
    {
        using var fixture = new Fixture();
        var dispatcher = fixture.NewDispatcher();

        await dispatcher.AcceptAsync(fixture.Response(fixture.Command("UnpairSunshine", payload: new { force })), default);

        Assert.True(fixture.Admin.LastForce is not null);
        Assert.Equal(force, fixture.Admin.LastForce!.Value);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"force\":\"true\"}")]
    [InlineData("{\"force\":1}")]
    public async Task AbsentOrNonBooleanForceDefaultsToFalse(string json)
    {
        using var fixture = new Fixture();
        var dispatcher = fixture.NewDispatcher();

        await dispatcher.AcceptAsync(fixture.Response(fixture.Command("RestartSunshine", rawPayload: json)), default);

        Assert.False(fixture.Admin.LastForce);
    }

    [Fact]
    public async Task AdminRejectionMapsToAFailedResultWithItsErrorCode()
    {
        using var fixture = new Fixture();
        fixture.Admin.Result = new(false, "UNPAIR_FAILED");
        var dispatcher = fixture.NewDispatcher();

        await dispatcher.AcceptAsync(fixture.Response(fixture.Command("UnpairSunshine")), default);

        Assert.Equal(new CommandResult("sunshine", "failed", 0, "UNPAIR_FAILED"),
            Assert.Single(dispatcher.PendingResults));
    }

    [Fact]
    public async Task AdminRejectionWithoutACodeUsesTheDefaultFailureCode()
    {
        using var fixture = new Fixture();
        fixture.Admin.Result = new(false, null);
        var dispatcher = fixture.NewDispatcher();

        await dispatcher.AcceptAsync(fixture.Response(fixture.Command("RestartSunshine")), default);

        Assert.Equal(new CommandResult("sunshine", "failed", 0, "SunshineMaintenanceFailed"),
            Assert.Single(dispatcher.PendingResults));
    }

    [Fact]
    public async Task AdminExceptionFailsTheCommandWithoutEscapingTheDispatcher()
    {
        using var fixture = new Fixture();
        fixture.Admin.Throw = true;
        var dispatcher = new LeaseCommandDispatcher(fixture.Enforcer);

        await dispatcher.AcceptAsync(fixture.Response(fixture.Command("RestartSunshine")), default);

        Assert.Equal(new CommandResult("sunshine", "failed", 0, "SunshineMaintenanceUnavailable"),
            Assert.Single(dispatcher.PendingResults));
    }

    [Fact]
    public async Task MaintenanceCommandsAreHandledBeforeTheCycleAcknowledgesThem()
    {
        using var fixture = new Fixture();
        var dispatcher = fixture.NewDispatcher();
        var response = fixture.Response(fixture.Command("RestartSunshine"), fixture.Command("UnpairSunshine", id: "sunshine-2"));

        await dispatcher.AcceptAsync(response, default);
        Assert.Equal(2, dispatcher.PendingResults.Length);

        // A successful sync acknowledges the sent results.
        await dispatcher.AcceptAsync(fixture.Response(), default);
        Assert.Empty(dispatcher.PendingResults);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Directory.CreateDirectory(DataDirectory);
            Enforcer = new LeaseEnforcer(new LeaseStore(DataDirectory), Clock, Clock, null, new NoopShutdown(), new TestMaintenanceFence());
        }

        public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "kr-sunshine-admin-" + Guid.NewGuid().ToString("N"));
        public Clock Clock { get; } = new();
        public Admin Admin { get; } = new();
        public LeaseEnforcer Enforcer { get; }

        public LeaseCommandDispatcher NewDispatcher() => new(Enforcer, sunshineAdmin: Admin, time: Clock);

        public CommandEnvelope Command(
            string type,
            string id = "sunshine",
            string bootId = "boot",
            TimeSpan? validFor = null,
            object? payload = null,
            string? rawPayload = null) =>
            new(id, type, "", 0, bootId, Clock.Now.Add(validFor ?? TimeSpan.FromMinutes(1)),
                rawPayload is not null ? JsonSerializer.SerializeToElement(JsonDocument.Parse(rawPayload).RootElement)
                    : JsonSerializer.SerializeToElement(payload ?? new { }));

        public SyncResponse Response(params CommandEnvelope[] commands) =>
            new(Clock.Now, new(1, 5000, 6000), commands);

        public void Dispose()
        {
            if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, recursive: true);
        }
    }

    private sealed class Clock : TimeProvider, IBootClock
    {
        public string BootId => "boot";
        public long UptimeMilliseconds { get; } = 1000;
        public DateTimeOffset Now { get; } = DateTimeOffset.Parse("2026-10-29T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class NoopShutdown : IShutdown
    {
        public Task RequestImmediateShutdownAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Admin : ISunshineAdmin
    {
        public int Calls => RestartCalls + UnpairCalls;
        public int RestartCalls { get; private set; }
        public int UnpairCalls { get; private set; }
        public bool? LastForce { get; private set; }
        public SunshineAdminResult Result { get; set; } = new(true, null);
        public bool Throw { get; set; }

        public Task<SunshineAdminResult> RestartAsync(bool force, CancellationToken ct)
        {
            RestartCalls++;
            LastForce = force;
            if (Throw) throw new IOException("sunshine admin unavailable");
            return Task.FromResult(Result);
        }

        public Task<SunshineAdminResult> UnpairAsync(bool force, CancellationToken ct)
        {
            UnpairCalls++;
            LastForce = force;
            if (Throw) throw new IOException("sunshine admin unavailable");
            return Task.FromResult(Result);
        }
    }
}
