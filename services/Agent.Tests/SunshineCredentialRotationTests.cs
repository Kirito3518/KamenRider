using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using KamenRaider.Agent.Sunshine;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent.Tests;

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
        using var pairing = new LazySunshinePairingSession(
            credentialStore,
            () => 47990,
            new ClosedPairingFence(),
            () => null,
            TimeProvider.System,
            NullLogger<LazySunshinePairingSession>.Instance,
            () => appliedRevision,
            () =>
            {
                loads++;
                return loaded;
            });

        Assert.Empty(await pairing.ListAsync(default));
        Assert.Equal(1, loads);

        Assert.Empty(await pairing.ListAsync(default));
        Assert.Equal(1, loads);

        // The credential source changing alone does not disturb the cached session.
        loaded = new SunshineManagementCredential("fleet-user", "second-password");
        Assert.Empty(await pairing.ListAsync(default));
        Assert.Equal(1, loads);

        // Persisting the newly applied fleet revision invalidates the session so the next pairing
        // operation loads the rotated credential without a process restart.
        appliedRevision = 2;
        Assert.Empty(await pairing.ListAsync(default));
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task CredentialLoaderFailureLeavesPairingUnavailableWithoutThrowing()
    {
        using var directory = new TempDirectory();
        var credentialStore = NewStore(directory.Path);
        using var pairing = new LazySunshinePairingSession(
            credentialStore,
            () => 47990,
            new ClosedPairingFence(),
            () => null,
            TimeProvider.System,
            NullLogger<LazySunshinePairingSession>.Instance,
            static () => 0,
            () => throw new UnauthorizedAccessException("protected credential unreadable"));

        Assert.Empty(await pairing.ListAsync(default));
        Assert.Null(await pairing.CreateOfferAsync(default));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RealProtectedCredentialFileRotationTakesEffectOnAppliedRevisionChange()
    {
        if (!OperatingSystem.IsWindows() || !WindowsAclProbe.CanSetProtectedAcl) return;
        using var directory = new TempDirectory();
        var protector = new CountingProtector();
        var credentialStore = NewStore(directory.Path, protector);
        credentialStore.Save(new SunshineManagementCredential("fleet-user", "first-password"));
        var appliedRevision = 1L;
        using var pairing = new LazySunshinePairingSession(
            credentialStore,
            () => 47990,
            new ClosedPairingFence(),
            () => null,
            TimeProvider.System,
            NullLogger<LazySunshinePairingSession>.Instance,
            () => appliedRevision);

        Assert.Empty(await pairing.ListAsync(default));
        Assert.Equal(1, protector.UnprotectCalls);
        Assert.Contains("first-password", protector.LastPlaintext);

        credentialStore.Save(new SunshineManagementCredential("fleet-user", "second-password"), replace: true);
        Assert.Empty(await pairing.ListAsync(default));
        Assert.Equal(1, protector.UnprotectCalls);

        appliedRevision = 2;
        Assert.Empty(await pairing.ListAsync(default));
        Assert.Equal(2, protector.UnprotectCalls);
        Assert.Contains("second-password", protector.LastPlaintext);
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

    private sealed class ClosedPairingFence : IPairingFence
    {
        public Task<bool> VerifyOpenForLeaseAsync(PairingScope scope, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RunFencedCleanupAsync(Func<CancellationToken, Task> clear, CancellationToken ct) => Task.FromResult(false);
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
