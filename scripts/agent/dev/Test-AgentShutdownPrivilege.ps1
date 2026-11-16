$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$serviceName = 'KamenRaider Agent'
$service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
$report = [pscustomobject]@{
    ServiceName = $serviceName
    ServiceAccount = if ($service) { [string]$service.StartName } else { $null }
    ProcessId = if ($service) { [uint32]$service.ProcessId } else { $null }
    Status = 'Unavailable'
    SeShutdownPrivilegePresent = $null
    SeShutdownPrivilegeEnabled = $null
    ErrorType = $null
}

if ($null -eq $service) {
    $report.Status = 'ServiceNotFound'
    $report
    return
}

if ($service.State -ne 'Running' -or [uint32]$service.ProcessId -eq 0) {
    $report.Status = 'NotRunning'
    $report
    return
}

if (-not [string]::Equals([string]$service.StartName, 'NT SERVICE\KamenRaider Agent', [StringComparison]::OrdinalIgnoreCase)) {
    $report.Status = 'ServiceAccountMismatch'
    $report
    return
}

$serviceImageCommand = [string]$service.PathName
$imageMatch = [regex]::Match($serviceImageCommand, '^\s*(?:"(?<quoted>[^"]+)"|(?<unquoted>.+?\.exe))(?=\s|$)', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
if (-not $imageMatch.Success) {
    $report.Status = 'ServiceImageUnavailable'
    $report
    return
}
$configuredImagePath = if ($imageMatch.Groups['quoted'].Success) { $imageMatch.Groups['quoted'].Value } else { $imageMatch.Groups['unquoted'].Value }
if (-not [IO.Path]::IsPathRooted($configuredImagePath)) {
    $report.Status = 'ServiceImageUnavailable'
    $report
    return
}
try {
    $configuredImagePath = [IO.Path]::GetFullPath($configuredImagePath)
} catch {
    $report.Status = 'ServiceImageUnavailable'
    $report
    return
}

if (-not ('KamenRaider.AgentShutdownPrivilegeProbe' -as [type])) {
    $nativeSource = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public sealed class KamenRaiderAgentShutdownPrivilegeResult
{
    public string Status { get; set; }
    public bool Success { get; set; }
    public bool Present { get; set; }
    public bool Enabled { get; set; }
    public string ErrorType { get; set; }
    public string ProcessImagePath { get; set; }
}

public static class KamenRaiderAgentShutdownPrivilegeProbe
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenPrivileges = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInsufficientBuffer = 122;
    private const uint SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivilegesHeader
    {
        public uint PrivilegeCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr processHandle, uint flags, StringBuilder imageName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int informationClass, IntPtr information, uint informationLength, out uint returnLength);

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeName(string systemName, ref Luid luid, StringBuilder name, ref uint nameLength);

    public static KamenRaiderAgentShutdownPrivilegeResult Inspect(uint processId)
    {
        IntPtr processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == IntPtr.Zero)
            return Failure(Marshal.GetLastWin32Error());

        try
        {
            uint imageCapacity = 32768;
            StringBuilder imagePath = new StringBuilder((int)imageCapacity);
            if (!QueryFullProcessImageName(processHandle, 0, imagePath, ref imageCapacity))
                return Failure(Marshal.GetLastWin32Error());

            IntPtr tokenHandle;
            if (!OpenProcessToken(processHandle, TokenQuery, out tokenHandle))
                return Failure(Marshal.GetLastWin32Error());

            try
            {
                uint requiredLength;
                bool firstRead = GetTokenInformation(tokenHandle, TokenPrivileges, IntPtr.Zero, 0, out requiredLength);
                int firstReadError = Marshal.GetLastWin32Error();
                if (requiredLength == 0 || (!firstRead && firstReadError != ErrorInsufficientBuffer))
                    return Failure(firstReadError);

                IntPtr buffer = Marshal.AllocHGlobal(checked((int)requiredLength));
                try
                {
                    uint returnedLength;
                    if (!GetTokenInformation(tokenHandle, TokenPrivileges, buffer, requiredLength, out returnedLength))
                        return Failure(Marshal.GetLastWin32Error());

                    int headerSize = Marshal.SizeOf(typeof(TokenPrivilegesHeader));
                    int entrySize = Marshal.SizeOf(typeof(LuidAndAttributes));
                    if (returnedLength < (uint)headerSize || returnedLength > requiredLength)
                        return new KamenRaiderAgentShutdownPrivilegeResult { Status = "Unavailable", ErrorType = "InvalidTokenInformation" };
                    uint count = unchecked((uint)Marshal.ReadInt32(buffer));
                    if (count > (returnedLength - (uint)headerSize) / (uint)entrySize)
                        return new KamenRaiderAgentShutdownPrivilegeResult { Status = "Unavailable", ErrorType = "InvalidTokenInformation" };

                    bool present = false;
                    bool enabled = false;
                    for (uint index = 0; index < count; index++)
                    {
                        IntPtr entryAddress = IntPtr.Add(buffer, checked(headerSize + (int)index * entrySize));
                        LuidAndAttributes entry = (LuidAndAttributes)Marshal.PtrToStructure(entryAddress, typeof(LuidAndAttributes));
                        uint nameLength = 0;
                        bool firstNameRead = LookupPrivilegeName(null, ref entry.Luid, null, ref nameLength);
                        int firstNameError = Marshal.GetLastWin32Error();
                        if (nameLength == 0 || (!firstNameRead && firstNameError != ErrorInsufficientBuffer))
                            return Failure(firstNameError);

                        StringBuilder name = new StringBuilder(checked((int)nameLength + 1));
                        uint nameCapacity = (uint)name.Capacity;
                        if (!LookupPrivilegeName(null, ref entry.Luid, name, ref nameCapacity))
                            return Failure(Marshal.GetLastWin32Error());

                        if (String.Equals(name.ToString(), "SeShutdownPrivilege", StringComparison.OrdinalIgnoreCase))
                        {
                            present = true;
                            enabled = (entry.Attributes & SePrivilegeEnabled) != 0;
                            break;
                        }
                    }

                    return new KamenRaiderAgentShutdownPrivilegeResult
                    {
                        Status = "Inspected",
                        Success = true,
                        Present = present,
                        Enabled = enabled,
                        ProcessImagePath = imagePath.ToString()
                    };
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    private static KamenRaiderAgentShutdownPrivilegeResult Failure(int errorCode)
    {
        return new KamenRaiderAgentShutdownPrivilegeResult
        {
            Status = errorCode == ErrorAccessDenied ? "AccessDenied" : "Unavailable",
            ErrorType = "Win32Exception"
        };
    }
}
'@
    Add-Type -TypeDefinition $nativeSource -Language CSharp
}

$tokenResult = [KamenRaiderAgentShutdownPrivilegeProbe]::Inspect([uint32]$service.ProcessId)
try {
    $currentService = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
} catch {
    $report.Status = 'ServiceRequeryFailed'
    $report.ErrorType = $_.Exception.GetType().Name
    $report
    return
}

if ($null -eq $currentService -or $currentService.State -ne 'Running' -or [uint32]$currentService.ProcessId -eq 0) {
    $report.Status = 'NotRunning'
    $report
    return
}

$sameService = [uint32]$currentService.ProcessId -eq [uint32]$service.ProcessId -and
    [string]::Equals([string]$currentService.StartName, [string]$service.StartName, [StringComparison]::OrdinalIgnoreCase) -and
    [string]::Equals([string]$currentService.PathName, [string]$service.PathName, [StringComparison]::OrdinalIgnoreCase)
if (-not $sameService) {
    $report.Status = 'ServiceChanged'
    $report
    return
}

if ($tokenResult.Success) {
    try {
        $actualImagePath = [IO.Path]::GetFullPath([string]$tokenResult.ProcessImagePath)
    } catch {
        $report.Status = 'ProcessImageUnavailable'
        $report
        return
    }
    if (-not [string]::Equals($actualImagePath, $configuredImagePath, [StringComparison]::OrdinalIgnoreCase)) {
        $report.Status = 'ProcessImageMismatch'
        $report
        return
    }

    $report.Status = 'Inspected'
    $report.SeShutdownPrivilegePresent = [bool]$tokenResult.Present
    $report.SeShutdownPrivilegeEnabled = [bool]$tokenResult.Enabled
} else {
    $report.Status = [string]$tokenResult.Status
    $report.ErrorType = [string]$tokenResult.ErrorType
}
$report
