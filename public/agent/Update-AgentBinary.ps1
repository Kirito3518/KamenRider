<#
.SYNOPSIS
  Update the installed KamenRaider Agent binaries from the published AgentPackage.zip
  without changing the machine identity, enrollment ticket or receipt.

.DESCRIPTION
  Existing installations cannot upgrade through the one-click installer (it only repairs the
  enrollment ticket), and the gated self-update path may be disabled. This script replaces the
  binaries under C:\Program Files\KamenRaider\Agent with the verified ZIP contents, keeps the
  protected data under C:\ProgramData\KamenRaider\Agent untouched, restarts both Windows services
  and rolls back automatically if the new Agent does not start.

  Run on the child PC (it self-elevates):
    irm "https://<web-origin>/agent/Update-AgentBinary.ps1" | iex

  Optional:
    & .\Update-AgentBinary.ps1 -BaseUrl https://kamanriderplay.com -Plan
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'https://kamanriderplay.com',
    [string]$ServiceName = 'KamenRaider Agent',
    [string]$GuardServiceName = 'KamenRaider Stream Guard',
    [string]$InstallDir = 'C:\Program Files\KamenRaider\Agent',
    [string]$DataDir = 'C:\ProgramData\KamenRaider\Agent',
    [switch]$Plan
)

$ErrorActionPreference = 'Stop'
$selfUrl = "$($BaseUrl.TrimEnd('/'))/agent/Update-AgentBinary.ps1"

function Write-Step([string]$Message) { Write-Host "== $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host "OK  $Message" -ForegroundColor Green }

try {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Host 'Administrator rights are required; opening an elevated PowerShell window...' -ForegroundColor Yellow
        $command = "irm '$selfUrl' | iex"
        Start-Process powershell.exe -Verb RunAs -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-Command', $command
        ) | Out-Null
        return
    }

    $base = $BaseUrl.TrimEnd('/')
    if ($base -notmatch '^https://') { throw 'BaseUrl must be an absolute HTTPS origin.' }
    $packageUrl = "$base/agent/AgentPackage.zip"
    $shaUrl = "$base/agent/AgentPackage.sha256"

    Write-Step 'Preflight'
    foreach ($name in @($ServiceName, $GuardServiceName)) {
        $service = Get-Service -Name $name -ErrorAction SilentlyContinue
        if (-not $service) {
            throw "Service '$name' is not installed. Run the normal one-click installer first."
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $InstallDir 'Agent.exe'))) {
        throw "Agent.exe was not found in '$InstallDir'. Run the normal one-click installer first."
    }
    if (-not (Test-Path -LiteralPath $DataDir)) {
        throw "The protected Agent data directory '$DataDir' is missing; refusing to update."
    }

    if ($Plan) {
        Write-Ok "Would download $packageUrl and update $InstallDir"
        Write-Ok "Would stop '$ServiceName' and '$GuardServiceName', then restart both"
        Write-Ok 'Machine identity, enrollment ticket and receipt stay untouched under the data directory'
        return
    }

    $stamp = Get-Date -Format 'yyyyMMddTHHmmss'
    $tempRoot = Join-Path $env:TEMP "kr-agent-binary-$stamp"
    $zipPath = Join-Path $tempRoot 'AgentPackage.zip'
    $shaPath = Join-Path $tempRoot 'AgentPackage.sha256'
    $extractRoot = Join-Path $tempRoot 'extract'
    $backupDir = Join-Path $DataDir "binary-backup-$stamp"
    New-Item -ItemType Directory -Force -Path $tempRoot, $extractRoot, $backupDir | Out-Null

    try {
        Write-Step 'Download and verify the Agent package'
        Invoke-WebRequest -Uri $packageUrl -OutFile $zipPath -UseBasicParsing -TimeoutSec 600
        Invoke-WebRequest -Uri $shaUrl -OutFile $shaPath -UseBasicParsing -TimeoutSec 60
        $expected = (Get-Content -LiteralPath $shaPath -Raw).Trim().ToUpperInvariant()
        $actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($expected -notmatch '^[0-9A-F]{64}$' -or $expected -ne $actual) {
            throw 'Downloaded Agent package hash mismatch; refusing to update.'
        }
        Write-Ok "SHA256 $actual"

        Expand-Archive -LiteralPath $zipPath -DestinationPath $extractRoot -Force
        $newAgentRoot = Join-Path $extractRoot 'Agent'
        if (-not (Test-Path -LiteralPath (Join-Path $newAgentRoot 'Agent.exe')) -or
            -not (Test-Path -LiteralPath (Join-Path $newAgentRoot 'Agent.dll'))) {
            throw 'The package does not contain the expected Agent binaries.'
        }

        Write-Step 'Back up the current installation'
        Copy-Item -LiteralPath (Join-Path $InstallDir '*') -Destination $backupDir -Recurse -Force
        Write-Ok "Backup: $backupDir"

        Write-Step 'Stop Agent services'
        $originalStartTypes = @{}
        foreach ($name in @($ServiceName, $GuardServiceName)) {
            $service = Get-Service -Name $name
            $originalStartTypes[$name] = $service.StartType
            if ($service.Status -ne 'Stopped') {
                Set-Service -Name $name -StartupType Manual
                Stop-Service -Name $name -Force
            }
        }
        foreach ($name in @($ServiceName, $GuardServiceName)) {
            for ($i = 0; $i -lt 30; $i++) {
                if ((Get-Service -Name $name).Status -eq 'Stopped') { break }
                Start-Sleep -Seconds 1
            }
            if ((Get-Service -Name $name).Status -ne 'Stopped') {
                throw "Service '$name' did not stop within 30 seconds."
            }
        }
        Get-Process -Name Agent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        Write-Ok 'Services stopped'

        Write-Step 'Install the new binaries'
        Get-ChildItem -LiteralPath $newAgentRoot -Force | ForEach-Object {
            # Keep the machine-local configuration that the package intentionally omits.
            if ($_.Name -ieq 'appsettings.json' -and (Test-Path -LiteralPath (Join-Path $InstallDir 'appsettings.json'))) {
                return
            }
            Copy-Item -LiteralPath $_.FullName -Destination $InstallDir -Recurse -Force
        }
        Write-Ok "Binaries updated in $InstallDir"

        Write-Step 'Start services'
        try {
            foreach ($name in @($GuardServiceName, $ServiceName)) {
                Set-Service -Name $name -StartupType $originalStartTypes[$name]
            }
            Start-Service -Name $GuardServiceName
            Start-Sleep -Seconds 2
            Start-Service -Name $ServiceName
            for ($i = 0; $i -lt 30; $i++) {
                if ((Get-Service -Name $ServiceName).Status -eq 'Running') { break }
                Start-Sleep -Seconds 2
            }
            if ((Get-Service -Name $ServiceName).Status -ne 'Running') {
                throw "Service '$ServiceName' did not start after the update."
            }
        } catch {
            Write-Host 'New binaries failed to start; rolling back...' -ForegroundColor Yellow
            Get-Process -Name Agent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            Copy-Item -LiteralPath (Join-Path $backupDir '*') -Destination $InstallDir -Recurse -Force
            foreach ($name in @($GuardServiceName, $ServiceName)) {
                Set-Service -Name $name -StartupType $originalStartTypes[$name]
                Start-Service -Name $name -ErrorAction SilentlyContinue
            }
            throw
        }

        Write-Host ''
        Write-Step 'Result'
        Get-Service -Name $ServiceName, $GuardServiceName |
            Select-Object Name, Status, StartType | Format-Table -AutoSize
        $versionFile = Join-Path $InstallDir 'Agent.dll'
        if (Test-Path -LiteralPath $versionFile) {
            $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($versionFile).FileVersion
            Write-Ok "Installed Agent.dll version: $version"
        }
        Write-Ok 'Enrollment identity and ticket were not modified. Wait ~30 seconds, then refresh /admin/machines.'
    } finally {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
} catch {
    Write-Host ''
    Write-Host ("FAILED: " + $_.Exception.Message) -ForegroundColor Red
    Write-Host 'Nothing was left half-installed: on any failure the previous binaries are restored.' -ForegroundColor Yellow
    exit 1
}
