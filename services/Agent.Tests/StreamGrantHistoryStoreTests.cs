using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using KamenRaider.Agent.Sunshine;

namespace Agent.Tests;

public class StreamGrantHistoryStoreTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void PrivateDirectorySecurityIsProtectedForSystemAndAdministrators()
    {
        var security = WindowsStreamGrantHistoryStore.CreatePrivateDirectorySecurity();
        var owner = Assert.IsType<SecurityIdentifier>(security.GetOwner(typeof(SecurityIdentifier)));
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal("S-1-5-18", owner.Value);
        Assert.Equal(2, rules.Length);
        Assert.Contains(rules, rule => rule.IdentityReference.Value == "S-1-5-18" && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        Assert.Contains(rules, rule => rule.IdentityReference.Value == "S-1-5-32-544" && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        Assert.All(rules, rule => Assert.Equal(AccessControlType.Allow, rule.AccessControlType));
    }

}
