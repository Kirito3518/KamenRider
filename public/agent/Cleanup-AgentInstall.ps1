<#
.SYNOPSIS
  Clean up a failed or partial KamenRaider Agent installation on a child PC, then report any
  ACL problems that would make the bootstrap installer refuse to run.

.DESCRIPTION
  Run on a CHILD PC (the rental machine). The script:
    * stops and deletes the KamenRaider Agent / Stream Guard services,
    * removes the protected install directories (C:\Program Files\KamenRaider and
      C:\ProgramData\KamenRaider) including installer staging/evidence leftovers,
    * removes the KamenRaider firewall rules created by the installer,
    * prints a diagnostic of the ACL chain that the bootstrap installer validates, and
      reports the exact untrusted writable ACEs (with the icacls command to remove them).

  It never touches Sunshine (including SunshineService) or the Controller certificate store.
  Use it after a failed install, before running a fresh one-liner from /admin/machines.

.PARAMETER Plan
  Show what would be removed without changing anything.

.PARAMETER DiagnoseOnly
  Only run the ACL/path diagnostics.

.PARAMETER RepairAcl
  Remove the untrusted writable ACEs found on C:\Program Files or the product directory.
  Use only when the diagnostic reports a specific offender; the script prints the commands
  instead of changing the parent ACL unless this switch is given.

.EXAMPLE
  # One command from PowerShell (self-elevates via UAC)
  irm "https://kamanriderplay.com/agent/Cleanup-AgentInstall.ps1" | iex

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\Cleanup-AgentInstall.ps1 -Plan
#>
param(
    [switch]$Plan,
    [switch]$DiagnoseOnly,
    [switch]$RepairAcl
)

$scriptUrl = 'https://kamanriderplay.com/agent/Cleanup-AgentInstall.ps1'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    Write-Host ''
    Write-Host 'KamenRaider cleanup needs Administrator rights.' -ForegroundColor Yellow
    Write-Host 'A new elevated PowerShell window will open - accept the Windows prompt.' -ForegroundColor Yellow
    $command = "irm '$scriptUrl' | iex"
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $command)
    exit 0
}

$trustedSids = @(
    'S-1-5-18',                                                                 # SYSTEM
    'S-1-5-32-544',                                                             # Administrators
    'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464',           # TrustedInstaller
    'S-1-3-0'                                                                   # CREATOR OWNER
)

function Test-WritableAceRights {
    param([int]$Rights)
    $write = [Security.AccessControl.FileSystemRights]::WriteData -bor
        [Security.AccessControl.FileSystemRights]::AppendData -bor
        [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    return (($Rights -band [int]$write) -ne 0 -or ($Rights -band [int]268435456) -ne 0 -or ($Rights -band [int]1073741824) -ne 0)
}

function Get-AclOffenders {
    param([Parameter(Mandatory)][string]$Path)
    $offenders = [Collections.Generic.List[object]]::new()
    $acl = Get-Acl -LiteralPath $Path
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { continue }
        if ($rule.IdentityReference.Value -in $trustedSids) { continue }
        if (Test-WritableAceRights ([int]$rule.FileSystemRights)) {
            $identityName = $rule.IdentityReference.Value
            try { $identityName = $rule.IdentityReference.Translate([Security.Principal.NTAccount]).Value } catch { }
            $offenders.Add([pscustomobject]@{
                Path = $Path
                Identity = $rule.IdentityReference.Value
                IdentityName = $identityName
                Rights = $rule.FileSystemRights.ToString()
                Inherited = $rule.IsInherited
            })
        }
    }
    [pscustomobject]@{ Path = $Path; Owner = $owner; Offenders = $offenders.ToArray() }
}

function Show-PathDiagnostics {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host "[skip] $Path (does not exist)" -ForegroundColor DarkGray
        return $true
    }
    $result = Get-AclOffenders -Path $Path
    if ($result.Owner -notin $trustedSids) {
        Write-Host ("[warn] {0}: owner {1} is not trusted by the bootstrap installer" -f $Path, $result.Owner) -ForegroundColor Yellow
        Write-Host ("       fix: icacls `"{0}`" /setowner `"NT SERVICE\TrustedInstaller`"" -f $Path) -ForegroundColor Yellow
    }
    if ($result.Offenders.Count -eq 0) {
        Write-Host "[ok]   $Path : no untrusted writable ACEs" -ForegroundColor Green
        return $true
    }
    Write-Host ("[fail] {0}: untrusted writable ACE(s)" -f $Path) -ForegroundColor Red
    foreach ($offender in $result.Offenders) {
        Write-Host ("       {0} ({1}) : {2} (inherited={3})" -f $offender.Identity, $offender.IdentityName, $offender.Rights, $offender.Inherited) -ForegroundColor Red
        Write-Host ("       fix: icacls `"{0}`" /remove:g `"{1}`"" -f $offender.Path, $offender.Identity) -ForegroundColor Yellow
    }
    return $false
}

function Invoke-NativeText {
    param([string]$Exe, [string[]]$Arguments)
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Exe @Arguments 2>&1
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $saved
    }
    $output | ForEach-Object { Write-Host "       $_" }
    return $code
}

function Remove-ProtectedDirectory {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    if ($Plan) {
        Write-Host "[plan] would remove $Path"
        return $true
    }
    try {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
        return -not (Test-Path -LiteralPath $Path)
    } catch {
        Write-Host "       normal delete failed: $($_.Exception.Message)" -ForegroundColor DarkGray
    }
    Write-Host "       taking ownership and retrying..." -ForegroundColor DarkGray
    $null = Invoke-NativeText 'takeown.exe' @('/f', $Path, '/r', '/d', 'y')
    $null = Invoke-NativeText 'icacls.exe' @($Path, '/grant', 'Administrators:F', '/t', '/c')
    try {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
    } catch {
        Write-Host "       delete still failed: $($_.Exception.Message)" -ForegroundColor Red
    }
    return -not (Test-Path -LiteralPath $Path)
}

Write-Host ''
Write-Host '=== KamenRaider Agent cleanup (child PC) ===' -ForegroundColor Cyan
Write-Host "Computer: $env:COMPUTERNAME"
Write-Host "Mode    : $(if ($Plan) { 'Plan (no changes)' } elseif ($DiagnoseOnly) { 'Diagnose only' } else { 'Cleanup' })"
Write-Host ''

$productDirectory = Join-Path $env:ProgramFiles 'KamenRaider'
$dataDirectory = Join-Path $env:ProgramData 'KamenRaider'

if (-not $DiagnoseOnly) {
    Write-Host '[*] Removing KamenRaider services'
    $services = @(Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^KamenRaider' })
    if ($services.Count -eq 0) {
        Write-Host '    no KamenRaider services found'
    }
    foreach ($service in $services) {
        Write-Host "    $($service.Name) [$($service.Status)]"
        if (-not $Plan) {
            try { Stop-Service -Name $service.Name -Force -ErrorAction Stop } catch { }
            $null = Invoke-NativeText 'sc.exe' @('delete', $service.Name)
        }
    }

    Write-Host '[*] Removing install directories'
    foreach ($directory in @($productDirectory, $dataDirectory)) {
        $removed = Remove-ProtectedDirectory -Path $directory
        if ($removed) { Write-Host "    removed/absent: $directory" -ForegroundColor Green }
        else { Write-Host "    STILL PRESENT: $directory" -ForegroundColor Red }
    }

    Write-Host '[*] Removing KamenRaider firewall rules'
    $rules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match 'KamenRaider' })
    if ($rules.Count -eq 0) { Write-Host '    no KamenRaider firewall rules found' }
    foreach ($rule in $rules) {
        Write-Host "    $($rule.DisplayName)"
        if (-not $Plan) {
            try { Remove-NetFirewallRule -Name $rule.Name -ErrorAction Stop } catch { Write-Host "       remove failed: $($_.Exception.Message)" -ForegroundColor DarkGray }
        }
    }
}

Write-Host ''
Write-Host '=== ACL diagnostics (bootstrap installer trust check) ===' -ForegroundColor Cyan
$problemFound = $false
foreach ($path in @($env:ProgramFiles, $productDirectory, $dataDirectory)) {
    if (-not (Show-PathDiagnostics -Path $path)) { $problemFound = $true }
}

if ($RepairAcl -and -not $Plan -and -not $DiagnoseOnly) {
    Write-Host ''
    Write-Host '[*] Repairing untrusted writable ACEs on the product directory'
    if (Test-Path -LiteralPath $productDirectory) {
        foreach ($offender in (Get-AclOffenders -Path $productDirectory).Offenders) {
            $null = Invoke-NativeText 'icacls.exe' @($offender.Path, '/remove:g', $offender.Identity)
        }
    }
    Write-Host '[ok] repair attempted; rerun the diagnostics to verify'
}

Write-Host ''
if ($problemFound) {
    Write-Host 'RESULT: cleanup finished but an ACL problem remains.' -ForegroundColor Yellow
    if (-not $RepairAcl) {
        Write-Host 'Run the "fix:" icacls command(s) above, then run this cleanup once more.' -ForegroundColor Yellow
        Write-Host 'If the problem is on C:\Program Files and you are unsure, send the printed lines to the operator.' -ForegroundColor Yellow
    }
    Write-Host 'After the ACL problem is cleared, request a fresh one-liner from /admin/machines and run it.' -ForegroundColor Yellow
} else {
    Write-Host 'RESULT: leftovers removed and ACLs pass the installer trust check.' -ForegroundColor Green
    Write-Host 'Next: get a fresh one-liner from /admin/machines and run it on this PC.' -ForegroundColor Green
}
Write-Host ''
if ($Plan) { Write-Host 'Nothing was changed (Plan mode).' -ForegroundColor DarkGray }
