#Requires -Version 5.1
<#
.SYNOPSIS
  Temporarily open the Sunshine web GUI (maintenance mode) on a child PC that is protected by the
  KamenRaider Stream Guard, then restore the guard when finished.

.DESCRIPTION
  The Stream Guard normally keeps SunshineService stopped/manual and blocks inbound Sunshine.
  For LAB/MAINTENANCE you sometimes need the Sunshine GUI itself. This script:
    * stops the KamenRaider Agent (so it does not enforce the lease/stream gate),
    * disables the "KamenRaider Stream Guard - Sunshine" firewall block rule,
    * starts SunshineService and opens https://localhost:47990,
    * optionally allows the GUI port from another PC (LocalSubnet or one client IP).

  Run with -Restore afterwards to remove the temporary rule, re-enable the guard block and start
  the Agent again.

  LAB / TEST ONLY. Do not leave this enabled on a machine that is rented out.

.PARAMETER AllowClientIp
  Allow inbound TCP 47990 from this one IPv4 address (for opening the GUI from another PC).

.PARAMETER AllowLan
  Allow inbound TCP 47990 from the local subnet (default when AllowClientIp is not given).

.PARAMETER NoBrowser
  Do not launch the local browser.

.PARAMETER Restore
  Undo maintenance mode and re-enable the guard/Agent.

.EXAMPLE
  # On the child PC (self-elevates)
  irm "https://kamanriderplay.com/agent/Enable-SunshineGui.ps1" | iex

.EXAMPLE
  # From another PC on the LAN, allow only that client IP
  & ([scriptblock]::Create((irm "https://kamanriderplay.com/agent/Enable-SunshineGui.ps1"))) -AllowClientIp 192.168.1.34

.EXAMPLE
  & ([scriptblock]::Create((irm "https://kamanriderplay.com/agent/Enable-SunshineGui.ps1"))) -Restore
#>
param(
    [string]$AllowClientIp = '',
    [switch]$AllowLan,
    [switch]$NoBrowser,
    [switch]$Restore,
    [string]$AgentService = 'KamenRaider Agent',
    [string]$SunshineService = 'SunshineService'
)

$scriptUrl = 'https://kamanriderplay.com/agent/Enable-SunshineGui.ps1'
$statePath = Join-Path $env:ProgramData 'KamenRaider\logs\sunshine-gui-maintenance.json'
$tempRuleName = 'KamenRaider Sunshine GUI (temporary)'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    Write-Host ''
    Write-Host 'Sunshine GUI maintenance needs Administrator rights. A new elevated PowerShell will open.' -ForegroundColor Yellow
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
        ("& ([scriptblock]::Create((irm '" + $scriptUrl + "'))) " + $(if ($Restore) { '-Restore' } else { '' }))
    )
    exit 0
}

$ErrorActionPreference = 'Stop'

function Get-LanIPv4 {
    $candidates = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -like '192.168.*' -or $_.IPAddress -like '10.*' -or $_.IPAddress -like '172.1[6-9].*' -or $_.IPAddress -like '172.2[0-9].*' -or $_.IPAddress -like '172.3[0-1].*' }
    $preferred = $candidates | Where-Object { $_.InterfaceAlias -notmatch 'Tailscale|Loopback|vEthernet' } | Select-Object -First 1
    if ($preferred) { return $preferred.IPAddress }
    if ($candidates) { return ($candidates | Select-Object -First 1).IPAddress }
    return $null
}

if ($Restore) {
    Write-Host ''
    Write-Host '=== Restore Stream Guard after Sunshine GUI maintenance ===' -ForegroundColor Cyan
    try { Stop-Service -Name $SunshineService -Force -ErrorAction SilentlyContinue } catch { }
    try { Remove-NetFirewallRule -DisplayName $tempRuleName -ErrorAction SilentlyContinue } catch { }

    $enabled = @()
    if (Test-Path -LiteralPath $statePath) {
        try { $enabled = @((Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json).BlockRules) } catch { }
    }
    $blockRules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match 'KamenRaider Stream Guard' })
    foreach ($rule in $blockRules) {
        try { Enable-NetFirewallRule -Name $rule.Name -ErrorAction Stop; Write-Host "Enabled: $($rule.DisplayName)" } catch { }
    }
    Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
    try { Start-Service -Name $AgentService -ErrorAction Stop; Write-Host "Started $AgentService" -ForegroundColor Green } catch { Write-Warning "Could not start ${AgentService}: $($_.Exception.Message)" }
    Get-Service $AgentService, 'KamenRaider Stream Guard', $SunshineService -ErrorAction SilentlyContinue | Format-Table Name, Status, StartType
    Write-Host 'Restored. The guard will stop/block Sunshine again as before.' -ForegroundColor Green
    exit 0
}

Write-Host ''
Write-Host '=== Enable Sunshine GUI (LAB maintenance mode) ===' -ForegroundColor Cyan

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $statePath) | Out-Null
$agentState = (Get-Service -Name $AgentService -ErrorAction SilentlyContinue).Status
$sunshineState = (Get-Service -Name $SunshineService -ErrorAction SilentlyContinue).Status
$blockRules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match 'KamenRaider Stream Guard' })
$blockState = @($blockRules | ForEach-Object { [pscustomobject]@{ Name = $_.Name; DisplayName = $_.DisplayName; Enabled = [string]$_.Enabled } })
$state = [ordered]@{
    AgentWasRunning    = ($agentState -eq 'Running')
    SunshineWasRunning = ($sunshineState -eq 'Running')
    BlockRules         = $blockState
}
ConvertTo-Json -InputObject $state -Depth 4 | Set-Content -LiteralPath $statePath -Encoding UTF8

Write-Host 'Stopping Agent...'
Stop-Service -Name $AgentService -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

Write-Host 'Disabling Stream Guard firewall block...'
foreach ($rule in $blockRules) {
    try { Disable-NetFirewallRule -Name $rule.Name -ErrorAction Stop; Write-Host "  disabled: $($rule.DisplayName)" } catch { Write-Warning "  $($rule.DisplayName): $($_.Exception.Message)" }
}

try { Remove-NetFirewallRule -DisplayName $tempRuleName -ErrorAction SilentlyContinue } catch { }
$remote = if ($AllowClientIp) { $AllowClientIp } else { 'LocalSubnet' }
$profile = 'Any'
try {
    New-NetFirewallRule -DisplayName $tempRuleName -Direction Inbound -Protocol TCP -LocalPort 47990 `
        -RemoteAddress $remote -Action Allow -Profile $profile | Out-Null
    Write-Host "Firewall allows TCP 47990 from $remote" -ForegroundColor Green
} catch { Write-Warning "Could not add GUI firewall rule: $($_.Exception.Message)" }

try {
    Start-Service -Name $SunshineService -ErrorAction Stop
} catch {
    Write-Warning "Could not start ${SunshineService}: $($_.Exception.Message)"
    Write-Warning 'If it fails because Guard is stopped, start the Stream Guard then rerun this script.'
}
Start-Sleep -Seconds 5
$svc = Get-Service -Name $SunshineService -ErrorAction SilentlyContinue
Write-Host "SunshineService: $($svc.Status)" -ForegroundColor $(if ($svc.Status -eq 'Running') { 'Green' } else { 'Yellow' })

$lanIp = Get-LanIPv4
Write-Host ''
Write-Host 'Sunshine GUI URLs:' -ForegroundColor Cyan
Write-Host '  local (on this PC): https://localhost:47990'
if ($lanIp) { Write-Host "  from another PC : https://${lanIp}:47990" }
Write-Host '  (use the Sunshine username/password set on this PC)'

if (-not $NoBrowser) {
    try { Start-Process -FilePath 'https://localhost:47990' } catch { }
}

Write-Host ''
Write-Host 'When finished, restore the guard:' -ForegroundColor Yellow
Write-Host ('  & ([scriptblock]::Create((irm "' + $scriptUrl + '"))) -Restore') -ForegroundColor Yellow
Write-Host 'LAB ONLY: the Agent is stopped and the guard block is disabled until -Restore.' -ForegroundColor Yellow
