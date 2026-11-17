using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace KamenRaider.Agent.Probe;

// One directory only. A caller must assess every ancestor and the managed child, retain all
// successful handles for the whole operation, and perform the separate reparse/volume/ACL checks.
internal sealed class ProbeDirectoryAssessment : IDisposable
{
    internal ProbeDirectoryAssessment(
        SafeFileHandle handle,
        ProbeFileAccessVerdict verdict,
        string? finalPath = null,
        uint volumeSerialNumber = 0,
        string? fileId = null,
        string? requestedPath = null)
    {
        Handle = handle;
        Verdict = verdict;
        FinalPath = finalPath;
        VolumeSerialNumber = volumeSerialNumber;
        FileId = fileId;
        RequestedPath = requestedPath;
    }

    internal SafeFileHandle Handle { get; }
    internal ProbeFileAccessVerdict Verdict { get; }
    internal string? FinalPath { get; }
    internal uint VolumeSerialNumber { get; }
    internal string? FileId { get; }
    internal string? RequestedPath { get; }

    public void Dispose() => Handle.Dispose();
}

[SupportedOSPlatform("windows")]
internal static class WindowsProbeAccessEvaluator
{
    private const uint ReadControl = 0x00020000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint SecurityInformationOwner = 0x00000001;
    private const uint SecurityInformationDacl = 0x00000004;
    private const uint FileObjectType = 1;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint MaximumPrivilegeSetBytes = 1024 * 64;
    private const uint DirectoryAttributes = (uint)FileAttributes.Directory;
    private const uint ReparseAttribute = (uint)FileAttributes.ReparsePoint;

    private static readonly uint[] DirectoryMutationRights =
    [
        0x00000002, // FILE_ADD_FILE
        0x00000004, // FILE_ADD_SUBDIRECTORY
        0x00000040, // FILE_DELETE_CHILD
        0x00000100, // FILE_WRITE_ATTRIBUTES
        0x00010000, // DELETE
        0x00040000, // WRITE_DAC
        0x00080000, // WRITE_OWNER
    ];

    private static readonly uint[] FileMutationRights =
    [
        0x00000002, // FILE_WRITE_DATA
        0x00000004, // FILE_APPEND_DATA
        0x00000010, // FILE_WRITE_EA
        0x00000100, // FILE_WRITE_ATTRIBUTES
        0x00010000, // DELETE
        0x00040000, // WRITE_DAC
        0x00080000, // WRITE_OWNER
    ];

    private static readonly GenericMapping FileGenericMapping = new()
    {
        GenericRead = 0x00120089,
        GenericWrite = 0x00120116,
        GenericExecute = 0x001200A0,
        GenericAll = 0x001F01FF,
    };

    internal static ProbeDirectoryAssessment AssessDirectory(
        string path,
        SafeAccessTokenHandle tenantToken,
        uint agentAdditionalAccess = 0)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Managed probe ACL assessment requires Windows.");

        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            tenantToken is null || tenantToken.IsInvalid || tenantToken.IsClosed)
            return new(InvalidHandle(), ProbeFileAccessVerdict.Rejected);

        var handle = CreateFile(
            path,
            ReadControl | FileReadAttributes | agentAdditionalAccess,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return new(InvalidHandle(), ProbeFileAccessVerdict.Unavailable);
        }

        if (!GetFileInformationByHandle(handle, out var information))
            return new(handle, ProbeFileAccessVerdict.Unavailable);
        if ((information.FileAttributes & DirectoryAttributes) == 0 ||
            (information.FileAttributes & ReparseAttribute) != 0)
            return new(handle, ProbeFileAccessVerdict.Rejected);

        var finalPath = GetFinalPath(handle);
        if (finalPath is null)
            return new(handle, ProbeFileAccessVerdict.Unavailable);
        var expectedPath = "\\\\?\\" + Path.GetFullPath(path);
        if (!string.Equals(finalPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            return new(handle, ProbeFileAccessVerdict.Rejected, finalPath);

        if (!GetFileInformationByHandleEx(
                handle,
                FileIdInfoClass,
                out var fileIdentity,
                checked((uint)Marshal.SizeOf<FileIdInformation>())))
            return new(handle, ProbeFileAccessVerdict.Unavailable, finalPath, information.VolumeSerialNumber);

        var verdict = AssessEffectiveMutationRights(handle, tenantToken, DirectoryMutationRights);
        return new(handle, verdict, finalPath, information.VolumeSerialNumber, fileIdentity.FileId.ToString("N"), Path.GetFullPath(path));
    }

    internal static ProbeFileAccessVerdict AssessFileAccess(SafeFileHandle file, SafeAccessTokenHandle tenantToken)
    {
        if (!OperatingSystem.IsWindows() || file is null || file.IsInvalid || file.IsClosed ||
            tenantToken is null || tenantToken.IsInvalid || tenantToken.IsClosed)
            return ProbeFileAccessVerdict.Unavailable;
        return AssessEffectiveMutationRights(file, tenantToken, FileMutationRights);
    }

    internal static SafeAccessTokenHandle DuplicateTenantToken(SafeAccessTokenHandle tenantToken)
    {
        if (!OperatingSystem.IsWindows() || tenantToken is null || tenantToken.IsInvalid || tenantToken.IsClosed)
            throw new UnauthorizedAccessException("A live tenant token is required for managed probe access checks.");
        if (!DuplicateTokenEx(
                tenantToken,
                TokenQuery | TokenDuplicate,
                IntPtr.Zero,
                SecurityImpersonation,
                TokenImpersonation,
                out var impersonationToken))
            throw new IOException("The tenant token could not be duplicated for effective access checks.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        return impersonationToken;
    }

    internal static string? GetTokenUserSid(SafeAccessTokenHandle token)
    {
        if (!OperatingSystem.IsWindows() || token is null || token.IsInvalid || token.IsClosed)
            return null;

        _ = GetTokenInformation(token, TokenUserInformationClass, IntPtr.Zero, 0, out var requiredLength);
        if (requiredLength == 0 || requiredLength > 1024 * 1024)
            return null;

        var buffer = Marshal.AllocHGlobal(checked((int)requiredLength));
        try
        {
            if (!GetTokenInformation(token, TokenUserInformationClass, buffer, requiredLength, out _))
                return null;
            var tokenUser = Marshal.PtrToStructure<TokenUser>(buffer);
            return tokenUser.UserSid == IntPtr.Zero ? null : new SecurityIdentifier(tokenUser.UserSid).Value;
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new System.Text.StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
            return null;
        if (length >= buffer.Capacity)
        {
            buffer = new System.Text.StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
                return null;
        }

        return buffer.ToString();
    }

    private static ProbeFileAccessVerdict AssessEffectiveMutationRights(
        SafeFileHandle directory,
        SafeAccessTokenHandle tenantToken,
        IReadOnlyList<uint> mutationRights)
    {
        var error = GetSecurityInfo(
            directory,
            FileObjectType,
            SecurityInformationOwner | SecurityInformationDacl,
            out _,
            out _,
            out _,
            out _,
            out var securityDescriptor);
        if (error != 0 || securityDescriptor == IntPtr.Zero)
            return ProbeFileAccessVerdict.Unavailable;

        try
        {
            SafeAccessTokenHandle impersonationToken;
            try
            {
                impersonationToken = DuplicateTenantToken(tenantToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return ProbeFileAccessVerdict.Unavailable;
            }

            using (impersonationToken)
            {
                foreach (var right in mutationRights)
                {
                    var assessment = HasAccess(securityDescriptor, impersonationToken, right);
                    if (assessment is null)
                        return ProbeFileAccessVerdict.Unavailable;
                    if (assessment.Value)
                        return ProbeFileAccessVerdict.Rejected;
                }
            }

            return ProbeFileAccessVerdict.Validated;
        }
        finally
        {
            _ = LocalFree(securityDescriptor);
        }
    }

    private static bool? HasAccess(IntPtr securityDescriptor, SafeAccessTokenHandle token, uint right)
    {
        var privilegeSetLength = 1024u;
        var privilegeSet = Marshal.AllocHGlobal((int)privilegeSetLength);
        var genericMapping = FileGenericMapping;
        try
        {
            if (!AccessCheck(
                    securityDescriptor,
                    token,
                    right,
                    ref genericMapping,
                    privilegeSet,
                    ref privilegeSetLength,
                    out _,
                    out var accessStatus))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorInsufficientBuffer || privilegeSetLength > MaximumPrivilegeSetBytes)
                    return null;

                Marshal.FreeHGlobal(privilegeSet);
                privilegeSet = Marshal.AllocHGlobal(checked((int)privilegeSetLength));
                if (!AccessCheck(
                        securityDescriptor,
                        token,
                        right,
                        ref genericMapping,
                        privilegeSet,
                        ref privilegeSetLength,
                        out _,
                        out accessStatus))
                    return null;
            }

            return accessStatus;
        }
        finally
        {
            Marshal.FreeHGlobal(privilegeSet);
        }
    }

    private static SafeFileHandle InvalidHandle() => new(IntPtr.Zero, ownsHandle: false);

    private const uint ErrorInsufficientBuffer = 122;
    private const uint TokenUserInformationClass = 1;
    private const int FileIdInfoClass = 18;
    private const int SecurityImpersonation = 2;
    private const int TokenImpersonation = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct GenericMapping
    {
        internal uint GenericRead;
        internal uint GenericWrite;
        internal uint GenericExecute;
        internal uint GenericAll;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        internal ulong VolumeSerialNumber;
        internal Guid FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUser
    {
        internal IntPtr UserSid;
        internal uint Attributes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileIdInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        System.Text.StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle,
        uint objectType,
        uint securityInfo,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        uint tokenInformationClass,
        IntPtr tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AccessCheck(
        IntPtr securityDescriptor,
        SafeAccessTokenHandle clientToken,
        uint desiredAccess,
        ref GenericMapping genericMapping,
        IntPtr privilegeSet,
        ref uint privilegeSetLength,
        out uint grantedAccess,
        [MarshalAs(UnmanagedType.Bool)] out bool accessStatus);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
