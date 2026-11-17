#Requires -Version 5.1
<#
.SYNOPSIS
  Repair the Agent's private Sunshine credential store ACL so the fleet Sunshine credential can be
  applied (lab setup).

.DESCRIPTION
  The Agent stores the fleet Sunshine credential at:
    C:\ProgramData\KamenRaider\Agent\sunshine\
  and validates a strict private ACL: protected, owner SYSTEM/Administrators, and FullControl for
  SYSTEM, Administrators and the KamenRaider Agent virtual service SID.

  A store created by an older/partial installer can end up with only "Modify" for the Agent
  service (or inherited rules), which makes the Agent log:
    "Sunshine credential applied revision is unavailable (UnauthorizedAccessException)"

  This script normalizes the folder ACL, removes stale store files, restarts the Agent and prints
  the relevant Agent event-log lines. Run on the CHILD PC as Administrator.
  LAB / TEST ONLY.

.EXAMPLE
  irm "https://kamanriderplay.com/agent/Fix-AgentSunshineStore.ps1" | iex
#>
param(
    [string]$ServiceName = 'KamenRaider Agent'
)

$scriptUrl = 'https://kamanriderplay.com/agent/Fix-AgentSunshineStore.ps1'
$dataDirectory = Join-Path $env:ProgramData 'KamenRaider\Agent'
$storeDirectory = Join-Path $dataDirectory 'sunshine'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    Write-Host ''
    Write-Host 'Repairing the Agent Sunshine store needs Administrator rights. A new elevated PowerShell will open.' -ForegroundColor Yellow
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
    return ([Security.Principal.NTAccount]::new("NT SERVICE\$Name")).Translate([Security.Principal.SecurityIdentifier])
}

function New-StoreAcl([Security.Principal.SecurityIdentifier]$AgentSid) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($administratorsSid)
    foreach ($sid in @($systemSid, $administratorsSid, $AgentSid)) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    return $acl
}

function Test-StoreAcl([string]$Path, [Security.Principal.SecurityIdentifier]$AgentSid) {
    $acl = Get-Acl -LiteralPath $Path
    $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    if (-not $acl.AreAccessRulesProtected) { throw 'ACL is not protected.' }
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
    if ($owner.Value -ne $administratorsSid.Value -and $owner.Value -ne $systemSid.Value) {
        throw "Unexpected owner: $($owner.Value)"
    }
    $trusted = @($systemSid.Value, $administratorsSid.Value, $AgentSid.Value)
    if ($rules.Count -ne 3) { throw "Expected 3 ACEs but found $($rules.Count)." }
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $rule.IdentityReference.Value -notin $trusted -or
            [int]$rule.FileSystemRights -ne [int][Security.AccessControl.FileSystemRights]::FullControl -or
            $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit' -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None) {
            throw 'Unexpected access rule.'
        }
    }
}

Write-Host ''
Write-Host '=== Repair Agent Sunshine credential store ===' -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $dataDirectory -PathType Container)) {
    throw "Agent data directory not found: $dataDirectory"
}

$agentSid = Get-AgentServiceSid -Name $ServiceName
Write-Host "Agent service SID: $($agentSid.Value)"

Write-Host 'Stopping Agent...'
Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
# Remove stale store files first (they will be recreated with the correct ACL by the Agent).
$storeExisted = Test-Path -LiteralPath $storeDirectory -PathType Container
if ($storeExisted) {
    Write-Host 'Removing stale store files...'
    & takeown.exe /f $storeDirectory /r /d y 2>&1 | Out-Null
    & icacls.exe $storeDirectory /grant 'Administrators:F' /t /c 2>&1 | Out-Null
    Get-ChildItem -LiteralPath $storeDirectory -Force -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}
if (-not (Test-Path -LiteralPath $storeDirectory -PathType Container)) {
    New-Item -ItemType Directory -Force -Path $storeDirectory | Out-Null
}

Set-Acl -LiteralPath $storeDirectory -AclObject (New-StoreAcl -AgentSid $agentSid)
Test-StoreAcl -Path $storeDirectory -AgentSid $agentSid
Write-Host 'Store directory ACL verified.' -ForegroundColor Green

Write-Host 'Starting Agent...'
Start-Service -Name $ServiceName
Start-Sleep -Seconds 15
$service = Get-Service -Name $ServiceName
Write-Host "Service $($service.Name): $($service.Status)" -ForegroundColor Green

Write-Host ''
Write-Host '=== Agent event log (latest 10) ===' -ForegroundColor Cyan
Get-WinEvent -ProviderName 'Agent' -MaxEvents 10 -ErrorAction SilentlyContinue |
    Select-Object TimeCreated, Id, LevelDisplayName, Message | Format-List

Write-Host ''
Write-Host 'DONE. Ask the admin to check appliedSunshineCredentialRevision (should reach the fleet revision).' -ForegroundColor Green
Write-Host 'LAB ONLY.' -ForegroundColor Yellow
