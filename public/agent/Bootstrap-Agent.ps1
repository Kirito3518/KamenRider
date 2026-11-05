[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ControllerUrl,
    [Parameter(Mandatory)][string]$PackageUrl,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$PackageSha256,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$TrustedRootThumbprint,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]{43}$')][string]$EnrollmentTicket,
    [Parameter(Mandatory)][string]$InstallReturnUrl,
    [Parameter(Mandatory)][string]$VncAllowedSourceAddress,
    [string]$PackageArchivePath,
    [switch]$Plan
)

$ErrorActionPreference = 'Stop'

function ConvertTo-AllowedVncSourceAddress {
    param([Parameter(Mandatory)][string]$Address)
    $parsed = $null
    if (-not [Net.IPAddress]::TryParse($Address, [ref]$parsed) -or $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'VncAllowedSourceAddress must be one private or loopback IPv4 address.'
    }
    $bytes = $parsed.GetAddressBytes()
    $private = $bytes[0] -eq 10 -or $bytes[0] -eq 127 -or
        ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
        ($bytes[0] -eq 192 -and $bytes[1] -eq 168)
    if (-not $private -or $parsed.ToString() -cne $Address) {
        throw 'VncAllowedSourceAddress must be one private or loopback IPv4 address.'
    }
    return $parsed.ToString()
}

function Get-BootstrapTrustedSids {
    @(
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'),
        [Security.Principal.SecurityIdentifier]::new('S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'),
        [Security.Principal.SecurityIdentifier]::new('S-1-3-0'),
        [Security.Principal.WindowsIdentity]::GetCurrent().User
    )
}

function Get-BootstrapServiceSid {
    param([Parameter(Mandatory)][string]$ServiceName)
    $scPath = Join-Path $env:SystemRoot 'System32\sc.exe'
    $output = @(& $scPath showsid $ServiceName 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Unable to derive the virtual service SID for $ServiceName." }
    $matches = [regex]::Matches(($output -join "`n"), 'S-1-5-80(?:-\d+){5}')
    if ($matches.Count -ne 1) { throw "Service SID output for $ServiceName did not contain exactly one service SID." }
    return [Security.Principal.SecurityIdentifier]::new($matches[0].Value)
}

function Test-BootstrapWritableAceRights {
    param([Parameter(Mandatory)][int]$Rights)
    $write = [Security.AccessControl.FileSystemRights]::WriteData -bor [Security.AccessControl.FileSystemRights]::AppendData -bor [Security.AccessControl.FileSystemRights]::WriteAttributes -bor [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    return (($Rights -band [int]$write) -ne 0 -or ($Rights -band [int]268435456) -ne 0 -or ($Rights -band [int]1073741824) -ne 0)
}

function Assert-BootstrapTrustedPath {
    param([Parameter(Mandatory)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.StartsWith('\\')) { throw 'Network installer paths are not supported.' }
    $trusted = @(Get-BootstrapTrustedSids | ForEach-Object { $_.Value })
    $current = $full
    while (-not (Test-Path -LiteralPath $current)) {
        $parent = [IO.Path]::GetDirectoryName($current)
        if (-not $parent -or $parent -eq $current) { throw 'No existing trusted installer parent.' }
        $current = $parent
    }
    if (-not (Get-Item -LiteralPath $current -Force).PSIsContainer) { $current = [IO.Path]::GetDirectoryName($current) }
    $systemRoot = [IO.Path]::GetPathRoot($full).TrimEnd([IO.Path]::DirectorySeparatorChar)
    while ($current) {
        $item = Get-Item -LiteralPath $current -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Bootstrap path has a reparse point or non-directory ancestor.' }
        $acl = Get-Acl -LiteralPath $current
        if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) { throw 'Bootstrap path has an untrusted owner.' }
        $currentFull = [IO.Path]::GetFullPath($current).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if ([string]::Equals($currentFull, $systemRoot, [StringComparison]::OrdinalIgnoreCase)) {
            $trustedInstallerSid = 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
            if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $trustedInstallerSid) { throw 'System drive root owner is not trusted.' }
            $authUsersSid = 'S-1-5-11'
            $rootRules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
            $createDirectory = @($rootRules | Where-Object { $_.IdentityReference.Value -eq $authUsersSid -and (($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::CreateDirectories) -ne 0) -and $_.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]::None -and $_.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::None -and -not $_.IsInherited })
            $childInheritance = @($rootRules | Where-Object { $_.IdentityReference.Value -eq $authUsersSid -and [int]$_.FileSystemRights -eq -536805376 -and $_.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit' -and $_.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::InheritOnly -and -not $_.IsInherited })
            if ($createDirectory.Count -ne 1 -or $childInheritance.Count -ne 1) { throw 'System drive root permissions are not the expected Windows defaults.' }
            foreach ($rule in $rootRules) {
                if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $rule.IdentityReference.Value -eq $authUsersSid -and (Test-BootstrapWritableAceRights ([int]$rule.FileSystemRights))) {
                    $rightsWithoutSync = ([int]$rule.FileSystemRights) -band (-bnot [int][Security.AccessControl.FileSystemRights]::Synchronize)
                    $isExpectedDirectoryCreate = $rightsWithoutSync -eq [int][Security.AccessControl.FileSystemRights]::CreateDirectories -and $rule.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]::None -and $rule.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::None -and -not $rule.IsInherited
                    $isExpectedChildInheritance = [int]$rule.FileSystemRights -eq -536805376 -and $rule.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit' -and $rule.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::InheritOnly -and -not $rule.IsInherited
                    if (-not $isExpectedDirectoryCreate -and -not $isExpectedChildInheritance) { throw 'System drive root has unexpected writable access.' }
                }
                if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $rule.IdentityReference.Value -notin $trusted -and $rule.IdentityReference.Value -ne $authUsersSid -and (Test-BootstrapWritableAceRights ([int]$rule.FileSystemRights))) { throw 'System drive root has unexpected writable access.' }
            }
        } else {
            foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
                if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $rule.IdentityReference.Value -notin $trusted -and (Test-BootstrapWritableAceRights ([int]$rule.FileSystemRights))) {
                    throw 'Bootstrap path has an untrusted writable ancestor.'
                }
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
}

function New-BootstrapPrivateDirectory {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$ServiceSid)
    $parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
    Assert-BootstrapTrustedPath $parent
    if (-not ('KamenRaider.Install.BootstrapDirectories' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace KamenRaider.Install {
 public static class BootstrapDirectories {
  [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
  [DllImport("kernel32.dll", EntryPoint="CreateDirectoryW", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectoryNative(string path, ref SecurityAttributes attributes);
  public static void CreateDirectoryWithSecurity(string path, byte[] descriptor) {
   IntPtr sd=Marshal.AllocHGlobal(descriptor.Length);
   try { Marshal.Copy(descriptor,0,sd,descriptor.Length); SecurityAttributes attributes=new SecurityAttributes { Length=Marshal.SizeOf(typeof(SecurityAttributes)), SecurityDescriptor=sd, InheritHandle=false }; if(!CreateDirectoryNative(path,ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
   finally { Marshal.FreeHGlobal(sd); }
  }
 }
}
'@
    }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $adminSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $installerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.SetOwner($installerSid)
    foreach ($sid in @($systemSid, $adminSid, $installerSid)) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit', [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    $serviceRule = [Security.AccessControl.FileSystemAccessRule]::new($ServiceSid, [Security.AccessControl.FileSystemRights]::ReadAndExecute, [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit', [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($serviceRule)
    [KamenRaider.Install.BootstrapDirectories]::CreateDirectoryWithSecurity([IO.Path]::GetFullPath($Path), $acl.GetSecurityDescriptorBinaryForm())
    Assert-BootstrapTrustedPath $Path
}

function Assert-AgentPackageHash {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$ExpectedSha256)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Agent package archive is missing.' }
    $file = Get-Item -LiteralPath $Path -Force
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Agent package archive must not be a reparse point.' }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $ExpectedSha256.ToUpperInvariant()) { throw 'Agent package hash mismatch.' }
    return $actual
}

try { $controller = [Uri]::new($ControllerUrl, [UriKind]::Absolute) }
catch { throw 'ControllerUrl must be an absolute HTTPS origin.' }
if ($controller.Scheme -ne 'https' -or $controller.UserInfo -or $controller.Query -or $controller.Fragment -or $controller.AbsolutePath -ne '/') {
    throw 'ControllerUrl must be an HTTPS origin without credentials, path, query or fragment.'
}

try { $package = [Uri]::new($PackageUrl, [UriKind]::Absolute) }
catch { throw 'PackageUrl must be an absolute HTTPS URL or loopback HTTP URL.' }
$loopbackPackageHttp = $package.Scheme -eq 'http' -and $package.IsLoopback
if ((($package.Scheme -ne 'https') -and -not $loopbackPackageHttp) -or $package.UserInfo -or $package.Query -or $package.Fragment) {
    throw 'PackageUrl must use HTTPS, or loopback HTTP for local development, without credentials, query or fragment.'
}

try { $returnUri = [Uri]::new($InstallReturnUrl, [UriKind]::Absolute) }
catch { throw 'InstallReturnUrl must be an absolute URL to /admin/machines.' }
$loopbackReturnHttp = $returnUri.Scheme -eq 'http' -and $returnUri.IsLoopback
if ((($returnUri.Scheme -ne 'https') -and -not $loopbackReturnHttp) -or $returnUri.UserInfo -or $returnUri.Query -or $returnUri.Fragment -or $returnUri.AbsolutePath -ne '/admin/machines') {
    throw 'InstallReturnUrl must use HTTPS, or loopback HTTP for local development, and point to /admin/machines without credentials, query or fragment.'
}

$normalizedController = $controller.GetLeftPart([UriPartial]::Authority) + '/'
$normalizedVncSourceAddress = ConvertTo-AllowedVncSourceAddress $VncAllowedSourceAddress
if ($Plan) {
    Assert-BootstrapTrustedPath $env:ProgramFiles
    $plannedStageParent = Join-Path $env:ProgramFiles 'KamenRaider'
    if (Test-Path -LiteralPath $plannedStageParent) { Assert-BootstrapTrustedPath $plannedStageParent }
    $verifiedHash = $null
    if ($PackageArchivePath) {
        if (-not [IO.Path]::IsPathRooted($PackageArchivePath)) { throw 'PackageArchivePath must be absolute.' }
        $verifiedHash = Assert-AgentPackageHash -Path $PackageArchivePath -ExpectedSha256 $PackageSha256
    }
    [pscustomobject]@{
        ControllerUrl = $normalizedController
        VncAllowedSourceAddress = $normalizedVncSourceAddress
        PackageUrl = $package.AbsoluteUri
        PackageSha256 = $PackageSha256.ToUpperInvariant()
        TrustedRootThumbprint = $TrustedRootThumbprint.ToUpperInvariant()
        EnrollmentTicketProvided = $true
        InstallReturnUrl = $returnUri.AbsoluteUri
        StageParent = $plannedStageParent
        PackageArchivePath = $PackageArchivePath
        VerifiedPackageSha256 = $verifiedHash
    }
    return
}

if ($env:OS -ne 'Windows_NT') { throw 'Windows is required.' }
if (-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Open PowerShell as Administrator and run the command again.'
}

$productDirectory = Join-Path $env:ProgramFiles 'KamenRaider'
$stage = Join-Path $productDirectory ('.agent-bootstrap-' + [Guid]::NewGuid().ToString('N'))
$serviceSid = Get-BootstrapServiceSid -ServiceName 'KamenRaider Agent'
Assert-BootstrapTrustedPath $env:ProgramFiles
if (Test-Path -LiteralPath $productDirectory) {
    Assert-BootstrapTrustedPath $productDirectory
} else {
    New-BootstrapPrivateDirectory $productDirectory $serviceSid
}
New-BootstrapPrivateDirectory $stage $serviceSid
try {
    $archive = Join-Path $stage 'agent.zip'
    Invoke-WebRequest -UseBasicParsing -Uri $package.AbsoluteUri -OutFile $archive -ErrorAction Stop
    $null = Assert-AgentPackageHash -Path $archive -ExpectedSha256 $PackageSha256

    $expanded = Join-Path $stage 'package'
    Expand-Archive -LiteralPath $archive -DestinationPath $expanded -ErrorAction Stop
    $installer = Join-Path $expanded 'Install-AgentPackage.ps1'
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Agent package has no qualified installer.' }
    $installerBlock = [ScriptBlock]::Create([IO.File]::ReadAllText($installer))
    & $installerBlock -PackageRoot $expanded -ControllerUrl $normalizedController `
        -TrustedRootThumbprint $TrustedRootThumbprint -EnrollmentTicket $EnrollmentTicket `
        -InstallReturnUrl $returnUri.AbsoluteUri -VncAllowedSourceAddress $normalizedVncSourceAddress
} finally {
    $resolved = [IO.Path]::GetFullPath($stage)
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($resolved), [IO.Path]::GetFullPath($productDirectory), [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike '.agent-bootstrap-*') {
        throw 'Unsafe installer cleanup path.'
    }
    Assert-BootstrapTrustedPath $productDirectory
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
