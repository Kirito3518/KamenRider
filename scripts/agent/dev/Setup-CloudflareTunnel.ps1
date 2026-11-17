<#
.SYNOPSIS
  Set up a Cloudflare Tunnel for the Mother web app on the apex domain and point the app at
  https://<domain> (no subdomain), leaving the Agent transport on the LAN.

.DESCRIPTION
  Run on the Mother PC (192.168.1.100) from an ELEVATED PowerShell (the cloudflared service
  needs administrator rights). Steps:
    1. makes sure cloudflared is installed (winget, or downloads the Windows binary),
    2. installs/refreshes the cloudflared Windows service with the tunnel token,
    3. optionally stops the current web dev server on port 3000,
    4. runs Configure-MotherLan.ps1 -WebHttpsOrigin https://<Domain> so BETTER_AUTH_URL and the
       Agent install/package URLs use the public HTTPS origin, while
       AGENT_CONTROLLER_URL stays https://<LanIp>:7443,
    5. verifies https://<domain>/login and https://<domain>/agent/AgentPackage.zip,
    6. prints the remaining Cloudflare dashboard checklist (public hostname, SSL/TLS, WAF skip
       for /agent/*, Cloudflare Access for /admin*).

  The tunnel token is used only for `cloudflared service install`; it is never written to .env.local
  or printed in full (masked in output).

.PARAMETER Domain
  Apex domain already active on Cloudflare, e.g. kamanriderplay.com.

.PARAMETER TunnelToken
  Token from Cloudflare Dashboard -> Zero Trust -> Networks -> Tunnels -> <tunnel> -> install.
  Required unless the cloudflared service is already installed (-SkipServiceInstall).

.PARAMETER LanIp
  Mother LAN IPv4 used for the Agent 7443 channel (default 192.168.1.100).

.PARAMETER RestartWeb
  Stop whatever listens on port 3000 before reconfiguring, so the new BETTER_AUTH_URL is loaded.

.PARAMETER SkipServiceInstall
  Skip winget/cloudflared service install (tunnel service is already installed).

.PARAMETER SkipAppConfigure
  Only install/refresh the tunnel; do not touch .env.local or start the app.

.PARAMETER NoStart
  Passed to Configure-MotherLan.ps1 as no -Start (configure only).

.PARAMETER Plan
  Show what would be done without changing anything.

.EXAMPLE
  # ELEVATED · Mother
  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\dev\Setup-CloudflareTunnel.ps1 `
    -Domain kamanriderplay.com -TunnelToken '<TOKEN>' -RestartWeb
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9.-]+\.[A-Za-z]{2,}$')][string]$Domain,
    [string]$TunnelToken = '',
    [string]$LanIp = '192.168.1.100',
    [switch]$RestartWeb,
    [switch]$SkipServiceInstall,
    [switch]$SkipAppConfigure,
    [switch]$NoStart,
    [switch]$Plan
)

$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + $env:Path
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$configureScript = Join-Path $PSScriptRoot 'Configure-MotherLan.ps1'
$logDir = Join-Path $repo '.next-local\mother'
$logFile = Join-Path $logDir 'cloudflare-setup.log'
$origin = 'https://' + $Domain.Trim()
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Write-Step([string]$m) { Write-Host "[*] $m" }
function Write-Ok([string]$m) { Write-Host "[ok] $m" -ForegroundColor Green }
function Write-Warn2([string]$m) { Write-Host "[warn] $m" -ForegroundColor Yellow }
function Write-Fail([string]$m) { Write-Host "[fail] $m" -ForegroundColor Red }
function Mask-Token([string]$t) {
    if ([string]::IsNullOrEmpty($t)) { return '(none)' }
    if ($t.Length -le 12) { return '********' }
    return $t.Substring(0, 6) + '...' + $t.Substring($t.Length - 4) + " (len $($t.Length))"
}
function Invoke-Native([string]$Exe, [string[]]$Arguments, [string]$Label) {
    # PowerShell 5.1 turns native stderr into a terminating error under
    # $ErrorActionPreference = 'Stop'. cloudflared/winget write their progress/info logs to
    # stderr, so capture with 'Continue' and decide on $LASTEXITCODE instead.
    $savedEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Exe @Arguments 2>&1
        $exit = $LASTEXITCODE
    } finally { $ErrorActionPreference = $savedEap }
    if ($output) { $output | ForEach-Object { Write-Host "    $_" } }
    return $exit
}

function Test-PortListening([int]$Port) {
    return [bool]@(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue).Count
}

if (-not $Plan) {
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    try { Start-Transcript -LiteralPath $logFile -Append -Force | Out-Null } catch { }
}

Write-Host ''
Write-Host '=== Cloudflare Tunnel setup (apex domain) ===' -ForegroundColor Cyan
Write-Host "Repo:        $repo"
Write-Host "Public URL:  $origin"
Write-Host "Agent (LAN): https://${LanIp}:7443"
Write-Host "Token:       $(Mask-Token $TunnelToken)"
Write-Host ''

if (-not $SkipAppConfigure -and -not (Test-Path -LiteralPath $configureScript -PathType Leaf)) {
    Write-Fail "Configure-MotherLan.ps1 not found at $configureScript (git pull?)."
    exit 1
}

# --- 1. cloudflared ----------------------------------------------------------
Write-Step 'Checking cloudflared'
$cloudflared = (Get-Command cloudflared -ErrorAction SilentlyContinue).Source
if (-not $cloudflared) {
    $fallback = Join-Path $env:ProgramFiles 'cloudflared\cloudflared.exe'
    if (Test-Path -LiteralPath $fallback -PathType Leaf) {
        $cloudflared = $fallback
        $env:Path = (Split-Path -Parent $fallback) + ';' + $env:Path
    }
}
if (-not $cloudflared) {
    if ($Plan) {
        Write-Host '    would install cloudflared (winget install --id Cloudflare.cloudflared -e, or download the binary)'
    } else {
        Write-Step 'Installing cloudflared'
        $installed = $false
        if (Get-Command winget -ErrorAction SilentlyContinue) {
            try {
                $wingetExe = (Get-Command winget -ErrorAction SilentlyContinue).Source
                if ($wingetExe) {
                    $null = Invoke-Native $wingetExe @('install', '--id', 'Cloudflare.cloudflared', '-e', '--accept-source-agreements', '--accept-package-agreements', '--silent') 'winget'
                }
                $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + $env:Path
                $cloudflared = (Get-Command cloudflared -ErrorAction SilentlyContinue).Source
                $installed = [bool]$cloudflared
            } catch { }
        }
        if (-not $installed) {
            Write-Warn2 'winget install failed/unavailable; downloading cloudflared from GitHub releases'
            $dir = Join-Path $env:ProgramFiles 'cloudflared'
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $exe = Join-Path $dir 'cloudflared.exe'
            Invoke-WebRequest -Uri 'https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe' -OutFile $exe -UseBasicParsing
            $cloudflared = $exe
            $env:Path = $dir + ';' + $env:Path
        }
        if (-not $cloudflared) { Write-Fail 'cloudflared could not be installed.'; exit 1 }
    }
}
if ($cloudflared) { Write-Ok "cloudflared: $cloudflared" }

# --- 2. tunnel service -------------------------------------------------------
Write-Step 'Checking the cloudflared Windows service'
$service = Get-Service cloudflared -ErrorAction SilentlyContinue
if ($SkipServiceInstall) {
    if (-not $service) { Write-Warn2 '-SkipServiceInstall was used but the cloudflared service does not exist.' }
    else { Write-Ok 'cloudflared service exists (install step skipped)' }
} elseif ([string]::IsNullOrWhiteSpace($TunnelToken)) {
    Write-Fail 'TunnelToken is required to install the service (or use -SkipServiceInstall if it is already installed).'
    Write-Host 'Get it from Cloudflare Dashboard -> Zero Trust -> Networks -> Tunnels -> your tunnel -> Install.'
    exit 1
} elseif ($Plan) {
    Write-Host '    would run: cloudflared service install <token> (requires an elevated PowerShell)'
} elseif (-not $isAdmin) {
    Write-Fail 'cloudflared service install requires an ELEVATED PowerShell (Run as Administrator).'
    exit 1
} else {
    if ($Plan) {
        Write-Host "    would run: cloudflared service uninstall (if needed) ; cloudflared service install <token>"
    } else {
        if ($service) {
            Write-Step 'Removing the existing cloudflared service before reinstalling'
            try { $null = Invoke-Native $cloudflared @('service', 'uninstall') 'cloudflared service uninstall' } catch { Write-Warn2 ("service uninstall failed: " + $_.Exception.Message) }
            Start-Sleep -Seconds 2
        }
        Write-Step 'Installing the cloudflared service with the tunnel token'
        $serviceExit = Invoke-Native $cloudflared @('service', 'install', $TunnelToken) 'cloudflared service install'
        if ($serviceExit -ne 0) { Write-Fail "cloudflared service install failed (exit $serviceExit)."; exit 1 }
        Start-Service cloudflared -ErrorAction SilentlyContinue
        Set-Service cloudflared -StartupType Automatic -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3
        $service = Get-Service cloudflared -ErrorAction SilentlyContinue
        if ($service -and $service.Status -eq 'Running') { Write-Ok 'cloudflared service is Running/Automatic' }
        else { Write-Warn2 "cloudflared service state: $(if ($service) { $service.Status } else { 'missing' }) - check Cloudflare dashboard logs" }
    }
}

# --- 3. optional web restart (so BETTER_AUTH_URL takes effect) --------------
if ($RestartWeb) {
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort 3000 -ErrorAction SilentlyContinue)
    foreach ($listener in $listeners) {
        if ($Plan) { Write-Host "    would stop pid $($listener.OwningProcess) (port 3000)" ; continue }
        try { Stop-Process -Id $listener.OwningProcess -Force -ErrorAction Stop; Write-Ok "Stopped web pid $($listener.OwningProcess)" }
        catch { Write-Warn2 ("could not stop web pid " + $listener.OwningProcess + ": " + $_.Exception.Message) }
    }
}

# --- 4. app configuration ----------------------------------------------------
if (-not $SkipAppConfigure) {
    $configureArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $configureScript,
        '-LanIp', $LanIp, '-WebHttpsOrigin', $origin, '-Force')
    if (-not $NoStart) { $configureArgs += '-Start' }
    Write-Step ('Configuring the app for ' + $origin)
    if ($Plan) {
        Write-Host ('    would run: powershell ' + ($configureArgs -join ' '))
    } else {
        $configureExit = Invoke-Native 'powershell.exe' $configureArgs 'Configure-MotherLan.ps1'
        if ($configureExit -ne 0) { Write-Warn2 "Configure-MotherLan.ps1 returned exit code $configureExit; read the messages above." }
    }
}

# --- 5. verification ---------------------------------------------------------
if (-not $Plan -and -not $SkipAppConfigure) {
    Write-Step "Verifying $origin"
    $deadline = (Get-Date).AddSeconds(90)
    $loginOk = $false
    while ((Get-Date) -lt $deadline -and -not $loginOk) {
        try { $status = (Invoke-WebRequest -Uri "$origin/login" -UseBasicParsing -TimeoutSec 20).StatusCode; if ($status -eq 200) { $loginOk = $true } }
        catch { Start-Sleep -Seconds 3 }
    }
    if ($loginOk) { Write-Ok "$origin/login -> 200" }
    else { Write-Warn2 "$origin/login did not return 200 yet (DNS/tunnel/web may still be starting)." }

    try {
        $package = Invoke-WebRequest -Uri "$origin/agent/AgentPackage.zip" -Method Head -UseBasicParsing -TimeoutSec 30
        if ($package.Headers['Content-Type'] -match 'zip|octet') { Write-Ok "Package download reachable ($($package.StatusCode))" }
        else { Write-Warn2 "Package URL returned $($package.StatusCode) but Content-Type '$($package.Headers['Content-Type'])' - Cloudflare may be challenging the request (add the WAF skip rule)." }
    } catch { Write-Warn2 ("Package URL check failed: " + $_.Exception.Message + " - check tunnel/web and the WAF skip rule for /agent/*") }
}

# --- 6. dashboard checklist --------------------------------------------------
Write-Host ''
Write-Host '=== Cloudflare Dashboard checklist (one-time) ===' -ForegroundColor Cyan
Write-Host "1) Zero Trust -> Networks -> Tunnels -> your tunnel: status must be HEALTHY"
Write-Host "2) Public Hostname -> Add:  Subdomain = (leave empty / @)   Domain = $Domain"
Write-Host "   Service = HTTP  ->  127.0.0.1:3000"
Write-Host "3) SSL/TLS -> Overview: Full  +  Always Use HTTPS = On"
Write-Host "4) Security -> WAF -> Custom rules: Skip for expression (http.request.uri.path contains `"/agent/`")"
Write-Host "     skip: Browser Integrity Check, Security Level, Bot Fight Mode"
Write-Host "5) Recommended: Zero Trust -> Access -> Applications -> Self-hosted $Domain path admin* (email/OTP policy)"
Write-Host ''
Write-Host "After the dashboard steps: open https://$Domain/login and sign in again (origin changed)." -ForegroundColor Green
Write-Host "Agent PCs: import .next-local\mother\controller-root.cer into LocalMachine\Root, then run the install command from https://$Domain/admin/machines"
Write-Host "They must still reach https://${LanIp}:7443 on the LAN for the Controller channel."
Write-Host ''
Write-Host 'Security reminder: rotate the secrets in .env.local before real use.' -ForegroundColor Yellow

if (-not $Plan) { try { Stop-Transcript | Out-Null } catch { } }
