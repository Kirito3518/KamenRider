using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using KamenRaider.Agent;
using KamenRaider.Agent.Configuration;
using KamenRaider.Agent.Sunshine;
using KamenRaider.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agent.Tests;

/// <summary>
/// SunshineService owns the lazily-built Sunshine REST client. The cached client is invalidated
/// when the configured port or the applied fleet credential revision changes, so a rotated
/// credential takes effect without an Agent restart.
/// </summary>
[SupportedOSPlatform("windows")]
public class SunshineCredentialRotationTests
{
    [Fact]
    public async Task AppliedRevisionChangeReloadsTheCachedPairingCredentialWithoutRestart()
    {
        using var directory = new TempDirectory();
        var credentialStore = NewStore(directory.Path);
        var loaded = new SunshineManagementCredential("fleet-user", "first-password");
        var loads = 0;
        var appliedRevision = 1L;
        using var service = NewService(
            appliedRevision: () => appliedRevision,
            credentialLoader: () =>
            {
                loads++;
                return loaded;
            });

        Assert.Empty(await service.ListAsync(default));
        Assert.Equal(1, loads);

        Assert.Empty(await service.ListAsync(default));
        Assert.Equal(1, loads);

        // Changing the credential source alone does not disturb the cached client.
        loaded = new SunshineManagementCredential("fleet-user", "second-password");
        Assert.Empty(await service.ListAsync(default));
        Assert.Equal(1, loads);

        // Persisting the newly applied fleet revision invalidates the cached client so the next
        // operation loads the rotated credential without a process restart.
        appliedRevision = 2;
        Assert.Empty(await service.ListAsync(default));
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task PortChangeRebuildsTheCachedClientAndReloadsTheCredential()
    {
        using var directory = new TempDirectory();
        var loads = 0;
        var port = 47990;
        using var service = NewService(
            configuration: () => new ConfigurationDto(1, 47989, port),
            credentialLoader: () =>
            {
                loads++;
                return new SunshineManagementCredential("fleet-user", "password");
            });

        Assert.Empty(await service.ListAsync(default));
        Assert.Equal(1, loads);

        port = 47991;
        Assert.Empty(await service.ListAsync(default));
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task CredentialLoaderFailureLeavesTheLoopbackSessionUsableWithoutThrowing()
    {
        using var directory = new TempDirectory();
        using var service = NewService(
            credentialLoader: () => throw new UnauthorizedAccessException("protected credential unreadable"));

        Assert.Empty(await service.ListAsync(default));
        Assert.Null(await service.CreateOfferAsync(default));

        var command = PairCommand();
        var result = await service.ExecuteAsync(command, default);

        // The session exists (loopback Sunshine needs no credential); only the missing lease
        // scope rejects the command.
        Assert.Equal(new CommandResult(command.CommandId, "failed", command.LeaseRevision, "PairingRejected"), result);
    }

    [Fact]
    public async Task InvalidCredentialKeepsPairingUnavailableInsteadOfThrowing()
    {
        using var directory = new TempDirectory();
        using var service = NewService(
            credentialLoader: () => new SunshineManagementCredential("fleet-user", ""));

        Assert.Empty(await service.ListAsync(default));
        var command = PairCommand();
        Assert.Equal(new CommandResult(command.CommandId, "failed", command.LeaseRevision, "PairingUnavailable"),
            await service.ExecuteAsync(command, default));
    }

    [Fact]
    public async Task RealProtectedCredentialFileRotationTakesEffectOnAppliedRevisionChange()
    {
        if (!OperatingSystem.IsWindows() || !WindowsAclProbe.CanSetProtectedAcl) return;
        using var directory = new TempDirectory();
        var protector = new CountingProtector();
        var credentialStore = NewStore(directory.Path, protector);
        credentialStore.Save(new SunshineManagementCredential("fleet-user", "first-password"));
        var appliedRevision = 1L;
        using var service = NewService(
            appliedRevision: () => appliedRevision,
            credentialLoader: credentialStore.Load);

        Assert.Empty(await service.ListAsync(default));
        Assert.Equal(1, protector.UnprotectCalls);
        Assert.Contains("first-password", protector.LastPlaintext);

        credentialStore.Save(new SunshineManagementCredential("fleet-user", "second-password"), replace: true);
        Assert.Empty(await service.ListAsync(default));
        Assert.Equal(1, protector.UnprotectCalls);

        appliedRevision = 2;
        Assert.Empty(await service.ListAsync(default));
        Assert.Equal(2, protector.UnprotectCalls);
        Assert.Contains("second-password", protector.LastPlaintext);
    }

    private static CommandEnvelope PairCommand() => new(
        "pair-command", "PairPin", "rental", 1, "boot", DateTimeOffset.UtcNow.AddMinutes(1),
        System.Text.Json.JsonSerializer.SerializeToElement(new { pairingId = "slot", pin = "1234" }));

    private static SunshineService NewService(
        Func<ConfigurationDto?>? configuration = null,
        Func<long>? appliedRevision = null,
        Func<SunshineManagementCredential?>? credentialLoader = null,
        Func<PairingScope?>? scope = null,
        Func<bool>? streamReady = null)
        => new(
            configuration ?? (() => new ConfigurationDto(1, 47989, 47990)),
            appliedRevision ?? (static () => 0),
            credentialLoader ?? (static () => null),
            streamReady ?? (static () => true),
            scope ?? (static () => null),
            new FakeLiveness(),
            Options.Create(new AgentOptions { MoonlightPort = 47989, SunshinePort = 47990 }),
            TimeProvider.System,
            NullLogger<SunshineService>.Instance);

    private sealed class FakeLiveness : ISunshineLiveness
    {
        public SunshineLivenessSnapshot Current { get; } = new(
            true, SunshineLivenessProbe.ReasonReady, DateTimeOffset.UtcNow, 47989, 47990);

        public Task<SunshineLivenessSnapshot> ProbeOnceAsync(CancellationToken ct) => Task.FromResult(Current);
    }

    private static WindowsSunshineCredentialStore NewStore(string directory, ISunshineCredentialProtector? protector = null)
    {
        var currentUser = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Windows user SID is unavailable.");
        return new WindowsSunshineCredentialStore(directory, currentUser, protector ?? new CountingProtector());
    }

    private sealed class CountingProtector : ISunshineCredentialProtector
    {
        private static readonly byte[] Prefix = [0x4B, 0x52, 0x43, 0x31];

        public int UnprotectCalls { get; private set; }
        public string LastPlaintext { get; private set; } = "";

        public byte[] Protect(ReadOnlySpan<byte> plaintext) => [.. Prefix, .. plaintext];

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
        {
            if (!ciphertext.StartsWith(Prefix)) throw new InvalidDataException("Invalid test ciphertext.");
            UnprotectCalls++;
            LastPlaintext = Encoding.UTF8.GetString(ciphertext[Prefix.Length..]);
            return ciphertext[Prefix.Length..].ToArray();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kr-sunshine-rotation-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
