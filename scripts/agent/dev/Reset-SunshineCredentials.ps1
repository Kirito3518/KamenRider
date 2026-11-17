[CmdletBinding()]
param(
    [string]$UserName = 'admin',
    [switch]$Plan
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# One-shot local operator wrapper for the paused Sunshine setup work on this host:
#  1. verifies the closed Stream Guard state and the installed Agent/Sunshine services,
#  2. builds the SunshineCredentialReset helper into an administrator-only staging directory,
#  3. provisions a Windows-trusted localhost TLS certificate for the Sunshine Web manager,
#  4. resets the Sunshine Web manager credentials and mirrors them into the Agent DPAPI store,
#  5. writes a user-only credential note outside the repository and verifies the final closed state.
# The generated password is never printed. Run this from elevated PowerShell.

$script:SunshineRoot = 'C:\Program Files\Sunshine'
$script:SunshineServiceName = 'SunshineService'
$script:SunshinePort = 47990
$script:AgentDataDirectory = Join-Path $env:ProgramData 'KamenRaider\Agent'
$script:StageRoot = Join-Path $env:ProgramData 'KamenRaider\SunshineSetup'
$script:CredentialNotePath = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'sunshine-web-credentials.txt'
$script:BlockRuleName = 'KamenRaider Stream Guard - Sunshine'
$script:HelperResultPath = Join-Path $script:StageRoot 'helper-result.json'
$script:ResultPath = Join-Path $script:StageRoot 'reset-result.json'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-ManagedSunshineBlock {
    $rules = @(Get-NetFirewallRule -Name $script:BlockRuleName -PolicyStore ActiveStore -ErrorAction SilentlyContinue)
    if ($rules.Count -ne 1) { return $false }
    $rule = $rules[0]
    return ($rule.Enabled -eq 'True') -and ($rule.Action -eq 'Block') -and ($rule.Direction -eq 'Inbound')
}

function Test-SunshinePortClosed {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.BeginConnect('127.0.0.1', $script:SunshinePort, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne(600, $false)) { return $true }
        try { $client.EndConnect($connect); return $false }
        catch { return $true }
    }
    catch {
        return $true
    }
    finally {
        $client.Dispose()
    }
}

function Set-AdministratorOnlyAcl {
    param([Parameter(Mandatory)][string]$Path)

    $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($administratorsSid)
    foreach ($sid in @($systemSid, $administratorsSid)) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $security.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $security
}

function Test-RootStoreContains {
    param(
        [Parameter(Mandatory)][ValidateSet('LocalMachine', 'CurrentUser')][string]$StoreLocation,
        [Parameter(Mandatory)][string]$Thumbprint
    )

    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::Root,
        [Security.Cryptography.X509Certificates.StoreLocation]$StoreLocation)
    try {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        return $store.Certificates.Find(
            [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $Thumbprint,
            $false).Count -gt 0
    }
    finally {
        $store.Dispose()
    }
}

$outcome = [ordered]@{
    Mode = if ($Plan) { 'Plan' } else { 'Apply' }
    UserName = $UserName
    SunshineRoot = $script:SunshineRoot
    AgentDataDirectory = $script:AgentDataDirectory
    CredentialNotePath = $script:CredentialNotePath
    StageRoot = $script:StageRoot
    Status = 'NotStarted'
    FailureCategory = $null
    Gate = 'NotChecked'
    GuardService = 'Unknown'
    AgentService = 'Unknown'
    SunshineBefore = 'Unknown'
    TlsAction = $null
    TlsThumbprint = $null
    TrustedInLocalMachineRoot = $false
    TrustedInCurrentUserRoot = $false
    CredentialState = 'NotChanged'
    PairingStatePreserved = $false
    AgentStoreVerified = $false
    SunshineAfter = 'Unknown'
    GuardAfter = 'Unknown'
    FirewallBlockAfter = $false
    SunshinePortClosedAfter = $false
    HelperResultPath = $script:HelperResultPath
    ResultPath = $script:ResultPath
}

try {
    if (-not (Test-IsAdministrator)) { throw 'Run this script from an elevated (Administrator) PowerShell session.' }
    if ($env:OS -ne 'Windows_NT') { throw 'This script supports Windows only.' }

    $userNameIsValid = -not [string]::IsNullOrWhiteSpace($UserName) -and
        $UserName.Length -le 128 -and
        -not $UserName.Contains(':') -and
        -not ($UserName.ToCharArray() | Where-Object { [char]::IsControl($_) })
    if (-not $userNameIsValid) { throw 'The Sunshine management user name is invalid.' }

    $guard = Get-Service -Name 'KamenRaider Stream Guard' -ErrorAction Stop
    $agent = Get-Service -Name 'KamenRaider Agent' -ErrorAction Stop
    $sunshine = Get-Service -Name $script:SunshineServiceName -ErrorAction Stop
    $outcome.GuardService = $guard.Status.ToString()
    $outcome.AgentService = $agent.Status.ToString()
    $outcome.SunshineBefore = $sunshine.Status.ToString()

    if ($guard.Status.ToString() -ne 'Running') { throw 'The Stream Guard service must be Running.' }
    if ($agent.Status.ToString() -ne 'Running') { throw 'The KamenRaider Agent service must be Running.' }
    if ($sunshine.Status.ToString() -ne 'Stopped') { throw 'The Sunshine service must be Stopped; Stream Guard keeps it stopped while no stream authority is active.' }
    if (-not (Test-ManagedSunshineBlock)) { throw 'The managed Stream Guard firewall block for Sunshine is not in place.' }
    if (-not (Test-SunshinePortClosed)) { throw 'Sunshine port 47990 is already accepting connections.' }
    if (-not (Test-Path -LiteralPath (Join-Path $script:AgentDataDirectory 'identity\identity.json') -PathType Leaf)) {
        throw 'The installed Agent identity is unavailable; the Agent data directory does not look initialized.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $script:SunshineRoot 'Sunshine.exe') -PathType Leaf) -and
        -not (Test-Path -LiteralPath (Join-Path $script:SunshineRoot 'sunshine.exe') -PathType Leaf)) {
        throw 'Sunshine.exe was not found under the configured Sunshine root.'
    }
    $outcome.Gate = 'ClosedForSetup'

    if ($Plan) {
        $outcome.Status = 'PlanOnly'
    }
    else {
        if (-not (Test-Path -LiteralPath $script:StageRoot -PathType Container)) {
            New-Item -ItemType Directory -Path $script:StageRoot -ErrorAction Stop | Out-Null
        }
        Set-AdministratorOnlyAcl -Path $script:StageRoot
        $publishPath = Join-Path $script:StageRoot 'publish'
        $artifactsPath = Join-Path $script:StageRoot 'artifacts'
        foreach ($path in @($publishPath, $artifactsPath)) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
            New-Item -ItemType Directory -Path $path -ErrorAction Stop | Out-Null
        }
        Remove-Item -LiteralPath $script:HelperResultPath -Force -ErrorAction SilentlyContinue

        $helperProject = Join-Path $PSScriptRoot 'SunshineCredentialReset\SunshineCredentialReset.csproj'
        if (-not (Test-Path -LiteralPath $helperProject -PathType Leaf)) { throw 'The SunshineCredentialReset helper project is missing.' }
        $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
        Write-Host 'Building the Sunshine setup helper...'
        & $dotnet publish $helperProject --configuration Release --runtime win-x64 --self-contained true --output $publishPath --artifacts-path $artifactsPath '--property:RestoreIgnoreFailedSources=true' '-p:DefaultItemExcludesInProjectFolder=**/obj*/**'
        if ($LASTEXITCODE -ne 0) { throw 'SunshineCredentialResetBuildFailed' }
        $helper = Join-Path $publishPath 'SunshineCredentialReset.exe'
        if (-not (Test-Path -LiteralPath $helper -PathType Leaf)) { throw 'SunshineCredentialResetBuildFailed' }

        Write-Host 'Applying the Sunshine management certificate and credentials...'
        & $helper --apply --sunshine-root $script:SunshineRoot --agent-data $script:AgentDataDirectory --username $UserName --credential-note $script:CredentialNotePath --result-file $script:HelperResultPath
        $helperExitCode = $LASTEXITCODE
        if (-not (Test-Path -LiteralPath $script:HelperResultPath -PathType Leaf)) { throw 'SunshineSetupHelperResultMissing' }
        $setup = Get-Content -LiteralPath $script:HelperResultPath -Raw | ConvertFrom-Json

        $outcome.TlsAction = [string]$setup.TlsAction
        $outcome.TlsThumbprint = [string]$setup.TlsThumbprint
        $outcome.TrustedInLocalMachineRoot = [bool]$setup.TrustedInLocalMachineRoot
        $outcome.TrustedInCurrentUserRoot = [bool]$setup.TrustedInCurrentUserRoot
        $outcome.CredentialState = [string]$setup.CredentialStateVerification
        $outcome.PairingStatePreserved = [bool]$setup.PairingStatePreserved
        $outcome.AgentStoreVerified = [bool]$setup.AgentStoreVerified

        if ($helperExitCode -ne 0 -or [string]$setup.Status -ne 'Succeeded') {
            $outcome.FailureCategory = if ($setup.FailureCategory) { [string]$setup.FailureCategory } else { 'SunshineSetupHelperFailed' }
            throw $outcome.FailureCategory
        }

        # Verify the resulting state independently of the helper's own report.
        $outcome.SunshineAfter = (Get-Service -Name $script:SunshineServiceName -ErrorAction Stop).Status.ToString()
        $outcome.GuardAfter = (Get-Service -Name 'KamenRaider Stream Guard' -ErrorAction Stop).Status.ToString()
        $outcome.FirewallBlockAfter = Test-ManagedSunshineBlock
        $outcome.SunshinePortClosedAfter = Test-SunshinePortClosed

        $statePath = Join-Path $script:SunshineRoot 'config\sunshine_state.json'
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        $credentialsNoteExists = Test-Path -LiteralPath $script:CredentialNotePath -PathType Leaf
        $agentStorePath = Join-Path $script:AgentDataDirectory 'sunshine\management-credentials.dpapi'
        $agentStoreExists = Test-Path -LiteralPath $agentStorePath -PathType Leaf
        $thumbprint = [string]$setup.TlsThumbprint
        $trustedLocalMachine = $thumbprint.Length -gt 0 -and (Test-RootStoreContains -StoreLocation 'LocalMachine' -Thumbprint $thumbprint)
        $trustedCurrentUser = $thumbprint.Length -gt 0 -and (Test-RootStoreContains -StoreLocation 'CurrentUser' -Thumbprint $thumbprint)

        $verified = [string]$state.username -eq $UserName -and
            $outcome.GuardAfter -eq 'Running' -and
            $outcome.SunshineAfter -eq 'Stopped' -and
            $outcome.FirewallBlockAfter -and
            $outcome.SunshinePortClosedAfter -and
            $trustedLocalMachine -and
            $agentStoreExists -and
            $credentialsNoteExists

        $outcome.TrustedInLocalMachineRoot = $trustedLocalMachine
        $outcome.TrustedInCurrentUserRoot = $trustedCurrentUser
        $outcome.AgentStoreVerified = $outcome.AgentStoreVerified -and $agentStoreExists
        if (-not $verified) {
            $outcome.FailureCategory = 'FinalStateNotVerified'
            throw 'FinalStateNotVerified'
        }

        $outcome.Status = 'Succeeded'
    }
}
catch {
    if ($outcome.Status -eq 'NotStarted') { $outcome.Status = 'Failed' }
    if (-not $outcome.FailureCategory) { $outcome.FailureCategory = $_.Exception.GetType().Name }
    $outcome.FailureDetail = $_.Exception.Message
}
finally {
    if ($outcome.Status -eq 'Succeeded' -and -not $Plan) {
        try {
            [IO.File]::WriteAllText($script:ResultPath, ($outcome | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
        }
        catch {
            $outcome.Status = 'ResultWriteFailed'
        }
    }
    [pscustomobject]$outcome
}

if ($outcome.Status -notin @('Succeeded', 'PlanOnly')) { throw ("Sunshine credential reset failed: " + $outcome.FailureCategory) }
