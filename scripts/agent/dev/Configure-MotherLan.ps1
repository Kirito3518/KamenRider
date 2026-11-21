<#
.SYNOPSIS
  Configure this clone as a LAN "Mother" (Controller + web) reachable at a fixed LAN IP.

.DESCRIPTION
  Run on the Mother PC (repo can live anywhere, e.g. Desktop\KamenRider). It:
    * rewrites the committed .env.local absolute paths to this clone,
    * creates a protected data-directory ACL for the Controller database,
    * generates a self-signed Controller TLS certificate for the LAN IP
      (PFX for Kestrel + CER to install on Child PCs),
    * updates .env.local (BETTER_AUTH_URL, Controller/Agent URLs, VNC source,
      Wake-on-LAN interface, package URLs, trust thumbprint),
    * writes a ready-to-run Controller launcher under .next-local\mother\,
    * tries to add the LAN firewall rules (needs an elevated shell; otherwise prints them),
    * optionally starts the Controller and web and verifies them.

  The web origin defaults to plain HTTP (http://<ip>:<WebPort>) so the admin UI is
  reachable on the LAN immediately. Cross-PC Agent installation requires the web to be
  HTTPS: run again with -HttpsWeb (or -WebHttpsOrigin https://host) after putting a TLS
  reverse proxy (Caddy) in front of the web, as described in docs/production-commands.md.

  This script never deletes data. .env.local is backed up under .next-local\mother\.

.PARAMETER LanIp
  The Mother LAN IPv4 address, e.g. 192.168.1.100.

.PARAMETER WebPort
  Web dev/production port (default 3000).

.PARAMETER AgentPort
  Controller Agent HTTPS port (default 7443).

.PARAMETER HttpsWeb
  Write https://<LanIp> as the browser/install origin (requires a TLS reverse proxy on 443).

.PARAMETER WebHttpsOrigin
  Explicit HTTPS origin for the web (overrides -HttpsWeb), e.g. https://kamen.example.com.

.PARAMETER Start
  Start the Controller (dotnet run) and `pnpm dev`, then verify them.

.PARAMETER Force
  Allow writing while ports 5081/7443/<WebPort> are already listening.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\dev\Configure-MotherLan.ps1 -LanIp 192.168.1.100 -Start
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d{1,3}(\.\d{1,3}){3}$')][string]$LanIp,
    [ValidateRange(1, 65535)][int]$WebPort = 3000,
    [ValidateRange(1, 65535)][int]$AgentPort = 7443,
    [switch]$HttpsWeb,
    [string]$WebHttpsOrigin = '',
    [switch]$Start,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
# Pick up PATH changes made after this PowerShell session started (e.g. a just-installed SDK).
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User') + ';' + $env:Path
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$legacyRepo = 'C:\Users\User\Documents\kamenraiderplay'
$envFile = Join-Path $repo '.env.local'
$dataDir = Join-Path $repo 'data'
# Keep runtime files out of .next-local: that directory is Next's build output (distDir) and is
# deleted/replaced on every "next build".
$motherDir = Join-Path $env:ProgramData 'KamenRaider\mother'
try { New-Item -ItemType Directory -Force -Path $motherDir -ErrorAction Stop | Out-Null }
catch { $motherDir = Join-Path $env:LOCALAPPDATA 'KamenRaider\mother' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

function Write-Step([string]$m) { Write-Host "[*] $m" }
function Write-Ok([string]$m) { Write-Host "[ok] $m" -ForegroundColor Green }
function Write-Warn2([string]$m) { Write-Host "[warn] $m" -ForegroundColor Yellow }
function Write-Fail([string]$m) { Write-Host "[fail] $m" -ForegroundColor Red }

function Test-PrivateIpv4([string]$ip) {
    $p = $ip.Split('.') | ForEach-Object { [int]$_ }
    if ($p.Count -ne 4 -or $p | Where-Object { $_ -lt 0 -or $_ -gt 255 }) { return $false }
    return ($p[0] -eq 10) -or ($p[0] -eq 172 -and $p[1] -ge 16 -and $p[1] -le 31) -or ($p[0] -eq 192 -and $p[1] -eq 168)
}

function Test-PortListening([int]$Port) {
    return [bool]@(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue).Count
}

function Get-EnvValue([string]$Name) {
    $match = Select-String -LiteralPath $envFile -Pattern ("^" + [regex]::Escape($Name) + "=(.*)$")
    if (-not $match) { return $null }
    return $match.Matches[0].Groups[1].Value.Trim()
}

function Set-EnvValues([hashtable]$Values) {
    $lines = Get-Content -LiteralPath $envFile
    $updated = foreach ($line in $lines) {
        $key = ($line -split '=', 2)[0]
        if ($Values.ContainsKey($key)) { "$key=$($Values[$key])" } else { $line }
    }
    Set-Content -LiteralPath $envFile -Value $updated -Encoding ASCII
}

Write-Host ''
Write-Host '=== KamenRaider Mother (LAN) configuration ===' -ForegroundColor Cyan
Write-Host "Repo:  $repo"
Write-Host "LAN IP: $LanIp  (web :$WebPort, Agent HTTPS :$AgentPort)"
Write-Host ''

# --- 1. Basic checks ---------------------------------------------------------
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { Write-Fail '.env.local is missing; clone the repository first.'; exit 1 }
if (-not (Test-Path -LiteralPath (Join-Path $repo 'public\agent\AgentPackage.zip') -PathType Leaf)) {
    Write-Warn2 'public\agent\AgentPackage.zip is missing; build it with scripts\agent\Build-AgentPackage.ps1 before installing Agents.'
}
if (-not (Test-PrivateIpv4 $LanIp)) { Write-Fail "$LanIp is not a private LAN IPv4 address."; exit 1 }

$localIps = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notmatch '^(127\.|169\.254\.)' } | ForEach-Object { $_.IPAddress })
if ($localIps -notcontains $LanIp) {
    Write-Warn2 "$LanIp is not assigned to this machine right now (found: $($localIps -join ', '))."
    Write-Warn2 'Set the static IP (or DHCP reservation) on the Mother NIC before relying on LAN access.'
}

$listening = @(foreach ($port in @(5081, 7443, $WebPort)) { if (Test-PortListening $port) { $port } })
if ($listening.Count -gt 0 -and -not $Force) {
    Write-Fail ("Ports already in use: " + ($listening -join ', ') + '. Stop the running stack first (or use -Force).')
    exit 1
}

# --- 2. Rewrite committed paths in .env.local ---------------------------------
$envLines = Get-Content -LiteralPath $envFile
$changed = 0
$updatedLines = foreach ($line in $envLines) {
    $newLine = $line
    if ($line.IndexOf($legacyRepo, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $newLine = $line.Replace($legacyRepo, $repo) }
    if ($newLine -match '^CONTROLLER_DATABASE_PATH=(.*)$' -and -not [IO.Path]::IsPathRooted($Matches[1].Trim())) {
        $newLine = 'CONTROLLER_DATABASE_PATH=' + (Join-Path $dataDir 'controller.sqlite')
    }
    if ($newLine -cne $line) { $changed++ }
    $newLine
}
if ($changed -gt 0) {
    New-Item -ItemType Directory -Force -Path $motherDir | Out-Null
    Copy-Item -LiteralPath $envFile -Destination (Join-Path $motherDir "env-local-$stamp.bak") -Force
    Set-Content -LiteralPath $envFile -Value $updatedLines -Encoding ASCII
    Write-Ok "Updated $changed .env.local line(s) to this clone path"
} else {
    Write-Ok '.env.local paths already point at this clone'
}

# --- 3. Protected data directory --------------------------------------------
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
$user = "$env:USERDOMAIN\$env:USERNAME"
& icacls.exe $dataDir '/inheritance:r' '/grant:r' 'SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F' "${user}:(OI)(CI)F" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Fail "icacls failed for $dataDir"; exit 1 }
Write-Ok 'data directory DACL is protected'

if (Test-Path -LiteralPath (Join-Path $dataDir 'controller.sqlite')) {
    Write-Warn2 'A Controller database already exists in data\ (the committed copy starts in restore quarantine).'
    Write-Warn2 'For a clean, non-quarantined test: stop the stack, run Bootstrap-DevClone.ps1 once, then run this script with -Force.'
}

# --- 4. Controller TLS certificate for the LAN IP ---------------------------
$certPfx = Join-Path $motherDir 'controller.pfx'
$certCer = Join-Path $motherDir 'controller-root.cer'
$certMeta = Join-Path $motherDir 'cert.json'
New-Item -ItemType Directory -Force -Path $motherDir | Out-Null

$thumbprint = $null
$pfxPassword = $null
if ((Test-Path -LiteralPath $certPfx) -and (Test-Path -LiteralPath $certMeta)) {
    $meta = Get-Content -LiteralPath $certMeta -Raw | ConvertFrom-Json
    $pfxPassword = $meta.Password
    $thumbprint = $meta.Thumbprint
    Write-Ok "Reusing Controller certificate $thumbprint (delete $motherDir\cert.json to regenerate)"
} else {
    Write-Step "Creating a Controller TLS certificate for $LanIp"
    $pfxPassword = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 32 | ForEach-Object { [char]$_ })
    $san = "2.5.29.17={text}IPAddress=$LanIp"
    $cert = New-SelfSignedCertificate `
        -Type Custom -Subject "CN=$LanIp" `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
        -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(3) `
        -TextExtension @($san, '2.5.29.37={text}1.3.6.1.5.5.7.3.1')
    $thumbprint = $cert.Thumbprint
    Export-PfxCertificate -Cert $cert -FilePath $certPfx -Password (ConvertTo-SecureString $pfxPassword -AsPlainText -Force) | Out-Null
    Export-Certificate -Cert $cert -FilePath $certCer | Out-Null
    @{ Thumbprint = $thumbprint; Password = $pfxPassword; LanIp = $LanIp; Created = (Get-Date).ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath $certMeta -Encoding ASCII
    Write-Ok "Created certificate $thumbprint"
    Write-Ok "Child PCs must trust: $certCer (import into LocalMachine\Root)"
}

# --- 5. Update .env.local for the LAN role ---------------------------------
$webOrigin = if (-not [string]::IsNullOrWhiteSpace($WebHttpsOrigin)) {
    $WebHttpsOrigin.TrimEnd('/')
} elseif ($HttpsWeb) {
    "https://$LanIp"
} else {
    "http://${LanIp}:$WebPort"
}
if ($webOrigin -notmatch '^https?://[^/]+$') { Write-Fail "Invalid web origin: $webOrigin"; exit 1 }

$wakeBroadcast = ($LanIp.Split('.')[0..2] -join '.') + '.255'
$values = @{
    'BETTER_AUTH_URL'                     = $webOrigin
    'CONTROLLER_INTERNAL_URL'             = 'http://127.0.0.1:5081'
    'CONTROLLER_AGENT_URL'                = "https://${LanIp}:$AgentPort"
    'AGENT_CONTROLLER_URL'                = "https://${LanIp}:$AgentPort"
    'AGENT_INSTALL_BASE_URL'              = $webOrigin
    'AGENT_PACKAGE_URL'                   = "$webOrigin/agent/AgentPackage.zip"
    'AGENT_TRUST_ROOT_THUMBPRINT'         = $thumbprint
    'AGENT_VNC_ALLOWED_SOURCE_IP'         = $LanIp
    'CONTROLLER_WAKE_INTERFACE_ADDRESS'   = $LanIp
    'CONTROLLER_WAKE_BROADCAST_ADDRESS'   = $wakeBroadcast
}
Set-EnvValues $values
Write-Ok ".env.local updated (web origin: $webOrigin)"
if ($webOrigin -like 'http://*') {
    Write-Warn2 'The web is HTTP: the admin UI works on the LAN, but the Agent install command needs HTTPS.'
    Write-Warn2 'Before installing Agents on other PCs, put a TLS reverse proxy in front and rerun with -HttpsWeb.'
}

# --- 6. Write the Mother Controller launcher -------------------------------
$dbPath = (Get-EnvValue 'CONTROLLER_DATABASE_PATH')
$serviceCredential = (Get-EnvValue 'CONTROLLER_SERVICE_CREDENTIAL')
$sunshineKey = (Get-EnvValue 'CONTROLLER_SUNSHINE_CREDENTIAL_KEY')
$publicIpEndpoint = (Get-EnvValue 'CONTROLLER_PUBLIC_IP_ENDPOINT')
if (-not $dbPath) { Write-Fail 'CONTROLLER_DATABASE_PATH is missing from .env.local'; exit 1 }

$launcherPath = Join-Path $motherDir 'Start-MotherController.ps1'
$webCmdPath = Join-Path $motherDir 'web-dev.cmd'
@"
# Generated by Configure-MotherLan.ps1 — starts the Controller in Production mode for $LanIp.
`$ErrorActionPreference = 'Stop'
Set-Location '$repo'
`$env:InternalUrl = 'http://127.0.0.1:5081'
`$env:AgentUrl = 'https://${LanIp}:$AgentPort'
`$env:DatabasePath = '$dbPath'
`$env:ServiceCredential = '$serviceCredential'
`$env:SunshineCredentialKey = '$sunshineKey'
`$env:PublicIpEndpoint = '$publicIpEndpoint'
`$env:WakeInterfaceAddress = '$LanIp'
`$env:WakeBroadcastAddress = '$(($LanIp -replace '\.[0-9]+$','') + '.255')'
`$env:Kestrel__Certificates__Default__Path = '$certPfx'
`$env:Kestrel__Certificates__Default__Password = '$pfxPassword'
`$env:DOTNET_ENVIRONMENT = 'Production'
`$env:ASPNETCORE_ENVIRONMENT = 'Production'
dotnet run --no-launch-profile --project '.\services\Controller\Controller.csproj'
"@ | Set-Content -LiteralPath $launcherPath -Encoding ASCII
Write-Ok "Wrote Controller launcher: $launcherPath"

# Batch launcher for the web dev server that keeps its output in a log file, so a failed start
# (missing module, port in use, etc.) is diagnosable even if the console window closes.
$webLog = Join-Path $motherDir 'web-dev.log'
@"
@echo off
cd /d "$repo"
pnpm dev > "$webLog" 2>&1
"@ | Set-Content -LiteralPath $webCmdPath -Encoding ASCII
Write-Ok "Wrote web launcher: $webCmdPath"

# --- 7. Firewall (best effort) ---------------------------------------------
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($elevated) {
    foreach ($rule in @(
        @{ Name = 'KamenRaider Controller'; Port = $AgentPort },
        @{ Name = 'KamenRaider Web'; Port = $WebPort }
    )) {
        try {
            if (-not (Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue)) {
                New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Protocol TCP -LocalPort $rule.Port -Action Allow -Profile Private | Out-Null
            }
        } catch {
            Write-Warn2 ("Could not add firewall rule {0}: {1}" -f $rule.Name, $_.Exception.Message)
        }
    }
    Write-Ok "Firewall rules ready (TCP $AgentPort, $WebPort)"
} else {
    Write-Warn2 'Not elevated: run these once in an elevated PowerShell to allow LAN clients:'
    Write-Host "    New-NetFirewallRule -DisplayName 'KamenRaider Controller' -Direction Inbound -Protocol TCP -LocalPort $AgentPort -Action Allow -Profile Private"
    Write-Host "    New-NetFirewallRule -DisplayName 'KamenRaider Web' -Direction Inbound -Protocol TCP -LocalPort $WebPort -Action Allow -Profile Private"
}

# --- 8. Optional start ------------------------------------------------------
if ($Start) {
    Write-Step 'Starting Controller and web'
    Start-Process -FilePath 'powershell.exe' `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $launcherPath) `
        -WorkingDirectory $repo
    Start-Process -FilePath 'cmd.exe' `
        -ArgumentList @('/c', "`"$webCmdPath`"") `
        -WorkingDirectory $repo

    $deadline = (Get-Date).AddSeconds(150)
    $controllerUp = $false
    $webUp = $false
    while ((Get-Date) -lt $deadline -and (-not $controllerUp -or -not $webUp)) {
        Start-Sleep -Milliseconds 750
        if (-not $controllerUp) { $controllerUp = Test-PortListening 5081 }
        if (-not $webUp) { $webUp = Test-PortListening $WebPort }
    }
    if ($controllerUp) {
        try {
            $health = (Invoke-WebRequest 'http://127.0.0.1:5081/internal/health' -Headers @{ 'X-Service-Credential' = $serviceCredential } -UseBasicParsing -TimeoutSec 10).Content
            Write-Ok "Controller health: $health"
        } catch { Write-Warn2 ("Controller listening but health failed: " + $_.Exception.Message) }
    } else { Write-Warn2 'Controller did not start listening on 5081; check its window.' }
    if ($webUp) {
        try {
            $status = (Invoke-WebRequest "http://127.0.0.1:$WebPort/" -UseBasicParsing -TimeoutSec 60).StatusCode
            Write-Ok "Web (local) -> $status"
        } catch { Write-Warn2 ("Web listening but the first request failed: " + $_.Exception.Message) }
    } else {
        Write-Warn2 "Web did not start listening; last lines of ${webLog}:"
        if (Test-Path -LiteralPath $webLog) { Get-Content -LiteralPath $webLog -Tail 25 | ForEach-Object { Write-Host "    $_" } }
        Write-Host "    Run manually: `"$webCmdPath`"   (or: cd `"$repo`"; pnpm dev)"
    }
}

# --- 9. Summary -------------------------------------------------------------
Write-Host ''
Write-Host '=== Mother configuration done ===' -ForegroundColor Cyan
Write-Host "Web (LAN):      $webOrigin"
Write-Host "Controller TLS: https://${LanIp}:$AgentPort  (cert $thumbprint)"
Write-Host "Launcher:       $launcherPath"
Write-Host "DB path:        $dbPath"
Write-Host "Package hash:   $((Get-EnvValue 'AGENT_PACKAGE_SHA256'))"
Write-Host ''
Write-Host 'Next steps:'
Write-Host "  1) Start:  powershell -NoProfile -ExecutionPolicy Bypass -File `"$launcherPath`"   (Controller window)"
Write-Host "  2) Web:    `"$webCmdPath`"   (or: pnpm dev / rerun with -Start)"
Write-Host "  3) Admin:  $webOrigin/login  ->  /admin/machines , /admin/settings/sunshine"
Write-Host "  4) Child PCs: import `"$certCer`" into LocalMachine\Root, then use the install command from /admin/machines"
Write-Host '  5) For cross-PC Agent installs the web must be HTTPS: set up Caddy (docs/production-commands.md), then rerun this script with -HttpsWeb.'
Write-Host ''
Write-Host 'WARNING: .env.local/data are tracked test data with secrets; rotate them before real use.' -ForegroundColor Yellow
