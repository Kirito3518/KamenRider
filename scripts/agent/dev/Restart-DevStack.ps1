<#
.SYNOPSIS
  Restart the local development stack (dev Controller + Next.js dev server) used on this workstation.

.DESCRIPTION
  Stops whatever currently listens on ports 3000 (web), 5081 (Controller loopback HTTP) and
  7443 (Controller Agent HTTPS), then starts:
    * scripts\agent\dev\Run-Controller-Local.ps1  (dev Controller, reads .env.local)
    * pnpm dev                                    (Next.js development server)
  Both are started in their own PowerShell windows so their logs stay visible.

  The dev Controller launcher is intentionally restricted to the configured localhost endpoints
  (http://127.0.0.1:5081 and https://localhost:7443) and Development environment. For the
  production-style Mother setup use C:\KamenRaider\controller.cmd instead
  (see docs\production-commands.md).

.PARAMETER SkipController
  Do not start the Controller (only restart the web server).

.PARAMETER SkipWeb
  Do not start the web server (only restart the Controller).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\agent\dev\Restart-DevStack.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipController,
    [switch]$SkipWeb
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$envFile = Join-Path $repo '.env.local'
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { throw '.env.local is missing.' }

Write-Host "Repo: $repo"

# Stop current listeners (web / Controller HTTP / Controller HTTPS).
foreach ($port in @(3000, 5081, 7443)) {
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
    foreach ($listener in $listeners) {
        $pidValue = $listener.OwningProcess
        if (-not $pidValue) { continue }
        try {
            Stop-Process -Id $pidValue -Force -ErrorAction Stop
            Write-Host ("Stopped pid {0} on port {1}" -f $pidValue, $port)
        } catch {
            Write-Warning ("Could not stop pid {0} on port {1}: {2}" -f $pidValue, $port, $_.Exception.Message)
        }
    }
}
Start-Sleep -Seconds 1

if (-not $SkipController) {
    $launcher = Join-Path $PSScriptRoot 'Run-Controller-Local.ps1'
    Start-Process -FilePath 'powershell.exe' `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $launcher) `
        -WorkingDirectory $repo
    Write-Host 'Starting dev Controller window...'
}

if (-not $SkipWeb) {
    Start-Process -FilePath 'powershell.exe' `
        -ArgumentList @('-NoProfile', '-Command', "Set-Location '$repo'; pnpm dev") `
        -WorkingDirectory $repo
    Write-Host 'Starting Next.js dev window...'
}

# Wait for ports and verify.
$deadline = (Get-Date).AddSeconds(120)
$controllerUp = $false
$webUp = $false
while ((Get-Date) -lt $deadline -and (-not $controllerUp -or -not $webUp)) {
    Start-Sleep -Milliseconds 750
    if (-not $controllerUp) {
        $controllerUp = [bool]@(Get-NetTCPConnection -State Listen -LocalPort 5081 -ErrorAction SilentlyContinue).Count
    }
    if (-not $webUp) {
        $webUp = [bool]@(Get-NetTCPConnection -State Listen -LocalPort 3000 -ErrorAction SilentlyContinue).Count
    }
}

if (-not $SkipController) {
    if (-not $controllerUp) {
        Write-Error 'Controller did not start listening on 127.0.0.1:5081. Check its window for the error.'
        exit 1
    }
    $credentialLine = Select-String -LiteralPath $envFile -Pattern '^CONTROLLER_SERVICE_CREDENTIAL=(.*)$'
    if (-not $credentialLine) { throw 'CONTROLLER_SERVICE_CREDENTIAL is missing from .env.local.' }
    $credential = $credentialLine.Matches[0].Groups[1].Value.Trim()
    try {
        $health = (Invoke-WebRequest -Uri 'http://127.0.0.1:5081/internal/health' `
            -Headers @{ 'X-Service-Credential' = $credential } -UseBasicParsing -TimeoutSec 10).Content
        Write-Host "Controller health: $health"
    } catch {
        Write-Warning ("Controller is listening but health check failed: " + $_.Exception.Message)
    }
    Write-Host 'Note: restarting the Controller on an existing database starts restore quarantine; sync will return 409 until that design is resolved.'
}

if (-not $SkipWeb) {
    if (-not $webUp) {
        Write-Error 'Web dev server did not start listening on port 3000. Check its window for the error.'
        exit 1
    }
    try {
        $status = (Invoke-WebRequest -Uri 'http://localhost:3000/' -UseBasicParsing -TimeoutSec 60).StatusCode
        Write-Host "Web: http://localhost:3000 -> $status"
    } catch {
        Write-Warning ("Web is listening but the first request failed: " + $_.Exception.Message)
    }
}

Write-Host 'Dev stack restart finished.'
