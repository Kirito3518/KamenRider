using System.Security.AccessControl;
using System.Security.Principal;

#pragma warning disable CA1416 // Windows-only probe with an explicit runtime guard.

namespace Agent.Tests;

/// <summary>
/// Some restricted shells/sandboxes cannot write NTFS security descriptors even for files the current
/// user owns, which makes every ACL-protected Agent store fail before its logic runs. That is an
/// environment limitation, not an Agent behavior: tests that need a real protected Agent file check
/// this probe and return early, exactly like the existing OS guards.
/// </summary>
internal static class WindowsAclProbe
{
    public static bool CanSetProtectedAcl { get; } = Probe();

    private static bool Probe()    {
        if (!OperatingSystem.IsWindows()) return false;
        var directory = Path.Combine(Path.GetTempPath(), "kr-acl-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "probe.txt");
            File.WriteAllText(path, "probe");
            var currentUser = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Windows user SID is unavailable.");
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(currentUser);
            security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
            return new FileInfo(path).GetAccessControl().AreAccessRulesProtected;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // The probe result is already decided; a leftover temp directory is harmless.
            }
        }
    }
}

#pragma warning restore CA1416
