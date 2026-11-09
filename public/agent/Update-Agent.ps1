[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ControllerUrl,
    [Parameter(Mandatory)][string]$MachineId,
    [Parameter(Mandatory)][string]$OperationId,
    [Parameter(Mandatory)][string]$UpdateTicket,
    [Parameter(Mandatory)][string]$ReleaseVersion,
    [Parameter(Mandatory)][string]$TargetVersion,
    [Parameter(Mandatory)][string]$PackageSha256,
    [Parameter(Mandatory)][string]$TrustedRootThumbprint,
    [Parameter(Mandatory)][string]$AdminReturnUrl,
    [switch]$DefineFunctionsOnly
)

$ErrorActionPreference = 'Stop'

function Test-AgentUpdateRequest {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Request)
    foreach ($name in @('ControllerUrl','MachineId','OperationId','UpdateTicket','ReleaseVersion','TargetVersion','PackageSha256','TrustedRootThumbprint','AdminReturnUrl')) {
        if (-not $Request.PSObject.Properties[$name] -or [string]::IsNullOrWhiteSpace([string]$Request.$name)) { throw "Update request is missing $name." }
    }
    if ($Request.MachineId -notmatch '\A[0-9a-fA-F]{32}\z' -or $Request.OperationId -notmatch '\A[0-9a-fA-F]{32}\z') { throw 'MachineId and OperationId must be 32-character hexadecimal GUIDs.' }
    if ($Request.UpdateTicket -notmatch '\A[A-Za-z0-9_-]{43}\z') { throw 'UpdateTicket is invalid.' }
    if ($Request.PackageSha256 -notmatch '\A[0-9a-fA-F]{64}\z') { throw 'PackageSha256 must be a SHA-256 digest.' }
    if ($Request.TrustedRootThumbprint -notmatch '\A[0-9a-fA-F]{40}\z') { throw 'TrustedRootThumbprint is invalid.' }
    foreach ($version in @($Request.ReleaseVersion,$Request.TargetVersion)) {
        if ($version.Length -gt 64 -or $version -notmatch '\A(?:\d+\.\d+\.\d+|1\.0\.0\+g[0-9a-f]{12,40})\z') { throw 'ReleaseVersion and TargetVersion must be supported clean Agent release versions.' }
    }
    if ($Request.ReleaseVersion -cne $Request.TargetVersion) { throw 'ReleaseVersion and TargetVersion must identify the same published Agent package.' }
    try { $controller = [Uri]::new([string]$Request.ControllerUrl,[UriKind]::Absolute) } catch { throw 'ControllerUrl must be an absolute HTTPS origin.' }
    if ($controller.Scheme -ne 'https' -or $controller.UserInfo -or $controller.Query -or $controller.Fragment -or $controller.AbsolutePath -ne '/') { throw 'ControllerUrl must be an HTTPS origin without credentials, path, query or fragment.' }
    try { $return = [Uri]::new([string]$Request.AdminReturnUrl,[UriKind]::Absolute) } catch { throw 'AdminReturnUrl must point to /admin/agent-releases.' }
    $loopbackHttp = $return.Scheme -eq 'http' -and $return.IsLoopback
    if ((($return.Scheme -ne 'https') -and -not $loopbackHttp) -or $return.UserInfo -or $return.Query -or $return.Fragment -or $return.AbsolutePath -ne '/admin/agent-releases') { throw 'AdminReturnUrl must be HTTPS, or loopback HTTP, and point to /admin/agent-releases.' }
    return [pscustomobject]@{
        ControllerUrl = $controller.GetLeftPart([UriPartial]::Authority) + '/'
        MachineId = $Request.MachineId.ToLowerInvariant()
        OperationId = $Request.OperationId.ToLowerInvariant()
        UpdateTicket = $Request.UpdateTicket
        ReleaseVersion = $Request.ReleaseVersion
        TargetVersion = $Request.TargetVersion
        PackageSha256 = $Request.PackageSha256.ToUpperInvariant()
        TrustedRootThumbprint = $Request.TrustedRootThumbprint.ToUpperInvariant()
        AdminReturnUrl = $return.AbsoluteUri
    }
}

function Test-AgentUpdateLocalState {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Request, [Parameter(Mandatory)]$State)
    if (-not [string]::Equals([string]$State.IdentityMachineId,[string]$Request.MachineId,[StringComparison]::OrdinalIgnoreCase)) { throw 'Installed Agent identity does not match the update command machine.' }
    if ($State.ReceiptMachineId -and -not [string]::Equals([string]$State.ReceiptMachineId,[string]$Request.MachineId,[StringComparison]::OrdinalIgnoreCase)) { throw 'Enrollment receipt identity does not match the installed Agent identity.' }
    if (-not $State.InstallPathSafe -or -not $State.DataPathSafe) { throw 'Agent Program Files or ProgramData path has an unsafe ACL, reparse point or traversal.' }
    if ($State.PSObject.Properties['WorkingDirectorySafe'] -and -not $State.WorkingDirectorySafe) { throw 'Agent update recovery directory has an unsafe ACL, reparse point or path.' }
    if (-not $State.StreamFenceClosed) { throw 'The verified Sunshine stream fence is missing or open.' }
    $install = [IO.Path]::GetFullPath([string]$State.InstallDirectory)
    $data = [IO.Path]::GetFullPath([string]$State.DataDirectory)
    $expectedInstall = [IO.Path]::GetFullPath((Join-Path (Join-Path $env:ProgramFiles 'KamenRaider') 'Agent'))
    $expectedData = [IO.Path]::GetFullPath((Join-Path (Join-Path $env:ProgramData 'KamenRaider') 'Agent'))
    if (-not [string]::Equals([string]$State.InstallDirectory,$expectedInstall,[StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$State.DataDirectory,$expectedData,[StringComparison]::OrdinalIgnoreCase)) { throw 'Agent installation paths do not match the managed Program Files and ProgramData layout.' }
    if ($State.WorkingDirectoryPath) {
        $expectedWork = Join-Path (Join-Path $env:ProgramFiles 'KamenRaider') ('.Agent-update-work-' + $Request.OperationId.ToLowerInvariant())
        if (-not [string]::Equals([string]$State.WorkingDirectoryPath,$expectedWork,[StringComparison]::OrdinalIgnoreCase)) { throw 'Agent update recovery directory is not in the managed install parent.' }
    }
    $agentPath = [IO.Path]::GetFullPath((Join-Path $install 'Agent.exe'))
    $expectedAgentPath = ('"{0}" --contentRoot "{1}"' -f $agentPath,$install)
    $expectedGuardPath = $expectedAgentPath + ' --stream-guard'
    if ($State.AgentService.Name -cne 'KamenRaider Agent' -or $State.AgentService.Account -ine 'NT SERVICE\KamenRaider Agent' -or $State.AgentService.StartMode -ine 'Auto' -or
        -not [string]::Equals([string]$State.AgentService.BinaryPath,$expectedAgentPath,[StringComparison]::OrdinalIgnoreCase) -or
        'KamenRaider Stream Guard' -notin @($State.AgentService.Dependencies)) { throw 'Existing Agent service identity, binary path, startup or dependency does not match the managed installation.' }
    if ($State.GuardService.Name -cne 'KamenRaider Stream Guard' -or $State.GuardService.Account -ine 'LocalSystem' -or $State.GuardService.StartMode -ine 'Auto' -or
        -not [string]::Equals([string]$State.GuardService.BinaryPath,$expectedGuardPath,[StringComparison]::OrdinalIgnoreCase)) { throw 'Existing Stream Guard service identity or path does not match the managed Agent installation.' }
    if ($State.SunshineService.Name -cne 'SunshineService' -or $State.SunshineService.StartMode -ine 'Manual' -or 'KamenRaider Stream Guard' -notin @($State.SunshineService.Dependencies)) { throw 'Sunshine must retain its verified Guard dependency and manual startup.' }
    if ($State.ConfigurationDataDirectory -and -not [string]::Equals([string]$State.ConfigurationDataDirectory,$data,[StringComparison]::OrdinalIgnoreCase)) { throw 'Existing Agent configuration points to a different or noncanonical data directory.' }
    if ($State.ConfigurationControllerUrl -and -not [string]::Equals([string]$State.ConfigurationControllerUrl,[string]$Request.ControllerUrl,[StringComparison]::OrdinalIgnoreCase)) { throw 'Existing Agent configuration points to a different Controller.' }
    if ($State.TrustedRootPresent -eq $false) { throw 'The configured Controller trust root is not installed in LocalMachine\Root.' }
    return $true
}

function Invoke-AgentUpdateWorkflow {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Request, [Parameter(Mandatory)]$LocalState, [Parameter(Mandatory)]$Actions)
    $normalized = Test-AgentUpdateRequest -Request $Request
    Test-AgentUpdateLocalState -Request $normalized -State $LocalState | Out-Null
    if ($Actions.PSObject.Properties['Prepare']) { & $Actions.Prepare $normalized }
    $started = $false
    & $Actions.Start $normalized
    $started = $true
    try {
        $package = & $Actions.Download $normalized
        if (-not $package) { throw 'Controller did not provide an Agent package.' }
        if ($package.Path -and (Test-Path -LiteralPath ([string]$package.Path) -PathType Leaf)) {
            $packageLength = (Get-Item -LiteralPath $package.Path -Force).Length
            $actualHash = (Get-FileHash -LiteralPath $package.Path -Algorithm SHA256).Hash.ToUpperInvariant()
        } elseif ($package.Stream -is [IO.Stream] -and $package.Stream.CanSeek) {
            $packageLength = if ($package.PSObject.Properties['Length']) { [long]$package.Length } else { [long]$package.Stream.Length }
            $package.Stream.Position = 0
            $hasher = [Security.Cryptography.SHA256]::Create()
            try { $actualHash = [BitConverter]::ToString($hasher.ComputeHash($package.Stream)).Replace('-','') } finally { $hasher.Dispose(); $package.Stream.Position = 0 }
        } else { throw 'Controller did not provide a readable Agent package stream.' }
        if ($packageLength -gt 104857600) { throw 'Agent package exceeds 100 MiB.' }
        if ($actualHash -cne $normalized.PackageSha256) { throw 'Agent package SHA-256 does not match the Admin-pinned release hash.' }
        if ($Actions.PSObject.Properties['Validate']) { & $Actions.Validate $normalized $package }
        $result = & $Actions.Apply $normalized $package
        $status = switch ([string]$result.Outcome) {
            'applied' { 'awaitingHeartbeat' }
            'rolledBack' { 'rollingBack' }
            default { 'failed' }
        }
        & $Actions.Report $normalized $status
        return [pscustomobject]@{ Status = $status; Result = $result }
    } catch {
        if ($started) {
            try { & $Actions.Report $normalized 'failed' } catch { Write-Warning 'Agent update failed and its Controller status report also failed; the maintenance lock remains closed.' }
        }
        throw
    } finally {
        if ($Actions.PSObject.Properties['Cleanup']) {
            try { & $Actions.Cleanup } catch { Write-Warning 'Agent update working files could not be removed; local binaries and Controller lock were left unchanged.' }
        }
    }
}

function Get-AgentUpdateServiceSid {
    param([Parameter(Mandatory)][string]$ServiceName)
    $scPath = Join-Path $env:SystemRoot 'System32\sc.exe'
    $output = @(& $scPath showsid $ServiceName 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Unable to derive the virtual service SID for $ServiceName." }
    $matches = [regex]::Matches(($output -join "`n"),'S-1-5-80(?:-\d+){5}')
    if ($matches.Count -ne 1) { throw "Service SID output for $ServiceName did not contain exactly one service SID." }
    return [Security.Principal.SecurityIdentifier]::new($matches[0].Value)
}

function Test-AgentUpdateWriteRights {
    param([int]$Rights,[switch]$Private)
    $write = [Security.AccessControl.FileSystemRights]::WriteData -bor [Security.AccessControl.FileSystemRights]::AppendData -bor
        [Security.AccessControl.FileSystemRights]::WriteAttributes -bor [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    if ($Private) { $write = $write -bor [Security.AccessControl.FileSystemRights]::Read }
    return (($Rights -band [int]$write) -ne 0 -or ($Rights -band [int]268435456) -ne 0 -or ($Rights -band [int]1073741824) -ne 0 -or ($Private -and ($Rights -band [int]-2147483648) -ne 0))
}

function Assert-AgentUpdatePathAcl {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier[]]$TrustedSids,[Security.Principal.SecurityIdentifier]$RequireModifySid,[Security.Principal.SecurityIdentifier]$RequireReadOnlySid,[Security.Principal.SecurityIdentifier]$DisallowServiceWriteSid,[switch]$Private,[Security.AccessControl.FileSystemSecurity]$Acl)
    if (-not $Acl) { $Acl = Get-Acl -LiteralPath $Path -ErrorAction Stop }
    $trusted = @($TrustedSids | ForEach-Object { $_.Value })
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) { throw "Installation path has an untrusted owner: $Path" }
    $rules = @($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $rule.IdentityReference.Value -notin $trusted -and (Test-AgentUpdateWriteRights -Rights ([int]$rule.FileSystemRights) -Private:$Private)) { throw "Installation path has an untrusted writable ACL: $Path" }
    }
    if ($RequireReadOnlySid) { $DisallowServiceWriteSid = $RequireReadOnlySid }
    if ($DisallowServiceWriteSid) {
        $serviceRules = @($rules | Where-Object { $_.IdentityReference.Value -eq $DisallowServiceWriteSid.Value })
        $writable = @($serviceRules | Where-Object { $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and (Test-AgentUpdateWriteRights -Rights ([int]$_.FileSystemRights)) }).Count -gt 0
        if ($writable) { throw "Agent service must not have write access to installed binaries: $Path" }
    }
    if ($RequireReadOnlySid) {
        $readExecute = [int][Security.AccessControl.FileSystemRights]::ReadAndExecute
        $hasRead = @($serviceRules | Where-Object { $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and (([int]$_.FileSystemRights -band $readExecute) -eq $readExecute) }).Count -gt 0
        $denied = @($serviceRules | Where-Object { $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Deny -and (([int]$_.FileSystemRights -band $readExecute) -ne 0) }).Count -gt 0
        if (-not $hasRead -or $denied -or -not $acl.AreAccessRulesProtected) { throw "Agent binary directory does not grant protected read-only service access: $Path" }
    }
    if ($RequireModifySid) {
        $modify = [Security.AccessControl.FileSystemRights]::Modify
        $serviceRules = @($rules | Where-Object { $_.IdentityReference.Value -eq $RequireModifySid.Value })
        $hasModify = @($serviceRules | Where-Object { $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and ((([int]$_.FileSystemRights -band [int]$modify) -eq [int]$modify) -or (([int]$_.FileSystemRights -band 268435456) -ne 0)) }).Count -gt 0
        $denied = @($serviceRules | Where-Object { $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Deny -and (Test-AgentUpdateWriteRights -Rights ([int]$_.FileSystemRights) -Private) }).Count -gt 0
        if (-not $hasModify -or $denied -or -not $acl.AreAccessRulesProtected) { throw "Agent data directory ACL is not private or does not grant the service its required access: $Path" }
    }
}

function Assert-AgentUpdateProgramDataRoot {
    param([Parameter(Mandatory)][string]$Path)
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-18') { throw 'ProgramData root must be owned by LocalSystem.' }
    $usersSid = 'S-1-5-32-545'
    $trusted = @('S-1-5-18','S-1-5-32-544','S-1-3-0','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464',[Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    $usersWrite = @()
    foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or $rule.IdentityReference.Value -in $trusted) { continue }
        $writes = Test-AgentUpdateWriteRights -Rights ([int]$rule.FileSystemRights)
        if ($rule.IdentityReference.Value -eq $usersSid) {
            if (-not $writes) { continue }
            $withoutSynchronize = [int]$rule.FileSystemRights -band (-bnot [int][Security.AccessControl.FileSystemRights]::Synchronize)
            if ($withoutSynchronize -ne [int][Security.AccessControl.FileSystemRights]::Write -or $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]::ContainerInherit -or $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None -or $rule.IsInherited) { throw 'ProgramData root has a broader-than-default Users grant.' }
            $usersWrite += $rule
            continue
        }
        if ($writes) { throw 'ProgramData root has an untrusted writable grant.' }
    }
    if ($usersWrite.Count -ne 1) { throw 'ProgramData root does not have its expected create-subdirectory grant.' }
}

function Assert-AgentUpdateSystemRoot {
    param([Parameter(Mandatory)][string]$Path)
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $trustedInstaller = 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
    $authenticatedUsers = 'S-1-5-11'
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $trustedInstaller) { throw 'System drive root must be owned by TrustedInstaller.' }
    $createDirectories = @()
    $childRead = @()
    foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { continue }
        if ($rule.IdentityReference.Value -in @('S-1-5-18','S-1-5-32-544','S-1-3-0',$trustedInstaller,[Security.Principal.WindowsIdentity]::GetCurrent().User.Value)) { continue }
        $rights = [int]$rule.FileSystemRights
        $writes = Test-AgentUpdateWriteRights -Rights $rights
        if ($rule.IdentityReference.Value -eq $authenticatedUsers) {
            if ($rights -eq -536805376 -and $rule.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit' -and $rule.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::InheritOnly -and -not $rule.IsInherited) { $childRead += $rule; continue }
            $withoutSynchronize = $rights -band (-bnot [int][Security.AccessControl.FileSystemRights]::Synchronize)
            if ($writes -and $withoutSynchronize -eq [int][Security.AccessControl.FileSystemRights]::CreateDirectories -and $rule.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]::None -and $rule.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::None -and -not $rule.IsInherited) { $createDirectories += $rule; continue }
            if (-not $writes) { continue }
            throw 'System drive root has an unexpected Authenticated Users grant.'
        }
        if ($writes) { throw 'System drive root has an untrusted writable grant.' }
    }
    if ($createDirectories.Count -ne 1 -or $childRead.Count -ne 1) { throw 'System drive root does not have its expected Windows ACL.' }
}

function Assert-AgentUpdateTrustedTree {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier[]]$TrustedSids,[Security.Principal.SecurityIdentifier]$ServiceSid,[switch]$Private,[switch]$ReadOnly,[switch]$AncestorsOnly)
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.StartsWith('\\')) { throw 'Network Agent installation paths are not supported.' }
    $programData = [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\')
    $systemRoot = [IO.Path]::GetPathRoot([Environment]::GetFolderPath([Environment+SpecialFolder]::System)).TrimEnd('\')
    $current = $full
    while ($current) {
        if (-not (Test-Path -LiteralPath $current)) { throw "Agent installation path is missing: $current" }
        $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Agent installation path contains a reparse point: $current" }
        if ($item.PSIsContainer -and [string]::Equals([IO.Path]::GetFullPath($current).TrimEnd('\'),$programData,[StringComparison]::OrdinalIgnoreCase)) { Assert-AgentUpdateProgramDataRoot -Path $current }
        elseif ($item.PSIsContainer -and [string]::Equals([IO.Path]::GetFullPath($current).TrimEnd('\'),$systemRoot,[StringComparison]::OrdinalIgnoreCase)) { Assert-AgentUpdateSystemRoot -Path $current }
        elseif ($item.PSIsContainer) {
            $isTarget = [string]::Equals($current,$full,[StringComparison]::OrdinalIgnoreCase)
            $rootSid = if ($isTarget) { $ServiceSid } else { $null }
            Assert-AgentUpdatePathAcl -Path $current -TrustedSids $TrustedSids -RequireModifySid $(if (-not $ReadOnly) { $rootSid }) -RequireReadOnlySid $(if ($ReadOnly) { $rootSid }) -Private:($Private -and $isTarget)
        }
        else { Assert-AgentUpdatePathAcl -Path $current -TrustedSids $TrustedSids -Private:$Private }
        if ([string]::Equals($current,[IO.Path]::GetPathRoot($current).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = [IO.Path]::GetDirectoryName($current)
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
    $root = Get-Item -LiteralPath $full -Force
    if ($root.PSIsContainer -and -not $AncestorsOnly) {
        $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($full)
        while ($pending.Count -gt 0) {
            $directory = $pending.Pop()
            foreach ($child in @(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)) {
                if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Agent installation tree contains a reparse point: $($child.FullName)" }
                Assert-AgentUpdatePathAcl -Path $child.FullName -TrustedSids $TrustedSids -DisallowServiceWriteSid $(if ($ReadOnly) { $ServiceSid }) -Private:$Private
                if ($child.PSIsContainer) { $pending.Push($child.FullName) }
            }
        }
    }
}

function Assert-AgentUpdateLockParent {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier[]]$TrustedSids)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw 'The managed Agent installation parent is missing.' }
    Assert-AgentUpdateTrustedTree -Path $Path -TrustedSids $TrustedSids -AncestorsOnly
}

function Get-AgentUpdateLockParent {
    # The Windows common application-data location is machine-wide and does
    # not vary with the caller's ProgramFiles environment or bitness.
    $root = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    if (-not $root -or -not [IO.Path]::IsPathRooted($root) -or $root.StartsWith('\\')) { throw 'A local machine-wide ProgramData path is required for Agent updates.' }
    return Join-Path ([IO.Path]::GetFullPath($root)) 'KamenRaider'
}

function Initialize-AgentUpdateLockNativeType {
    if (-not ('KamenRaider.Update.NativeLockFileV1' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace KamenRaider.Update {
 public static class NativeLockFileV1 {
  [StructLayout(LayoutKind.Sequential)]
  private struct FileInformation {
   public uint Attributes;
   public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
   public uint Volume, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
  }
  [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
  public static bool IsRegularSingleLink(SafeFileHandle file) {
   FileInformation info;
   if (!GetFileInformationByHandle(file,out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
   const uint directory=0x10, reparsePoint=0x400;
   return (info.Attributes & (directory | reparsePoint)) == 0 && info.NumberOfLinks == 1;
  }
 }
}
'@
    }
}

function Enter-AgentUpdateLocalLock {
    $parent = Get-AgentUpdateLockParent
    $trusted = @([Security.Principal.SecurityIdentifier]::new('S-1-5-18'),[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'),[Security.Principal.SecurityIdentifier]::new('S-1-3-0'),[Security.Principal.SecurityIdentifier]::new('S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'),[Security.Principal.WindowsIdentity]::GetCurrent().User)
    # No service SID is trusted for writing the parent or lock file. The fixed
    # filename serializes every operation for this installation, across sessions.
    Assert-AgentUpdateLockParent -Path $parent -TrustedSids $trusted
    $path = Join-Path $parent '.Agent-update.lock'
    if (Test-Path -LiteralPath $path) {
        $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Agent update lock must be a regular single-link file.' }
    }
    try { $file = [IO.File]::Open($path,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
    catch [IO.IOException] { throw 'Another Agent update is already running or the local update lock is unavailable.' }
    try {
        Initialize-AgentUpdateLockNativeType
        if (-not [KamenRaider.Update.NativeLockFileV1]::IsRegularSingleLink($file.SafeFileHandle)) { throw 'Agent update lock must be a regular single-link file.' }
        $acl = if ($PSVersionTable.PSEdition -eq 'Desktop') { $file.GetAccessControl() } else { [IO.FileSystemAclExtensions]::GetAccessControl($file) }
        Assert-AgentUpdatePathAcl -Path $path -TrustedSids $trusted -Acl $acl
        return $file
    } catch { $file.Dispose(); throw }
}

function Remove-AgentUpdatePrivateWorkDirectory {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$Parent,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier[]]$TrustedSids)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $root = [IO.Path]::GetFullPath($Path)
    $expectedParent = [IO.Path]::GetFullPath($Parent)
    if ([IO.Path]::GetDirectoryName($root) -ne $expectedParent -or [IO.Path]::GetFileName($root) -notlike '.Agent-update-work-*') { throw 'Unsafe Agent update working-directory path.' }
    $item = Get-Item -LiteralPath $root -Force -ErrorAction Stop
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Agent update working directory must be a real protected directory.' }
    Assert-AgentUpdateTrustedTree -Path $root -TrustedSids $TrustedSids -Private
    Remove-Item -LiteralPath $root -Recurse -Force
}

function Get-AgentUpdateServiceRecord {
    param([Parameter(Mandatory)][string]$Name)
    $records = @(Get-CimInstance -ClassName Win32_Service -Filter ("Name='{0}'" -f $Name.Replace("'","''")) -ErrorAction Stop)
    if ($records.Count -ne 1) { throw "Managed Windows service '$Name' is missing or ambiguous." }
    $record = $records[0]
    $service = Get-Service -Name $Name -ErrorAction Stop
    $dependencies = @($service.ServicesDependedOn | ForEach-Object { [string]$_.ServiceName })
    [pscustomobject]@{ Name = [string]$record.Name; Account = [string]$record.StartName; StartMode = [string]$record.StartMode; BinaryPath = [string]$record.PathName; Dependencies = $dependencies; State = [string]$record.State }
}

function Get-AgentUpdateSunshineExecutable {
    param([Parameter(Mandatory)][string]$ImagePath)
    $value = [Environment]::ExpandEnvironmentVariables($ImagePath.Trim())
    if ($value.StartsWith('"')) {
        $end = $value.IndexOf('"',1)
        if ($end -lt 0) { throw 'SunshineService image path has an invalid quoted executable.' }
        $exe = $value.Substring(1,$end - 1)
    } else {
        $exe = ($value -split '\s+',2)[0]
    }
    if (-not [IO.Path]::IsPathRooted($exe) -or [IO.Path]::GetFileName($exe) -ine 'sunshinesvc.exe' -or [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($exe)) -ine 'tools') { throw 'SunshineService must point to Sunshine\tools\sunshinesvc.exe.' }
    return Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($exe))) 'sunshine.exe'
}

function Test-AgentUpdateStreamFence {
    param([Parameter(Mandatory)][string]$SunshineExecutable)
    $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop)
    foreach ($name in @('Domain','Private','Public')) {
        $profile = @($profiles | Where-Object { $_.Name -eq $name })
        if ($profile.Count -ne 1 -or [string]$profile[0].Enabled -ne 'True' -or [string]$profile[0].AllowLocalFirewallRules -ne 'True') { return $false }
    }
    $policy = New-Object -ComObject HNetCfg.FwPolicy2
    if ([int]$policy.CurrentProfileTypes -eq 0 -or [int]$policy.LocalPolicyModifyState -ne 0) { return $false }
    $rules = @(Get-NetFirewallRule -PolicyStore ActiveStore -Name 'KamenRaider Stream Guard - Sunshine' -ErrorAction Stop)
    if ($rules.Count -ne 1 -or [string]$rules[0].PolicyStoreSourceType -ne 'Local' -or [string]$rules[0].Enabled -ne 'True' -or
        [string]$rules[0].Direction -ne 'Inbound' -or [string]$rules[0].Action -ne 'Block' -or [string]$rules[0].Profile -ne 'Any' -or [string]$rules[0].EdgeTraversalPolicy -ne 'Block' -or
        ($null -ne $rules[0].OverrideBlockRules -and [string]$rules[0].OverrideBlockRules -ne 'False')) { return $false }
    $rule = $rules[0]
    $application = @($rule | Get-NetFirewallApplicationFilter -ErrorAction Stop)
    $service = @($rule | Get-NetFirewallServiceFilter -ErrorAction Stop)
    $ports = @($rule | Get-NetFirewallPortFilter -ErrorAction Stop)
    $addresses = @($rule | Get-NetFirewallAddressFilter -ErrorAction Stop)
    $interfaces = @($rule | Get-NetFirewallInterfaceFilter -ErrorAction Stop)
    $types = @($rule | Get-NetFirewallInterfaceTypeFilter -ErrorAction Stop)
    if ($application.Count -ne 1 -or $service.Count -ne 1 -or $ports.Count -ne 1 -or $addresses.Count -ne 1 -or $interfaces.Count -ne 1 -or $types.Count -ne 1) { return $false }
    return [string]::Equals([IO.Path]::GetFullPath([string]$application[0].Program),[IO.Path]::GetFullPath($SunshineExecutable),[StringComparison]::OrdinalIgnoreCase) -and
        [string]$service[0].Service -eq 'Any' -and [string]$ports[0].Protocol -eq 'Any' -and (@($ports[0].LocalPort) -join ',') -eq 'Any' -and (@($ports[0].RemotePort) -join ',') -eq 'Any' -and
        (@($addresses[0].LocalAddress) -join ',') -eq 'Any' -and (@($addresses[0].RemoteAddress) -join ',') -eq 'Any' -and (@($interfaces[0].InterfaceAlias) -join ',') -eq 'Any' -and [string]$types[0].InterfaceType -eq 'Any'
}

function Assert-AgentUpdateHost {
    param([Parameter(Mandatory)]$Request)
    if ($env:OS -ne 'Windows_NT') { throw 'Windows is required.' }
    if (-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run the Agent update from an elevated PowerShell session.' }
    if (-not (Test-Path -LiteralPath "Cert:\LocalMachine\Root\$($Request.TrustedRootThumbprint)")) { throw 'The configured Controller trust root is not installed in LocalMachine\Root.' }
}

function Assert-AgentUpdateVirtualAccountSid {
    param([Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$ServiceSid)
    $virtualAccountSid = ([Security.Principal.NTAccount]::new('NT SERVICE\KamenRaider Agent')).Translate([Security.Principal.SecurityIdentifier])
    if ($virtualAccountSid.Value -ne $ServiceSid.Value) { throw 'Agent service SID does not match its virtual service account.' }
}

function Get-AgentUpdateCurrentState {
    param([Parameter(Mandatory)]$Request)
    Assert-AgentUpdateHost -Request $Request
    $install = Join-Path (Join-Path $env:ProgramFiles 'KamenRaider') 'Agent'
    $data = Join-Path (Join-Path $env:ProgramData 'KamenRaider') 'Agent'
    $serviceSid = Get-AgentUpdateServiceSid -ServiceName 'KamenRaider Agent'
    Assert-AgentUpdateVirtualAccountSid -ServiceSid $serviceSid
    $trustedSids = @([Security.Principal.SecurityIdentifier]::new('S-1-5-18'),[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'),[Security.Principal.SecurityIdentifier]::new('S-1-3-0'),[Security.Principal.SecurityIdentifier]::new('S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'),[Security.Principal.WindowsIdentity]::GetCurrent().User,$serviceSid)
    Assert-AgentUpdateTrustedTree -Path $data -TrustedSids $trustedSids -ServiceSid $serviceSid -Private
    $parent = Join-Path $env:ProgramFiles 'KamenRaider'
    $operation = $Request.OperationId.ToLowerInvariant()
    $backupDirectory = Join-Path $parent ('.Agent-rollback-' + $operation)
    foreach ($prefix in @('.Agent-rollback-','.Agent-backup-stage-','.Agent-stage-','.Agent-prior-','.Agent-restore-')) {
        $recoveryPath = Join-Path $parent ($prefix + $operation)
        if (Test-Path -LiteralPath $recoveryPath) {
            if (-not (Test-Path -LiteralPath $recoveryPath -PathType Container)) { throw 'Agent update recovery path is not a real directory.' }
            Assert-AgentUpdateTrustedTree -Path $recoveryPath -TrustedSids $trustedSids -ServiceSid $serviceSid -ReadOnly -Private
        }
    }
    $hasBackup = Test-Path -LiteralPath $backupDirectory -PathType Container
    $hasInstall = Test-Path -LiteralPath $install -PathType Container
    if ($hasInstall) { Assert-AgentUpdateTrustedTree -Path $install -TrustedSids $trustedSids -ServiceSid $serviceSid -ReadOnly }
    elseif ((Test-Path -LiteralPath $install) -or -not $hasBackup) { throw 'Agent installation is missing and no protected operation backup is available.' }
    $workingDirectory = Join-Path $parent ('.Agent-update-work-' + $operation)
    if (Test-Path -LiteralPath $workingDirectory) {
        $workingItem = Get-Item -LiteralPath $workingDirectory -Force -ErrorAction Stop
        if (-not $workingItem.PSIsContainer -or ($workingItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Agent update recovery directory is not a real directory.' }
        Assert-AgentUpdateTrustedTree -Path $workingDirectory -TrustedSids $trustedSids -Private
    }
    $agentService = Get-AgentUpdateServiceRecord -Name 'KamenRaider Agent'
    $guardService = Get-AgentUpdateServiceRecord -Name 'KamenRaider Stream Guard'
    $sunshineService = Get-AgentUpdateServiceRecord -Name 'SunshineService'
    if ($agentService.State -notin @('Running','Stopped') -or $guardService.State -notin @('Running','Stopped')) { throw 'Agent or Stream Guard service state is uncertain.' }
    if (-not $hasBackup -and ($agentService.State -ne 'Running' -or $guardService.State -ne 'Running')) { throw 'Agent and Stream Guard services must be running before a new update.' }
    $identityPath = Join-Path $data 'identity\identity.json'
    if (-not (Test-Path -LiteralPath $identityPath -PathType Leaf)) { throw 'Agent identity file is missing.' }
    try { $identity = [IO.File]::ReadAllText($identityPath) | ConvertFrom-Json -ErrorAction Stop } catch { throw 'Agent identity file is invalid.' }
    $receiptPath = Join-Path $data 'enrollment-receipt.json'
    $receiptMachineId = $null
    if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
        try { $receipt = [IO.File]::ReadAllText($receiptPath) | ConvertFrom-Json -ErrorAction Stop; $receiptMachineId = [string]$receipt.MachineId } catch { throw 'Agent enrollment receipt is invalid.' }
    }
    # A published, protected backup is the authority during recovery. The
    # installation may be an incomplete copy, including a truncated config.
    $configurationPath = Join-Path $(if ($hasBackup) { $backupDirectory } else { $install }) 'appsettings.json'
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) { throw 'Existing Agent appsettings.json is missing.' }
    try { $configuration = [IO.File]::ReadAllText($configurationPath) | ConvertFrom-Json -ErrorAction Stop } catch { throw 'Existing Agent appsettings.json is invalid.' }
    $sunshineExecutable = Get-AgentUpdateSunshineExecutable -ImagePath $sunshineService.BinaryPath
    $state = [pscustomobject]@{
        InstallDirectory = $install; DataDirectory = $data; ServiceSid = $serviceSid
        IdentityMachineId = [string]$identity.MachineId; ReceiptMachineId = $receiptMachineId
        AgentService = $agentService; GuardService = $guardService; SunshineService = $sunshineService
        InstallPathSafe = $true; DataPathSafe = $true; StreamFenceClosed = (Test-AgentUpdateStreamFence -SunshineExecutable $sunshineExecutable)
        WorkingDirectorySafe = $true; WorkingDirectoryPath = $workingDirectory; TrustedSids = $trustedSids
        ConfigurationDataDirectory = [string]$configuration.DataDirectory; ConfigurationControllerUrl = [string]$configuration.ControllerUrl
        TrustedRootPresent = $true
    }
    Test-AgentUpdateLocalState -Request $Request -State $state | Out-Null
    return $state
}

function Test-AgentUpdatePackageArchive {
    [CmdletBinding()]
    param([string]$ArchivePath,[IO.Stream]$ArchiveStream,[Parameter(Mandatory)][string]$ExpectedSha256,[Parameter(Mandatory)][string]$TargetVersion)
    if ($ExpectedSha256 -notmatch '\A[0-9a-fA-F]{64}\z') { throw 'Expected package SHA-256 is invalid.' }
    $ownsStream = $false
    if (-not $ArchiveStream) {
        if ([string]::IsNullOrWhiteSpace($ArchivePath)) { throw 'An Agent package path or stream is required.' }
        $item = Get-Item -LiteralPath $ArchivePath -Force -ErrorAction Stop
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Agent package must be a regular file, not a reparse point.' }
        $ArchiveStream = [IO.File]::OpenRead($ArchivePath)
        $ownsStream = $true
    }
    if (-not $ArchiveStream.CanSeek -or $ArchiveStream.Length -gt 104857600) { if ($ownsStream) { $ArchiveStream.Dispose() }; throw 'Agent package stream is not seekable or exceeds 100 MiB.' }
    $ArchiveStream.Position = 0
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $actualHash = [BitConverter]::ToString($hasher.ComputeHash($ArchiveStream)).Replace('-','') } finally { $hasher.Dispose(); $ArchiveStream.Position = 0 }
    if ($actualHash -ine $ExpectedSha256) { if ($ownsStream) { $ArchiveStream.Dispose() }; throw 'Agent package SHA-256 does not match the Admin-pinned release hash.' }
    Add-Type -AssemblyName System.IO.Compression
    $zip = [IO.Compression.ZipArchive]::new($ArchiveStream,[IO.Compression.ZipArchiveMode]::Read,$true)
    try {
        if ($zip.Entries.Count -gt 5000) { throw 'Agent package contains more than 5,000 entries.' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $expanded = [long]0
        foreach ($entry in $zip.Entries) {
            $name = [string]$entry.FullName
            if ([string]::IsNullOrWhiteSpace($name) -or $name.Contains('\') -or $name.StartsWith('/') -or $name.Contains(':') -or $name -match '(^|/)\.\.?(/|$)' -or [IO.Path]::IsPathRooted($name)) { throw "Agent package contains unsafe path: $name" }
            if (-not $seen.Add($name)) { throw "Agent package contains a duplicate path: $name" }
            $parts = $name.TrimEnd('/').Split('/')
            if ($parts.Count -eq 0 -or $parts -contains '' -or $parts -contains '.' -or $parts -contains '..') { throw "Agent package contains unsafe path: $name" }
            if ($parts[0] -ne 'Agent' -and $parts[0] -notin @('Install-AgentPackage.ps1','Install-Service.ps1','Install-TightVncServer.ps1','Prepare-SunshineStreamGate.ps1','PrivateInstallFiles.ps1','ServiceInstallTransaction.ps1','Update-AgentPackage.ps1','Update-Agent.ps1','AgentPackage.manifest.json')) { throw "Unexpected Agent package entry: $name" }
            $expanded += [long]$entry.Length
            if ($expanded -gt 524288000) { throw 'Expanded Agent package exceeds 500 MiB.' }
        }
        foreach ($required in @('Agent/Agent.exe','Agent/Agent.deps.json','Install-AgentPackage.ps1','Install-Service.ps1','Install-TightVncServer.ps1','Prepare-SunshineStreamGate.ps1','PrivateInstallFiles.ps1','ServiceInstallTransaction.ps1','Update-AgentPackage.ps1','Update-Agent.ps1','AgentPackage.manifest.json')) {
            if (-not $seen.Contains($required)) { throw "Agent package is missing $required." }
        }
        if ($seen.Contains('Agent/appsettings.json')) { throw 'Agent package must not replace local appsettings.json.' }
        $manifestEntry = $zip.GetEntry('AgentPackage.manifest.json')
        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop } finally { $reader.Dispose() }
        if ($manifest.packageFormatVersion -ne 1 -or $manifest.agentInformationalVersion -cne $TargetVersion -or $manifest.sourceCommit -cnotmatch '\A[0-9a-f]{12}\z' -or $manifest.sourceDirty -ne $false -or $manifest.releaseBuild -ne $true -or $manifest.agentUpdateChallengeProtocol -ne 1) { throw 'Agent package manifest does not identify the requested clean release with update confirmation support.' }
        return [pscustomobject]@{ EntryCount = $zip.Entries.Count; ExpandedBytes = $expanded; Manifest = $manifest }
    } finally { $zip.Dispose(); $ArchiveStream.Position = 0; if ($ownsStream) { $ArchiveStream.Dispose() } }
}

function Expand-AgentUpdatePackageArchive {
    param([Parameter(Mandatory)][string]$ArchivePath,[Parameter(Mandatory)][string]$Destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $Destination) { throw 'Agent package extraction destination already exists.' }
    $root = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
    [void](New-Item -ItemType Directory -Path $root)
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in $zip.Entries) {
            $relative = $entry.FullName.Replace('/',[IO.Path]::DirectorySeparatorChar)
            $target = [IO.Path]::GetFullPath((Join-Path $root $relative))
            if (-not $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Agent package path escaped the extraction directory.' }
            if ($entry.FullName.EndsWith('/')) { [void](New-Item -ItemType Directory -Path $target -Force); continue }
            $parent = [IO.Path]::GetDirectoryName($target)
            if (-not (Test-Path -LiteralPath $parent)) { [void](New-Item -ItemType Directory -Path $parent -Force) }
            $input = $entry.Open()
            try {
                $output = [IO.File]::Open($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                try { $input.CopyTo($output); $output.Flush($true) } finally { $output.Dispose() }
            } finally { $input.Dispose() }
        }
    } finally { $zip.Dispose() }
    return $root
}

function Initialize-AgentPinnedHttpType {
    if (-not ('KamenRaider.Update.PinnedHttpClientV1' -as [type])) {
        Add-Type -AssemblyName System.Net.Http
        $source = @'
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
namespace KamenRaider.Update {
 public static class PinnedHttpClientV1 {
  public static HttpClient Create(string rootThumbprint) {
   string expected = new string(rootThumbprint.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
   var handler = new HttpClientHandler();
   handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) => {
    if (errors != SslPolicyErrors.None || chain == null || chain.ChainElements.Count == 0) return false;
    var root = chain.ChainElements[chain.ChainElements.Count - 1].Certificate;
    string actual = new string(root.Thumbprint.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
    return String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
   };
   return new HttpClient(handler, true);
  }
 }
}
'@
        if ($PSVersionTable.PSEdition -eq 'Desktop') {
            Add-Type -TypeDefinition $source -ReferencedAssemblies @('System.dll','System.Core.dll',[Net.Http.HttpClient].Assembly.Location)
        } else {
            Add-Type -TypeDefinition $source
        }
    }
}

function Invoke-AgentPinnedHttpRequest {
    param([Parameter(Mandatory)][string]$Uri,[Parameter(Mandatory)][string]$Method,[Parameter(Mandatory)][string]$TrustedRootThumbprint,[hashtable]$Headers=@(),[string]$JsonBody,[string]$OutputPath,[switch]$InMemory)
    Initialize-AgentPinnedHttpType
    $client = [KamenRaider.Update.PinnedHttpClientV1]::Create($TrustedRootThumbprint)
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method),[Uri]::new($Uri,[UriKind]::Absolute))
    foreach ($header in $Headers.GetEnumerator()) { [void]$request.Headers.TryAddWithoutValidation([string]$header.Key,[string]$header.Value) }
    if ($PSBoundParameters.ContainsKey('JsonBody')) { $request.Content = [Net.Http.StringContent]::new($JsonBody,[Text.Encoding]::UTF8,'application/json') }
    try {
        $response = $client.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            if (-not $response.IsSuccessStatusCode) { throw "Controller update request failed with HTTP $([int]$response.StatusCode)." }
            if ($InMemory) {
                $stream = [IO.MemoryStream]::new()
                $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                try {
                    $buffer = New-Object byte[] 81920
                    $total = [long]0
                    while (($read = $inputStream.Read($buffer,0,$buffer.Length)) -gt 0) {
                        $total += $read
                        if ($total -gt 104857600) { throw 'Agent package exceeds 100 MiB.' }
                        $stream.Write($buffer,0,$read)
                    }
                } catch { $stream.Dispose(); throw }
                finally { $inputStream.Dispose() }
                $stream.Position = 0
                return [pscustomobject]@{ Stream = $stream; Length = $total }
            }
            if ($OutputPath) {
                if (Test-Path -LiteralPath $OutputPath) { throw 'Agent package download destination already exists.' }
                $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                try {
                    $file = [IO.File]::Open($OutputPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                    try {
                        $buffer = New-Object byte[] 81920
                        $total = [long]0
                        while (($read = $stream.Read($buffer,0,$buffer.Length)) -gt 0) {
                            $total += $read
                            if ($total -gt 104857600) { throw 'Agent package exceeds 100 MiB.' }
                            $file.Write($buffer,0,$read)
                        }
                        $file.Flush($true)
                    } finally { $file.Dispose() }
                } finally { $stream.Dispose() }
                return [pscustomobject]@{ Path = $OutputPath }
            }
            return $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        } finally { $response.Dispose() }
    } finally { $request.Dispose(); $client.Dispose() }
}

function New-AgentUpdateActions {
    param([Parameter(Mandatory)]$Request,[Parameter(Mandatory)]$State)
    $normalized = Test-AgentUpdateRequest -Request $Request
    $controllerRoot = $normalized.ControllerUrl.TrimEnd('/')
    $operationBase = "$controllerRoot/agent/machines/$($normalized.MachineId)/agent-updates/$($normalized.OperationId)"
    $privateRoot = Join-Path (Join-Path $env:ProgramFiles 'KamenRaider') ('.Agent-update-work-' + $normalized.OperationId)
    $session = [pscustomobject]@{ Download = $null }
    return [pscustomobject]@{
        Start = {
            $body = @{ ticket=$normalized.UpdateTicket; releaseVersion=$normalized.ReleaseVersion; targetVersion=$normalized.TargetVersion; targetPackageSha256=$normalized.PackageSha256 } | ConvertTo-Json -Compress
            Invoke-AgentPinnedHttpRequest -Uri "$operationBase/start" -Method 'POST' -TrustedRootThumbprint $normalized.TrustedRootThumbprint -JsonBody $body | Out-Null
        }.GetNewClosure()
        Download = {
            $headers = @{
                'X-Agent-Update-Ticket' = $normalized.UpdateTicket
                'X-Agent-Update-Release-Version' = $normalized.ReleaseVersion
                'X-Agent-Update-Target-Version' = $normalized.TargetVersion
                'X-Agent-Update-Package-SHA256' = $normalized.PackageSha256
            }
            $session.Download = Invoke-AgentPinnedHttpRequest -Uri "$operationBase/package" -Method 'GET' -TrustedRootThumbprint $normalized.TrustedRootThumbprint -Headers $headers -InMemory
            return $session.Download
        }.GetNewClosure()
        Validate = {
            param($resolvedRequest,$package)
            Test-AgentUpdatePackageArchive -ArchiveStream $package.Stream -ExpectedSha256 $resolvedRequest.PackageSha256 -TargetVersion $resolvedRequest.TargetVersion | Out-Null
            Remove-AgentUpdatePrivateWorkDirectory -Path $privateRoot -Parent (Join-Path $env:ProgramFiles 'KamenRaider') -TrustedSids $State.TrustedSids
        }.GetNewClosure()
        Apply = {
            param($resolvedRequest,$package)
            if (Test-Path -LiteralPath $privateRoot) { throw 'A previous Agent update working directory exists; refusing to reuse it.' }
            [void](New-Item -ItemType Directory -Path $privateRoot)
            $acl = [Security.AccessControl.DirectorySecurity]::new()
            $acl.SetAccessRuleProtection($true,$false)
            $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
            $acl.SetOwner($owner)
            foreach ($sid in @([Security.Principal.SecurityIdentifier]::new('S-1-5-18'),[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'),$owner)) {
                $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
            }
            Set-Acl -LiteralPath $privateRoot -AclObject $acl
            $packagePath = Join-Path $privateRoot 'AgentPackage.zip'
            $package.Stream.Position = 0
            $file = [IO.File]::Open($packagePath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try { $package.Stream.CopyTo($file); $file.Flush($true) } finally { $file.Dispose(); $package.Stream.Position = 0 }
            if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ine $resolvedRequest.PackageSha256) { throw 'Staged Agent package changed after validation.' }
            $expanded = Expand-AgentUpdatePackageArchive -ArchivePath $packagePath -Destination (Join-Path $privateRoot 'expanded')
            $internal = Join-Path $expanded 'Update-AgentPackage.ps1'
            & $internal -Action Apply -InstallDirectory $State.InstallDirectory -DataDirectory $State.DataDirectory -PackageRoot $expanded -OperationId $resolvedRequest.OperationId -TargetVersion $resolvedRequest.TargetVersion -ServiceSid $State.ServiceSid.Value
        }.GetNewClosure()
        Cleanup = {
            if ($session.Download -and $session.Download.Stream) { $session.Download.Stream.Dispose() }
            Remove-AgentUpdatePrivateWorkDirectory -Path $privateRoot -Parent (Join-Path $env:ProgramFiles 'KamenRaider') -TrustedSids $State.TrustedSids
        }.GetNewClosure()
        Report = {
            param($resolvedRequest,$status)
            if ($status -notin @('awaitingHeartbeat','rollingBack','failed')) { throw 'Unsupported Agent update report status.' }
            $body = @{ ticket=$normalized.UpdateTicket; releaseVersion=$normalized.ReleaseVersion; targetVersion=$normalized.TargetVersion; targetPackageSha256=$normalized.PackageSha256; status=$status } | ConvertTo-Json -Compress
            Invoke-AgentPinnedHttpRequest -Uri "$operationBase/status" -Method 'POST' -TrustedRootThumbprint $normalized.TrustedRootThumbprint -JsonBody $body | Out-Null
        }.GetNewClosure()
    }
}

function Invoke-AgentUpdateMain {
    param([hashtable]$BoundParameters)
    $request = [pscustomobject]@{
        ControllerUrl = $BoundParameters.ControllerUrl; MachineId = $BoundParameters.MachineId; OperationId = $BoundParameters.OperationId
        UpdateTicket = $BoundParameters.UpdateTicket; ReleaseVersion = $BoundParameters.ReleaseVersion; TargetVersion = $BoundParameters.TargetVersion
        PackageSha256 = $BoundParameters.PackageSha256; TrustedRootThumbprint = $BoundParameters.TrustedRootThumbprint; AdminReturnUrl = $BoundParameters.AdminReturnUrl
    }
    $normalized = Test-AgentUpdateRequest -Request $request
    Assert-AgentUpdateHost -Request $normalized
    $lock = Enter-AgentUpdateLocalLock
    try {
        $state = Get-AgentUpdateCurrentState -Request $normalized
        $actions = New-AgentUpdateActions -Request $normalized -State $state
        $result = Invoke-AgentUpdateWorkflow -Request $normalized -LocalState $state -Actions $actions
    } finally { $lock.Dispose() }
    Write-Output $result.Result.Message
    if ($result.Status -eq 'awaitingHeartbeat') { Write-Output "Agent $($normalized.TargetVersion) is running. Controller maintenance remains locked until a later signed heartbeat echoes the current Controller update challenge with that version. The rollback copy is retained." }
    elseif ($result.Status -eq 'rollingBack') { Write-Warning 'Agent binaries were restored. Controller maintenance remains locked until a later signed heartbeat echoes the current update challenge with the previous version. Older Agents may require same-operation recovery to install the compatible target.' }
    else { Write-Warning 'Agent update failed or its outcome is uncertain. The machine remains locked; use the Admin recovery action after reviewing local state.' }
    try { Start-Process -FilePath $normalized.AdminReturnUrl } catch { Write-Warning 'The update finished, but the Admin machines page could not be opened automatically.' }
}

if ($DefineFunctionsOnly) { return }
$scriptRequest = [pscustomobject]@{
    ControllerUrl = $ControllerUrl; MachineId = $MachineId; OperationId = $OperationId; UpdateTicket = $UpdateTicket
    ReleaseVersion = $ReleaseVersion; TargetVersion = $TargetVersion; PackageSha256 = $PackageSha256
    TrustedRootThumbprint = $TrustedRootThumbprint; AdminReturnUrl = $AdminReturnUrl
}
Test-AgentUpdateRequest -Request $scriptRequest | Out-Null
Invoke-AgentUpdateMain -BoundParameters $PSBoundParameters
