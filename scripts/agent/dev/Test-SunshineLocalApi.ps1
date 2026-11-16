[CmdletBinding()]
param(
    [switch]$Plan
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:BlockRuleName = 'KamenRaider Stream Guard - Sunshine'
$script:SunshinePath = 'C:\Program Files\Sunshine\sunshine.exe'
$script:SunshineServiceName = 'SunshineService'
$script:ApiUri = 'https://localhost:47990/api/clients/list'
$script:AgentInstallRoot = 'C:\Program Files\KamenRaider\Agent'
$script:ExpectedDataDirectory = Join-Path $env:ProgramData 'KamenRaider\Agent'
$script:ProbeStagingRoot = 'C:\Windows\System32\KamenRaiderStreamGuardDevProbe'

function Test-AnyFilterValue {
    param([AllowNull()][string]$Value)

    return [string]::IsNullOrWhiteSpace($Value) -or
        $Value -eq '*' -or
        $Value -ieq 'Any'
}

function Test-ManagedSunshineBlock {
    $rules = @(Get-NetFirewallRule -Name $script:BlockRuleName -PolicyStore ActiveStore -ErrorAction Stop)
    if ($rules.Count -ne 1) { return $false }

    $rule = $rules[0]
    $application = $rule | Get-NetFirewallApplicationFilter -ErrorAction Stop
    $service = $rule | Get-NetFirewallServiceFilter -ErrorAction Stop
    $port = $rule | Get-NetFirewallPortFilter -ErrorAction Stop
    $address = $rule | Get-NetFirewallAddressFilter -ErrorAction Stop
    $interface = $rule | Get-NetFirewallInterfaceTypeFilter -ErrorAction Stop
    $overrideProperty = $rule.PSObject.Properties['OverrideBlockRules']
    $overrideBlockRules = $false
    if ($null -ne $overrideProperty) { $overrideBlockRules = [bool]$overrideProperty.Value }

    $actualPath = $null
    if (-not [string]::IsNullOrWhiteSpace($application.Program)) {
        try { $actualPath = [IO.Path]::GetFullPath($application.Program) } catch { return $false }
    }

    return ($rule.Enabled -eq 'True') -and
        $rule.Name -eq $script:BlockRuleName -and
        $rule.Action -eq 'Block' -and
        $rule.Direction -eq 'Inbound' -and
        $rule.Profile -eq 'Any' -and
        [string]::Equals($actualPath, $script:SunshinePath, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-AnyFilterValue $service.Service) -and
        $port.Protocol -eq 'Any' -and
        (Test-AnyFilterValue $port.LocalPort) -and
        (Test-AnyFilterValue $port.RemotePort) -and
        (Test-AnyFilterValue $address.LocalAddress) -and
        (Test-AnyFilterValue $address.RemoteAddress) -and
        ($interface.InterfaceType -eq 'Any' -or $interface.InterfaceType -eq 'All') -and
        $rule.EdgeTraversalPolicy -eq 'Block' -and
        -not $overrideBlockRules
}

function Test-WindowsFirewallReady {
    try {
        # ActiveStore reports effective policy. Require every profile because
        # the active network can change while the diagnostic runs.
        $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore -All -ErrorAction Stop)
        foreach ($name in @('Domain', 'Private', 'Public')) {
            $matchingProfiles = @($profiles | Where-Object { [string]$_.Name -eq $name })
            if ($matchingProfiles.Count -ne 1) { return $false }
            if ([string]$matchingProfiles[0].Enabled -ne 'True') { return $false }
        }

        $firewallService = Get-Service -Name 'MpsSvc' -ErrorAction Stop
        return $firewallService.Status.ToString() -eq 'Running'
    }
    catch {
        return $false
    }
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-SunshinePortClosed {
    $client = [System.Net.Sockets.TcpClient]::new()
    $connect = $null
    try {
        $connect = $client.BeginConnect('127.0.0.1', 47990, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne(600, $false)) { return $true }
        try { $client.EndConnect($connect); return $false }
        catch { return $true }
    }
    catch {
        return $true
    }
    finally {
        if ($null -ne $connect) { $connect.AsyncWaitHandle.Close() }
        $client.Dispose()
    }
}

function Set-SunshinePortWaitFailure {
    param([string]$SunshineState)

    if ($SunshineState -eq 'Running') { return $false }

    $guardState = (Get-Service -Name 'KamenRaider Stream Guard' -ErrorAction Stop).Status.ToString()
    $result.SunshineStateDuringPortWait = $SunshineState
    $result.GuardDuringPortWait = $guardState
    $result.Status = 'SunshineNotRunningDuringPortWait'
    $result.FailureCategory = 'SunshineNotRunningDuringPortWait'
    $result.FailureDetail = if ($guardState -eq 'Running') {
        'Stream Guard remained Running; Guard enforcement is possible.'
    }
    else {
        'Sunshine remained stopped during the port wait.'
    }
    return $true
}

function Get-SanitizedFailureCategory {
    param([System.Exception]$Exception)

    $current = $Exception
    while ($null -ne $current) {
        if ($current -is [System.Security.Authentication.AuthenticationException]) { return 'TlsValidationFailed' }
        $current = $current.InnerException
    }

    if ($Exception -is [System.Threading.Tasks.TaskCanceledException]) { return 'TimedOut' }
    if ($Exception -is [System.Net.Http.HttpRequestException]) { return 'HttpsRequestFailed' }
    if ($Exception -is [System.UnauthorizedAccessException]) { return 'AccessDenied' }
    if ($Exception -is [System.IO.IOException]) { return 'LocalIoFailed' }
    return $Exception.GetType().Name
}

function Get-LocalMachineId {
    $settingsPath = Join-Path $script:AgentInstallRoot 'appsettings.json'
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) { throw 'AgentConfigurationUnavailable' }

    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $dataDirectory = [IO.Path]::GetFullPath([string]$settings.DataDirectory)
    if (-not [string]::Equals(
        $dataDirectory.TrimEnd('\'),
        ([IO.Path]::GetFullPath($script:ExpectedDataDirectory)).TrimEnd('\'),
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'AgentDataDirectoryMismatch'
    }

    $identityDirectory = Join-Path $dataDirectory 'identity'
    $identityPath = Join-Path $identityDirectory 'identity.json'
    foreach ($path in @($dataDirectory, $identityDirectory, $identityPath)) {
        if (-not (Test-Path -LiteralPath $path)) { throw 'AgentIdentityUnavailable' }
        $item = Get-Item -LiteralPath $path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'AgentIdentityPathRejected' }
    }

    # Read the identity only to obtain the machine ID; never emit or pass its private key.
    $identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
    $machineId = [string]$identity.MachineId
    if ($machineId -notmatch '^[0-9a-f]{32}$') { throw 'AgentMachineIdInvalid' }
    return $machineId
}

function Get-LocalControllerValues {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
    $envFile = Join-Path $repoRoot '.env.local'
    if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { throw 'ControllerConfigurationUnavailable' }

    $values = @{}
    foreach ($line in Get-Content -LiteralPath $envFile) {
        if ($line -match '^\s*(CONTROLLER_INTERNAL_URL|CONTROLLER_SERVICE_CREDENTIAL)\s*=(.*)$') {
            $value = $Matches[2].Trim()
            if ($value.Length -ge 2 -and
                (($value[0] -eq '"' -and $value[$value.Length - 1] -eq '"') -or
                 ($value[0] -eq "'" -and $value[$value.Length - 1] -eq "'"))) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            $values[$Matches[1]] = $value
        }
    }

    if ([string]::IsNullOrWhiteSpace($values['CONTROLLER_INTERNAL_URL']) -or
        [string]::IsNullOrWhiteSpace($values['CONTROLLER_SERVICE_CREDENTIAL'])) {
        throw 'ControllerConfigurationIncomplete'
    }

    $controllerUri = $null
    if (-not [Uri]::TryCreate($values['CONTROLLER_INTERNAL_URL'], [UriKind]::Absolute, [ref]$controllerUri) -or
        $controllerUri.Scheme -ne 'http' -or -not $controllerUri.IsLoopback -or
        $controllerUri.UserInfo -or $controllerUri.Query -or $controllerUri.Fragment -or
        $controllerUri.AbsolutePath -notin @('/', '')) {
        throw 'ControllerEndpointNotLocal'
    }

    return [pscustomobject]@{
        BaseUri = $controllerUri.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
        Credential = $values['CONTROLLER_SERVICE_CREDENTIAL']
    }
}

function ConvertFrom-ControllerMachineJson {
    param(
        [Parameter(Mandatory)][string]$MachineId,
        [Parameter(Mandatory)][string]$Json
    )

    # Wrap the response so PowerShell preserves a one-element root array
    # as an array on both Windows PowerShell 5.1 and PowerShell 7.
    $wrapper = ConvertFrom-Json -InputObject ('{"response":' + $Json + '}') -ErrorAction Stop
    if ($wrapper -isnot [pscustomobject]) { throw 'ControllerMachineResponseInvalid' }
    $responseProperties = @($wrapper.PSObject.Properties | Where-Object { $_.Name -ceq 'response' })
    if ($responseProperties.Count -ne 1) { throw 'ControllerMachineResponseInvalid' }
    $records = $responseProperties[0].Value
    if ($records -isnot [System.Array]) { throw 'ControllerMachineResponseInvalid' }

    $matches = [System.Collections.Generic.List[object]]::new()
    foreach ($record in $records) {
        if ($record -isnot [pscustomobject]) { throw 'ControllerMachineResponseInvalid' }
        $machineIdProperties = @($record.PSObject.Properties | Where-Object { $_.Name -ceq 'machineId' })
        if ($machineIdProperties.Count -ne 1 -or $machineIdProperties[0].Value -isnot [string]) {
            throw 'ControllerMachineResponseInvalid'
        }
        if ($machineIdProperties[0].Value -ne $MachineId) { continue }

        $approvalProperties = @($record.PSObject.Properties | Where-Object { $_.Name -ceq 'approvalStatus' })
        $readinessProperties = @($record.PSObject.Properties | Where-Object { $_.Name -ceq 'readiness' })
        $canRentProperties = @($record.PSObject.Properties | Where-Object { $_.Name -ceq 'canRent' })
        if ($approvalProperties.Count -ne 1 -or $readinessProperties.Count -ne 1 -or
            $canRentProperties.Count -ne 1 -or $approvalProperties[0].Value -isnot [string] -or
            $readinessProperties[0].Value -isnot [string] -or $canRentProperties[0].Value -isnot [bool]) {
            throw 'ControllerMachineResponseInvalid'
        }

        $allocationProperties = @($record.PSObject.Properties | Where-Object { $_.Name -ceq 'allocationHeld' })
        if ($allocationProperties.Count -gt 1) { throw 'ControllerMachineResponseInvalid' }
        $allocationPresent = $allocationProperties.Count -eq 1
        $allocationValue = $null
        if ($allocationPresent) {
            if ($allocationProperties[0].Value -isnot [bool]) { throw 'ControllerMachineResponseInvalid' }
            $allocationValue = $allocationProperties[0].Value
        }

        $matches.Add([pscustomobject]@{
            MachineId = $MachineId
            ApprovalStatus = $approvalProperties[0].Value
            Readiness = $readinessProperties[0].Value
            CanRent = $canRentProperties[0].Value
            AllocationHeldPresent = $allocationPresent
            AllocationHeld = $allocationValue
        })
    }

    if ($matches.Count -ne 1) { throw 'ControllerMachineIdentityNotUnique' }
    return $matches[0]
}

function Test-ControllerMachineClosedForDevProbe {
    param([Parameter(Mandatory)][psobject]$MachineState)

    return $MachineState.ApprovalStatus -eq 'pending' -and
        $MachineState.Readiness -in @('notReady', 'unreachable') -and
        -not $MachineState.CanRent -and
        -not ($MachineState.AllocationHeldPresent -and $MachineState.AllocationHeld)
}

function Get-ControllerMachineState {
    param([Parameter(Mandatory)][string]$MachineId)

    $controller = Get-LocalControllerValues
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.UseCookies = $false
    $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(5)
    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Get,
        ($controller.BaseUri + '/internal/machines'))
    $request.Headers.Add('X-Service-Credential', $controller.Credential)
    try {
        $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            if (-not $response.IsSuccessStatusCode) { throw 'ControllerMachineQueryFailed' }
            $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            return ConvertFrom-ControllerMachineJson -MachineId $MachineId -Json $json
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $request.Dispose()
        $client.Dispose()
        $handler.Dispose()
    }
}

function Set-ProbeDirectoryAcl {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ProbeStagingPathRejected' }

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

    $applied = Get-Acl -LiteralPath $Path
    if (-not $applied.AreAccessRulesProtected) { throw 'ProbeStagingAclNotProtected' }
    $ownerSid = $applied.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($ownerSid -ne $administratorsSid.Value) { throw 'ProbeStagingOwnerUnexpected' }
    $allowedSids = @('S-1-5-18', 'S-1-5-32-544')
    foreach ($accessRule in $applied.Access) {
        $identity = $accessRule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        if ($identity -notin $allowedSids -or $accessRule.AccessControlType -ne 'Allow' -or
            $accessRule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl) {
            throw 'ProbeStagingAclUnexpected'
        }
    }
    if ($applied.Access.Count -ne 2 -or @($applied.Access | ForEach-Object {
        $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
    } | Select-Object -Unique).Count -ne 2) { throw 'ProbeStagingAclUnexpected' }
}

function Assert-ProbeAncestorSecurity {
    $systemSid = 'S-1-5-18'
    $administratorsSid = 'S-1-5-32-544'
    $trustedInstallerSid = (New-Object Security.Principal.NTAccount('NT SERVICE', 'TrustedInstaller')).Translate(
        [Security.Principal.SecurityIdentifier]).Value
    $trustedOwners = @($systemSid, $administratorsSid, $trustedInstallerSid)
    $creatorOwnerSid = 'S-1-3-0'
    $paths = @('C:\', 'C:\Windows', 'C:\Windows\System32')
    $writeMask = [long]0x10000000 -bor [long]0x40000000 -bor 2 -bor 4 -bor 16 -bor 64 -bor 256 -bor 65536 -bor 262144 -bor 524288

    foreach ($path in $paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw 'ProbeStagingPathUnavailable' }
        $item = Get-Item -LiteralPath $path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ProbeStagingPathRejected' }

        $acl = Get-Acl -LiteralPath $path
        $ownerSid = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($ownerSid -notin $trustedOwners) { throw 'ProbeStagingOwnerUnexpected' }
        foreach ($accessRule in $acl.Access) {
            if ($accessRule.AccessControlType -ne 'Allow') { continue }
            if (($accessRule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0) { continue }
            if ($accessRule.IdentityReference.Value -eq 'CREATOR OWNER' -or
                $accessRule.IdentityReference.Value -eq $creatorOwnerSid) { continue }
            try { $identitySid = $accessRule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value }
            catch { $identitySid = $null }
            if ($identitySid -in $trustedOwners) { continue }

            $effectiveWriteMask = $writeMask
            # The drive root grants authenticated users create-directory on
            # the root itself. It cannot replace the existing Windows child;
            # delete-child and all direct file-write rights remain forbidden.
            if ($path -eq 'C:\') { $effectiveWriteMask = $effectiveWriteMask -band (-bnot [long]4) }
            if (([long]$accessRule.FileSystemRights -band $effectiveWriteMask) -ne 0) {
                throw 'ProbeStagingAncestorWritable'
            }
        }
    }
}

function New-ProbeStage {
    Assert-ProbeAncestorSecurity
    $stagingRoot = $script:ProbeStagingRoot
    if (-not (Test-Path -LiteralPath $stagingRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $stagingRoot -ErrorAction Stop | Out-Null
    }
    $rootItem = Get-Item -LiteralPath $stagingRoot -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ProbeStagingPathRejected' }

    Set-ProbeDirectoryAcl -Path $stagingRoot
    $stagePath = Join-Path $stagingRoot ([Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $stagePath) { throw 'ProbeStagingCollision' }
    New-Item -ItemType Directory -Path $stagePath | Out-Null
    Set-ProbeDirectoryAcl -Path $stagePath
    return $stagePath
}

function Remove-ProbeStage {
    param([Parameter(Mandatory)][string]$Path)

    Assert-ProbeAncestorSecurity
    $stagingRootPath = [IO.Path]::GetFullPath($script:ProbeStagingRoot)
    $stagingRoot = $stagingRootPath.TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($stagingRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $fullPath) -notmatch '^[0-9a-f]{32}$') {
        throw 'ProbeStagingCleanupPathRejected'
    }
    $rootAcl = Get-Acl -LiteralPath $stagingRootPath
    if (-not $rootAcl.AreAccessRulesProtected -or
        $rootAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544') {
        throw 'ProbeStagingAclNotProtected'
    }
    $rootRules = @($rootAcl.Access)
    if ($rootRules.Count -ne 2) { throw 'ProbeStagingAclUnexpected' }
    foreach ($accessRule in $rootRules) {
        $identitySid = $accessRule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        if ($identitySid -notin @('S-1-5-18', 'S-1-5-32-544') -or
            $accessRule.AccessControlType -ne 'Allow' -or
            $accessRule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl) {
            throw 'ProbeStagingAclUnexpected'
        }
    }
    $item = Get-Item -LiteralPath $fullPath -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ProbeStagingCleanupPathRejected' }
    $stageAcl = Get-Acl -LiteralPath $fullPath
    if (-not $stageAcl.AreAccessRulesProtected -or
        $stageAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544' -or
        @($stageAcl.Access).Count -ne 2) { throw 'ProbeStagingAclUnexpected' }
    foreach ($accessRule in $stageAcl.Access) {
        $identitySid = $accessRule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        if ($identitySid -notin @('S-1-5-18', 'S-1-5-32-544') -or
            $accessRule.AccessControlType -ne 'Allow' -or
            $accessRule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl) {
            throw 'ProbeStagingAclUnexpected'
        }
    }
    foreach ($child in @(Get-ChildItem -LiteralPath $fullPath -Recurse -Force -ErrorAction Stop)) {
        if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ProbeStagingCleanupPathRejected' }
    }
    Remove-Item -LiteralPath $fullPath -Recurse -Force
}

function Wait-ServiceStatus {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Status,
        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $service = Get-Service -Name $Name -ErrorAction Stop
        if ($service.Status.ToString() -eq $Status) { return $true }
        if ([DateTime]::UtcNow -ge $deadline) { return $false }
        Start-Sleep -Milliseconds 250
    } while ($true)
}

function Stop-AndWaitProbeTask {
    param(
        [Parameter(Mandatory)][string]$Name,
        [int]$TimeoutSeconds = 30
    )

    $task = Get-ScheduledTask -TaskName $Name -ErrorAction Stop
    $state = $task.State.ToString()
    if ($state -in @('Running', 'Queued')) {
        Stop-ScheduledTask -TaskName $Name -ErrorAction Stop
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $task = Get-ScheduledTask -TaskName $Name -ErrorAction Stop
        $state = $task.State.ToString()
        if ($state -in @('Ready', 'Disabled')) { return $true }
        if ([DateTime]::UtcNow -ge $deadline) { return $false }
        Start-Sleep -Milliseconds 250
    } while ($true)
}

$result = [ordered]@{
    Mode = if ($Plan) { 'Plan' } else { 'Run' }
    Status = 'NotStarted'
    Preflight = 'NotRun'
    AgentService = 'Unknown'
    GuardService = 'Unknown'
    WindowsFirewallBefore = $false
    WindowsFirewallAfter = $null
    SunshineBefore = 'Unknown'
    SunshineAfter = 'Unknown'
    FirewallBlockBefore = $false
    FirewallBlockAfter = $null
    SunshinePortClosedBefore = $false
    SunshinePortClosedAfter = $null
    MachineIdVerified = $false
    ControllerMachineStatus = 'NotChecked'
    AllocationHeldVerified = $false
    LeaseSafetyEvidence = 'NotChecked'
    GuardClosedStateVerified = $false
    ProbeTaskStatus = 'NotCreated'
    ApiStatus = 'NotAttempted'
    TlsValidated = $false
    FailureCategory = $null
    FailureDetail = $null
    CleanupStatus = 'NotNeeded'
    AgentRestartStatus = 'NotNeeded'
}

$agentWasRunning = $false
$agentStopAttempted = $false
$taskName = $null
$taskRegistered = $false
$stagePath = $null
$helperExitCode = $null
$probeResultPath = $null
$controllerState = $null

try {
    if ($env:OS -ne 'Windows_NT') { throw [PlatformNotSupportedException]::new() }

    $agent = Get-Service -Name 'KamenRaider Agent' -ErrorAction Stop
    $guard = Get-Service -Name 'KamenRaider Stream Guard' -ErrorAction Stop
    $sunshine = Get-Service -Name $script:SunshineServiceName -ErrorAction Stop

    $result.AgentService = $agent.Status.ToString()
    $result.GuardService = $guard.Status.ToString()
    $result.SunshineBefore = $sunshine.Status.ToString()
    $agentWasRunning = $agent.Status.ToString() -eq 'Running'
    $result.WindowsFirewallBefore = Test-WindowsFirewallReady
    $result.FirewallBlockBefore = Test-ManagedSunshineBlock
    $result.SunshinePortClosedBefore = Test-SunshinePortClosed

    if (-not $agentWasRunning -or $result.GuardService -ne 'Running') {
        $result.Preflight = 'Failed'
        $result.Status = 'PreflightFailed'
        $result.FailureCategory = 'AgentOrGuardNotRunning'
    }
    elseif (-not $result.WindowsFirewallBefore) {
        $result.Preflight = 'Failed'
        $result.Status = 'PreflightFailed'
        $result.FailureCategory = 'WindowsFirewallNotReady'
    }
    elseif (-not $result.FirewallBlockBefore) {
        $result.Preflight = 'Failed'
        $result.Status = 'PreflightFailed'
        $result.FailureCategory = 'ManagedBlockNotVerified'
    }
    elseif ($result.SunshineBefore -ne 'Stopped' -or -not $result.SunshinePortClosedBefore) {
        $result.Preflight = 'Failed'
        $result.Status = 'PreflightFailed'
        $result.FailureCategory = 'SunshineNotClosedBeforeProbe'
    }
    else {
        $result.Preflight = 'Passed'
        $result.GuardClosedStateVerified = $true

        if ($Plan) {
            $result.Status = 'PlanOnly'
        }
        elseif (-not (Test-IsAdministrator)) {
            # Do not request elevation; the owner must launch an elevated shell manually.
            $result.Status = 'ElevationRequired'
            $result.FailureCategory = 'RunFromElevatedPowerShell'
        }
        else {
            $machineId = Get-LocalMachineId
            $result.MachineIdVerified = $true
            $controllerState = Get-ControllerMachineState -MachineId $machineId
            $result.AllocationHeldVerified = $controllerState.AllocationHeldPresent -and -not $controllerState.AllocationHeld

            if (-not (Test-ControllerMachineClosedForDevProbe -MachineState $controllerState)) {
                $result.ControllerMachineStatus = 'NotClosedForDevProbe'
                $result.Status = 'PreflightFailed'
                $result.FailureCategory = 'ControllerMachineNotClosedForDevProbe'
            }
            else {
                $result.ControllerMachineStatus = 'VerifiedClosedForDevProbe'
                $result.LeaseSafetyEvidence = if ($result.AllocationHeldVerified) {
                    'Controller reports this exact machine as pending and notReady or unreachable, canRent=false, and allocationHeld=false.'
                }
                else {
                    'Controller reports this exact machine as pending and notReady or unreachable, with canRent=false; allocationHeld is not present in the live API response.'
                }
                $helperProject = Join-Path $PSScriptRoot 'StreamGuardFencedApiProbe\StreamGuardFencedApiProbe.csproj'
                if (-not (Test-Path -LiteralPath $helperProject -PathType Leaf)) { throw 'FencedProbeHelperUnavailable' }

                $stagePath = New-ProbeStage
                $publishPath = Join-Path $stagePath 'publish'
                $artifactsPath = Join-Path $stagePath 'artifacts'
                New-Item -ItemType Directory -Path $publishPath | Out-Null
                New-Item -ItemType Directory -Path $artifactsPath | Out-Null
                $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
                & $dotnet publish $helperProject --configuration Release --runtime win-x64 --self-contained true --output $publishPath --artifacts-path $artifactsPath '--property:RestoreIgnoreFailedSources=true' '-p:DefaultItemExcludesInProjectFolder=**/obj*/**'
                if ($LASTEXITCODE -ne 0) { throw 'FencedProbeHelperBuildFailed' }

                $helperExe = Join-Path $publishPath 'StreamGuardFencedApiProbe.exe'
                if (-not (Test-Path -LiteralPath $helperExe -PathType Leaf)) { throw 'FencedProbeHelperBuildFailed' }
                $probeResultPath = Join-Path $publishPath 'result.json'
                $taskName = 'KamenRaider-Sunshine-DevProbe-' + [Guid]::NewGuid().ToString('N')
                $action = New-ScheduledTaskAction -Execute $helperExe
                $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
                $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -MultipleInstances IgnoreNew
                Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Description 'One-shot local development Sunshine TLS probe.' | Out-Null
                $taskRegistered = $true
                $result.ProbeTaskStatus = 'RegisteredAsSystem'

                # Re-check the exact local machine and closed stream state after build/staging,
                # immediately before interrupting the Agent pipe session.
                $controllerState = Get-ControllerMachineState -MachineId $machineId
                if (-not (Test-ControllerMachineClosedForDevProbe -MachineState $controllerState)) {
                    throw 'ControllerMachineChangedBeforeProbe'
                }
                $result.ControllerMachineStatus = 'VerifiedImmediatelyBeforeStop'
                $agent = Get-Service -Name 'KamenRaider Agent' -ErrorAction Stop
                $guard = Get-Service -Name 'KamenRaider Stream Guard' -ErrorAction Stop
                $sunshine = Get-Service -Name $script:SunshineServiceName -ErrorAction Stop
                if ($agent.Status.ToString() -ne 'Running' -or $guard.Status.ToString() -ne 'Running' -or
                    $sunshine.Status.ToString() -ne 'Stopped' -or -not (Test-WindowsFirewallReady) -or
                    -not (Test-ManagedSunshineBlock) -or -not (Test-SunshinePortClosed)) {
                    throw 'LocalGuardStateChangedBeforeProbe'
                }

                Write-Host 'Local development probe: the Agent service will be stopped briefly to release its single Stream Guard pipe session, then restarted in finally. Stream Guard stays running; Sunshine will be started only behind its verified firewall block and stopped by the fence.'
                $agentStopAttempted = $true
                Stop-Service -Name 'KamenRaider Agent' -ErrorAction Stop
                if (-not (Wait-ServiceStatus -Name 'KamenRaider Agent' -Status 'Stopped')) { throw 'AgentStopTimedOut' }

                Start-ScheduledTask -TaskName $taskName
                $result.ProbeTaskStatus = 'RunningAsSystem'
                $taskDeadline = [DateTime]::UtcNow.AddSeconds(75)
                $taskWasRunning = $false
                do {
                    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
                    $taskState = $task.State.ToString()
                    if ($taskState -eq 'Running') { $taskWasRunning = $true }
                    $hasResult = Test-Path -LiteralPath $probeResultPath -PathType Leaf
                    if ($hasResult -and $taskState -in @('Ready', 'Disabled')) { break }
                    if (-not $hasResult -and $taskWasRunning -and $taskState -in @('Ready', 'Disabled')) {
                        throw 'FencedProbeTaskExitedWithoutResult'
                    }
                    if ([DateTime]::UtcNow -ge $taskDeadline) { throw 'FencedProbeTaskTimedOut' }
                    Start-Sleep -Milliseconds 250
                } while ($true)

                $probe = Get-Content -LiteralPath $probeResultPath -Raw | ConvertFrom-Json
                $taskInfo = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction Stop
                $helperExitCode = [int]$taskInfo.LastTaskResult
                $result.ApiStatus = if ($null -ne $probe.HttpStatus) { 'HTTP ' + [int]$probe.HttpStatus } else { 'NotReached' }
                $result.TlsValidated = [bool]$probe.TlsValidated
                if ($probe.Status -eq 'RequestCompleted' -and $helperExitCode -eq 0) {
                    $result.Status = 'RequestCompleted'
                    $result.CleanupStatus = 'FencedCleanupCompleted'
                }
                else {
                    $result.Status = 'RequestFailed'
                    $result.FailureCategory = if ($probe.FailureCategory) { [string]$probe.FailureCategory } else { 'FencedProbeFailed' }
                }
            }
        }
    }
}
catch {
    $result.Status = 'ProbeFailed'
    $result.FailureCategory = Get-SanitizedFailureCategory $_.Exception
}
finally {
    $probeTaskQuiesced = -not $taskRegistered
    if ($taskRegistered -and $taskName) {
        try {
            if (Stop-AndWaitProbeTask -Name $taskName -TimeoutSeconds 30) {
                $probeTaskQuiesced = $true
                Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
                $result.ProbeTaskStatus = 'Removed'
            }
            else {
                $result.ProbeTaskStatus = 'StopUnconfirmed'
                $result.Status = 'ProbeTaskStopUnconfirmed'
                $result.FailureDetail = 'The scheduled probe task may still be running. Agent restart and staging cleanup were skipped; the helper stage was retained.'
                if (-not $result.FailureCategory) { $result.FailureCategory = 'ScheduledTaskStopUnconfirmed' }
            }
        }
        catch {
            $result.ProbeTaskStatus = 'StopOrRemovalFailed'
            $result.Status = if ($probeTaskQuiesced) { 'ScheduledTaskCleanupFailed' } else { 'ProbeTaskStopUnconfirmed' }
            if (-not $probeTaskQuiesced) {
                $result.FailureDetail = 'The scheduled probe task could not be confirmed stopped. Agent restart and staging cleanup were skipped; the helper stage was retained.'
            }
            if (-not $result.FailureCategory) {
                $result.FailureCategory = if ($probeTaskQuiesced) { 'ScheduledTaskCleanupFailed' } else { 'ScheduledTaskStopUnconfirmed' }
            }
        }
    }

    if ($agentWasRunning -and $agentStopAttempted) {
        if (-not $probeTaskQuiesced) {
            $result.AgentRestartStatus = 'SkippedTaskMayBeRunning'
        }
        else {
            try {
                $currentAgent = Get-Service -Name 'KamenRaider Agent' -ErrorAction Stop
                if ($currentAgent.Status.ToString() -ne 'Running') {
                    Start-Service -Name 'KamenRaider Agent' -ErrorAction Stop
                }
                if (Wait-ServiceStatus -Name 'KamenRaider Agent' -Status 'Running' -TimeoutSeconds 45) {
                    $result.AgentRestartStatus = 'Running'
                }
                else {
                    $result.AgentRestartStatus = 'RestartTimedOut'
                    $result.Status = 'AgentRestartFailed'
                    if (-not $result.FailureCategory) { $result.FailureCategory = 'AgentRestartTimedOut' }
                }
            }
            catch {
                $result.AgentRestartStatus = 'RestartFailed'
                $result.Status = 'AgentRestartFailed'
                if (-not $result.FailureCategory) { $result.FailureCategory = 'AgentRestartFailed' }
            }
        }
    }

    if ($stagePath) {
        if (-not $probeTaskQuiesced) {
            $result.CleanupStatus = 'StagingRetainedTaskMayBeRunning'
        }
        else {
            try {
                Remove-ProbeStage -Path $stagePath
                $result.CleanupStatus = if ($result.CleanupStatus -eq 'NotNeeded') { 'StagingRemoved' } else { $result.CleanupStatus + ';StagingRemoved' }
            }
            catch {
                if (-not $result.FailureCategory) { $result.FailureCategory = 'StagingCleanupFailed' }
                $result.CleanupStatus = 'StagingCleanupFailed'
            }
        }
    }

    try { $result.SunshineAfter = (Get-Service -Name $script:SunshineServiceName -ErrorAction Stop).Status.ToString() }
    catch { $result.SunshineAfter = 'Unavailable' }
    try { $result.GuardService = (Get-Service -Name 'KamenRaider Stream Guard' -ErrorAction Stop).Status.ToString() }
    catch { $result.GuardService = 'Unavailable' }
    $result.WindowsFirewallAfter = Test-WindowsFirewallReady
    try { $result.FirewallBlockAfter = Test-ManagedSunshineBlock }
    catch { $result.FirewallBlockAfter = $false }
    try { $result.SunshinePortClosedAfter = Test-SunshinePortClosed }
    catch { $result.SunshinePortClosedAfter = $false }

    if ($result.SunshineAfter -ne 'Stopped' -or -not $result.FirewallBlockAfter -or -not $result.WindowsFirewallAfter -or -not $result.SunshinePortClosedAfter) {
        if (-not $result.FailureCategory) { $result.FailureCategory = 'FinalGuardStateNotVerified' }
        if ($result.Status -eq 'RequestCompleted') { $result.Status = 'FinalStateNotVerified' }
    }

    [pscustomobject]$result
}
