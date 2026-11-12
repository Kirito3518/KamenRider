using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace KamenRaider.Controller.Data;

/// <summary>
/// Maintains the external generation marker used to invalidate Controller state after restore.
/// This marker is deliberately outside SQLite so restoring a database snapshot cannot roll it back.
/// </summary>
/// <remarks>
/// Directory and file ACLs are read from verified handles, and verified ancestors remain open
/// without delete sharing during path-based publication. The final MoveFileEx operation accepts
/// names rather than directory handles, so a trusted administrator or SYSTEM process remains
/// outside this protection boundary and could race a namespace change.
/// The database directory and its ancestors must be created with protected ACLs before untrusted
/// identities can obtain writable directory handles. Current ACL checks cannot revoke rights
/// already granted to handles that were opened before ACL tightening. In particular, an attacker
/// holding a preexisting writable directory handle can race the sidecar name after publication
/// and before the newly published file is opened and verified. The lifetime lease below protects
/// the verified file after that open; it does not close this publication-to-open race.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ControllerRestoreEpoch
{
    private static readonly byte[] magic = Encoding.ASCII.GetBytes("KREPOCH1");
    private const int EpochPrefixLength = 16;
    private const int ChecksumLength = 32;
    private const int EncodedLength = EpochPrefixLength + ChecksumLength;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ReadControl = 0x00020000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint DriveFixed = 3;
    private const uint OpenExisting = 3;
    private const uint CreateNew = 1;
    private const uint MoveFileReplaceExisting = 0x00000001;
    private const uint MoveFileWriteThrough = 0x00000008;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorSharingViolation = 32;
    private const int SecurityObjectFile = 1;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const ushort SecurityDescriptorDaclPresent = 0x0004;
    private const ushort SecurityDescriptorDaclProtected = 0x1000;

    /// <summary>
    /// Advances the persisted epoch while holding the Controller database maintenance lock.
    /// A missing or malformed marker is an admission failure; only explicit first-install
    /// provisioning may create an initial marker.
    /// </summary>
    /// <remarks>
    /// Advancement also excludes an active Controller instance. File data is flushed before a
    /// write-through rename, but Windows does not provide a portable parent-directory fsync
    /// guarantee through this API. Any reported or detected ambiguous publication failure throws;
    /// callers must keep admission closed and retry the normal advance path.
    /// </remarks>
    internal static AdmissionLease AdvanceBeforeAdmission(string databasePath, Action<long> beforeEpochAdvance, Action<long> afterEpochAdvance)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(beforeEpochAdvance);
        ArgumentNullException.ThrowIfNull(afterEpochAdvance);
        var fullPath = NormalizeDatabasePath(databasePath);
        using var maintenanceLock = AcquireValidatedLock(
            fullPath,
            ".maintenance.lock",
            "Another database maintenance operation is already running.");
        return AdvanceForAdmissionWithMaintenanceLockHeld(fullPath, maintenanceLock, beforeEpochAdvance, afterEpochAdvance);
    }

    /// <summary>
    /// Creates epoch 1 only for a new deployment before its SQLite database exists.
    /// Startup and restore paths must compare and bind the database epoch before admission.
    /// </summary>
    public static long InitializeForNewDatabase(string databasePath)
    {
        using var admission = InitializeForNewDatabaseAdmission(databasePath);
        return admission.Epoch;
    }

    /// <summary>
    /// First-install provisioning for a path that has no database or restore history. It keeps the
    /// active lock and verified sidecar handle together so a host cannot admit requests between
    /// epoch creation and its normal database initialization.
    /// </summary>
    public static AdmissionLease InitializeForNewDatabaseAdmission(string databasePath)
    {
        EnsureWindows();
        var fullPath = NormalizeDatabasePath(databasePath);
        using var verifiedDirectories = ValidateDatabaseDirectory(fullPath);
        if (PathEntryExists(fullPath))
            throw new InvalidDataException("A restore epoch can only be initialized before the new database exists.");

        using var maintenanceLock = AcquireValidatedLock(
            fullPath,
            ".maintenance.lock",
            "Another database maintenance operation is already running.");
        FileStream? activeLock = null;
        SafeFileHandle? sidecarHandle = null;
        try
        {
            activeLock = AcquireValidatedLock(
                fullPath,
                ".active.lock",
                "Stop the Controller before initializing its restore epoch.");
            if (PathEntryExists(fullPath))
                throw new InvalidDataException("The database appeared before initial restore epoch creation.");

            var sidecarPath = fullPath + ".restore-epoch";
            if (PathEntryExists(sidecarPath))
                throw new InvalidDataException("A restore epoch already exists; refusing to replace its history.");

            var publishedIdentity = WriteAtomically(sidecarPath, EncodeEpoch(1), replaceExisting: false);
            sidecarHandle = OpenVerifiedSidecar(sidecarPath, out var initialEpoch, out var sidecarIdentity);
            if (initialEpoch != 1 || sidecarIdentity != publishedIdentity)
                throw new InvalidDataException("The initial restore epoch could not be verified after publication.");

            var admission = new AdmissionLease(initialEpoch, activeLock, sidecarHandle);
            activeLock = null;
            sidecarHandle = null;
            return admission;
        }
        finally
        {
            sidecarHandle?.Dispose();
            activeLock?.Dispose();
        }
    }

    /// <summary>Checks an existing sidecar without modifying it; maintenance callers use this before changing live state.</summary>
    public static long ValidateExistingEpoch(string databasePath)
    {
        EnsureWindows();
        var fullPath = NormalizeDatabasePath(databasePath);
        using var verifiedDirectories = ValidateDatabaseDirectory(fullPath);
        using var databaseHandle = ValidateDatabaseFile(fullPath);
        using var sidecarHandle = OpenVerifiedSidecar(fullPath + ".restore-epoch", out var epoch, out _);
        return epoch;
    }

    /// <summary>Validates and reports a protected in-progress restore marker for explicit retry.</summary>
    internal static bool HasValidRestorePendingMarker(string databasePath)
    {
        EnsureWindows();
        var fullPath = NormalizeDatabasePath(databasePath);
        using var verifiedDirectories = ValidateDatabaseDirectory(fullPath);
        var pendingPath = fullPath + ".restore-pending";
        if (!PathEntryExists(pendingPath)) return false;
        using var pending = OpenVerifiedSidecar(pendingPath, out _, out _);
        return true;
    }

    /// <summary>
    /// Writes a protected pending marker before restore mutations. Startup checks this marker
    /// before admission and fails closed until a complete restore publishes and clears it.
    /// </summary>
    internal static void BeginRestoreWithMaintenanceLockHeld(string databasePath, FileStream maintenanceLock)
    {
        EnsureWindows();
        var fullPath = NormalizeDatabasePath(databasePath);
        using var verifiedDirectories = ValidateDatabaseDirectory(fullPath);
        VerifyLockPathAndAcl(fullPath + ".maintenance.lock", maintenanceLock);
        using var activeLock = AcquireValidatedLock(fullPath, ".active.lock", "Stop the Controller before restoring its database.");

        var pendingPath = fullPath + ".restore-pending";
        if (PathEntryExists(pendingPath))
        {
            using var existing = OpenVerifiedSidecar(pendingPath, out _, out _);
            return;
        }

        var publishedIdentity = WriteAtomically(pendingPath, EncodeEpoch(1), replaceExisting: false);
        using var pending = OpenVerifiedSidecar(pendingPath, out var epoch, out var identity);
        if (epoch != 1 || identity != publishedIdentity)
            throw new InvalidDataException("The pending restore marker could not be verified after publication.");
    }

    /// <summary>Clears the protected pending marker only after the restored database is published.</summary>
    internal static void CompleteRestoreWithMaintenanceLockHeld(string databasePath, FileStream maintenanceLock)
    {
        EnsureWindows();
        var fullPath = NormalizeDatabasePath(databasePath);
        using var verifiedDirectories = ValidateDatabaseDirectory(fullPath);
        VerifyLockPathAndAcl(fullPath + ".maintenance.lock", maintenanceLock);
        using var activeLock = AcquireValidatedLock(fullPath, ".active.lock", "Stop the Controller before completing its restore.");

        var pendingPath = fullPath + ".restore-pending";
        if (!PathEntryExists(pendingPath))
            throw new InvalidDataException("The pending restore marker is missing; refusing to complete an ambiguous restore.");
        using (OpenVerifiedSidecar(pendingPath, out _, out _)) { }
        File.Delete(pendingPath);
        if (PathEntryExists(pendingPath))
            throw new InvalidDataException("The pending restore marker remained after cleanup.");
    }

    /// <summary>
    /// Explicit restore into a new destination. The destination must still be absent; writing the
    /// epoch before SQLite publication makes an interrupted restore fail closed at next startup.
    /// </summary>
    internal static long InitializeForRestoreWithMaintenanceLockHeld(string databasePath, FileStream maintenanceLock)
    {
        EnsureWindows();
        var fullPath = NormalizeDatabasePath(databasePath);
        using var verifiedDirectories = ValidateDatabaseDirectory(fullPath);
        VerifyLockPathAndAcl(fullPath + ".maintenance.lock", maintenanceLock);
        using var activeLock = AcquireValidatedLock(fullPath, ".active.lock", "Stop the Controller before restoring its database.");
        if (PathEntryExists(fullPath))
            throw new InvalidDataException("The restore destination appeared before restore epoch initialization.");

        var sidecarPath = fullPath + ".restore-epoch";
        if (PathEntryExists(sidecarPath))
        {
            var pendingPath = fullPath + ".restore-pending";
            if (!PathEntryExists(pendingPath))
                throw new InvalidDataException("An epoch exists without its database; refusing to treat an interrupted restore as a new installation.");
            _ = ReadEpoch(sidecarPath);
            using var pending = OpenVerifiedSidecar(pendingPath, out _, out _);
            return ReadEpoch(sidecarPath);
        }

        var publishedIdentity = WriteAtomically(sidecarPath, EncodeEpoch(1), replaceExisting: false);
        using var sidecarHandle = OpenVerifiedSidecar(sidecarPath, out var epoch, out var identity);
        if (epoch != 1 || identity != publishedIdentity)
            throw new InvalidDataException("The restore epoch could not be verified before database publication.");
        return epoch;
    }

    /// <summary>
    /// Explicit operator bootstrap for an existing legacy deployment after its quarantine marker
    /// has been durably set. It is idempotent: a verified existing epoch is preserved, and an
    /// invalid entry is never overwritten.
    /// </summary>
    internal static long ProvisionQuarantinedExistingDatabaseWithMaintenanceLockHeld(string databasePath, FileStream maintenanceLock)
    {
        EnsureWindows();
        var fullPath = NormalizeDatabasePath(databasePath);
        using var verifiedDirectories = ValidateDatabaseDirectory(fullPath);
        VerifyLockPathAndAcl(fullPath + ".maintenance.lock", maintenanceLock);
        using var activeLock = AcquireValidatedLock(fullPath, ".active.lock", "Stop the Controller before provisioning its restore epoch.");
        using var databaseHandle = ValidateDatabaseFile(fullPath);
        var sidecarPath = fullPath + ".restore-epoch";
        if (PathEntryExists(sidecarPath)) return ReadEpoch(sidecarPath);

        var publishedIdentity = WriteAtomically(sidecarPath, EncodeEpoch(1), replaceExisting: false);
        using var sidecarHandle = OpenVerifiedSidecar(sidecarPath, out var epoch, out var identity);
        if (epoch != 1 || identity != publishedIdentity)
            throw new InvalidDataException("The bootstrapped restore epoch could not be verified after publication.");
        return epoch;
    }

    /// <summary>
    /// Restore integration calls this only after acquiring the same exclusive .maintenance.lock
    /// used to publish the restored SQLite image. Keeping the lock stream in the signature makes
    /// accidental lock re-entry and unlocked call sites visible during review.
    /// </summary>
    internal static long AdvanceWithMaintenanceLockHeld(string databasePath, FileStream maintenanceLock)
    {
        EnsureWindows();
        var fullPath = NormalizeDatabasePath(databasePath);
        using var verifiedDirectories = ValidateDatabaseDirectory(fullPath);
        VerifyLockPathAndAcl(fullPath + ".maintenance.lock", maintenanceLock);
        using var activeLock = AcquireValidatedLock(
            fullPath,
            ".active.lock",
            "Stop the Controller before advancing its restore epoch.");
        using var databaseHandle = ValidateDatabaseFile(fullPath);

        return AdvanceEpoch(fullPath + ".restore-epoch");
    }

    private static AdmissionLease AdvanceForAdmissionWithMaintenanceLockHeld(string databasePath, FileStream maintenanceLock,
        Action<long> beforeEpochAdvance, Action<long> afterEpochAdvance)
    {
        using var verifiedDirectories = ValidateDatabaseDirectory(databasePath);
        VerifyLockPathAndAcl(databasePath + ".maintenance.lock", maintenanceLock);
        var pendingPath = databasePath + ".restore-pending";
        if (PathEntryExists(pendingPath))
        {
            using var pending = OpenVerifiedSidecar(pendingPath, out _, out _);
            throw new InvalidDataException("A restore is incomplete; refusing Controller admission until an operator completes or retries the restore.");
        }

        FileStream? activeLock = null;
        SafeFileHandle? sidecarHandle = null;
        try
        {
            activeLock = AcquireValidatedLock(
                databasePath,
                ".active.lock",
                "Stop the Controller before advancing its restore epoch.");
            using var databaseHandle = ValidateDatabaseFile(databasePath);

            var sidecarPath = databasePath + ".restore-epoch";
            var previousEpoch = ReadEpoch(sidecarPath);
            beforeEpochAdvance(previousEpoch);
            var (epoch, verifiedSidecar) = AdvanceEpochAndRetainSidecar(sidecarPath, expectedCurrentEpoch: previousEpoch);
            sidecarHandle = verifiedSidecar;
            afterEpochAdvance(epoch);

            var admission = new AdmissionLease(epoch, activeLock, sidecarHandle);
            activeLock = null;
            sidecarHandle = null;
            return admission;
        }
        finally
        {
            activeLock?.Dispose();
            sidecarHandle?.Dispose();
        }
    }

    private static long AdvanceEpoch(string sidecarPath)
    {
        var (epoch, verifiedSidecar) = AdvanceEpochAndRetainSidecar(sidecarPath);
        verifiedSidecar.Dispose();
        return epoch;
    }

    private static (long Epoch, SafeFileHandle SidecarHandle) AdvanceEpochAndRetainSidecar(string sidecarPath, long? expectedCurrentEpoch = null)
    {
        var currentEpoch = ReadEpoch(sidecarPath);
        if (expectedCurrentEpoch is not null && currentEpoch != expectedCurrentEpoch)
            throw new InvalidDataException("The Controller restore epoch changed during admission reconciliation.");
        if (currentEpoch == long.MaxValue)
            throw new InvalidDataException("The restore epoch is exhausted and cannot be safely advanced.");

        var nextEpoch = currentEpoch + 1;
        var publishedIdentity = WriteAtomically(sidecarPath, EncodeEpoch(nextEpoch), replaceExisting: true);
        var sidecarHandle = OpenVerifiedSidecar(sidecarPath, out var publishedEpoch, out var verifiedIdentity);
        if (publishedEpoch != nextEpoch || verifiedIdentity != publishedIdentity)
        {
            sidecarHandle.Dispose();
            throw new InvalidDataException("The advanced restore epoch could not be verified against the published file identity.");
        }

        return (nextEpoch, sidecarHandle);
    }

    /// <summary>
    /// Owns the startup epoch, exclusive active lock, and verified sidecar handle as one admission
    /// token. <see cref="TakeLifetimeLease"/> transfers all three together to the long-lived
    /// Controller owner. There is intentionally no API for transferring or disposing the handles
    /// independently, because the sidecar must stay write- and replacement-excluded for the full
    /// time the active lock prevents another Controller instance from starting.
    /// </summary>
    public sealed class AdmissionLease : IDisposable
    {
        private readonly object gate = new();
        private LifetimeOwnership? ownership;

        internal AdmissionLease(long epoch, FileStream activeLock, SafeFileHandle sidecarHandle)
        {
            Epoch = epoch;
            ownership = new LifetimeOwnership(activeLock, sidecarHandle);
        }

        public long Epoch { get; }

        /// <summary>Transfers the active lock and verified sidecar handle together exactly once.</summary>
        public ControllerLifetimeLease TakeLifetimeLease()
        {
            lock (gate)
            {
                var owned = ownership ?? throw new InvalidOperationException("The admission lease no longer owns its lifetime resources.");
                var lifetimeLease = CreateLifetimeLease(Epoch, owned);
                ownership = null;
                return lifetimeLease;
            }
        }

        public void Dispose()
        {
            LifetimeOwnership? owned;
            lock (gate)
            {
                owned = ownership;
                ownership = null;
            }

            owned?.Dispose();
        }
    }

    /// <summary>
    /// Holds the active lock and verified sidecar handle together for the complete Controller
    /// lifetime. Disposing this token releases both resources; neither handle is exposed separately.
    /// </summary>
    public sealed class ControllerLifetimeLease : IDisposable
    {
        private LifetimeOwnership? ownership;

        internal ControllerLifetimeLease(long epoch, LifetimeOwnership ownership)
        {
            Epoch = epoch;
            this.ownership = ownership;
        }

        public long Epoch { get; }

        public void Dispose() => Interlocked.Exchange(ref ownership, null)?.Dispose();
    }

    internal sealed class LifetimeOwnership(FileStream activeLock, SafeFileHandle sidecarHandle) : IDisposable
    {
        private FileStream? activeLock = activeLock;
        private SafeFileHandle? sidecarHandle = sidecarHandle;

        public void Dispose()
        {
            var retainedSidecar = Interlocked.Exchange(ref sidecarHandle, null);
            var retainedActiveLock = Interlocked.Exchange(ref activeLock, null);
            try
            {
                retainedSidecar?.Dispose();
            }
            finally
            {
                retainedActiveLock?.Dispose();
            }
        }
    }

    private static ControllerLifetimeLease CreateLifetimeLease(long epoch, LifetimeOwnership ownership) =>
        new(epoch, ownership);

    private static string NormalizeDatabasePath(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A database path is required.", nameof(databasePath));

        var fullPath = ControllerDatabaseActivityLock.Normalize(databasePath, nameof(databasePath));
        var root = Path.GetPathRoot(fullPath);
        if (root is null || root.Length < 3 || root[1] != ':' || fullPath.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("The restore epoch requires a local drive path.");

        ValidateFixedDrivePath(fullPath, NativeMethods.GetDriveType);

        return fullPath;
    }

    internal static void ValidateFixedDrivePath(string path, Func<string, uint> getDriveType)
    {
        ArgumentNullException.ThrowIfNull(getDriveType);
        var root = Path.GetPathRoot(path);
        if (root is null || root.Length < 3 || root[1] != ':' || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("The restore epoch requires a local drive path.");

        if (getDriveType(root) != DriveFixed)
            throw new InvalidDataException("The restore epoch requires a persistent fixed local drive, not a mapped, removable, optical, or RAM drive.");
    }

    private static VerifiedDirectoryChain ValidateDatabaseDirectory(string databasePath)
    {
        var directoryPath = Path.GetDirectoryName(databasePath);
        if (directoryPath is null)
            throw new InvalidDataException("The Controller database directory must already exist.");

        return ValidateDirectoryChain(directoryPath);
    }

    private static VerifiedDirectoryChain ValidateDirectoryChain(string directoryPath)
    {
        var fullDirectoryPath = Path.GetFullPath(directoryPath);
        var root = Path.GetPathRoot(fullDirectoryPath);
        if (root is null || root.Length < 3 || root[1] != ':' || fullDirectoryPath.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("The restore epoch requires a local drive directory.");

        ValidateFixedDrivePath(fullDirectoryPath, NativeMethods.GetDriveType);
        var segments = fullDirectoryPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var chain = new VerifiedDirectoryChain();
        try
        {
            var currentPath = root;
            var rootHandle = OpenDirectoryPath(currentPath);
            chain.Add(rootHandle);
            ValidateDirectoryHandle(rootHandle, currentPath, databaseDirectory: segments.Length == 0);

            for (var index = 0; index < segments.Length; index++)
            {
                currentPath = Path.Combine(currentPath, segments[index]);
                var directoryHandle = OpenDirectoryPath(currentPath);
                chain.Add(directoryHandle);
                ValidateDirectoryHandle(directoryHandle, currentPath, databaseDirectory: index == segments.Length - 1);
            }

            return chain;
        }
        catch
        {
            chain.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenDirectoryPath(string path)
    {
        // Omitting FILE_SHARE_DELETE while retaining each ancestor handle prevents its path
        // component from being renamed or replaced while the protected sidecar is advanced.
        var handle = NativeMethods.CreateFile(
            path,
            FileReadAttributes | ReadControl,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (!handle.IsInvalid) return handle;

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error == ErrorFileNotFound || error == ErrorPathNotFound)
            throw new InvalidDataException("The Controller database directory must already exist.");
        throw new IOException("A Controller database path directory could not be opened safely.", new Win32Exception(error));
    }

    private static void ValidateDirectoryHandle(SafeFileHandle handle, string path, bool databaseDirectory)
    {
        var information = ReadFileInformation(handle);
        if ((information.FileAttributes & FileAttributes.Directory) == 0
            || (information.FileAttributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A reparse point or non-directory exists in the Controller database path.");
        ValidateHandlePathMatches(handle, path);

        var security = ReadHandleSecurity(handle);
        var allowedTrustees = AllowedTrustees();
        if (!allowedTrustees.Contains(security.Owner))
            throw new InvalidDataException("The Controller database directory has an untrusted owner.");
        if (databaseDirectory && !security.DaclProtected)
            throw new InvalidDataException("The Controller database directory must have a protected DACL and be provisioned with secure ACLs before untrusted identities can obtain writable handles.");

        var replacementRights = FileSystemRights.Delete
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership
            | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.WriteAttributes
            | FileSystemRights.WriteExtendedAttributes;
        var prohibitedRights = databaseDirectory
            ? replacementRights | FileSystemRights.Write | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories
            : replacementRights;
        EnsureNoUntrustedWriteGrant(security.Dacl, allowedTrustees, prohibitedRights, path);
    }

    private static void EnsureNoUntrustedWriteGrant(RawAcl? dacl, HashSet<SecurityIdentifier> allowedTrustees, FileSystemRights prohibitedRights, string path)
    {
        if (dacl is null)
            throw new InvalidDataException($"The Controller path component '{path}' has a null or missing DACL.");

        var prohibitedMask = ToNativeWriteMask(prohibitedRights);
        for (var index = 0; index < dacl.Count; index++)
        {
            if (dacl[index] is not QualifiedAce ace
                || ace.AceQualifier != AceQualifier.AccessAllowed
                || (ace.AceFlags & AceFlags.InheritOnly) != 0
                || ace.SecurityIdentifier is not { } trustee
                || allowedTrustees.Contains(trustee))
                continue;

            if ((unchecked((uint)ace.AccessMask) & prohibitedMask) != 0)
                throw new InvalidDataException($"Untrusted principal '{trustee}' has write or replacement rights on Controller path component '{path}'.");
        }
    }

    private static uint ToNativeWriteMask(FileSystemRights rights)
    {
        const uint genericAll = 0x10000000;
        const uint genericWrite = 0x40000000;
        const uint writeDac = 0x00040000;
        const uint writeOwner = 0x00080000;
        const uint delete = 0x00010000;
        const uint writeDataOrAddFile = 0x00000002;
        const uint appendOrAddDirectory = 0x00000004;
        const uint writeExtendedAttributes = 0x00000010;
        const uint writeAttributes = 0x00000100;

        var mask = unchecked((uint)rights) | genericAll | genericWrite | writeDac | writeOwner | delete
            | writeExtendedAttributes | writeAttributes;
        if ((rights & (FileSystemRights.CreateFiles | FileSystemRights.WriteData)) != 0) mask |= writeDataOrAddFile;
        if ((rights & (FileSystemRights.CreateDirectories | FileSystemRights.AppendData)) != 0) mask |= appendOrAddDirectory;
        return mask;
    }

    private static SafeFileHandle ValidateDatabaseFile(string databasePath)
    {
        var handle = OpenExistingPath(
            databasePath,
            allowDirectory: false,
            missingIsInvalid: true,
            shareMode: FileShareRead | FileShareWrite);
        try
        {
            var information = ReadFileInformation(handle);
            if ((information.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || information.NumberOfLinks != 1)
                throw new InvalidDataException("The Controller database must be a regular single-link file.");
            ValidateHandlePathMatches(handle, databasePath);

            var allowedTrustees = AllowedTrustees();
            var security = ReadHandleSecurity(handle);
            if (!allowedTrustees.Contains(security.Owner))
                throw new InvalidDataException("The Controller database file has an untrusted owner.");
            EnsureNoUntrustedWriteGrant(
                security.Dacl,
                allowedTrustees,
                FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership,
                databasePath);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static FileStream AcquireValidatedLock(string databasePath, string suffix, string conflictMessage)
    {
        databasePath = Path.GetFullPath(databasePath);
        var lockPath = Path.GetFullPath(databasePath + suffix);
        ValidateLockEntryBeforeAcquire(lockPath, conflictMessage);

        var lockStream = ControllerDatabaseFileLocks.Acquire(databasePath, suffix, conflictMessage);
        try
        {
            VerifyLockPathAndAcl(lockPath, lockStream);
            return lockStream;
        }
        catch
        {
            lockStream.Dispose();
            throw;
        }
    }

    private static void ValidateLockEntryBeforeAcquire(string lockPath, string conflictMessage)
    {
        var handle = NativeMethods.CreateFile(
            lockPath,
            FileReadAttributes | ReadControl,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == ErrorFileNotFound || error == ErrorPathNotFound) return;
            if (error == ErrorSharingViolation)
                throw new InvalidOperationException(conflictMessage, new IOException("The Controller lock is already held.", new Win32Exception(error)));
            throw new IOException("The Controller lock entry could not be inspected safely.", new Win32Exception(error));
        }

        using (handle)
        {
            var information = ReadFileInformation(handle);
            if ((information.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || information.NumberOfLinks != 1)
                throw new InvalidDataException("The Controller lock entry must be a regular single-link file, not a reparse point.");
            ValidateHandlePathMatches(handle, lockPath);

            var allowedTrustees = AllowedTrustees();
            var security = ReadHandleSecurity(handle);
            if (!allowedTrustees.Contains(security.Owner))
                throw new InvalidDataException("The Controller lock file has an untrusted owner.");
            EnsureNoUntrustedWriteGrant(
                security.Dacl,
                allowedTrustees,
                FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership,
                lockPath);
        }
    }

    private static void VerifyLockPathAndAcl(string expectedLockPath, FileStream lockStream)
    {
        ArgumentNullException.ThrowIfNull(lockStream);
        if (!lockStream.CanRead || !lockStream.CanWrite || string.IsNullOrWhiteSpace(lockStream.Name))
            throw new InvalidDataException("A live exclusive Controller lock is required.");

        expectedLockPath = Path.GetFullPath(expectedLockPath);
        if (!string.Equals(Path.GetFullPath(lockStream.Name), expectedLockPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The supplied lock does not protect this Controller database.");

        var information = ReadFileInformation(lockStream.SafeFileHandle);
        if ((information.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            || information.NumberOfLinks != 1)
            throw new InvalidDataException("The Controller lock is not a regular single-link file.");
        ValidateHandlePathMatches(lockStream.SafeFileHandle, expectedLockPath);

        var allowedTrustees = AllowedTrustees();
        var security = ReadHandleSecurity(lockStream.SafeFileHandle);
        if (!allowedTrustees.Contains(security.Owner))
            throw new InvalidDataException("The Controller lock file has an untrusted owner.");
        EnsureNoUntrustedWriteGrant(
            security.Dacl,
            allowedTrustees,
            FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership,
            expectedLockPath);

        try
        {
            using var competingOpen = new FileStream(expectedLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            throw new InvalidDataException("The supplied Controller lock is not exclusive.");
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) == ErrorSharingViolation)
        {
            // The expected exclusive handle prevents another reader or writer from opening it.
        }
    }

    private static long ReadEpoch(string sidecarPath)
    {
        using var handle = OpenVerifiedSidecar(sidecarPath, out var epoch, out _);
        return epoch;
    }

    private static SafeFileHandle OpenVerifiedSidecar(string sidecarPath, out long epoch, out FileIdentity identity)
    {
        var handle = OpenExistingPath(
            sidecarPath,
            allowDirectory: false,
            missingIsInvalid: true,
            shareMode: FileShareRead);
        try
        {
            var information = ReadFileInformation(handle);
            ValidateRegularSidecar(handle, sidecarPath, information);

            var bytes = new byte[EncodedLength + 1];
            var bytesRead = 0;
            while (bytesRead < bytes.Length)
            {
                var read = RandomAccess.Read(handle, bytes.AsSpan(bytesRead), bytesRead);
                if (read == 0) break;
                bytesRead += read;
            }

            if (bytesRead != EncodedLength)
                throw new InvalidDataException("The restore epoch sidecar has an invalid length.");

            epoch = DecodeEpoch(bytes.AsSpan(0, EncodedLength));
            identity = FileIdentity.From(information);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void ValidateRegularSidecar(SafeFileHandle handle, string path, ByHandleFileInformation information)
    {
        if ((information.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            || information.NumberOfLinks != 1)
            throw new InvalidDataException("The restore epoch sidecar must be a regular single-link file.");

        ValidateHandlePathMatches(handle, path);
        var trusted = AllowedTrustees();
        var security = ReadHandleSecurity(handle);
        var currentUser = CurrentUserSid();
        if (!security.Owner.Equals(currentUser) || !security.DaclProtected)
            throw new InvalidDataException("The restore epoch sidecar owner or DACL is not protected.");

        var currentUserHasFullControl = false;
        if (security.Dacl is null)
            throw new InvalidDataException("The restore epoch sidecar has a null or missing DACL.");
        for (var index = 0; index < security.Dacl.Count; index++)
        {
            if (security.Dacl[index] is not QualifiedAce rule
                || rule.AceQualifier != AceQualifier.AccessAllowed
                || (rule.AceFlags & AceFlags.Inherited) != 0
                || rule.SecurityIdentifier is not { } trustee
                || !trusted.Contains(trustee))
                throw new InvalidDataException("The restore epoch sidecar DACL contains an untrusted rule.");

            if (trustee.Equals(currentUser)
                && (rule.AccessMask & (int)FileSystemRights.FullControl) == (int)FileSystemRights.FullControl)
                currentUserHasFullControl = true;
        }

        if (!currentUserHasFullControl)
            throw new InvalidDataException("The Controller service identity cannot safely replace the restore epoch sidecar.");
    }

    private static FileIdentity WriteAtomically(string sidecarPath, byte[] bytes, bool replaceExisting)
    {
        var parentPath = Path.GetDirectoryName(sidecarPath)!;
        using var verifiedDirectories = ValidateDirectoryChain(parentPath);

        var temporaryPath = Path.Combine(parentPath, "." + Path.GetFileName(sidecarPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var publicationAttempted = false;
        try
        {
            using (var createHandle = CreateProtectedTemporaryFile(temporaryPath))
            using (var create = new FileStream(createHandle, FileAccess.Write, bufferSize: 4096, isAsync: false))
            {
                create.Flush(flushToDisk: true);
            }

            ValidateTemporaryFile(temporaryPath);

            using (var output = new FileStream(temporaryPath, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }

            var temporaryIdentity = ValidateTemporaryFile(temporaryPath);

            var flags = MoveFileWriteThrough | (replaceExisting ? MoveFileReplaceExisting : 0);
            // Flush(true) and MOVEFILE_WRITE_THROUGH request durable file data and a flushed move.
            // Windows does not expose a portable parent-directory fsync guarantee here, so this
            // code does not claim that a successful rename survives every power-loss scenario.
            // If publication reports an error, preserve the protected temp as evidence and throw;
            // callers must keep admission closed and retry only through the normal epoch advance.
            publicationAttempted = true;
            if (!NativeMethods.MoveFileEx(temporaryPath, sidecarPath, flags))
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException("The restore epoch sidecar could not be atomically published.", new Win32Exception(error));
            }
            if (PathEntryExists(temporaryPath))
                throw new IOException("The restore epoch publication outcome is ambiguous; the protected temporary file was preserved.");
            return temporaryIdentity;
        }
        finally
        {
            if (!publicationAttempted && PathEntryExists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static FileIdentity ValidateTemporaryFile(string temporaryPath)
    {
        using var handle = OpenExistingPath(temporaryPath, allowDirectory: false, missingIsInvalid: true);
        var information = ReadFileInformation(handle);
        ValidateRegularSidecar(handle, temporaryPath, information);
        return FileIdentity.From(information);
    }

    private static SafeFileHandle CreateProtectedTemporaryFile(string temporaryPath)
    {
        var descriptor = CreateProtectedFileSecurity().GetSecurityDescriptorBinaryForm();
        var descriptorPin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        var attributesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityAttributes>());
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptorPin.AddrOfPinnedObject(),
                InheritHandle = false
            };
            Marshal.StructureToPtr(attributes, attributesPointer, fDeleteOld: false);

            var handle = NativeMethods.CreateFile(
                temporaryPath,
                GenericWrite,
                FileShareRead,
                attributesPointer,
                CreateNew,
                FileFlagWriteThrough,
                IntPtr.Zero);
            if (!handle.IsInvalid) return handle;

            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("The protected restore epoch temporary file could not be created.", new Win32Exception(error));
        }
        finally
        {
            Marshal.FreeHGlobal(attributesPointer);
            descriptorPin.Free();
        }
    }

    private static FileSecurity CreateProtectedFileSecurity()
    {
        var security = new FileSecurity();
        var currentUser = CurrentUserSid();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentUser);
        AddFullControl(security, currentUser);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        return security;
    }

    private static void AddFullControl(FileSecurity security, SecurityIdentifier trustee) =>
        security.AddAccessRule(new FileSystemAccessRule(trustee, FileSystemRights.FullControl, AccessControlType.Allow));

    private static HashSet<SecurityIdentifier> AllowedTrustees() =>
    [
        CurrentUserSid(),
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")
    ];

    private static SecurityIdentifier CurrentUserSid() =>
        WindowsIdentity.GetCurrent().User ?? throw new InvalidDataException("The Controller service identity has no Windows SID.");

    private static byte[] EncodeEpoch(long epoch)
    {
        if (epoch <= 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        var bytes = new byte[EncodedLength];
        magic.CopyTo(bytes, 0);
        BitConverter.TryWriteBytes(bytes.AsSpan(magic.Length, sizeof(long)), epoch);
        SHA256.HashData(bytes.AsSpan(0, EpochPrefixLength), bytes.AsSpan(EpochPrefixLength, ChecksumLength));
        return bytes;
    }

    private static long DecodeEpoch(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != EncodedLength || !bytes[..magic.Length].SequenceEqual(magic))
            throw new InvalidDataException("The restore epoch sidecar has an unsupported format.");

        var epoch = BitConverter.ToInt64(bytes.Slice(magic.Length, sizeof(long)));
        if (epoch <= 0)
            throw new InvalidDataException("The restore epoch sidecar contains an invalid epoch value.");

        Span<byte> expectedChecksum = stackalloc byte[ChecksumLength];
        SHA256.HashData(bytes[..EpochPrefixLength], expectedChecksum);
        if (!CryptographicOperations.FixedTimeEquals(expectedChecksum, bytes[EpochPrefixLength..]))
            throw new InvalidDataException("The restore epoch sidecar checksum is invalid.");

        return epoch;
    }

    private static SafeFileHandle OpenExistingPath(
        string path,
        bool allowDirectory,
        bool missingIsInvalid,
        uint shareMode = FileShareRead | FileShareWrite | FileShareDelete)
    {
        var flags = FileFlagOpenReparsePoint | (allowDirectory ? FileFlagBackupSemantics : 0);
        var handle = NativeMethods.CreateFile(
            path,
            GenericRead | FileReadAttributes | ReadControl,
            shareMode,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);
        if (!handle.IsInvalid) return handle;

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (missingIsInvalid && (error == ErrorFileNotFound || error == ErrorPathNotFound))
            throw new InvalidDataException("The Controller database or restore epoch sidecar is missing.");
        throw new IOException("The Controller database or restore epoch path could not be opened safely.", new Win32Exception(error));
    }

    private static HandleSecurity ReadHandleSecurity(SafeFileHandle handle)
    {
        var error = NativeMethods.GetSecurityInfo(
            handle,
            SecurityObjectFile,
            OwnerSecurityInformation | DaclSecurityInformation,
            out var ownerPointer,
            out _,
            out var daclPointer,
            out _,
            out var descriptorPointer);
        if (error != 0)
            throw new IOException("The Controller path security descriptor could not be read from its verified handle.", new Win32Exception(unchecked((int)error)));

        try
        {
            if (ownerPointer == IntPtr.Zero || descriptorPointer == IntPtr.Zero)
                throw new InvalidDataException("The Controller path has an incomplete security descriptor.");
            if (!NativeMethods.GetSecurityDescriptorControl(descriptorPointer, out var control, out _))
                throw new IOException("The Controller path security descriptor flags could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));

            RawAcl? dacl = null;
            if ((control & SecurityDescriptorDaclPresent) != 0 && daclPointer != IntPtr.Zero)
            {
                const int aclHeaderLength = 8;
                var header = new byte[aclHeaderLength];
                Marshal.Copy(daclPointer, header, 0, header.Length);
                var aclLength = BitConverter.ToUInt16(header, 2);
                if (aclLength < aclHeaderLength)
                    throw new InvalidDataException("The Controller path DACL has an invalid length.");
                var aclBytes = new byte[aclLength];
                Marshal.Copy(daclPointer, aclBytes, 0, aclBytes.Length);
                dacl = new RawAcl(aclBytes, 0);
            }

            return new HandleSecurity(
                new SecurityIdentifier(ownerPointer),
                dacl,
                (control & SecurityDescriptorDaclProtected) != 0);
        }
        finally
        {
            _ = NativeMethods.LocalFree(descriptorPointer);
        }
    }

    private static void ValidateHandlePathMatches(SafeFileHandle handle, string expectedPath)
    {
        var capacity = 512;
        var buffer = new StringBuilder(capacity);
        var length = NativeMethods.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, flags: 0);
        if (length == 0)
            throw new IOException("The Controller file handle could not be bound to its final path.", new Win32Exception(Marshal.GetLastWin32Error()));
        if (length >= buffer.Capacity)
        {
            capacity = checked((int)length + 1);
            buffer = new StringBuilder(capacity);
            length = NativeMethods.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, flags: 0);
            if (length == 0 || length >= buffer.Capacity)
                throw new IOException("The Controller file handle final path could not be read completely.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var expectedFinalPath = "\\\\?\\" + Path.GetFullPath(expectedPath);
        if (!string.Equals(buffer.ToString(), expectedFinalPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The verified Controller handle does not resolve to the requested path entry.");
    }

    private static bool PathEntryExists(string path)
    {
        var handle = NativeMethods.CreateFile(
            path,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
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
        if (error == ErrorFileNotFound || error == ErrorPathNotFound) return false;
        throw new IOException("A Controller database path entry could not be inspected safely.", new Win32Exception(error));
    }

    private static ByHandleFileInformation ReadFileInformation(SafeFileHandle handle)
    {
        if (!NativeMethods.GetFileInformationByHandle(handle, out var information))
            throw new IOException("The Controller database file identity could not be verified.", new Win32Exception(Marshal.GetLastWin32Error()));
        return information;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Protected Controller restore epochs require Windows ACL and durable rename support.");
    }

    private sealed record HandleSecurity(SecurityIdentifier Owner, RawAcl? Dacl, bool DaclProtected);

    private readonly record struct FileIdentity(uint VolumeSerialNumber, uint FileIndexHigh, uint FileIndexLow)
    {
        public static FileIdentity From(ByHandleFileInformation information) =>
            new(information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow);
    }

    private sealed class VerifiedDirectoryChain : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];

        public void Add(SafeFileHandle handle) => handles.Add(handle);

        public void Dispose()
        {
            foreach (var handle in handles)
                handle.Dispose();
            handles.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public FileAttributes FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private static class NativeMethods
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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
        public static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder filePath, uint filePathLength, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
        public static extern uint GetDriveType(string rootPathName);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern uint GetSecurityInfo(
            SafeFileHandle handle,
            int objectType,
            uint securityInfo,
            out IntPtr owner,
            out IntPtr group,
            out IntPtr dacl,
            out IntPtr sacl,
            out IntPtr securityDescriptor);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSecurityDescriptorControl(IntPtr securityDescriptor, out ushort control, out uint revision);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
    }
}
