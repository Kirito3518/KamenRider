[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ControllerUrl,
    [Parameter(Mandatory)][string]$MachineId,
    [Parameter(Mandatory)][string]$OperationId,
    [string]$ChainId,
    [string]$HeadOperationId,
    [long]$Generation,
    [string]$EnrollmentKeyFingerprint,
    [long]$PackageLength,
    [Parameter(Mandatory)][string]$UpdateTicket,
    [Parameter(Mandatory)][string]$ReleaseVersion,
    [Parameter(Mandatory)][string]$TargetVersion,
    [Parameter(Mandatory)][string]$PackageSha256,
    [Parameter(Mandatory)][string]$TrustedRootThumbprint,
    [Parameter(Mandatory)][string]$AdminReturnUrl,
    [string]$AssetBaseUrl,
    [string]$TargetRuntime,
    [string]$WrapperSha256,
    [long]$WrapperLength,
    [string]$HelperRelativeUrl,
    [string]$HelperSha256,
    [long]$HelperLength,
    [int]$HelperProtocol,
    [switch]$DefineFunctionsOnly
)

$ErrorActionPreference = 'Stop'
$script:MaximumAgentMaintenanceHelperBytes = 157286400

function Get-AgentUpdateProperty {
    param([Parameter(Mandatory)]$Request,[Parameter(Mandatory)][string]$Name)
    if ($Request -is [Collections.IDictionary]) {
        if ($Request.Contains($Name)) { return $Request[$Name] }
        return $null
    }
    $property = $Request.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    return $null
}

function ConvertTo-AgentUpdateHttpsOrigin {
    param([Parameter(Mandatory)][string]$Value,[Parameter(Mandatory)][string]$Name)
    try { $uri = [Uri]::new($Value,[UriKind]::Absolute) } catch { throw "$Name must be an HTTPS origin." }
    if ($uri.Scheme -cne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -cne '/') {
        throw "$Name must be an HTTPS origin without credentials, path, query or fragment."
    }
    return $uri.GetLeftPart([UriPartial]::Authority) + '/'
}

function Test-AgentUpdateRequest {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Request)

    $names = @(
        'ControllerUrl','MachineId','OperationId','ChainId','HeadOperationId','Generation','EnrollmentKeyFingerprint','PackageLength',
        'UpdateTicket','ReleaseVersion','TargetVersion',
        'PackageSha256','TrustedRootThumbprint','AdminReturnUrl','AssetBaseUrl','TargetRuntime',
        'WrapperSha256','WrapperLength','HelperRelativeUrl','HelperSha256','HelperLength','HelperProtocol'
    )
    foreach ($name in $names) {
        $value = Get-AgentUpdateProperty -Request $Request -Name $name
        if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) { throw "Update request is missing $name." }
    }

    $machineId = [string](Get-AgentUpdateProperty -Request $Request -Name 'MachineId')
    $operationId = [string](Get-AgentUpdateProperty -Request $Request -Name 'OperationId')
    $chainId = [string](Get-AgentUpdateProperty -Request $Request -Name 'ChainId')
    $headOperationId = [string](Get-AgentUpdateProperty -Request $Request -Name 'HeadOperationId')
    $generation = [long](Get-AgentUpdateProperty -Request $Request -Name 'Generation')
    $enrollmentKeyFingerprint = [string](Get-AgentUpdateProperty -Request $Request -Name 'EnrollmentKeyFingerprint')
    $packageLength = [long](Get-AgentUpdateProperty -Request $Request -Name 'PackageLength')
    $ticket = [string](Get-AgentUpdateProperty -Request $Request -Name 'UpdateTicket')
    $releaseVersion = [string](Get-AgentUpdateProperty -Request $Request -Name 'ReleaseVersion')
    $targetVersion = [string](Get-AgentUpdateProperty -Request $Request -Name 'TargetVersion')
    $packageHash = [string](Get-AgentUpdateProperty -Request $Request -Name 'PackageSha256')
    $thumbprint = [string](Get-AgentUpdateProperty -Request $Request -Name 'TrustedRootThumbprint')
    $runtime = [string](Get-AgentUpdateProperty -Request $Request -Name 'TargetRuntime')
    $wrapperHash = [string](Get-AgentUpdateProperty -Request $Request -Name 'WrapperSha256')
    $helperHash = [string](Get-AgentUpdateProperty -Request $Request -Name 'HelperSha256')
    $helperUrl = [string](Get-AgentUpdateProperty -Request $Request -Name 'HelperRelativeUrl')
    $wrapperLength = [long](Get-AgentUpdateProperty -Request $Request -Name 'WrapperLength')
    $helperLength = [long](Get-AgentUpdateProperty -Request $Request -Name 'HelperLength')
    $helperProtocol = [int](Get-AgentUpdateProperty -Request $Request -Name 'HelperProtocol')

    if ($machineId -notmatch '\A[0-9a-fA-F]{32}\z' -or $operationId -notmatch '\A[0-9a-fA-F]{32}\z') {
        throw 'MachineId and OperationId must be 32-character hexadecimal GUIDs.'
    }
    if ($chainId -notmatch '\A[0-9a-fA-F]{32}\z' -or $headOperationId -notmatch '\A[0-9a-fA-F]{32}\z' -or $headOperationId -ne $operationId -or $generation -lt 1) {
        throw 'ChainId, HeadOperationId, OperationId and Generation are inconsistent.'
    }
    if ($enrollmentKeyFingerprint -notmatch '\A[0-9a-fA-F]{64}\z') { throw 'EnrollmentKeyFingerprint is invalid.' }
    if ($packageLength -lt 1 -or $packageLength -gt 104857600) { throw 'PackageLength is invalid.' }
    if ($ticket -notmatch '\A[A-Za-z0-9_-]{43}\z') { throw 'UpdateTicket is invalid.' }
    foreach ($version in @($releaseVersion,$targetVersion)) {
        if ($version.Length -gt 64 -or $version -notmatch '\A(?:\d+\.\d+\.\d+|1\.0\.0\+g[0-9a-f]{12,40})\z') {
            throw 'ReleaseVersion and TargetVersion must be supported clean Agent release versions.'
        }
    }
    if ($releaseVersion -cne $targetVersion) { throw 'ReleaseVersion and TargetVersion must identify the same published Agent package.' }
    if ($packageHash -notmatch '\A[0-9a-fA-F]{64}\z') { throw 'PackageSha256 must be a SHA-256 digest.' }
    if ($thumbprint -notmatch '\A[0-9a-fA-F]{40}\z') { throw 'TrustedRootThumbprint is invalid.' }
    if ($runtime -cnotin @('win-x64','win-arm64')) { throw 'TargetRuntime must identify a supported native Windows architecture.' }
    if ($wrapperHash -notmatch '\A[0-9a-fA-F]{64}\z' -or $wrapperLength -lt 1 -or $wrapperLength -gt 2097152) {
        throw 'Pinned wrapper length or SHA-256 is invalid.'
    }
    if ($helperHash -notmatch '\A[0-9a-fA-F]{64}\z' -or $helperLength -lt 256 -or $helperLength -gt $script:MaximumAgentMaintenanceHelperBytes) {
        throw 'Pinned maintenance helper length or SHA-256 is invalid.'
    }
    if ($helperProtocol -ne 1) { throw 'The maintenance helper protocol is unsupported.' }
    $expectedHelperUrl = '/agent/maintenance/sha256/' + $helperHash.ToLowerInvariant() + '/AgentMaintenance.exe'
    if ($helperUrl -cne $expectedHelperUrl) { throw 'Maintenance helper URL is not the fixed content-addressed deployment path.' }

    $controller = ConvertTo-AgentUpdateHttpsOrigin -Value ([string](Get-AgentUpdateProperty -Request $Request -Name 'ControllerUrl')) -Name 'ControllerUrl'
    $assetBase = ConvertTo-AgentUpdateHttpsOrigin -Value ([string](Get-AgentUpdateProperty -Request $Request -Name 'AssetBaseUrl')) -Name 'AssetBaseUrl'
    try { $return = [Uri]::new([string](Get-AgentUpdateProperty -Request $Request -Name 'AdminReturnUrl'),[UriKind]::Absolute) }
    catch { throw 'AdminReturnUrl must point to /admin/agent-releases.' }
    $loopbackHttp = $return.Scheme -eq 'http' -and $return.IsLoopback
    if ((($return.Scheme -ne 'https') -and -not $loopbackHttp) -or $return.UserInfo -or $return.Query -or $return.Fragment -or $return.AbsolutePath -cne '/admin/agent-releases') {
        throw 'AdminReturnUrl must be HTTPS, or loopback HTTP, and point to /admin/agent-releases.'
    }

    return [pscustomobject]@{
        ControllerUrl = $controller
        AssetBaseUrl = $assetBase
        MachineId = $machineId.ToLowerInvariant()
        OperationId = $operationId.ToLowerInvariant()
        ChainId = $chainId.ToLowerInvariant()
        HeadOperationId = $headOperationId.ToLowerInvariant()
        Generation = $generation
        EnrollmentKeyFingerprint = $enrollmentKeyFingerprint.ToUpperInvariant()
        PackageLength = $packageLength
        UpdateTicket = $ticket
        ReleaseVersion = $releaseVersion
        TargetVersion = $targetVersion
        TargetRuntime = $runtime
        PackageSha256 = $packageHash.ToUpperInvariant()
        TrustedRootThumbprint = $thumbprint.ToUpperInvariant()
        AdminReturnUrl = $return.AbsoluteUri
        WrapperSha256 = $wrapperHash.ToUpperInvariant()
        WrapperLength = $wrapperLength
        HelperRelativeUrl = $helperUrl
        HelperSha256 = $helperHash.ToUpperInvariant()
        HelperLength = $helperLength
        HelperProtocol = $helperProtocol
    }
}

function Get-AgentUpdateBootstrapRoot {
    $commonData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    if ([string]::IsNullOrWhiteSpace($commonData) -or -not [IO.Path]::IsPathRooted($commonData)) {
        throw 'Native CommonApplicationData is unavailable.'
    }
    return Join-Path $commonData 'KamenRaider.AgentMaintenanceBootstrap'
}

function Assert-AgentUpdateNoReparseAncestors {
    param([Parameter(Mandatory)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Maintenance bootstrap path contains a reparse point: $current" }
        }
        $root = [IO.Path]::GetPathRoot($current)
        if (-not $root -or [string]::Equals($current.TrimEnd('\'),$root.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = [IO.Path]::GetDirectoryName($current)
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
}

function Get-AgentUpdateProtectedSids {
    return @(
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    )
}

function Get-AgentUpdatePrivateOwnerSids {
    return @(
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    )
}

function Get-AgentUpdateTrustedAncestorOwnerSids {
    return @(
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'),
        [Security.Principal.SecurityIdentifier]::new('S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    )
}

function New-AgentUpdatePrivateDirectorySecurity {
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true,$false)
    $security.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach ($sid in (Get-AgentUpdateProtectedSids)) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $sid,[Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow))
    }
    return $security
}

function New-AgentUpdatePrivateFileSecurity {
    $security = [Security.AccessControl.FileSecurity]::new()
    $security.SetAccessRuleProtection($true,$false)
    $security.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach ($sid in (Get-AgentUpdateProtectedSids)) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $sid,[Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.AccessControlType]::Allow))
    }
    return $security
}

function Set-AgentUpdatePrivateFileSecurity {
    param([Parameter(Mandatory)][string]$Path)
    Set-Acl -LiteralPath $Path -AclObject (New-AgentUpdatePrivateFileSecurity) -ErrorAction Stop
}

function Test-AgentUpdateStandardAncestorCreateGrant {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][Security.AccessControl.FileSystemAccessRule]$Rule)
    $ownerSid = $Rule.IdentityReference.Value
    $ruleRights = [int]$Rule.FileSystemRights -band (-bnot [int][Security.AccessControl.FileSystemRights]::Synchronize)
    $pathFull = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($pathFull)
    $commonData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    $commonData = if ($commonData) { [IO.Path]::GetFullPath($commonData) } else { '' }

    if ([string]::Equals($pathFull.TrimEnd('\'),$root.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase) -and
        $ownerSid -eq 'S-1-5-11' -and
        $ruleRights -eq [int][Security.AccessControl.FileSystemRights]::CreateDirectories -and
        $Rule.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]::None -and
        $Rule.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::None) {
        return $true
    }

    if ($commonData -and [string]::Equals($pathFull.TrimEnd('\'),$commonData.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase) -and
        $ownerSid -eq 'S-1-5-32-545' -and
        $ruleRights -eq [int][Security.AccessControl.FileSystemRights]::Write -and
        $Rule.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]::ContainerInherit -and
        $Rule.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::None) {
        return $true
    }

    return $false
}

function Assert-AgentUpdateTrustedAncestorChain {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $trustedOwners = @((Get-AgentUpdateTrustedAncestorOwnerSids) | ForEach-Object { $_.Value })
    $trustedPrincipals = @((Get-AgentUpdateProtectedSids) | ForEach-Object { $_.Value })
    $unsafeRights = [int][Security.AccessControl.FileSystemRights]::WriteData -bor
        [int][Security.AccessControl.FileSystemRights]::AppendData -bor
        [int][Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [int][Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [int][Security.AccessControl.FileSystemRights]::Delete -bor
        [int][Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [int][Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [int][Security.AccessControl.FileSystemRights]::TakeOwnership
    $inheritOnly = [int][Security.AccessControl.PropagationFlags]::InheritOnly
    Assert-AgentUpdateNoReparseAncestors -Path $Path

    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Maintenance staging ancestor is not a regular directory: $current"
            }
            $acl = Get-Acl -LiteralPath $current -ErrorAction Stop
            $ownerSid = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
            if ($ownerSid -notin $trustedOwners) { throw "Maintenance staging ancestor has an untrusted owner: $current" }

            foreach ($rule in @($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))) {
                if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
                    $rule.IdentityReference.Value -in $trustedPrincipals -or
                    (([int]$rule.PropagationFlags -band $inheritOnly) -ne 0)) { continue }
                if (Test-AgentUpdateStandardAncestorCreateGrant -Path $current -Rule $rule) { continue }
                if (([int]$rule.FileSystemRights -band $unsafeRights) -ne 0) {
                    throw "Maintenance staging ancestor grants untrusted write, delete, or ACL-change rights: $current"
                }
            }
        }

        $root = [IO.Path]::GetPathRoot($current)
        if (-not $root -or [string]::Equals($current.TrimEnd('\'),$root.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = [IO.Path]::GetDirectoryName($current)
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
}

function Assert-AgentUpdatePrivateAcl {
    param([Parameter(Mandatory)][string]$Path,[switch]$Directory)
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    $trustedOwners = @((Get-AgentUpdatePrivateOwnerSids) | ForEach-Object { $_.Value })
    if ($owner -notin $trustedOwners) { throw 'Maintenance bootstrap asset has an unsafe owner.' }
    if (-not $acl.AreAccessRulesProtected) { throw 'Maintenance bootstrap ACL is inherited.' }
    $rules = @($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne 2) { throw 'Maintenance bootstrap ACL contains unexpected access rules.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $expectedInheritance = if ($Directory) {
        [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    } else { [Security.AccessControl.InheritanceFlags]::None }
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544') -or
            $rule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl -or
            $rule.IsInherited -or $rule.InheritanceFlags -ne $expectedInheritance -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None -or
            -not $seen.Add($rule.IdentityReference.Value)) {
            throw 'Maintenance bootstrap ACL is not limited to LocalSystem and Administrators.'
        }
    }
    if (-not $seen.Contains('S-1-5-18') -or -not $seen.Contains('S-1-5-32-544')) {
        throw 'Maintenance bootstrap ACL is missing a required trusted principal.'
    }
    if (-not $Directory) {
        Initialize-AgentUpdateNativeFileInfoType
        if ([KamenRaider.Update.NativeFileInfoV1]::LinkCount($Path) -ne 1) {
            throw 'Maintenance bootstrap asset must have exactly one hard link.'
        }
    }
}

function New-AgentUpdatePrivateStage {
    param([Parameter(Mandatory)]$Request)
    if (-not [Environment]::OSVersion.Platform.ToString().StartsWith('Win')) { throw 'Agent maintenance bootstrap requires Windows.' }
    $root = Get-AgentUpdateBootstrapRoot
    $commonData = [IO.Path]::GetDirectoryName($root)
    Assert-AgentUpdateTrustedAncestorChain -Path $commonData
    Assert-AgentUpdateTrustedAncestorChain -Path $root

    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        [void][IO.DirectoryInfo]::new($root).Create((New-AgentUpdatePrivateDirectorySecurity))
    }
    Assert-AgentUpdateNoReparseAncestors -Path $root
    Assert-AgentUpdatePrivateAcl -Path $root -Directory

    $stage = Join-Path $root ($Request.OperationId + '-' + [guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $stage) { throw 'Maintenance bootstrap staging path already exists.' }
    [void][IO.DirectoryInfo]::new($stage).Create((New-AgentUpdatePrivateDirectorySecurity))
    Assert-AgentUpdateNoReparseAncestors -Path $stage
    Assert-AgentUpdatePrivateAcl -Path $stage -Directory
    return $stage
}

function Assert-AgentUpdatePrivateStage {
    param([Parameter(Mandatory)][string]$Path)
    $root = [IO.Path]::GetFullPath((Get-AgentUpdateBootstrapRoot))
    $full = [IO.Path]::GetFullPath($Path)
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($full),$root,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Maintenance bootstrap stage escaped its protected root.'
    }
    Assert-AgentUpdateTrustedAncestorChain -Path $full
    Assert-AgentUpdatePrivateAcl -Path $full -Directory
}

function Get-AgentUpdateAssetBytes {
    param([Parameter(Mandatory)]$Request,[Parameter(Mandatory)][string]$RelativeUrl,[Parameter(Mandatory)][long]$ExpectedLength)
    if ($RelativeUrl -cne '/agent/Update-Agent.ps1' -and $RelativeUrl -cne $Request.HelperRelativeUrl) {
        throw 'Maintenance bootstrap rejected an asset outside the pinned deployment manifest.'
    }
    $uri = $Request.AssetBaseUrl.TrimEnd('/') + $RelativeUrl
    $response = Invoke-AgentPinnedHttpRequest -Uri $uri -Method 'GET' -TrustedRootThumbprint $Request.TrustedRootThumbprint -MaximumBytes $ExpectedLength -InMemory
    if ($response -is [byte[]]) { $bytes = $response }
    elseif ($response -and $response.PSObject.Properties['Bytes'] -and $response.Bytes -is [byte[]]) { $bytes = $response.Bytes }
    else { throw 'Pinned asset download did not return bounded bytes.' }
    if ($bytes.LongLength -ne $ExpectedLength) { throw 'Pinned asset length does not match the deployment manifest.' }
    return ,$bytes
}

function Write-AgentUpdateStageAsset {
    param([Parameter(Mandatory)][string]$Stage,[Parameter(Mandatory)][string]$Name,[Parameter(Mandatory)][byte[]]$Bytes,[Parameter(Mandatory)][long]$ExpectedLength,[Parameter(Mandatory)][string]$ExpectedSha256)
    if ($Name -notin @('Update-Agent.ps1','AgentMaintenance.exe')) { throw 'Maintenance bootstrap rejected an unexpected staging asset name.' }
    Assert-AgentUpdatePrivateStage -Path $Stage
    $path = Join-Path $Stage $Name
    if (Test-Path -LiteralPath $path) { throw 'Maintenance bootstrap asset path already exists.' }
    if ($Bytes.LongLength -ne $ExpectedLength -or (Get-AgentUpdateSha256Hex -Bytes $Bytes) -cne $ExpectedSha256) {
        throw 'Downloaded maintenance bootstrap asset does not match its pinned length and SHA-256.'
    }
    $file = [IO.FileStream]::new($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,65536,[IO.FileOptions]::WriteThrough)
    try { $file.Write($Bytes,0,$Bytes.Length); $file.Flush($true) }
    finally { $file.Dispose() }
    Set-AgentUpdatePrivateFileSecurity -Path $path
    Assert-AgentUpdateNoReparseAncestors -Path $path
    Assert-AgentUpdatePrivateAcl -Path $path
    Assert-AgentUpdateFilePin -Path $path -ExpectedLength $ExpectedLength -ExpectedSha256 $ExpectedSha256
    return $path
}

function Get-AgentUpdateSha256Hex {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($Bytes)).Replace('-','') }
    finally { $algorithm.Dispose() }
}

function Assert-AgentUpdateFilePin {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][long]$ExpectedLength,[Parameter(Mandatory)][string]$ExpectedSha256)
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -ne $ExpectedLength) {
        throw 'Maintenance bootstrap staged asset type or length is unsafe.'
    }
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $actual = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','')
        if ($actual -cne $ExpectedSha256) { throw 'Maintenance bootstrap staged asset SHA-256 changed before launch.' }
        if ($stream.Length -ne $ExpectedLength) { throw 'Maintenance bootstrap staged asset length changed before launch.' }
    }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Get-AgentMaintenancePeMachine {
    param([Parameter(Mandatory)][string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 256) { throw 'Pinned maintenance helper is too short to be a PE image.' }
        $reader = [IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Pinned maintenance helper has no DOS header.' }
        $stream.Position = 0x3c
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt $stream.Length - 6) { throw 'Pinned maintenance helper PE offset is invalid.' }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Pinned maintenance helper has no PE signature.' }
        return $reader.ReadUInt16()
    }
    finally { $stream.Dispose() }
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
   var handler = new HttpClientHandler { AllowAutoRedirect = false };
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
}
'@
        if ($PSVersionTable.PSEdition -eq 'Desktop') {
            Add-Type -TypeDefinition $source -ReferencedAssemblies @('System.dll','System.Core.dll',[Net.Http.HttpClient].Assembly.Location)
        }
        else { Add-Type -TypeDefinition $source }
    }
}

function Initialize-AgentUpdateNativeFileInfoType {
    if (-not ('KamenRaider.Update.NativeFileInfoV1' -as [type])) {
        $source = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace KamenRaider.Update {
 [StructLayout(LayoutKind.Sequential)] public struct ByHandleFileInformationV1 {
  public uint FileAttributes;
  public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
  public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
  public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
  public uint VolumeSerialNumber;
  public uint FileSizeHigh;
  public uint FileSizeLow;
  public uint NumberOfLinks;
  public uint FileIndexHigh;
  public uint FileIndexLow;
 }
 public static class NativeFileInfoV1 {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
  [DllImport("kernel32.dll", SetLastError=true)]
  private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformationV1 info);
  public static uint LinkCount(string path) {
   using (var handle = CreateFile(path, 0x80000000, 0x00000007, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero)) {
    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
    ByHandleFileInformationV1 info;
    if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
    return info.NumberOfLinks;
   }
  }
 }
}
'@
        if ($PSVersionTable.PSEdition -eq 'Desktop') {
            Add-Type -TypeDefinition $source -ReferencedAssemblies @('System.dll','System.Core.dll',[Microsoft.Win32.SafeHandles.SafeFileHandle].Assembly.Location)
        }
        else { Add-Type -TypeDefinition $source }
    }
}

function Invoke-AgentPinnedHttpRequest {
    param([Parameter(Mandatory)][string]$Uri,[Parameter(Mandatory)][string]$Method,[Parameter(Mandatory)][string]$TrustedRootThumbprint,[long]$MaximumBytes=2097152,[switch]$InMemory)
    $target = [Uri]::new($Uri,[UriKind]::Absolute)
    if ($target.Scheme -cne 'https' -or $target.UserInfo -or $target.Fragment) { throw 'Pinned maintenance asset requests require HTTPS and a canonical URL.' }
    Initialize-AgentPinnedHttpType
    $client = [KamenRaider.Update.PinnedHttpClientV1]::Create($TrustedRootThumbprint)
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method),$target)
    try {
        $response = $client.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            if (-not $response.IsSuccessStatusCode) { throw "Pinned maintenance asset request failed with HTTP $([int]$response.StatusCode)." }
            if (-not $InMemory) { throw 'Only bounded in-memory maintenance asset reads are supported.' }
            $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $memory = [IO.MemoryStream]::new()
            try {
                $buffer = New-Object byte[] 81920
                $total = [long]0
                while (($read = $stream.Read($buffer,0,$buffer.Length)) -gt 0) {
                    $total += $read
                    if ($total -gt $MaximumBytes) { throw 'Pinned maintenance asset exceeds its manifest length.' }
                    $memory.Write($buffer,0,$read)
                }
                return ,$memory.ToArray()
            }
            finally { $memory.Dispose(); $stream.Dispose() }
        }
        finally { $response.Dispose() }
    }
    finally { $request.Dispose(); $client.Dispose() }
}

function Get-NativeAgentMaintenanceRuntime {
    if (-not ('KamenRaider.Update.NativeSystemInfoV1' -as [type])) {
        $source = @'
using System.Runtime.InteropServices;
namespace KamenRaider.Update {
 [StructLayout(LayoutKind.Sequential)] public struct NativeSystemInfo { public ushort ProcessorArchitecture; public ushort Reserved; public uint PageSize; public System.IntPtr MinimumApplicationAddress; public System.IntPtr MaximumApplicationAddress; public System.IntPtr ActiveProcessorMask; public uint NumberOfProcessors; public uint ProcessorType; public uint AllocationGranularity; public ushort ProcessorLevel; public ushort ProcessorRevision; }
 public static class NativeSystemInfoV1 {
  [DllImport("kernel32.dll")] public static extern void GetNativeSystemInfo(out NativeSystemInfo info);
  public static ushort Architecture() { NativeSystemInfo info; GetNativeSystemInfo(out info); return info.ProcessorArchitecture; }
 }
}
'@
        Add-Type -TypeDefinition $source
    }
    switch ([KamenRaider.Update.NativeSystemInfoV1]::Architecture()) {
        9 { return 'win-x64' }
        12 { return 'win-arm64' }
        default { throw 'Native Windows architecture is unsupported for Agent maintenance.' }
    }
}

function Assert-AgentUpdateElevated {
    param([Parameter(Mandatory)]$Request)
    if (-not [Environment]::OSVersion.Platform.ToString().StartsWith('Win')) { throw 'Agent maintenance bootstrap requires Windows.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run the Agent maintenance command from an elevated Administrator session.' }
}

function Get-AgentUpdateAgentServiceSid {
    $scPath = Join-Path $env:SystemRoot 'System32\sc.exe'
    if (-not (Test-Path -LiteralPath $scPath)) { throw 'The Agent service SID lookup tool is unavailable.' }
    $output = (& $scPath showsid 'KamenRaider Agent' 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'The installed KamenRaider Agent service is required before the update wrapper can run.' }
    $match = [regex]::Match($output,'SERVICE SID:\s*(S-1-5-80(?:-\d+){5})',[Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) { throw 'The installed KamenRaider Agent service SID could not be resolved.' }
    return [Security.Principal.SecurityIdentifier]::new($match.Groups[1].Value)
}

function New-AgentUpdateFenceDirectorySecurity {
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true,$false)
    $security.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    foreach ($sid in @([Security.Principal.SecurityIdentifier]::new('S-1-5-18'),[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $sid,[Security.AccessControl.FileSystemRights]::FullControl,$inherit,
            [Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
    }
    $serviceSid = Get-AgentUpdateAgentServiceSid
    $serviceRights = [Security.AccessControl.FileSystemRights]'ReadAndExecute,Synchronize'
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $serviceSid,$serviceRights,$inherit,
        [Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
    return $security
}

function Assert-AgentUpdateFenceAcl {
    param([Parameter(Mandatory)][string]$Path)
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin @('S-1-5-18','S-1-5-32-544') -or -not $acl.AreAccessRulesProtected) {
        throw 'The maintenance directory owner or ACL is unsafe; the update wrapper will not repair it automatically.'
    }
    $serviceSid = (Get-AgentUpdateAgentServiceSid).Value
    $rules = @($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne 3) { throw 'The maintenance directory ACL is unexpected; the update wrapper will not repair it automatically.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or $rule.IsInherited -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None -or
            $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit' -or
            -not $seen.Add($rule.IdentityReference.Value)) {
            throw 'The maintenance directory ACL is unexpected; the update wrapper will not repair it automatically.'
        }
        if ($rule.IdentityReference.Value -in @('S-1-5-18','S-1-5-32-544')) {
            if ($rule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl) { throw 'The maintenance directory ACL is missing a trusted full-control rule.' }
        }
        elseif ($rule.IdentityReference.Value -ceq $serviceSid) {
            if ($rule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]'ReadAndExecute,Synchronize') { throw 'The maintenance directory ACL grants the Agent service unexpected rights.' }
        }
        else { throw 'The maintenance directory ACL contains an unexpected principal.' }
    }
    if (-not $seen.Contains('S-1-5-18') -or -not $seen.Contains('S-1-5-32-544') -or -not $seen.Contains($serviceSid)) {
        throw 'The maintenance directory ACL is missing a required principal.'
    }
}

function Initialize-AgentUpdateWorkStore {
    $commonData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    if ([string]::IsNullOrWhiteSpace($commonData) -or -not [IO.Path]::IsPathRooted($commonData)) { throw 'Native CommonApplicationData is unavailable.' }
    $root = Join-Path $commonData 'KamenRaider.AgentMaintenance'
    Assert-AgentUpdateNoReparseAncestors -Path $root
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        [void][IO.DirectoryInfo]::new($root).Create((New-AgentUpdateFenceDirectorySecurity))
    }
    Assert-AgentUpdateFenceAcl -Path $root
    $work = Join-Path $root 'work'
    if (-not (Test-Path -LiteralPath $work -PathType Container)) {
        [void][IO.DirectoryInfo]::new($work).Create((New-AgentUpdatePrivateDirectorySecurity))
    }
    Assert-AgentUpdateNoReparseAncestors -Path $work
    Assert-AgentUpdatePrivateAcl -Path $work -Directory
    return [pscustomobject]@{
        Root = $root
        Work = $work
        Request = (Join-Path $work 'request.json')
        Archive = (Join-Path $work 'target.zip')
    }
}

function Write-AgentUpdateProtectedJson {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][hashtable]$Value)
    $json = $Value | ConvertTo-Json -Depth 4 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    if ($bytes.Length -le 0 -or $bytes.Length -gt 16384) { throw 'The maintenance request exceeds its size limit.' }
    $directory = [IO.Path]::GetDirectoryName($Path)
    Assert-AgentUpdatePrivateAcl -Path $directory -Directory
    $temporary = Join-Path $directory ('.request-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $file = [IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
        try { $file.Write($bytes,0,$bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
        Set-AgentUpdatePrivateFileSecurity -Path $temporary
        Assert-AgentUpdatePrivateAcl -Path $temporary
        if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force -ErrorAction Stop }
        [IO.File]::Move($temporary,$Path)
        Assert-AgentUpdatePrivateAcl -Path $Path
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    }
}

function New-AgentUpdateLifecycleRequest {
    param([Parameter(Mandatory)]$Request,[Parameter(Mandatory)][string]$Command,[string]$HelperBuildSha256)
    $value = [ordered]@{
        schemaVersion = 1
        command = $Command
        operationId = $Request.OperationId
        chainId = $Request.ChainId
        headOperationId = $Request.HeadOperationId
        generation = [long]$Request.Generation
        controllerOrigin = $Request.ControllerUrl
        enrollmentKeyFingerprint = $Request.EnrollmentKeyFingerprint
        helperProtocol = [int]$Request.HelperProtocol
        targetVersion = $Request.TargetVersion
        targetRuntime = $Request.TargetRuntime
        targetPackageSha256 = $Request.PackageSha256
        releaseVersion = $Request.ReleaseVersion
        ticket = $Request.UpdateTicket
    }
    if ($Command -ceq 'bind-source') { $value.helperBuildSha256 = $HelperBuildSha256 }
    return $value
}

function New-AgentUpdateArchiveRequest {
    param([Parameter(Mandatory)]$Request,[Parameter(Mandatory)][string]$Command)
    $value = [ordered]@{
        schemaVersion = 1
        command = $Command
        operationId = $Request.OperationId
        targetVersion = $Request.TargetVersion
        targetRuntime = $Request.TargetRuntime
        targetPackageSha256 = $Request.PackageSha256
        targetPackageLength = [long]$Request.PackageLength
    }
    if ($Command -ceq 'apply') {
        $value.chainId = $Request.ChainId
        $value.headOperationId = $Request.HeadOperationId
        $value.generation = [long]$Request.Generation
    }
    return $value
}

function Invoke-AgentMaintenanceCommand {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$Verb,[int]$TimeoutSeconds = 600)
    if ($Verb -cnotin @('inspect','start','extract','bind-source','apply','report','reconcile')) {
        throw 'Maintenance workflow requested an unsupported helper verb.'
    }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Path
    $start.Arguments = $Verb
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($Path)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Pinned maintenance helper did not start.' }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            throw "Pinned maintenance helper $Verb timed out."
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $parsed = $null
        if (-not [string]::IsNullOrWhiteSpace($stdout)) {
            try { $parsed = $stdout | ConvertFrom-Json -ErrorAction Stop }
            catch { throw "Pinned maintenance helper $Verb returned malformed JSON." }
        }
        return [pscustomobject]@{
            Verb = $Verb
            ExitCode = $process.ExitCode
            Result = $parsed
            StandardError = $stderr
        }
    }
    finally { $process.Dispose() }
}

function Get-AgentMaintenanceResultCategory {
    param($Result)
    if (-not $Result) { return 'helperResultMissing' }
    $category = [string]$Result.category
    if ([string]::IsNullOrWhiteSpace($category)) { $category = [string]$Result.Category }
    if ([string]::IsNullOrWhiteSpace($category)) { return 'helperResultRejected' }
    return $category
}

function Save-AgentUpdatePackage {
    param([Parameter(Mandatory)]$Request,[Parameter(Mandatory)][string]$Destination)
    if (Test-Path -LiteralPath $Destination) {
        Assert-AgentUpdatePrivateAcl -Path $Destination
        Remove-Item -LiteralPath $Destination -Force -ErrorAction Stop
    }
    $directory = [IO.Path]::GetDirectoryName($Destination)
    Assert-AgentUpdatePrivateAcl -Path $directory -Directory
    $relative = '/agent/machines/' + [Uri]::EscapeDataString($Request.MachineId) + '/agent-updates/' + [Uri]::EscapeDataString($Request.OperationId) + '/package'
    $target = [Uri]::new([Uri]::new($Request.ControllerUrl,[UriKind]::Absolute),$relative)
    if ($target.Scheme -cne 'https') { throw 'Controller package downloads require HTTPS.' }
    Initialize-AgentPinnedHttpType
    $client = [KamenRaider.Update.PinnedHttpClientV1]::Create($Request.TrustedRootThumbprint)
    $message = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,$target)
    $stream = $null
    $file = $null
    $hash = $null
    try {
        [void]$message.Headers.TryAddWithoutValidation('X-Agent-Update-Ticket',$Request.UpdateTicket)
        [void]$message.Headers.TryAddWithoutValidation('X-Agent-Update-Release-Version',$Request.ReleaseVersion)
        [void]$message.Headers.TryAddWithoutValidation('X-Agent-Update-Target-Version',$Request.TargetVersion)
        [void]$message.Headers.TryAddWithoutValidation('X-Agent-Update-Package-SHA256',$Request.PackageSha256)
        $response = $client.SendAsync($message,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            if (-not $response.IsSuccessStatusCode) { throw "Pinned package download failed with HTTP $([int]$response.StatusCode)." }
            $declared = $response.Content.Headers.ContentLength
            if ($declared -and [long]$declared -ne [long]$Request.PackageLength) { throw 'Pinned package length does not match the update ticket.' }
            $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $file = [IO.FileStream]::new($Destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,65536,[IO.FileOptions]::WriteThrough)
            $hash = [Security.Cryptography.SHA256]::Create()
            $buffer = New-Object byte[] 81920
            $total = [long]0
            while (($read = $stream.Read($buffer,0,$buffer.Length)) -gt 0) {
                $total += $read
                if ($total -gt [long]$Request.PackageLength -or $total -gt 104857600) { throw 'Pinned package exceeds its declared length.' }
                [void]$hash.TransformBlock($buffer,0,$read,$null,0)
                $file.Write($buffer,0,$read)
            }
            [void]$hash.TransformFinalBlock((New-Object byte[] 0),0,0)
            $file.Flush($true)
        }
        finally {
            if ($file) { $file.Dispose(); $file = $null }
            if ($stream) { $stream.Dispose(); $stream = $null }
            $response.Dispose()
        }
        Set-AgentUpdatePrivateFileSecurity -Path $Destination
        Assert-AgentUpdateNoReparseAncestors -Path $Destination
        Assert-AgentUpdatePrivateAcl -Path $Destination
        if ((Get-Item -LiteralPath $Destination).Length -ne [long]$Request.PackageLength) { throw 'Downloaded package length changed after download.' }
        $actual = [BitConverter]::ToString($hash.Hash).Replace('-','')
        if ($actual -cne $Request.PackageSha256) { throw 'Downloaded package does not match the signed package SHA-256.' }
    }
    finally {
        if ($file) { $file.Dispose() }
        if ($stream) { $stream.Dispose() }
        if ($hash) { $hash.Dispose() }
        $message.Dispose()
        $client.Dispose()
    }
}

function Assert-AgentMaintenanceStep {
    param([Parameter(Mandatory)]$Step,[Parameter(Mandatory)][string]$Name)
    if (-not $Step.Result -or [string]$Step.Result.status -cne 'ok') {
        throw ("Trusted maintenance " + $Name + " did not complete: " + (Get-AgentMaintenanceResultCategory -Result $Step.Result))
    }
}

function Enter-AgentUpdateWorkflowLock {
    param([Parameter(Mandatory)]$WorkStore)
    $path = Join-Path $WorkStore.Work '.update.lock'
    if (-not (Test-Path -LiteralPath $path)) {
        $created = [IO.FileStream]::new($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
        try { } finally { $created.Dispose() }
        Set-AgentUpdatePrivateFileSecurity -Path $path
    }
    Assert-AgentUpdateNoReparseAncestors -Path $path
    Assert-AgentUpdatePrivateAcl -Path $path
    try {
        $stream = [IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None,4096)
    }
    catch [IO.IOException] {
        throw 'Another Agent update wrapper is already active on this machine.'
    }
    try {
        $owner = [Text.Encoding]::UTF8.GetBytes([string]$PID)
        $stream.SetLength(0)
        $stream.Write($owner,0,$owner.Length)
        $stream.Flush($true)
    }
    catch {
        $stream.Dispose()
        throw
    }
    return $stream
}

function Invoke-AgentMaintenanceProcess {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$Verb)
    if ($Verb -cne 'inspect') { throw 'Maintenance bootstrap only permits the read-only inspect verb.' }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Path
    $start.Arguments = 'inspect'
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($Path)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Pinned maintenance helper did not start.' }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            try { $process.Kill() } catch { }
            throw 'Pinned maintenance helper read-only inspection timed out.'
        }
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $stdoutTask.GetAwaiter().GetResult()
            StandardError = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally { $process.Dispose() }
}

function Invoke-AgentUpdateMain {
    [CmdletBinding()]
    param([Parameter(Mandatory)][hashtable]$BoundParameters)

    $request = [pscustomobject]@{
        ControllerUrl = $BoundParameters.ControllerUrl
        AssetBaseUrl = $BoundParameters.AssetBaseUrl
        MachineId = $BoundParameters.MachineId
        OperationId = $BoundParameters.OperationId
        ChainId = $BoundParameters.ChainId
        HeadOperationId = $BoundParameters.HeadOperationId
        Generation = $BoundParameters.Generation
        EnrollmentKeyFingerprint = $BoundParameters.EnrollmentKeyFingerprint
        PackageLength = $BoundParameters.PackageLength
        UpdateTicket = $BoundParameters.UpdateTicket
        ReleaseVersion = $BoundParameters.ReleaseVersion
        TargetVersion = $BoundParameters.TargetVersion
        TargetRuntime = $BoundParameters.TargetRuntime
        PackageSha256 = $BoundParameters.PackageSha256
        TrustedRootThumbprint = $BoundParameters.TrustedRootThumbprint
        AdminReturnUrl = $BoundParameters.AdminReturnUrl
        WrapperSha256 = $BoundParameters.WrapperSha256
        WrapperLength = $BoundParameters.WrapperLength
        HelperRelativeUrl = $BoundParameters.HelperRelativeUrl
        HelperSha256 = $BoundParameters.HelperSha256
        HelperLength = $BoundParameters.HelperLength
        HelperProtocol = $BoundParameters.HelperProtocol
    }
    $normalized = Test-AgentUpdateRequest -Request $request
    Assert-AgentUpdateElevated -Request $normalized

    $nativeRuntime = Get-NativeAgentMaintenanceRuntime
    if ($normalized.TargetRuntime -cne $nativeRuntime) { throw 'Target Agent runtime does not match the native Windows architecture; no update was started.' }

    $stage = New-AgentUpdatePrivateStage -Request $normalized
    Assert-AgentUpdatePrivateStage -Path $stage

    $wrapperBytes = Get-AgentUpdateAssetBytes -Request $normalized -RelativeUrl '/agent/Update-Agent.ps1' -ExpectedLength $normalized.WrapperLength
    if ((Get-AgentUpdateSha256Hex -Bytes $wrapperBytes) -cne $normalized.WrapperSha256) { throw 'Downloaded wrapper does not match the server-pinned SHA-256.' }
    [void](Write-AgentUpdateStageAsset -Stage $stage -Name 'Update-Agent.ps1' -Bytes $wrapperBytes -ExpectedLength $normalized.WrapperLength -ExpectedSha256 $normalized.WrapperSha256)

    $helperBytes = Get-AgentUpdateAssetBytes -Request $normalized -RelativeUrl $normalized.HelperRelativeUrl -ExpectedLength $normalized.HelperLength
    if ((Get-AgentUpdateSha256Hex -Bytes $helperBytes) -cne $normalized.HelperSha256) { throw 'Downloaded maintenance helper does not match the server-pinned SHA-256.' }
    $helperPath = Write-AgentUpdateStageAsset -Stage $stage -Name 'AgentMaintenance.exe' -Bytes $helperBytes -ExpectedLength $normalized.HelperLength -ExpectedSha256 $normalized.HelperSha256
    $expectedMachine = if ($nativeRuntime -ceq 'win-x64') { 0x8664 } else { 0xAA64 }
    if ((Get-AgentMaintenancePeMachine -Path $helperPath) -ne $expectedMachine) { throw 'Server-pinned maintenance helper PE architecture does not match the native Windows architecture.' }

    Assert-AgentUpdatePrivateStage -Path $stage
    Assert-AgentUpdateFilePin -Path (Join-Path $stage 'Update-Agent.ps1') -ExpectedLength $normalized.WrapperLength -ExpectedSha256 $normalized.WrapperSha256
    Assert-AgentUpdateFilePin -Path $helperPath -ExpectedLength $normalized.HelperLength -ExpectedSha256 $normalized.HelperSha256

    $probe = Invoke-AgentMaintenanceProcess -Path $helperPath -Verb 'inspect'
    if (-not $probe -or $probe.ExitCode -ne 0) { throw 'Trusted maintenance helper capability is unavailable; the local inspection could not complete.' }
    try { $result = [string]$probe.StandardOutput | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'Trusted maintenance helper capability is unavailable; the read-only inspection result was invalid.' }
    if (-not $result -or $result.status -cne 'ok') { throw 'Trusted maintenance helper capability is unavailable; the read-only inspection was rejected.' }

    $workStore = Initialize-AgentUpdateWorkStore
    $workflowLock = $null
    try {
    $workflowLock = Enter-AgentUpdateWorkflowLock -WorkStore $workStore

    Write-AgentUpdateProtectedJson -Path $workStore.Request -Value (New-AgentUpdateLifecycleRequest -Request $normalized -Command 'start')
    $startStep = Invoke-AgentMaintenanceCommand -Path $helperPath -Verb 'start' -TimeoutSeconds 300
    if (-not $startStep.Result -or [string]$startStep.Result.status -cne 'ok') {
        $startCategory = Get-AgentMaintenanceResultCategory -Result $startStep.Result
        if ($startCategory -ceq 'controllerStartUnconfirmed') {
            [void](Invoke-AgentMaintenanceCommand -Path $helperPath -Verb 'reconcile' -TimeoutSeconds 600)
        }
        throw ("Trusted maintenance start did not complete: " + $startCategory)
    }

    Save-AgentUpdatePackage -Request $normalized -Destination $workStore.Archive

    Write-AgentUpdateProtectedJson -Path $workStore.Request -Value (New-AgentUpdateArchiveRequest -Request $normalized -Command 'extract')
    $staleExtractionRoot = Join-Path $workStore.Work 'target'
    if (Test-Path -LiteralPath $staleExtractionRoot -PathType Container) {
        Assert-AgentUpdateNoReparseAncestors -Path $staleExtractionRoot
        Assert-AgentUpdatePrivateAcl -Path $staleExtractionRoot -Directory
        Remove-Item -LiteralPath $staleExtractionRoot -Recurse -Force -ErrorAction Stop
    }
    Assert-AgentMaintenanceStep -Step (Invoke-AgentMaintenanceCommand -Path $helperPath -Verb 'extract' -TimeoutSeconds 900) -Name 'extract'

    Write-AgentUpdateProtectedJson -Path $workStore.Request -Value (New-AgentUpdateLifecycleRequest -Request $normalized -Command 'bind-source' -HelperBuildSha256 $normalized.HelperSha256)
    Assert-AgentMaintenanceStep -Step (Invoke-AgentMaintenanceCommand -Path $helperPath -Verb 'bind-source' -TimeoutSeconds 900) -Name 'source binding'

    Write-AgentUpdateProtectedJson -Path $workStore.Request -Value (New-AgentUpdateArchiveRequest -Request $normalized -Command 'apply')
    $applyStep = Invoke-AgentMaintenanceCommand -Path $helperPath -Verb 'apply' -TimeoutSeconds 1800

    Write-AgentUpdateProtectedJson -Path $workStore.Request -Value (New-AgentUpdateLifecycleRequest -Request $normalized -Command 'report')
    $reportStep = Invoke-AgentMaintenanceCommand -Path $helperPath -Verb 'report' -TimeoutSeconds 900
    if (-not $reportStep.Result -or [string]$reportStep.Result.status -cne 'ok') {
        throw ("Trusted maintenance outcome report did not complete: " + (Get-AgentMaintenanceResultCategory -Result $reportStep.Result))
    }
    if (-not $applyStep.Result -or [string]$applyStep.Result.status -cne 'ok') {
        $applyOutcome = [string]$applyStep.Result.applyOutcome
        throw ("Trusted maintenance apply did not complete: " + (Get-AgentMaintenanceResultCategory -Result $applyStep.Result) + "; reported outcome: " + $applyOutcome)
    }

    [pscustomobject]@{
        status = 'ok'
        operationId = $normalized.OperationId
        chainId = $normalized.ChainId
        generation = $normalized.Generation
        targetVersion = $normalized.TargetVersion
        applyOutcome = [string]$applyStep.Result.applyOutcome
    } | ConvertTo-Json -Compress | Write-Output
    }
    finally {
        if ($workflowLock) { $workflowLock.Dispose() }
    }
}

if ($DefineFunctionsOnly) { return }
Invoke-AgentUpdateMain -BoundParameters $PSBoundParameters
