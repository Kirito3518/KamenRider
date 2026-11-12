using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using KamenRaider.Controller.Data;

namespace KamenRaider.Controller.Tests;

[SupportedOSPlatform("windows")]
public sealed class AgentRestoreQuarantineTests
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int NativeFileRenameInformationClass = 10;

    [Fact]
    public void NewDatabaseEpochIsProtectedAndAdvancesMonotonicallyBeforeAdmission()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = directory.DatabasePath;

        Assert.Equal(1L, InvokeEpochMethod("InitializeForNewDatabase", databasePath));
        Assert.False(File.Exists(databasePath));
        File.WriteAllText(databasePath, "database placeholder");

        var sidecarPath = databasePath + ".restore-epoch";
        var initialBytes = File.ReadAllBytes(sidecarPath);
        Assert.Equal(2L, InvokeEpochMethod("AdvanceBeforeAdmission", databasePath));
        var advancedBytes = File.ReadAllBytes(sidecarPath);
        Assert.False(initialBytes.SequenceEqual(advancedBytes));
        Assert.Equal(3L, InvokeEpochMethod("AdvanceBeforeAdmission", databasePath));

        var security = FileSystemAclExtensions.GetAccessControl(new FileInfo(sidecarPath));
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(WindowsIdentity.GetCurrent().User, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.restore-epoch.*.tmp"));
    }

    [Fact]
    public void MissingEpochSidecarFailsClosedWithoutRecreatingIt()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = directory.DatabasePath;
        Assert.Equal(1L, InvokeEpochMethod("InitializeForNewDatabase", databasePath));
        File.WriteAllText(databasePath, "database placeholder");
        File.Delete(databasePath + ".restore-epoch");

        Assert.Throws<InvalidDataException>(() => InvokeEpochMethod("AdvanceBeforeAdmission", databasePath));
        Assert.False(File.Exists(databasePath + ".restore-epoch"));
    }

    [Fact]
    public void CorruptEpochSidecarFailsClosedAndPreservesTheEvidence()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = directory.DatabasePath;
        Assert.Equal(1L, InvokeEpochMethod("InitializeForNewDatabase", databasePath));
        File.WriteAllText(databasePath, "database placeholder");
        var sidecarPath = databasePath + ".restore-epoch";
        var corruptBytes = new byte[] { 0x4B, 0x52, 0x45, 0x50, 0x4F, 0x43, 0x48, 0x31, 0x00 };
        File.WriteAllBytes(sidecarPath, corruptBytes);

        Assert.Throws<InvalidDataException>(() => InvokeEpochMethod("AdvanceBeforeAdmission", databasePath));
        Assert.Equal(corruptBytes, File.ReadAllBytes(sidecarPath));
    }

    [Fact]
    public void SidecarWithAnUntrustedWriteAclFailsClosedWithoutAdvancing()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = directory.DatabasePath;
        Assert.Equal(1L, InvokeEpochMethod("InitializeForNewDatabase", databasePath));
        File.WriteAllText(databasePath, "database placeholder");
        var sidecarPath = databasePath + ".restore-epoch";
        var originalBytes = File.ReadAllBytes(sidecarPath);

        var security = FileSystemAclExtensions.GetAccessControl(new FileInfo(sidecarPath));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new FileInfo(sidecarPath), security);

        Assert.Throws<InvalidDataException>(() => InvokeEpochMethod("AdvanceBeforeAdmission", databasePath));
        Assert.Equal(originalBytes, File.ReadAllBytes(sidecarPath));
    }

    [Fact]
    public void DatabaseWithAnUntrustedWriteAclFailsClosedWithoutAdvancing()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = directory.DatabasePath;
        Assert.Equal(1L, InvokeEpochMethod("InitializeForNewDatabase", databasePath));
        File.WriteAllText(databasePath, "database placeholder");
        var sidecarPath = databasePath + ".restore-epoch";
        var originalBytes = File.ReadAllBytes(sidecarPath);

        var security = FileSystemAclExtensions.GetAccessControl(new FileInfo(databasePath));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new FileInfo(databasePath), security);

        Assert.Throws<InvalidDataException>(() => InvokeEpochMethod("AdvanceBeforeAdmission", databasePath));
        Assert.Equal(originalBytes, File.ReadAllBytes(sidecarPath));
    }

    [Fact]
    public void UntrustedWritableAncestorBlocksEpochCreationEvenWhenDatabaseFolderIsProtected()
    {
        using var directory = new ProtectedTestDirectory();
        var writableAncestor = System.IO.Path.Combine(directory.Path, "shared-parent");
        var protectedDatabaseDirectory = System.IO.Path.Combine(writableAncestor, "controller-data");
        Directory.CreateDirectory(writableAncestor);
        SetDirectoryAcl(writableAncestor, allowWorldFullControl: true);
        Directory.CreateDirectory(protectedDatabaseDirectory);
        SetDirectoryAcl(protectedDatabaseDirectory, allowWorldFullControl: false);
        var databasePath = System.IO.Path.Combine(protectedDatabaseDirectory, "controller.db");

        Assert.Throws<InvalidDataException>(() => InvokeEpochMethod("InitializeForNewDatabase", databasePath));
        Assert.False(File.Exists(databasePath + ".restore-epoch"));
    }

    [Fact]
    public void DatabaseDirectoryWithoutAProtectedDaclFailsClosed()
    {
        using var directory = new ProtectedTestDirectory();
        var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(directory.Path));
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(directory.Path), security);

        Assert.Throws<InvalidDataException>(() => InvokeEpochMethod("InitializeForNewDatabase", directory.DatabasePath));
        Assert.False(File.Exists(directory.DatabasePath + ".restore-epoch"));
    }

    [Fact]
    public void InitialEpochCannotBeCreatedWhileAControllerInstanceOwnsTheDatabase()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = directory.DatabasePath;
        using var activeLock = new FileStream(databasePath + ".active.lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<InvalidOperationException>(() => InvokeEpochMethod("InitializeForNewDatabase", databasePath));
        Assert.False(File.Exists(databasePath + ".restore-epoch"));
    }

    [Fact]
    public void AdmissionEpochCannotAdvanceWhileAControllerInstanceOwnsTheDatabase()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = directory.DatabasePath;
        Assert.Equal(1L, InvokeEpochMethod("InitializeForNewDatabase", databasePath));
        File.WriteAllText(databasePath, "database placeholder");
        var sidecarPath = databasePath + ".restore-epoch";
        var originalBytes = File.ReadAllBytes(sidecarPath);
        using var activeLock = new FileStream(databasePath + ".active.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<InvalidOperationException>(() => InvokeEpochMethod("AdvanceBeforeAdmission", databasePath));
        Assert.Equal(originalBytes, File.ReadAllBytes(sidecarPath));
    }

    [Fact]
    public void MappedNetworkDriveIsRejectedByTheStorageInvariant()
    {
        Assert.Throws<InvalidDataException>(() => ControllerRestoreEpoch.ValidateFixedDrivePath(@"Z:\controller\controller.db", _ => 4));
        ControllerRestoreEpoch.ValidateFixedDrivePath(@"C:\controller\controller.db", _ => 3);
    }

    [Fact]
    public async Task ConcurrentStartupKeepsTheAdvancedEpochAndLifetimeOwnershipTogetherAfterTransfer()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = directory.DatabasePath;
        Assert.Equal(1L, InvokeEpochMethod("InitializeForNewDatabase", databasePath));
        File.WriteAllText(databasePath, "database placeholder");

        using var firstAdmissionReady = new ManualResetEventSlim();
        using var secondAttemptFinished = new ManualResetEventSlim();
        long firstEpoch = 0;

        var firstStart = Task.Run(() =>
        {
            using var admission = InvokeAdmissionLease(databasePath);
            firstEpoch = admission.Epoch;
            using var lifetime = admission.TakeLifetimeLease();
            admission.Dispose();
            firstAdmissionReady.Set();
            Assert.True(secondAttemptFinished.Wait(TimeSpan.FromSeconds(10)));
        });

        var secondStart = Task.Run(() =>
        {
            Assert.True(firstAdmissionReady.Wait(TimeSpan.FromSeconds(10)));
            try
            {
                using var admission = InvokeAdmissionLease(databasePath);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
            finally
            {
                secondAttemptFinished.Set();
            }
        });

        await firstStart;
        Assert.Equal(2L, firstEpoch);
        Assert.IsType<InvalidOperationException>(await secondStart);
        using var laterStart = InvokeAdmissionLease(databasePath);
        Assert.Equal(3L, laterStart.Epoch);
    }

    [Fact]
    public void TransferredLifetimeLeaseBlocksReplacementThroughAStaleDirectoryHandle()
    {
        using var directory = new ProtectedTestDirectory();
        var databasePath = directory.DatabasePath;
        Assert.Equal(1L, InvokeEpochMethod("InitializeForNewDatabase", databasePath));
        File.WriteAllText(databasePath, "database placeholder");
        var sidecarPath = databasePath + ".restore-epoch";
        var staleEpochBytes = File.ReadAllBytes(sidecarPath);

        using var admission = InvokeAdmissionLease(databasePath);
        Assert.Equal(2L, admission.Epoch);
        var advancedBytes = File.ReadAllBytes(sidecarPath);
        using var lifetime = admission.TakeLifetimeLease();
        admission.Dispose();

        SetDirectoryAcl(directory.Path, allowWorldFullControl: true);
        using var staleDirectoryHandle = OpenDirectoryWithAddAndDeleteChild(directory.Path);
        var replacementDirectory = System.IO.Path.Combine(directory.Path, "replacement-source");
        Directory.CreateDirectory(replacementDirectory);
        var replacementPath = System.IO.Path.Combine(replacementDirectory, "restore-epoch.replacement");
        File.WriteAllBytes(replacementPath, staleEpochBytes);
        FileSystemAclExtensions.SetAccessControl(new FileInfo(replacementPath), CreateProtectedFileSecurity());
        using var replacementHandle = OpenForRename(replacementPath);
        SetDirectoryAcl(directory.Path, allowWorldFullControl: false);

        Assert.False(TryRenameToName(replacementHandle, staleDirectoryHandle, System.IO.Path.GetFileName(sidecarPath), out var error),
            $"A stale directory handle replaced the sidecar while admission was open (Win32 error {error}).");
        Assert.True(error is ErrorAccessDenied or ErrorSharingViolation,
            $"The held sidecar handle failed for an unexpected reason (Win32 error {error}).");
        Assert.Equal(advancedBytes, File.ReadAllBytes(sidecarPath));

        lifetime.Dispose();
        Assert.True(TryRenameToName(replacementHandle, staleDirectoryHandle, System.IO.Path.GetFileName(sidecarPath), out error),
            $"The stale-handle replacement control did not work after releasing the lifetime lease (Win32 error {error}).");
        replacementHandle.Dispose();
        Assert.Equal(staleEpochBytes, File.ReadAllBytes(sidecarPath));
    }

    private static long InvokeEpochMethod(string methodName, string databasePath)
    {
        var result = InvokeEpochMethodRaw(methodName, databasePath);
        if (result is IDisposable lease)
        {
            using (lease)
                return ReadEpochProperty(lease);
        }

        return Assert.IsType<long>(result);
    }

    private static AdmissionHandle InvokeAdmissionLease(string databasePath) =>
        new(Assert.IsAssignableFrom<IDisposable>(InvokeEpochMethodRaw("AdvanceBeforeAdmission", databasePath)));

    private static object InvokeEpochMethodRaw(string methodName, string databasePath)
    {
        var epochType = typeof(ControllerDatabaseActivityLock).Assembly.GetType("KamenRaider.Controller.Data.ControllerRestoreEpoch");
        Assert.NotNull(epochType);
        MethodInfo? method;
        object?[] arguments;
        if (methodName == "AdvanceBeforeAdmission")
        {
            method = epochType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static,
                [typeof(string), typeof(Action<long>), typeof(Action<long>)]);
            arguments = [databasePath, new Action<long>(_ => { }), new Action<long>(_ => { })];
        }
        else
        {
            method = epochType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
            arguments = [databasePath];
        }
        Assert.NotNull(method);

        try
        {
            return Assert.IsAssignableFrom<object>(method.Invoke(null, arguments));
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static long ReadEpochProperty(IDisposable admission) =>
        Assert.IsType<long>(admission.GetType().GetProperty("Epoch", BindingFlags.Public | BindingFlags.Instance)?.GetValue(admission));

    private sealed class AdmissionHandle(IDisposable lease) : IDisposable
    {
        public long Epoch => ReadEpochProperty(lease);

        public IDisposable TakeLifetimeLease()
        {
            var method = lease.GetType().GetMethod("TakeLifetimeLease", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(method);
            return Assert.IsAssignableFrom<IDisposable>(method.Invoke(lease, null));
        }

        public void Dispose() => lease.Dispose();
    }

    private static SafeFileHandle OpenDirectoryWithAddAndDeleteChild(string path)
    {
        const uint fileFlagBackupSemantics = 0x02000000;
        const uint fileFlagOpenReparsePoint = 0x00200000;
        var handle = TestNativeMethods.CreateFile(
            path,
            unchecked((uint)(FileSystemRights.CreateFiles | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Traverse | FileSystemRights.ReadAttributes)),
            shareMode: 0x00000001 | 0x00000002 | 0x00000004,
            IntPtr.Zero,
            creationDisposition: 3,
            fileFlagOpenReparsePoint | fileFlagBackupSemantics,
            IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException("The test could not retain the stale writable directory handle.", new System.ComponentModel.Win32Exception(error));
    }

    private static SafeFileHandle OpenForRename(string path)
    {
        const uint delete = 0x00010000;
        const uint genericWrite = 0x40000000;
        var handle = TestNativeMethods.CreateFile(
            path,
            delete | genericWrite,
            shareMode: 0x00000001 | 0x00000002 | 0x00000004,
            IntPtr.Zero,
            creationDisposition: 3,
            flagsAndAttributes: 0,
            IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException("The test could not open the replacement file for rename.", new System.ComponentModel.Win32Exception(error));
    }

    private static bool TryRenameToName(SafeFileHandle sourceHandle, SafeFileHandle targetDirectoryHandle, string fileName, out int error)
    {
        var fileNameBytes = Encoding.Unicode.GetBytes(fileName);
        var nameOffset = Marshal.OffsetOf<TestFileRenameInformation>(nameof(TestFileRenameInformation.FileName)).ToInt32();
        var bufferLength = Marshal.SizeOf<TestFileRenameInformation>() + fileNameBytes.Length;
        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            Marshal.Copy(new byte[bufferLength], 0, buffer, bufferLength);
            Marshal.WriteInt32(buffer, Marshal.OffsetOf<TestFileRenameInformation>(nameof(TestFileRenameInformation.ReplaceIfExistsOrFlags)).ToInt32(), 1);
            Marshal.WriteIntPtr(buffer, Marshal.OffsetOf<TestFileRenameInformation>(nameof(TestFileRenameInformation.RootDirectory)).ToInt32(), targetDirectoryHandle.DangerousGetHandle());
            Marshal.WriteInt32(buffer, Marshal.OffsetOf<TestFileRenameInformation>(nameof(TestFileRenameInformation.FileNameLength)).ToInt32(), fileNameBytes.Length);
            Marshal.Copy(fileNameBytes, 0, IntPtr.Add(buffer, nameOffset), fileNameBytes.Length);
            var status = TestNativeMethods.NtSetInformationFile(
                sourceHandle,
                out _,
                buffer,
                (uint)bufferLength,
                fileInformationClass: NativeFileRenameInformationClass);
            if (status >= 0)
            {
                error = 0;
                return true;
            }

            error = unchecked((int)TestNativeMethods.RtlNtStatusToDosError(status));
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static FileSecurity CreateProtectedFileSecurity()
    {
        var security = new FileSecurity();
        var currentUser = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The test process has no Windows user SID.");
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentUser);
        security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestFileRenameInformation
    {
        // FILE_RENAME_INFORMATION defines ReplaceIfExists and Flags as a union. Its size is
        // four bytes, so writing the whole value also initializes the alignment bytes.
        public uint ReplaceIfExistsOrFlags;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct TestIoStatusBlockStatus
    {
        [FieldOffset(0)] public int Status;
        [FieldOffset(0)] public IntPtr Pointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestIoStatusBlock
    {
        public TestIoStatusBlockStatus StatusOrPointer;
        public UIntPtr Information;
    }

    private static class TestNativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("ntdll.dll")]
        public static extern int NtSetInformationFile(
            SafeFileHandle file,
            out TestIoStatusBlock ioStatusBlock,
            IntPtr fileInformation,
            uint bufferSize,
            int fileInformationClass);

        [DllImport("ntdll.dll")]
        public static extern uint RtlNtStatusToDosError(int status);
    }

    private sealed class ProtectedTestDirectory : IDisposable
    {
        private static readonly string TestRoot = ResolveTestRoot();
        public string Path { get; } = System.IO.Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        public string DatabasePath => System.IO.Path.Combine(Path, "controller.db");

        public ProtectedTestDirectory()
        {
            Directory.CreateDirectory(Path);
            SetDirectoryAcl(Path, allowWorldFullControl: false);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }

    }

    private static void SetDirectoryAcl(string path, bool allowWorldFullControl)
    {
        var currentUser = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The test process has no Windows user SID.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentUser);
        AddFullControl(security, currentUser);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        if (allowWorldFullControl)
            AddFullControl(security, new SecurityIdentifier(WellKnownSidType.WorldSid, null));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
    }

    private static void AddFullControl(DirectorySecurity security, SecurityIdentifier sid) =>
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));

    private static string ResolveTestRoot()
    {
        var controllerProjectPath = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "ControllerProjectPath")?
            .Value;
        if (controllerProjectPath is null)
            throw new InvalidOperationException("Controller.Tests must declare its ControllerProjectPath metadata for protected filesystem tests.");

        var controllerProjectDirectory = System.IO.Path.GetDirectoryName(controllerProjectPath)!;
        var servicesDirectory = Directory.GetParent(controllerProjectDirectory)!.FullName;
        var repositoryDirectory = Directory.GetParent(servicesDirectory)!.FullName;
        var root = System.IO.Path.Combine(repositoryDirectory, ".restore-epoch-tests");
        Directory.CreateDirectory(root);
        return root;
    }
}
