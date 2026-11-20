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

public class SunshineCredentialStoreTests
{
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kr-sunshine-credentials-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class TestProtector : ISunshineCredentialProtector
    {
        private static readonly byte[] Prefix = [0x4B, 0x52, 0x43, 0x31];

        public byte[] Protect(ReadOnlySpan<byte> plaintext) => [.. Prefix, .. plaintext];

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
        {
            if (!ciphertext.StartsWith(Prefix)) throw new InvalidDataException("Invalid test ciphertext.");
            return ciphertext[Prefix.Length..].ToArray();
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void MissingCredentialFileReturnsNull()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var store = NewStore(directory.Path, new TestProtector());

        Assert.Null(store.Load());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SaveWritesOnlyProtectedBytesAndLoadRestoresTheCredential()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var protector = new WindowsDpapiMachineSecretProtector();
        var store = NewStore(directory.Path, protector);
        var credential = new SunshineManagementCredential("sunshine-user", "secret-password");

        store.Save(credential);

        var bytes = File.ReadAllBytes(store.CredentialPath);
        Assert.DoesNotContain("sunshine-user", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-password", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(credential, store.Load());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ExistingCredentialCannotBeReplacedWithoutExplicitRequest()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var store = NewStore(directory.Path, new TestProtector());
        var original = new SunshineManagementCredential("sunshine-user", "original-password");
        store.Save(original);

        Assert.Throws<IOException>(() => store.Save(new("sunshine-user", "replacement-password")));
        Assert.Equal(original, store.Load());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ExplicitReplacementPublishesTheNewCredentialAtomically()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var store = NewStore(directory.Path, new TestProtector());
        store.Save(new("sunshine-user", "original-password"));
        var replacement = new SunshineManagementCredential("sunshine-user", "replacement-password");

        store.Save(replacement, replace: true);

        Assert.Equal(replacement, store.Load());
        Assert.Single(Directory.GetFiles(store.DirectoryPath));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void MachineDpapiProtectorRoundTripsWithoutUserProfileState()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protector = new WindowsDpapiMachineSecretProtector();
        var plaintext = Encoding.UTF8.GetBytes("credential payload");

        var ciphertext = protector.Protect(plaintext);

        Assert.NotEqual(plaintext, ciphertext);
        Assert.Equal(plaintext, protector.Unprotect(ciphertext));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AgentServiceSidMatchesWindowsScShowsid()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal("S-1-5-80-306869082-510711969-1671650537-2166470815-2388419276", WindowsSunshineCredentialStore.ResolveAgentServiceSid().Value);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task MissingCredentialStillAllowsLoopbackSunshineButNotPairingWithoutALease()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var store = NewStore(directory.Path, new TestProtector());
        using var service = new SunshineService(
            () => new ConfigurationDto(1, 47989, 47990),
            static () => 0,
            store.Load,
            static () => true,
            static () => null,
            new FakeLiveness(),
            Options.Create(new AgentOptions { MoonlightPort = 47989, SunshinePort = 47990 }),
            TimeProvider.System,
            NullLogger<SunshineService>.Instance);

        // 0jay-style: loopback Sunshine needs no fleet credential, so the session is usable;
        // without a current lease scope the command is rejected by the pairing session itself
        // (not with "PairingUnavailable").
        Assert.Empty(await service.ListAsync(default));
        Assert.Null(await service.CreateOfferAsync(default));
        var command = new CommandEnvelope(
            "pair-command", "PairPin", "rental", 1, "boot", DateTimeOffset.UtcNow.AddMinutes(1),
            System.Text.Json.JsonSerializer.SerializeToElement(new { pairingId = "slot", pin = "1234" }));
        Assert.Equal(new CommandResult("pair-command", "failed", 1, "PairingRejected"),
            await service.ExecuteAsync(command, default));
    }

    [SupportedOSPlatform("windows")]
    private static WindowsSunshineCredentialStore NewStore(string directory, ISunshineCredentialProtector protector)
    {
        var currentUser = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Windows user SID is unavailable.");
        return new(directory, currentUser, protector);
    }

    private sealed class FakeLiveness : ISunshineLiveness
    {
        public SunshineLivenessSnapshot Current { get; } = new(
            true, SunshineLivenessProbe.ReasonReady, DateTimeOffset.UtcNow, 47989, 47990);

        public Task<SunshineLivenessSnapshot> ProbeOnceAsync(CancellationToken ct) => Task.FromResult(Current);
    }
}
