using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace KamenRaider.Agent.Probe;

internal sealed record ProbeStorePolicy(
    string VolumeId,
    SecurityIdentifier AgentServiceSid,
    IReadOnlyList<string> ProtectedRoots);

internal sealed record ProbeVolumeIdentity(
    string VolumeId,
    string RootPath,
    string FileSystem,
    uint DriveType,
    uint VolumeSerialNumber)
{
    internal bool IsFixedNtfs =>
        DriveType == WindowsProbeVolume.DriveFixed &&
        string.Equals(FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);
}

[SupportedOSPlatform("windows")]
internal static class WindowsProbeVolume
{
    internal const uint DriveFixed = 3;

    internal static ProbeVolumeIdentity GetIdentity(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Managed probe volume inspection requires Windows.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var volumeRoot = new System.Text.StringBuilder(1024);
        if (!GetVolumePathName(Path.GetFullPath(path), volumeRoot, (uint)volumeRoot.Capacity))
            throw new IOException("The managed probe path did not resolve to a local volume.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));

        var root = volumeRoot.ToString();
        var driveType = GetDriveType(root);
        var volumeName = new System.Text.StringBuilder(1024);
        if (!GetVolumeNameForVolumeMountPoint(root, volumeName, (uint)volumeName.Capacity))
            throw new IOException("The managed probe volume identity could not be read.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));

        var fileSystemName = new System.Text.StringBuilder(64);
        if (!GetVolumeInformation(root, IntPtr.Zero, 0, out var serial, out _, out _, fileSystemName, (uint)fileSystemName.Capacity))
            throw new IOException("The managed probe filesystem could not be inspected.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));

        return new(volumeName.ToString(), root, fileSystemName.ToString(), driveType, serial);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetVolumePathNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, System.Text.StringBuilder volumePathName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, System.Text.StringBuilder volumeName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetVolumeInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        IntPtr volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        System.Text.StringBuilder fileSystemNameBuffer,
        uint fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDriveTypeW")]
    private static extern uint GetDriveType(string rootPathName);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsManagedProbeFile : IManagedProbeFile
{
    private const uint FileAddFile = 0x00000002;
    private const uint FileAddSubdirectory = 0x00000004;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ReadControl = 0x00020000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeDirectory = (uint)FileAttributes.Directory;
    private const uint FileAttributeReparsePoint = (uint)FileAttributes.ReparsePoint;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const uint FileObjectType = 1;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const ushort SecurityDescriptorDaclPresent = 0x0004;
    private const ushort SecurityDescriptorDaclProtected = 0x1000;
    private const int FileStandardInfoClass = 1;
    private const int FileIdInfoClass = 18;
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier OwnerRightsSid = new("S-1-3-4");

    private readonly ProbeStorePolicy policy;
    private readonly string[] protectedRoots;

    internal WindowsManagedProbeFile(ProbeStorePolicy policy)
    {
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (string.IsNullOrWhiteSpace(policy.VolumeId) || policy.AgentServiceSid is null ||
            policy.ProtectedRoots is null || policy.ProtectedRoots.Count == 0)
            throw new ArgumentException("Managed probe policy requires a volume ID, Agent service SID, and protected roots.", nameof(policy));

        protectedRoots = policy.ProtectedRoots
            .Select(root =>
            {
                if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                    throw new ArgumentException("Protected probe roots must be absolute local paths.", nameof(policy));
                return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            })
            .ToArray();
    }

    public Task<ValidatedProbeLocation> ValidateAsync(
        string parent,
        string volumeId,
        string machineId,
        MachineSecurityContext tenant,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Protected managed probe operations require Windows.");
        ArgumentNullException.ThrowIfNull(tenant);
        ct.ThrowIfCancellationRequested();

        var managedFilePath = ProbeStorePath.BuildManagedFilePath(parent, machineId);
        var fullParent = Path.GetFullPath(parent);
        var configuredVolume = WindowsProbeVolume.GetIdentity(fullParent);
        if (!configuredVolume.IsFixedNtfs ||
            !string.Equals(configuredVolume.VolumeId, volumeId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(configuredVolume.VolumeId, policy.VolumeId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The managed probe path is not on its configured fixed NTFS volume.");

        if (WouldOverlapProtectedRoot(Path.GetDirectoryName(managedFilePath)!, protectedRoots))
            throw new UnauthorizedAccessException("The managed probe child overlaps a protected system, application, or data path.");

        var actualTenantSid = WindowsProbeAccessEvaluator.GetTokenUserSid(tenant.TenantToken);
        if (actualTenantSid is null ||
            !string.Equals(actualTenantSid, tenant.UserSid, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The supplied tenant token does not match the selected user SID.");

        var assessments = new List<ProbeDirectoryAssessment>();
        SafeAccessTokenHandle? ownedTenantToken = null;
        try
        {
            ownedTenantToken = WindowsProbeAccessEvaluator.DuplicateTenantToken(tenant.TenantToken);
            foreach (var ancestor in DirectoryAncestors(fullParent))
            {
                ct.ThrowIfCancellationRequested();
                var additionalAccess = string.Equals(ancestor, fullParent, StringComparison.OrdinalIgnoreCase)
                    ? FileAddFile | FileAddSubdirectory
                    : 0;
                var assessment = WindowsProbeAccessEvaluator.AssessDirectory(ancestor, ownedTenantToken, additionalAccess);
                assessments.Add(assessment);
                var expectedFinalPath = "\\\\?\\" + Path.GetFullPath(ancestor);
                if (assessment.Verdict == ProbeFileAccessVerdict.Rejected ||
                    !string.Equals(assessment.FinalPath, expectedFinalPath, StringComparison.OrdinalIgnoreCase) ||
                    assessment.VolumeSerialNumber != configuredVolume.VolumeSerialNumber)
                    throw new UnauthorizedAccessException("The managed probe ancestry is writable by the tenant or does not match the validated path and volume.");
                if (assessment.Verdict != ProbeFileAccessVerdict.Validated)
                    throw new IOException("The managed probe ancestry could not be validated against the tenant token.");
            }

            var location = new WindowsValidatedProbeLocation(
                fullParent,
                configuredVolume.VolumeId,
                machineId,
                configuredVolume.VolumeSerialNumber,
                ownedTenantToken,
                assessments);
            ownedTenantToken = null;
            assessments = [];
            return Task.FromResult<ValidatedProbeLocation>(location);
        }
        catch
        {
            foreach (var assessment in assessments)
                assessment.Dispose();
            ownedTenantToken?.Dispose();
            throw;
        }
    }

    public Task<ProbeFileRead> ProvisionAsync(
        ValidatedProbeLocation location,
        ReadOnlyMemory<byte> clean,
        CancellationToken ct)
    {
        var scope = BeginOperation(location);
        try
        {
            ct.ThrowIfCancellationRequested();
            var record = ProbeFileFormat.Decode(clean.Span);
            if (record.Kind != "clean" || record.Generation != 0 || record.MachineId != scope.MachineId)
                throw new InvalidDataException("Provisioning requires the exact clean record for this machine.");

            using var directories = OpenManagedDirectories(scope, createIfMissing: true);
            if (TryOpenExistingMarker(scope, directories, readWrite: true, out var existing, out var existingFileId))
            {
                using (existing)
                {
                    var existingContent = WindowsProbeFileIo.ReadAndVerify(existing);
                    if (!CryptographicOperations.FixedTimeEquals(existingContent.Sha256, SHA256.HashData(clean.Span)))
                        throw new InvalidDataException("An existing managed marker is not the requested clean baseline; it will not be overwritten.");
                    return Task.FromResult(ProbeFileRead.FromContent(
                        existingContent,
                        scope.VolumeId,
                        existingFileId,
                        ProbeFileAccessVerdict.Validated));
                }
            }

            using var created = CreateMarker(scope);
            var createdFileId = ValidateMarkerHandle(scope, created.SafeFileHandle, readWrite: true);
            var content = WindowsProbeFileIo.WriteAndVerify(created, clean.Span);
            return Task.FromResult(ProbeFileRead.FromContent(
                content,
                scope.VolumeId,
                createdFileId,
                ProbeFileAccessVerdict.Validated));
        }
        finally
        {
            scope.Dispose();
        }
    }

    public Task<ProbeFileRead> ReadAsync(ValidatedProbeLocation location, CancellationToken ct)
    {
        var scope = BeginOperation(location);
        try
        {
            ct.ThrowIfCancellationRequested();
            using var directories = OpenManagedDirectories(scope, createIfMissing: false);
            if (!TryOpenExistingMarker(scope, directories, readWrite: false, out var marker, out var fileId))
                throw new FileNotFoundException("The managed probe marker is missing.");
            using (marker)
            {
                var content = WindowsProbeFileIo.ReadAndVerify(marker);
                return Task.FromResult(ProbeFileRead.FromContent(
                    content,
                    scope.VolumeId,
                    fileId,
                    ProbeFileAccessVerdict.Validated));
            }
        }
        finally
        {
            scope.Dispose();
        }
    }

    public Task<ProbeFileRead> WriteAndVerifyAsync(
        ValidatedProbeLocation location,
        ReadOnlyMemory<byte> expected,
        CancellationToken ct)
    {
        var scope = BeginOperation(location);
        try
        {
            ct.ThrowIfCancellationRequested();
            _ = ProbeFileFormat.Decode(expected.Span);
            using var directories = OpenManagedDirectories(scope, createIfMissing: false);
            if (!TryOpenExistingMarker(scope, directories, readWrite: true, out var marker, out var fileId))
                throw new FileNotFoundException("The managed probe marker is missing.");
            using (marker)
            {
                var content = WindowsProbeFileIo.WriteAndVerify(marker, expected.Span);
                return Task.FromResult(ProbeFileRead.FromContent(
                    content,
                    scope.VolumeId,
                    fileId,
                    ProbeFileAccessVerdict.Validated));
            }
        }
        finally
        {
            scope.Dispose();
        }
    }

    private WindowsValidatedProbeLocation BeginOperation(ValidatedProbeLocation location)
    {
        if (location is not WindowsValidatedProbeLocation windowsLocation)
            throw new ArgumentException("The validated probe location was not created by the Windows managed store.", nameof(location));
        windowsLocation.BeginOperation();
        return windowsLocation;
    }

    private SafeDirectorySet OpenManagedDirectories(WindowsValidatedProbeLocation location, bool createIfMissing)
    {
        RevalidateParentChain(location);
        var probeRoot = Path.Combine(location.ParentPath, "KamenRaiderProbe");
        var machineDirectory = Path.Combine(probeRoot, location.MachineId);
        var assessments = new List<ProbeDirectoryAssessment>();
        try
        {
            if (createIfMissing)
                EnsurePrivateDirectoryExists(probeRoot);
            var probeRootAssessment = WindowsProbeAccessEvaluator.AssessDirectory(
                probeRoot,
                location.TenantToken,
                FileAddFile | FileAddSubdirectory);
            assessments.Add(probeRootAssessment);
            EnsureManagedDirectoryAssessment(location, probeRootAssessment, probeRoot);

            if (createIfMissing)
                EnsurePrivateDirectoryExists(machineDirectory);
            var machineAssessment = WindowsProbeAccessEvaluator.AssessDirectory(
                machineDirectory,
                location.TenantToken,
                FileAddFile | FileAddSubdirectory);
            assessments.Add(machineAssessment);
            EnsureManagedDirectoryAssessment(location, machineAssessment, machineDirectory);
            return new SafeDirectorySet(assessments);
        }
        catch
        {
            foreach (var assessment in assessments)
                assessment.Dispose();
            throw;
        }
    }

    private void RevalidateParentChain(WindowsValidatedProbeLocation location)
    {
        foreach (var held in location.Assessments)
        {
            if (held.Handle.IsInvalid || held.Handle.IsClosed ||
                !Native.GetFileInformationByHandle(held.Handle, out var info) ||
                (info.FileAttributes & FileAttributeDirectory) == 0 ||
                (info.FileAttributes & FileAttributeReparsePoint) != 0 ||
                !HasExpectedFinalPath(held.Handle, held.FinalPath) ||
                info.VolumeSerialNumber != location.VolumeSerialNumber)
                throw new IOException("The validated managed probe ancestry changed during the operation.");

            using var rechecked = WindowsProbeAccessEvaluator.AssessDirectory(
                held.RequestedPath ?? throw new IOException("The validated directory path was lost."),
                location.TenantToken,
                string.Equals(held.RequestedPath, location.ParentPath, StringComparison.OrdinalIgnoreCase)
                    ? FileAddFile | FileAddSubdirectory
                    : 0);
            if (rechecked.Verdict != ProbeFileAccessVerdict.Validated ||
                !string.Equals(rechecked.FinalPath, held.FinalPath, StringComparison.OrdinalIgnoreCase) ||
                rechecked.VolumeSerialNumber != held.VolumeSerialNumber)
                throw new UnauthorizedAccessException("Tenant access changed for a validated managed probe ancestor.");
        }
    }

    private void EnsureManagedDirectoryAssessment(
        WindowsValidatedProbeLocation location,
        ProbeDirectoryAssessment assessment,
        string expectedPath)
    {
        if (assessment.Verdict != ProbeFileAccessVerdict.Validated ||
            !string.Equals(assessment.FinalPath, "\\\\?\\" + Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase) ||
            assessment.VolumeSerialNumber != location.VolumeSerialNumber ||
            !HasExpectedPrivateAcl(assessment.Handle, isDirectory: true))
            throw new UnauthorizedAccessException("A managed probe directory has unsafe ancestry, volume, owner, or ACL state.");
    }

    private void EnsurePrivateDirectoryExists(string path)
    {
        if (PathEntryExists(path))
            return;

        try
        {
            var security = CreatePrivateDirectorySecurity();
            new DirectoryInfo(path).Create(security);
        }
        catch (IOException) when (PathEntryExists(path))
        {
            // Another creator won the fixed-name race; the object is opened and fully validated below.
        }
    }

    private DirectorySecurity CreatePrivateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(policy.AgentServiceSid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(OwnerRightsSid, FileSystemRights.ReadPermissions | FileSystemRights.ReadAttributes, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private FileSecurity CreatePrivateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(policy.AgentServiceSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(OwnerRightsSid, FileSystemRights.ReadPermissions | FileSystemRights.ReadAttributes, AccessControlType.Allow));
        return security;
    }

    private bool TryOpenExistingMarker(
        WindowsValidatedProbeLocation location,
        SafeDirectorySet directories,
        bool readWrite,
        out FileStream stream,
        out string fileId)
    {
        var markerPath = Path.Combine(location.ParentPath, "KamenRaiderProbe", location.MachineId, "restore-test.bin");
        var desiredAccess = GenericRead | (readWrite ? GenericWrite : 0) | ReadControl | FileReadAttributes;
        var handle = Native.CreateFile(
            markerPath,
            desiredAccess,
            shareMode: 0,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagWriteThrough,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                stream = null!;
                fileId = string.Empty;
                return false;
            }
            throw new IOException("The managed probe file could not be opened exclusively and without following reparse points.", new System.ComponentModel.Win32Exception(error));
        }

        try
        {
            fileId = ValidateMarkerHandle(location, handle, readWrite);
            stream = new FileStream(handle, readWrite ? FileAccess.ReadWrite : FileAccess.Read, 4096, isAsync: false);
            return true;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private FileStream CreateMarker(WindowsValidatedProbeLocation location)
    {
        var path = Path.Combine(location.ParentPath, "KamenRaiderProbe", location.MachineId, "restore-test.bin");
        try
        {
            return FileSystemAclExtensions.Create(
                new FileInfo(path),
                FileMode.CreateNew,
                FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.AppendData |
                FileSystemRights.ReadAttributes | FileSystemRights.WriteAttributes |
                FileSystemRights.ReadExtendedAttributes | FileSystemRights.WriteExtendedAttributes |
                FileSystemRights.ReadPermissions | FileSystemRights.Synchronize,
                FileShare.None,
                4096,
                FileOptions.WriteThrough,
                CreatePrivateFileSecurity());
        }
        catch (IOException) when (PathEntryExists(path))
        {
            // The file is never overwritten by provisioning; caller will reopen and compare it with clean.
            throw new IOException("A managed marker appeared during provisioning; retry validation before deciding whether it is the expected clean record.");
        }
    }

    private string ValidateMarkerHandle(WindowsValidatedProbeLocation location, SafeFileHandle handle, bool readWrite)
    {
        if (!Native.GetFileInformationByHandle(handle, out var information) ||
            information.NumberOfLinks != 1 ||
            (information.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 ||
            information.VolumeSerialNumber != location.VolumeSerialNumber)
            throw new UnauthorizedAccessException("The managed marker is not a single-link regular file on the validated volume.");

        var id = ReadFileId(handle);
        var expectedPath = Path.Combine(location.ParentPath, "KamenRaiderProbe", location.MachineId, "restore-test.bin");
        if (id.VolumeSerialNumber == 0 ||
            id.VolumeSerialNumber != information.VolumeSerialNumber ||
            !HasExpectedFinalPath(handle, "\\\\?\\" + Path.GetFullPath(expectedPath)) ||
            !HasExpectedPrivateAcl(handle, isDirectory: false) ||
            WindowsProbeAccessEvaluator.AssessFileAccess(handle, location.TenantToken) != ProbeFileAccessVerdict.Validated)
            throw new UnauthorizedAccessException("The managed marker has unsafe path, volume, owner, ACL, or tenant access state.");

        if (!readWrite && (information.FileAttributes & FileAttributeDirectory) != 0)
            throw new UnauthorizedAccessException("The managed marker path is not a regular file.");
        return id.FileId.ToString("N");
    }

    private bool HasExpectedPrivateAcl(SafeFileHandle handle, bool isDirectory)
    {
        var error = Native.GetSecurityInfo(
            handle,
            FileObjectType,
            OwnerSecurityInformation | DaclSecurityInformation,
            out var ownerPointer,
            out _,
            out var daclPointer,
            out _,
            out var descriptor);
        if (error != 0 || descriptor == IntPtr.Zero || ownerPointer == IntPtr.Zero)
        {
            if (descriptor != IntPtr.Zero)
                _ = Native.LocalFree(descriptor);
            return false;
        }

        try
        {
            var owner = new SecurityIdentifier(ownerPointer);
            if (owner != SystemSid && owner != AdministratorsSid && owner != policy.AgentServiceSid)
                return false;
            if (!Native.GetSecurityDescriptorControl(descriptor, out var control, out _) ||
                (control & SecurityDescriptorDaclProtected) == 0 ||
                (control & SecurityDescriptorDaclPresent) == 0 ||
                !Native.GetSecurityDescriptorDacl(descriptor, out var daclPresent, out var actualDacl, out _) ||
                !daclPresent || actualDacl == IntPtr.Zero || actualDacl != daclPointer)
                return false;

            var header = new byte[8];
            Marshal.Copy(actualDacl, header, 0, header.Length);
            var aclLength = BitConverter.ToUInt16(header, 2);
            if (aclLength < header.Length)
                return false;
            var aclBytes = new byte[aclLength];
            Marshal.Copy(actualDacl, aclBytes, 0, aclBytes.Length);
            var acl = new RawAcl(aclBytes, 0);
            if (acl.Count != 4)
                return false;

            var expectedInheritance = isDirectory
                ? AceFlags.ContainerInherit | AceFlags.ObjectInherit
                : AceFlags.None;
            var trustees = new HashSet<SecurityIdentifier>();
            foreach (var ace in acl)
            {
                if (ace is not CommonAce commonAce ||
                    commonAce.AceQualifier != AceQualifier.AccessAllowed ||
                    commonAce.AceFlags != expectedInheritance ||
                    !trustees.Add(commonAce.SecurityIdentifier))
                    return false;
                var expectedMask = commonAce.SecurityIdentifier == OwnerRightsSid
                    ? (int)(FileSystemRights.ReadPermissions | FileSystemRights.ReadAttributes)
                    : commonAce.SecurityIdentifier == SystemSid || commonAce.SecurityIdentifier == AdministratorsSid ||
                      commonAce.SecurityIdentifier == policy.AgentServiceSid
                        ? (int)FileSystemRights.FullControl
                        : int.MinValue;
                if (commonAce.AccessMask != expectedMask)
                    return false;
            }

            return trustees.SetEquals([SystemSid, AdministratorsSid, policy.AgentServiceSid, OwnerRightsSid]);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return false;
        }
        finally
        {
            _ = Native.LocalFree(descriptor);
        }
    }

    private static bool HasExpectedFinalPath(SafeFileHandle handle, string? expectedPath)
    {
        if (expectedPath is null)
            return false;
        var buffer = new System.Text.StringBuilder(512);
        var length = Native.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
            return false;
        if (length >= buffer.Capacity)
        {
            buffer = new System.Text.StringBuilder(checked((int)length + 1));
            length = Native.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
                return false;
        }
        return string.Equals(buffer.ToString(), expectedPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEntryExists(string path)
    {
        var handle = Native.CreateFile(
            path,
            FileReadAttributes,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            handle.Dispose();
            return true;
        }
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is ErrorFileNotFound or ErrorPathNotFound)
            return false;
        throw new IOException("A managed probe path entry could not be inspected safely.", new System.ComponentModel.Win32Exception(error));
    }

    private static FileIdInformation ReadFileId(SafeFileHandle handle)
    {
        if (!Native.GetFileInformationByHandleEx(
                handle,
                FileIdInfoClass,
                out var fileId,
                checked((uint)Marshal.SizeOf<FileIdInformation>())))
            throw new IOException("The managed probe file ID could not be read from its open handle.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        return fileId;
    }

    private static bool WouldOverlapProtectedRoot(string managedDirectory, IEnumerable<string> roots)
    {
        var candidate = managedDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var root in roots)
        {
            if (IsSameOrDescendant(candidate, root) || IsSameOrDescendant(root, candidate))
                return true;
        }

        return false;
    }

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> DirectoryAncestors(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath)
            ?? throw new UnauthorizedAccessException("The managed probe parent has no volume root.");
        var ancestors = new List<string> { root };
        var relative = fullPath[root.Length..];
        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            ancestors.Add(current);
        }

        return ancestors;
    }

    private sealed class SafeDirectorySet(IReadOnlyList<ProbeDirectoryAssessment> assessments) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            foreach (var assessment in assessments)
                assessment.Dispose();
        }
    }

    private sealed class WindowsValidatedProbeLocation(
        string parentPath,
        string volumeId,
        string machineId,
        uint volumeSerialNumber,
        SafeAccessTokenHandle tenantToken,
        IReadOnlyList<ProbeDirectoryAssessment> assessments) : ValidatedProbeLocation
    {
        private int operationStarted;
        private int disposed;

        internal override string ParentPath => parentPath;
        internal override string VolumeId => volumeId;
        internal override string MachineId => machineId;
        internal uint VolumeSerialNumber => volumeSerialNumber;
        internal SafeAccessTokenHandle TenantToken => tenantToken;
        internal IReadOnlyList<ProbeDirectoryAssessment> Assessments => assessments;

        internal void BeginOperation()
        {
            if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref operationStarted, 1) != 0)
                throw new InvalidOperationException("A validated probe location is single-use and operation-scoped.");
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            foreach (var assessment in assessments)
                assessment.Dispose();
            tenantToken.Dispose();
        }
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

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation fileInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandleEx(
            SafeFileHandle handle,
            int fileInformationClass,
            out FileIdInformation fileInformation,
            uint bufferSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandle(
            SafeFileHandle handle,
            System.Text.StringBuilder path,
            uint pathLength,
            uint flags);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern uint GetSecurityInfo(
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
        internal static extern bool GetSecurityDescriptorControl(
            IntPtr securityDescriptor,
            out ushort control,
            out uint revision);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSecurityDescriptorDacl(
            IntPtr securityDescriptor,
            out bool daclPresent,
            out IntPtr dacl,
            out bool daclDefaulted);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr memory);
    }
}
