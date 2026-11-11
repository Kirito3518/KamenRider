using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using KamenRaider.Agent.Sunshine;

namespace Agent.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsMaintenanceFenceSecurityTests
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier AgentServiceSid = new("S-1-5-80-1-2-3-4-5");

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void FileFenceAclAllowsAgentReadButNoAgentOrUntrustedWrites(bool agentCanWrite, bool worldCanWrite, bool expected)
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.SetOwner(SystemSid);
        acl.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(
            AgentServiceSid,
            agentCanWrite ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));
        if (worldCanWrite)
            acl.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.Modify,
                AccessControlType.Allow));

        Assert.Equal(expected, WindowsMaintenanceFenceReader.IsPrivateSecurity(
            acl,
            AgentServiceSid,
            isDirectory: false));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void DirectoryFenceAclMustInheritReadOnlyAgentRule(bool agentCanWrite, bool expected)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.SetOwner(SystemSid);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        acl.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(
            AgentServiceSid,
            agentCanWrite ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));

        Assert.Equal(expected, WindowsMaintenanceFenceReader.IsPrivateSecurity(
            acl,
            AgentServiceSid,
            isDirectory: true));
    }
}
