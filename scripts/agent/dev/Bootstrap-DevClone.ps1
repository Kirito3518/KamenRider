<#
.SYNOPSIS
  Bootstrap a freshly cloned repository as a development workstation
  (Controller + Next.js) with the bundled databases, Agent package and .env.local.

.DESCRIPTION
  Run this once after `git clone` + `pnpm install` on a new machine. It:
    1. checks Node/pnpm/.NET 10 and installs dependencies when node_modules is missing,
    2. ensures the ASP.NET Core development HTTPS certificate exists,
    3. rewrites absolute paths in the tracked .env.local to this clone's location
       (the committed file contains the original dev machine path),
    4. protects the data directory with a Controller-acceptable DACL,
    5. by default archives the bundled Controller database so a fresh v25 database
       starts outside restore quarantine (the bundled auth database with the admin
       accounts is kept),
    6. verifies the bundled Agent package hash matches AGENT_PACKAGE_SHA256,
    7. optionally starts the dev Controller and web and waits for both.

  Nothing secret is printed. Existing files are archived under .next-local\bootstrap-archive,
  never deleted. Use -KeepBundledDatabase to reproduce the committed database state
  instead of starting fresh (it starts in restore quarantine).

.PARAMETER KeepBundledDatabase
  Keep data\controller.sqlite* as committed. The Controller then starts in restore
  quarantine (Agent sync returns 409). The sidecar file is ACL-protected.

.PARAMETER ResetAuthDatabase
  Archive data\auth.sqlite* and data\.auth-secret so the web recreates an empty auth
  database (you then register a new account and promote it).

.PARAMETER Start
  Start the dev Controller and the web dev server in separate windows and verify them.

.PARAMETER TrustCertificate
  Run `dotnet dev-certs https --trust` as well (may show a Windows prompt).

.PARAMETER SkipInstall
  Do not run `pnpm install` even if node_modules is missing.

.PARAMETER Force
  Allow the fresh-database step while ports 5081/7443/3000 are already listening.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\dev\Bootstrap-DevClone.ps1 -Start
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$KeepBundledDatabase,
    [switch]$ResetAuthDatabase,
    [switch]$Start,
    [switch]$TrustCertificate,
    [switch]$SkipInstall,
    [string]$DotnetPath = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
# Pick up PATH changes made after this PowerShell session started (e.g. a just-installed SDK).
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User') + ';' + $env:Path
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$legacyRepo = 'C:\Users\User\Documents\kamenraiderplay'   # committed in .env.local by the original dev machine
$envFile = Join-Path $repo '.env.local'
$dataDir = Join-Path $repo 'data'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$archiveRoot = Join-Path $repo '.next-local\bootstrap-archive'
$archiveDir = Join-Path $archiveRoot $stamp

function Write-Step([string]$Message) { Write-Host "[*] $Message" }
function Write-Ok([string]$Message) { Write-Host "[ok] $Message" -ForegroundColor Green }
function Write-Warn2([string]$Message) { Write-Host "[warn] $Message" -ForegroundColor Yellow }
function Write-Fail([string]$Message) { Write-Host "[fail] $Message" -ForegroundColor Red }

function Test-PortListening([int]$Port) {
    return [bool]@(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue).Count
}

function Get-EnvValue([string]$Name) {
    $match = Select-String -LiteralPath $envFile -Pattern ("^" + [regex]::Escape($Name) + "=(.*)$")
    if (-not $match) { return $null }
    return $match.Matches[0].Groups[1].Value.Trim()
}

function Protect-DirectoryAcl([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "Directory not found: $Path" }
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $arguments = @($Path, '/inheritance:r',
        '/grant:r', 'SYSTEM:(OI)(CI)F',
        'BUILTIN\Administrators:(OI)(CI)F',
        "${user}:(OI)(CI)F")
    if ($PSCmdlet.ShouldProcess($Path, 'Set protected DACL (SYSTEM/Administrators/current user)')) {
        & icacls.exe @arguments | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "icacls failed for directory: $Path" }
    }
}

function Protect-FileAcl([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $arguments = @($Path, '/inheritance:r',
        '/grant:r', 'SYSTEM:F',
        'BUILTIN\Administrators:F',
        "${user}:F")
    if ($PSCmdlet.ShouldProcess($Path, 'Set protected file DACL (Controller restore sidecar)')) {
        & icacls.exe @arguments | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "icacls failed for file: $Path" }
    }
}

function Move-ToArchive([string]$Pattern) {
    $items = @(Get-ChildItem -LiteralPath $dataDir -Force -Filter $Pattern -ErrorAction SilentlyContinue)
    if ($items.Count -eq 0) { return 0 }
    if (-not (Test-Path -LiteralPath $archiveDir -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $archiveDir | Out-Null
    }
    foreach ($item in $items) {
        if ($PSCmdlet.ShouldProcess($item.FullName, "Archive to $archiveDir")) {
            Move-Item -LiteralPath $item.FullName -Destination $archiveDir -Force
        }
    }
    return $items.Count
}

Write-Host ''
Write-Host "=== KamenRaider dev bootstrap ===" -ForegroundColor Cyan
Write-Host "Repo: $repo"
Write-Host ''

# --- 1. Tools -----------------------------------------------------------------
Write-Step 'Checking toolchain'
function Add-ToolFallback([string]$Tool, [string[]]$Candidates) {
    if (Get-Command $Tool -ErrorAction SilentlyContinue) { return $true }
    foreach ($candidate in $Candidates) {
        if (-not $candidate) { continue }
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $directory = Split-Path -Parent $candidate
            if (($env:Path -split ';') -notcontains $directory) { $env:Path = $directory + ';' + $env:Path }
            Write-Warn2 "Found $Tool at $candidate but it is not on this window's PATH (normal right after installing it). New PowerShell windows find it automatically."
            return $true
        }
    }
    return $false
}

$missing = @()
if ($DotnetPath) {
    if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) { Write-Fail "DotnetPath not found: $DotnetPath"; exit 1 }
    $dotnetDir = Split-Path -Parent (Resolve-Path -LiteralPath $DotnetPath).Path
    $env:Path = $dotnetDir + ';' + $env:Path
    $env:DOTNET_ROOT = $dotnetDir
    Write-Ok "Using dotnet from $dotnetDir"
}
$programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
$dotnetCandidates = @((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))
if ($programFilesX86) { $dotnetCandidates += (Join-Path $programFilesX86 'dotnet\dotnet.exe') }
$dotnetCandidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
$dotnetCandidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
if (-not (Add-ToolFallback 'dotnet' $dotnetCandidates)) { $missing += 'dotnet' }
if (-not (Add-ToolFallback 'node' @((Join-Path $env:ProgramFiles 'nodejs\node.exe')))) { $missing += 'node' }
$pnpmCandidates = @(
    (Join-Path $env:APPDATA 'npm\pnpm.cmd'),
    (Join-Path $env:ProgramFiles 'nodejs\pnpm.cmd'),
    (Join-Path $env:ProgramFiles 'nodejs\pnpm.exe')
)
if (-not (Add-ToolFallback 'pnpm' $pnpmCandidates)) { $missing += 'pnpm' }
if ($missing.Count -gt 0) {
    Write-Fail ("Missing tools: " + ($missing -join ', '))
    Write-Host 'Install Node.js 24 LTS and pnpm (npm install -g pnpm@11.8.0), plus the .NET 10 SDK, then rerun this script.'
    Write-Host 'If you JUST installed a tool, this window still has the old PATH: run the PATH refresh shown in the docs, sign out/in, or pass -DotnetPath C:\Program Files\dotnet\dotnet.exe.'
    exit 1
}
$nodeVersion = (& node --version).Trim()
$dotnetVersion = (& dotnet --version).Trim()
$pnpmVersion = (& pnpm --version).Trim()
$nodeMajor = [int](($nodeVersion.TrimStart('v').Split('.')[0]))
$dotnetMajor = 0
if ($dotnetVersion -match '^(\d+)\.') { $dotnetMajor = [int]$Matches[1] }
Write-Ok "node $nodeVersion, pnpm $pnpmVersion, dotnet $dotnetVersion"
if ($nodeMajor -lt 20) { Write-Warn2 'Node 20 or newer is required; Node 24 LTS was tested.' }
if ($dotnetMajor -lt 10) { Write-Warn2 'The .NET 10 SDK is required (the repo targets net10.0).' }

if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { Write-Fail '.env.local is missing from the clone.'; exit 1 }
if (-not (Test-Path -LiteralPath $dataDir -PathType Container)) { Write-Fail 'The data directory is missing from the clone.'; exit 1 }

# --- 2. Dependencies ----------------------------------------------------------
$nodeModules = Join-Path $repo 'node_modules'
if (-not (Test-Path -LiteralPath $nodeModules -PathType Container)) {
    if ($SkipInstall) {
        Write-Warn2 'node_modules is missing but -SkipInstall was used.'
    } elseif ($PSCmdlet.ShouldProcess($repo, 'pnpm install --frozen-lockfile')) {
        Write-Step 'Installing pnpm dependencies'
        & pnpm install --frozen-lockfile
        if ($LASTEXITCODE -ne 0) { Write-Fail 'pnpm install failed.'; exit 1 }
        Write-Ok 'Dependencies installed'
    }
} else {
    Write-Ok 'node_modules present'
}

# --- 3. Dev HTTPS certificate (Controller Agent listener on https://localhost:7443) --
$services = Get-Command dotnet -ErrorAction Stop
Write-Step 'Checking the ASP.NET Core development HTTPS certificate'
# PowerShell 5.1 turns native stderr into a terminating error when $ErrorActionPreference is
# 'Stop', and the very first dotnet invocation can print first-run messages to stderr. Capture
# the output with 'Continue' and decide on the exit code instead.
$savedEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $checkOutput = & dotnet dev-certs https --check --quiet 2>&1
    $checkExit = $LASTEXITCODE
} finally { $ErrorActionPreference = $savedEap }
if ($checkExit -ne 0) {
    if ($PSCmdlet.ShouldProcess('dotnet dev-certs', 'Create the development HTTPS certificate')) {
        $savedEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $createOutput = & dotnet dev-certs https 2>&1
            $createExit = $LASTEXITCODE
        } finally { $ErrorActionPreference = $savedEap }
        if ($createExit -ne 0) {
            Write-Fail 'Could not create the development HTTPS certificate.'
            if ($createOutput) { Write-Host (($createOutput | Out-String).Trim()) -ForegroundColor DarkGray }
            exit 1
        }
    }
    Write-Ok 'Created the development HTTPS certificate'
} else {
    Write-Ok 'Development HTTPS certificate exists'
}
if ($TrustCertificate) {
    if ($PSCmdlet.ShouldProcess('dotnet dev-certs', 'Trust the development HTTPS certificate')) {
        $savedEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { & dotnet dev-certs https --trust 2>&1 | Out-Null } finally { $ErrorActionPreference = $savedEap }
    }
}

# --- 4. Rewrite the committed .env.local paths --------------------------------
Write-Step 'Pointing .env.local at this clone'
$envLines = Get-Content -LiteralPath $envFile
$changed = 0
$updated = foreach ($line in $envLines) {
    $newLine = $line
    if ($line.IndexOf($legacyRepo, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $newLine = $line.Replace($legacyRepo, $repo)
    }
    if ($newLine -match '^CONTROLLER_DATABASE_PATH=(.*)$') {
        $value = $Matches[1].Trim()
        if (-not [IO.Path]::IsPathRooted($value)) {
            $newLine = 'CONTROLLER_DATABASE_PATH=' + (Join-Path $dataDir 'controller.sqlite')
        }
    }
    if ($newLine -cne $line) { $changed++ }
    $newLine
}
if ($changed -gt 0) {
    if ($PSCmdlet.ShouldProcess($envFile, "Rewrite $changed line(s) to the current clone path")) {
        New-Item -ItemType Directory -Force -Path (Join-Path $repo '.next-local') | Out-Null
        Copy-Item -LiteralPath $envFile -Destination (Join-Path $repo ".next-local\env-local-$stamp.bak") -Force
        Set-Content -LiteralPath $envFile -Value $updated -Encoding ASCII
    }
    Write-Ok "Updated $changed line(s) in .env.local (backup in .next-local)"
} else {
    Write-Ok '.env.local paths already point at this clone'
}

# --- 5. Data directory ACL (the Controller refuses insecure paths) ------------
Write-Step 'Protecting the data directory ACL'
Protect-DirectoryAcl $dataDir
Write-Ok 'data directory DACL is protected (SYSTEM/Administrators/current user)'

# --- 6. Controller database mode ---------------------------------------------
$listening = @()
foreach ($port in @(5081, 7443, 3000)) {
    if (Test-PortListening $port) { $listening += $port }
}

if ($KeepBundledDatabase) {
    Write-Step 'Keeping the bundled Controller database (starts in restore quarantine)'
    Protect-FileAcl (Join-Path $dataDir 'controller.sqlite.restore-epoch')
    Write-Warn2 'Agent sync will return 409 until the restore-quarantine design is resolved.'
} else {
    Write-Step 'Archiving the bundled Controller database for a fresh v25 start (no quarantine)'
    if ($listening.Count -gt 0 -and -not $Force) {
        Write-Fail ("Ports already in use: " + ($listening -join ', '))
        Write-Host 'Stop the running Controller/web first (or use -KeepBundledDatabase / -Force).'
        exit 1
    }
    $moved = 0
    $moved += Move-ToArchive 'controller.sqlite*'
    $moved += Move-ToArchive 'controller.sqlite.*'
    if ($moved -gt 0) {
        Write-Ok "Archived $moved Controller database/sidecar file(s) under .next-local\bootstrap-archive\$stamp"
    } else {
        Write-Ok 'No bundled Controller database found; a fresh one will be created on start'
    }
}

if ($ResetAuthDatabase) {
    Write-Step 'Archiving the bundled auth database'
    $movedAuth = Move-ToArchive 'auth.sqlite*'
    $movedSecret = Move-ToArchive '.auth-secret'
    Write-Ok "Archived $($movedAuth + $movedSecret) auth file(s); register and promote a new admin after the web starts"
}

# --- 7. Bundled Agent package integrity --------------------------------------
Write-Step 'Verifying the bundled Agent package'
$package = Join-Path $repo 'public\agent\AgentPackage.zip'
if (Test-Path -LiteralPath $package -PathType Leaf) {
    # Compute the hash with .NET so -WhatIf does not suppress the read-only operation.
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::OpenRead($package)
        try { $actual = [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '') }
        finally { $stream.Dispose() }
    } finally { $sha256.Dispose() }
    $expected = (Get-EnvValue 'AGENT_PACKAGE_SHA256')
    if ($expected -and ($expected.ToUpperInvariant() -eq $actual)) {
        Write-Ok "AgentPackage.zip SHA-256 matches .env.local ($actual)"
    } else {
        Write-Warn2 "AgentPackage.zip SHA-256 does not match AGENT_PACKAGE_SHA256 (file $actual / env $expected); rebuild with scripts\agent\Build-AgentPackage.ps1 and update .env.local."
    }
} else {
    Write-Warn2 'public\agent\AgentPackage.zip is missing; build it with scripts\agent\Build-AgentPackage.ps1 before generating an Agent install command.'
}

# --- 8. Environment sanity warnings ------------------------------------------
# Read-only discovery must not trigger -WhatIf noise from module auto-loading cmdlets.
$whatIfWas = $WhatIfPreference
$WhatIfPreference = $false
try {
    $wake = Get-EnvValue 'CONTROLLER_WAKE_INTERFACE_ADDRESS'
    if ($wake) {
        $localIps = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notmatch '^(127\.|169\.254\.)' } | ForEach-Object { $_.IPAddress })
        if ($localIps -notcontains $wake) {
            Write-Warn2 "CONTROLLER_WAKE_INTERFACE_ADDRESS=$wake is not an IP of this machine. Update it (and the broadcast) or leave both empty if you do not use Wake-on-LAN."
        }
    }
    $vncSource = Get-EnvValue 'AGENT_VNC_ALLOWED_SOURCE_IP'
    if ($vncSource) {
        Write-Warn2 "AGENT_VNC_ALLOWED_SOURCE_IP=$vncSource is inherited from the original dev machine; update it when you install an Agent on a different LAN."
    }
} finally {
    $WhatIfPreference = $whatIfWas
}

# --- 9. Optional start --------------------------------------------------------
if ($Start) {
    Write-Step 'Starting the dev Controller and web'
    if (Test-PortListening 5081 -or Test-PortListening 7443) {
        Write-Warn2 'Ports 5081/7443 are already in use; not starting another Controller.'
    } else {
        Start-Process -FilePath 'powershell.exe' `
            -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Run-Controller-Local.ps1')) `
            -WorkingDirectory $repo
    }
    if (Test-PortListening 3000) {
        Write-Warn2 'Port 3000 is already in use; not starting another web server.'
    } else {
        Start-Process -FilePath 'powershell.exe' `
            -ArgumentList @('-NoProfile', '-Command', "Set-Location '$repo'; pnpm dev") `
            -WorkingDirectory $repo
    }

    $deadline = (Get-Date).AddSeconds(120)
    $controllerUp = $false
    $webUp = $false
    while ((Get-Date) -lt $deadline -and (-not $controllerUp -or -not $webUp)) {
        Start-Sleep -Milliseconds 750
        if (-not $controllerUp) { $controllerUp = Test-PortListening 5081 }
        if (-not $webUp) { $webUp = Test-PortListening 3000 }
    }
    if ($controllerUp) {
        $credential = Get-EnvValue 'CONTROLLER_SERVICE_CREDENTIAL'
        try {
            $health = (Invoke-WebRequest 'http://127.0.0.1:5081/internal/health' -Headers @{ 'X-Service-Credential' = $credential } -UseBasicParsing -TimeoutSec 10).Content
            Write-Ok "Controller health: $health"
        } catch {
            Write-Warn2 ("Controller is listening but the health check failed: " + $_.Exception.Message)
        }
    } else {
        Write-Warn2 'Controller did not start listening on 5081; check its window.'
    }
    if ($webUp) {
        try {
            $status = (Invoke-WebRequest 'http://localhost:3000/' -UseBasicParsing -TimeoutSec 60).StatusCode
            Write-Ok "Web: http://localhost:3000 -> $status"
        } catch {
            Write-Warn2 ("Web is listening but the first request failed: " + $_.Exception.Message)
        }
    } else {
        Write-Warn2 'Web dev server did not start listening on 3000; check its window.'
    }
}

# --- 10. Summary --------------------------------------------------------------
Write-Host ''
Write-Host '=== Done ===' -ForegroundColor Cyan
Write-Host ("Repo:        " + $repo)
Write-Host ("DB mode:     " + $(if ($KeepBundledDatabase) { 'bundled database kept (restore quarantine expected)' } else { 'fresh database on next Controller start (no quarantine)' }))
Write-Host ("Auth DB:     " + $(if ($ResetAuthDatabase) { 'reset (register a new admin)' } else { 'bundled (existing admin accounts kept)' }))
Write-Host ''
Write-Host 'Next:'
Write-Host '  1) powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\dev\Restart-DevStack.ps1   (if -Start was not used)'
Write-Host '  2) open http://localhost:3000/login'
Write-Host '  3) admin pages: /admin/machines and /admin/settings/sunshine'
Write-Host ''
Write-Host 'Reminder: .env.local and data/ are tracked in this test repository and contain secrets from the original dev machine.' -ForegroundColor Yellow
Write-Host 'Rotate them before any real use. See docs/development-commands.md and docs/database-operations.md.' -ForegroundColor Yellow
