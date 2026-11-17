<#
.SYNOPSIS
  Prepare a Mother PC so the development agent can access it remotely over Tailscale + WinRM
  (HTTPS), then install/configure Cloudflare Tunnel and the app from that remote session.

  Run this ON THE MOTHER PC from an ELEVATED PowerShell. It:
    * installs Tailscale if needed and prints the Tailscale IPv4 (you must be signed in to the
      same tailnet as the dev PC - the dev PC is already on the account 0jaysnowy@),
    * enables PowerShell Remoting with an HTTPS listener on 5986 (self-signed),
    * allows the local administrator token over the network (LocalAccountTokenFilterPolicy),
    * opens TCP 5986 on the firewall (any profile, Tailscale is often "Public"),
    * creates a temporary local administrator account and prints its random password ONCE.

  Send the printed Tailscale IP + account + password back to the agent. The agent connects with
  PowerShell Remoting, installs the cloudflared service, configures the app and starts it.

  Revoke afterwards with:  .\Enable-MotherRemoteAdmin.ps1 -Remove

.PARAMETER AccountName
  Temporary local admin account name (default kr-deploy).

.PARAMETER AccountPassword
  Optional fixed password; by default a random strong password is generated and printed.

.PARAMETER Remove
  Remove the temporary account and the WinRM firewall rule (run elevated).

.PARAMETER SkipTailscale
  Do not install/check Tailscale (already installed and signed in).

.EXAMPLE
  # ELEVATED · Mother
  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\dev\Enable-MotherRemoteAdmin.ps1
#>
[CmdletBinding()]
param(
    [string]$AccountName = 'kr-deploy',
    [string]$AccountPassword = '',
    [switch]$Remove,
    [switch]$SkipTailscale
)

$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + $env:Path

function Write-Step([string]$m) { Write-Host "[*] $m" }
function Write-Ok([string]$m) { Write-Host "[ok] $m" -ForegroundColor Green }
function Write-Warn2([string]$m) { Write-Host "[warn] $m" -ForegroundColor Yellow }
function Write-Fail([string]$m) { Write-Host "[fail] $m" -ForegroundColor Red }
function Invoke-Native([string]$Exe, [string[]]$Arguments) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Exe @Arguments 2>&1
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $saved }
    if ($output) { $output | ForEach-Object { Write-Host "    $_" } }
    return $code
}
function New-RandomPassword([int]$Length = 24) {
    $chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
    $bytes = New-Object 'byte[]' $Length
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($Remove) {
    if (-not $isAdmin) { Write-Fail 'Run as Administrator to remove the remote admin setup.'; exit 1 }
    Write-Step "Removing account $AccountName"
    try { Remove-LocalUser -Name $AccountName -ErrorAction Stop; Write-Ok "Removed user $AccountName" } catch { Write-Warn2 "Remove-LocalUser: $($_.Exception.Message)" }
    try { Remove-NetFirewallRule -DisplayName 'KamenRaider WinRM HTTPS (Tailscale)' -ErrorAction Stop; Write-Ok 'Removed WinRM firewall rule' } catch { Write-Warn2 "firewall rule: $($_.Exception.Message)" }
    try { Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name LocalAccountTokenFilterPolicy -ErrorAction SilentlyContinue } catch { }
    Write-Ok 'Done. WinRM HTTPS listener/Tailscale can stay (they do not grant access without credentials).'
    exit 0
}

if (-not $isAdmin) { Write-Fail 'Run this from an ELEVATED PowerShell (Run as Administrator).'; exit 1 }

Write-Host ''
Write-Host '=== KamenRaider Mother remote-admin setup ===' -ForegroundColor Cyan
Write-Host ''

# --- Tailscale ---------------------------------------------------------------
if (-not $SkipTailscale) {
    Write-Step 'Checking Tailscale'
    $tailscale = (Get-Command tailscale -ErrorAction SilentlyContinue).Source
    if (-not $tailscale) {
        $fallback = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
        if (Test-Path -LiteralPath $fallback -PathType Leaf) { $tailscale = $fallback; $env:Path = (Split-Path $fallback -Parent) + ';' + $env:Path }
    }
    if (-not $tailscale) {
        Write-Step 'Installing Tailscale'
        $installed = $false
        $winget = (Get-Command winget -ErrorAction SilentlyContinue).Source
        if ($winget) {
            $null = Invoke-Native $winget @('install', '--id', 'tailscale.tailscale', '-e', '--accept-source-agreements', '--accept-package-agreements', '--silent')
            $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + $env:Path
            $tailscale = (Get-Command tailscale -ErrorAction SilentlyContinue).Source
            $installed = [bool]$tailscale
        }
        if (-not $installed) {
            Write-Fail 'Tailscale is not installed. Install it from https://tailscale.com/download/windows (or winget install tailscale.tailscale), sign in with the SAME account as the dev PC, then rerun.'
            exit 1
        }
    }
    Write-Ok "Tailscale: $tailscale"
    $tsStatus = & $tailscale status 2>&1
    $tsText = ($tsStatus | Out-String)
    if ($LASTEXITCODE -ne 0 -or $tsText -match 'Logged out|not logged in|Stopped') {
        Write-Warn2 'Tailscale is not signed in. Open the Tailscale tray app (or run: tailscale up) and sign in with the SAME account as the dev PC, then rerun this script.'
    }
}

$tsIp = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceAlias -like 'Tailscale*' -and $_.IPAddress -like '100.*' } | Select-Object -First 1 -ExpandProperty IPAddress)
if (-not $tsIp) {
    Write-Warn2 'Could not read a Tailscale IPv4 (100.x) from the Tailscale interface.'
} else {
    Write-Ok "Tailscale IPv4: $tsIp"
}

# --- WinRM --------------------------------------------------------------
Write-Step 'Enabling PowerShell Remoting (HTTP local + HTTPS 5986)'
try { Enable-PSRemoting -Force -SkipNetworkProfileCheck -WarningAction SilentlyContinue | Out-Null; Write-Ok 'PowerShell Remoting enabled' }
catch { Write-Warn2 ("Enable-PSRemoting: " + $_.Exception.Message) }

try {
    Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name LocalAccountTokenFilterPolicy -Value 1 -Type DWord -Force
    Write-Ok 'Local administrator token over the network enabled (LocalAccountTokenFilterPolicy=1)'
} catch { Write-Warn2 ("LocalAccountTokenFilterPolicy: " + $_.Exception.Message) }

try { Set-Item -Path WSMan:\localhost\Service\Auth\Basic -Value $true -Force; Write-Ok 'WinRM Basic auth enabled (only reachable through Tailscale in this setup)' }
catch { Write-Warn2 ("WinRM Basic auth: " + $_.Exception.Message) }

$cert = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq "CN=$env:COMPUTERNAME" -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    Write-Step 'Creating a self-signed WinRM certificate'
    $cert = New-SelfSignedCertificate -DnsName $env:COMPUTERNAME -CertStoreLocation 'Cert:\LocalMachine\My' -NotAfter (Get-Date).AddYears(3) -KeyExportPolicy Exportable
}
$listenerText = ''
try { $listenerText = ((& winrm enumerate winrm/config/Listener 2>&1) | Out-String) } catch { }
$hasHttps = $listenerText -match 'Transport = HTTPS'
if (-not $hasHttps) {
    try {
        New-Item -Path WSMan:\localhost\Listener -Transport HTTPS -Address * -CertificateThumbprint $cert.Thumbprint -Force | Out-Null
        Write-Ok "HTTPS WinRM listener created (cert $($cert.Thumbprint))"
    } catch { Write-Warn2 ("HTTPS listener: " + $_.Exception.Message) }
} else {
    Write-Ok 'HTTPS WinRM listener already exists'
}

try {
    if (-not (Get-NetFirewallRule -DisplayName 'KamenRaider WinRM HTTPS (Tailscale)' -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName 'KamenRaider WinRM HTTPS (Tailscale)' -Direction Inbound -Protocol TCP -LocalPort 5986 -Action Allow -Profile Any | Out-Null
    }
    Write-Ok 'Firewall allows TCP 5986'
} catch { Write-Warn2 ("firewall: " + $_.Exception.Message) }

# --- temporary admin account ------------------------------------------------
Write-Step "Preparing temporary administrator '$AccountName'"
$existing = Get-LocalUser -Name $AccountName -ErrorAction SilentlyContinue
if (-not $existing) {
    if ([string]::IsNullOrWhiteSpace($AccountPassword)) { $AccountPassword = New-RandomPassword }
    $secure = ConvertTo-SecureString $AccountPassword -AsPlainText -Force
    New-LocalUser -Name $AccountName -Password $secure -FullName 'KamenRaider remote deploy' -Description 'Temporary remote deployment account' -PasswordNeverExpires -AccountNeverExpires | Out-Null
    Add-LocalGroupMember -Group 'Administrators' -Member $AccountName
    Write-Ok "Created local administrator $AccountName"
} else {
    if (-not [string]::IsNullOrWhiteSpace($AccountPassword)) {
        Set-LocalUser -Name $AccountName -Password (ConvertTo-SecureString $AccountPassword -AsPlainText -Force)
        Write-Ok "Reset password for existing account $AccountName"
    } else {
        Write-Warn2 "Account $AccountName already exists; its current password is unknown to this script. Pass -AccountPassword to set one."
        $AccountPassword = '<existing password>'
    }
}
try { net.exe user $AccountName /active:yes | Out-Null } catch { }

# --- summary ----------------------------------------------------------------
Write-Host ''
Write-Host '=== Send these values to the agent (private chat) ===' -ForegroundColor Cyan
Write-Host ("Tailscale IPv4: " + $(if ($tsIp) { $tsIp } else { '<run tailscale ip -4 on this PC>' }))
Write-Host ("Account:        {0}\{1}" -f $env:COMPUTERNAME, $AccountName)
Write-Host ("Password:       {0}" -f $AccountPassword)
Write-Host 'WinRM:          HTTPS, port 5986 (self-signed; ignore certificate errors)'
Write-Host ''
Write-Host 'Next: send the Tailscale IP + account + password to the agent. It will connect with PowerShell Remoting,' -ForegroundColor Green
Write-Host 'install/configure the Cloudflare Tunnel and the app, then report back.'
Write-Host ''
Write-Host 'Revoke later (elevated):' -ForegroundColor Yellow
Write-Host "  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\dev\Enable-MotherRemoteAdmin.ps1 -Remove"
Write-Host '  # then rotate the app secrets and delete the Tailscale device if it is no longer needed.'
