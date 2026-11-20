#Requires -Version 5.1
<#
.SYNOPSIS
  Repair the KamenRaider Agent maintenance-fence file so the Agent can prepare a lease in the
  LAB test setup.

.DESCRIPTION
  The Agent treats a missing/unreadable maintenance-fence.json as "held" and refuses
  PrepareMachine. The installer normally creates:
    C:\ProgramData\KamenRaider.AgentMaintenance\maintenance-fence.json
  with a protected ACL (SYSTEM/Administrators full control, Agent service read+execute).
  If that file is missing (for example after a partial install/cleanup), this script recreates
  the directory, file, exact ACL and a valid clear fence document, then restarts the Agent.

  Run on the CHILD PC as Administrator (the script self-elevates via UAC when possible).
  LAB / TEST ONLY.

.EXAMPLE
  irm "https://kamanriderplay.com/agent/Fix-AgentMaintenanceFence.ps1" | iex
#>
param(
    [string]$ServiceName = 'KamenRaider Agent'
)

$scriptUrl = 'https://kamanriderplay.com/agent/Fix-AgentMaintenanceFence.ps1'
$directory = Join-Path $env:ProgramData 'KamenRaider.AgentMaintenance'
$marker = Join-Path $directory 'maintenance-fence.json'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    Write-Host ''
    Write-Host 'Fence repair needs Administrator rights. A new elevated PowerShell will open.' -ForegroundColor Yellow
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', ("irm '" + $scriptUrl + "' | iex")
    )
    exit 0
}

$ErrorActionPreference = 'Stop'
$systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$administratorsSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')

function Get-AgentServiceSid([string]$Name) {
    $sc = Join-Path $env:SystemRoot 'System32\sc.exe'
    $output = @(& $sc showsid $Name 2>&1)
    $match = [regex]::Match(($output -join "`n"), 'S-1-5-80(?:-\d+){5}')
    if ($match.Success) { return [Security.Principal.SecurityIdentifier]::new($match.Value) }
    $account = [Security.Principal.NTAccount]::new("NT SERVICE\$Name")
    return $account.Translate([Security.Principal.SecurityIdentifier])
}

function New-FenceSecurityDescriptor([Security.Principal.SecurityIdentifier]$AgentSid, [bool]$ForDirectory) {
    $acl = if ($ForDirectory) { [Security.AccessControl.DirectorySecurity]::new() } else { [Security.AccessControl.FileSecurity]::new() }
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($administratorsSid)
    $inheritance = if ($ForDirectory) {
        [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    } else {
        [Security.AccessControl.InheritanceFlags]::None
    }
    foreach ($sid in @($systemSid, $administratorsSid, $AgentSid)) {
        $rights = if ($sid.Value -eq $AgentSid.Value) {
            [Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [Security.AccessControl.FileSystemRights]::Synchronize
        } else {
            [Security.AccessControl.FileSystemRights]::FullControl
        }
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $sid, $rights, $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    return $acl
}

function Assert-FenceAcl([string]$Path, [bool]$ForDirectory) {
    $acl = if ($ForDirectory) { Get-Acl -LiteralPath $Path } else { Get-Acl -LiteralPath $Path }
    $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    if (-not $acl.AreAccessRulesProtected) { throw "ACL is not protected: $Path" }
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
    if ($owner.Value -ne $systemSid.Value -and $owner.Value -ne $administratorsSid.Value) { throw "Unexpected owner on $Path : $owner" }
    if ($rules.Count -ne 3) { throw "Expected 3 ACEs on $Path but found $($rules.Count)" }
}

Write-Host ''
Write-Host '=== Repair KamenRaider Agent maintenance fence ===' -ForegroundColor Cyan

$agentSid = Get-AgentServiceSid -Name $ServiceName
Write-Host "Agent service SID: $($agentSid.Value)"

New-Item -ItemType Directory -Force -Path $directory | Out-Null
Set-Acl -LiteralPath $directory -AclObject (New-FenceSecurityDescriptor -AgentSid $agentSid -ForDirectory $true)
Write-Host 'Directory ACL applied.'

$operationId = 'clear-' + [guid]::NewGuid().ToString('N')
$document = [ordered]@{
    schemaVersion   = 1
    state           = 'clear'
    operationId     = $operationId
    chainId         = $operationId
    headOperationId = $operationId
    generation      = 1
    phase           = 'controllerConfirmed'
}
$json = ConvertTo-Json -InputObject $document -Compress
[IO.File]::WriteAllText($marker, $json, [Text.UTF8Encoding]::new($false))
Set-Acl -LiteralPath $marker -AclObject (New-FenceSecurityDescriptor -AgentSid $agentSid -ForDirectory $false)
Write-Host 'Fence document written and ACL applied.'

Assert-FenceAcl -Path $directory -ForDirectory $true
Assert-FenceAcl -Path $marker -ForDirectory $false
Write-Host 'ACL verification passed.' -ForegroundColor Green
Write-Host "Marker: $marker"
Write-Host (Get-Content -LiteralPath $marker -Raw)

try {
    Restart-Service -Name $ServiceName -Force -ErrorAction Stop
    Start-Sleep -Seconds 5
    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    Write-Host "Service $($service.Name): $($service.Status)" -ForegroundColor Green
} catch {
    Write-Warning "Could not restart ${ServiceName}: $($_.Exception.Message)"
}

Write-Host ''
Write-Host 'DONE. The maintenance fence is clear; ask the customer to rent the machine again.' -ForegroundColor Green
Write-Host 'LAB ONLY: this bypasses the production fresh-install fence semantics.' -ForegroundColor Yellow
