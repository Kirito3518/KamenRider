$ErrorActionPreference = 'Continue'
$selfUrl = 'https://kamanriderplay.com/agent/Setup-TightVnc.ps1'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Elevation required; opening an elevated PowerShell window...' -ForegroundColor Yellow
    $command = "irm '$selfUrl' | iex"
    Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-Command', $command) | Out-Null
    return
}

$packageUrl = 'https://kamanriderplay.com/agent/AgentPackage.zip'
$shaUrl = 'https://kamanriderplay.com/agent/AgentPackage.sha256'
$dataDir = 'C:\ProgramData\KamenRaider\Agent'
$passwordPath = Join-Path $dataDir 'RemoteDesktop\vnc-password.txt'
$stamp = Get-Date -Format 'yyyyMMddTHHmmss'
$tempRoot = Join-Path $env:TEMP ("kr-vnc-setup-$stamp")
$zipPath = Join-Path $tempRoot 'AgentPackage.zip'
$shaPath = Join-Path $tempRoot 'AgentPackage.sha256'
$extractRoot = Join-Path $tempRoot 'extract'

Write-Host '=== KamenRaider VNC setup ===' -ForegroundColor Cyan
Write-Host 'Allowed VNC source: 192.168.1.100 (the Mother/Controller LAN address)'
Write-Host 'VNC target IP shown to the admin must be this machine LAN IP (currently 192.168.1.106).'

$existing = Get-Service -Name 'tvnserver' -ErrorAction SilentlyContinue
if ($existing -and -not (Test-Path -LiteralPath $passwordPath)) {
    Write-Host 'A TightVNC service already exists but it is not KamenRaider-managed.' -ForegroundColor Yellow
    Write-Host 'Uninstall the existing TightVNC from Apps & Features, then run this script again.' -ForegroundColor Yellow
}

New-Item -ItemType Directory -Force $tempRoot | Out-Null
Write-Host 'Downloading the Agent package (contains the reviewed TightVNC installer)...'
Invoke-WebRequest -Uri $packageUrl -OutFile $zipPath -UseBasicParsing -TimeoutSec 300
Invoke-WebRequest -Uri $shaUrl -OutFile $shaPath -UseBasicParsing -TimeoutSec 30
$expected = (Get-Content -LiteralPath $shaPath -Raw).Trim().ToUpperInvariant()
$actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToUpperInvariant()
Write-Host ("Package SHA256: " + $actual)
if ($expected -ne $actual) { throw 'Agent package hash mismatch; refusing to run the VNC setup.' }

Expand-Archive -LiteralPath $zipPath -DestinationPath $extractRoot -Force
$vncInstaller = Join-Path $extractRoot 'Install-TightVncServer.ps1'
if (-not (Test-Path -LiteralPath $vncInstaller)) { throw 'Install-TightVncServer.ps1 is missing from the package.' }

Write-Host 'Installing / repairing KamenRaider-managed TightVNC...'
& $vncInstaller -AllowedSourceAddress '192.168.1.100' -DataDirectory $dataDir -Confirm:$false

Write-Host ''
Write-Host '=== FINAL STATE ===' -ForegroundColor Cyan
Get-Service -Name 'tvnserver' -ErrorAction SilentlyContinue | Select-Object Name, Status, StartType | Format-Table -AutoSize
Get-NetTCPConnection -State Listen -LocalPort 5900 -ErrorAction SilentlyContinue | Select-Object LocalAddress, LocalPort, OwningProcess | Format-Table -AutoSize
Get-NetFirewallRule -DisplayName 'KamenRaider Admin VNC' -ErrorAction SilentlyContinue |
    Select-Object DisplayName, Enabled, Direction, Action, Profile, RemoteAddress | Format-List
if (Test-Path -LiteralPath $passwordPath) {
    Write-Host ('VNC password file: ' + $passwordPath)
    Write-Host ('VNC password (do not post publicly): ' + (Get-Content -LiteralPath $passwordPath -Raw).Trim()) -ForegroundColor Yellow
} else {
    Write-Host 'VNC password file was not created; send this output for diagnosis.' -ForegroundColor Red
}

Write-Host ''
Write-Host 'Next: in /admin/machines edit this machine and set the VNC target IP to its LAN IPv4 (192.168.1.106).' -ForegroundColor Green
Write-Host 'Then use /admin/pc-control -> open screen and enter the VNC password above.' -ForegroundColor Green
