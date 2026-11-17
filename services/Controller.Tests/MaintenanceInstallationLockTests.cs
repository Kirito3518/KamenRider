using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using KamenRaider.AgentMaintenance;

namespace Controller.Tests;

[SupportedOSPlatform("windows")]
public sealed class MaintenanceInstallationLockTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "kr-install-lock-" + Guid.NewGuid().ToString("N"));

    public MaintenanceInstallationLockTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string LockDirectory => Path.Combine(root, "KamenRaider.AgentMaintenance");
    private string ProtectedLockDirectory => Path.Combine(root, "protected", "KamenRaider.AgentMaintenance");

    [Fact]
    public void SecondUpdaterIsExcludedWhileTheInstallationLockIsHeld()
    {
        if (!OperatingSystem.IsWindows()) return;

        var first = WindowsMaintenanceInstallationLock.TryAcquire(LockDirectory, enforceProtectedSecurity: false);
        Assert.NotNull(first);
        using (first)
        {
            first!.VerifyContinuouslyHeld("operation-one");
            var second = WindowsMaintenanceInstallationLock.TryAcquire(LockDirectory, enforceProtectedSecurity: false);
            Assert.Null(second);
        }

        var third = WindowsMaintenanceInstallationLock.TryAcquire(LockDirectory, enforceProtectedSecurity: false);
        Assert.NotNull(third);
        third!.Dispose();

    }

    [Fact]
    public void ProviderReportsBusyLockAsUnqualifiedExclusion()
    {
        if (!OperatingSystem.IsWindows()) return;

        var provider = new WindowsMaintenanceInstallationLockProvider(LockDirectory, enforceProtectedSecurity: false);
        var first = provider.Acquire();
        Assert.NotNull(first);
        using (first)
        {
            var second = provider.Acquire();
            Assert.Null(second);
        }

        var third = provider.Acquire();
        Assert.NotNull(third);
        third!.Dispose();
    }

    [Fact]
    public void HeldLockRejectsAnotherOperationAndFailsClosedAfterRelease()
    {
        if (!OperatingSystem.IsWindows()) return;

        var lease = WindowsMaintenanceInstallationLock.TryAcquire(LockDirectory, enforceProtectedSecurity: false);
        Assert.NotNull(lease);
        lease!.VerifyContinuouslyHeld("operation-a");
        Assert.Throws<InvalidOperationException>(() => lease.VerifyContinuouslyHeld("operation-b"));
        Assert.Throws<ArgumentException>(() => lease.VerifyContinuouslyHeld("not valid/operation"));

        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => lease.VerifyContinuouslyHeld("operation-a"));
    }

    [Fact]
    public void LockFileExistsWhileTheInstallationLockIsHeld()
    {
        if (!OperatingSystem.IsWindows()) return;

        var lease = WindowsMaintenanceInstallationLock.TryAcquire(LockDirectory, enforceProtectedSecurity: false);
        Assert.NotNull(lease);
        using (lease)
        {
            lease!.VerifyContinuouslyHeld("operation-holder");
            var path = Path.Combine(LockDirectory, MaintenanceInstallationLockPath.LockFileName);
            Assert.True(File.Exists(path));
            var attributes = File.GetAttributes(path);
            Assert.False(attributes.HasFlag(FileAttributes.Directory));
            Assert.False(attributes.HasFlag(FileAttributes.ReparsePoint));
        }
    }

    [ElevatedWindowsWorkStoreFact]
    public void ProtectedLockDirectoryIsAdministratorsOnlyAndContinuesToVerify()
    {
        if (!OperatingSystem.IsWindows()) return;

        var lease = WindowsMaintenanceInstallationLock.TryAcquire(ProtectedLockDirectory, enforceProtectedSecurity: true);
        Assert.NotNull(lease);
        using (lease)
        {
            lease!.VerifyContinuouslyHeld("operation-protected");

            var security = new DirectoryInfo(ProtectedLockDirectory)
                .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            Assert.True(security.AreAccessRulesProtected);
            var owner = security.GetOwner(typeof(SecurityIdentifier));
            Assert.NotNull(owner);
            Assert.Contains(owner!.Value, new[] { "S-1-5-18", "S-1-5-32-544" });
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();
            Assert.Equal(3, rules.Length);
            Assert.Contains(rules, rule => rule.IdentityReference is SecurityIdentifier sid && sid.Value == "S-1-5-18" && rule.FileSystemRights == FileSystemRights.FullControl && !rule.IsInherited);
            Assert.Contains(rules, rule => rule.IdentityReference is SecurityIdentifier sid && sid.Value == "S-1-5-32-544" && rule.FileSystemRights == FileSystemRights.FullControl && !rule.IsInherited);
            var agentServiceRule = Assert.Single(rules, rule => rule.IdentityReference is SecurityIdentifier sid && sid.Value is not ("S-1-5-18" or "S-1-5-32-544"));
            Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, agentServiceRule.FileSystemRights);
            Assert.DoesNotContain(rules, rule =>
                rule.FileSystemRights != FileSystemRights.FullControl &&
                (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0);
        }
    }
}
